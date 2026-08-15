using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed class BackupService
{
    internal const string FormatName = "jarvis-portable-backup";
    internal const int FormatVersion = 1;
    internal const int CurrentDatabaseVersion = 9;
    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "jarvis.sqlite3";
    private const long MaximumManifestBytes = 64 * 1024;
    private const long MaximumDatabaseBytes = 4L * 1024 * 1024 * 1024;
    private readonly string _databasePath;
    private readonly string _dataDirectory;
    private readonly string _connectionString;
    private readonly IBackupPasswordStore _passwordStore;
    private readonly IBaiduClientProbe _baiduClientProbe;

    public BackupService(
        string databasePath,
        IBackupPasswordStore passwordStore,
        IBaiduClientProbe baiduClientProbe)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _dataDirectory = Path.GetDirectoryName(_databasePath)!;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        _passwordStore = passwordStore;
        _baiduClientProbe = baiduClientProbe;
    }

    public async Task ConfigureAsync(
        string directoryPath,
        string password,
        string confirmPassword,
        bool savePassword,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            throw new ArgumentException("两次输入的备份密码不一致。");
        ValidatePassword(password);
        var fullDirectory = Path.GetFullPath(directoryPath);
        if (string.Equals(fullDirectory, _dataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择专用同步子目录，不能直接使用 Jarvis 正式数据目录。");
        Directory.CreateDirectory(fullDirectory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_settings SET directory_path=$path,last_error=NULL WHERE singleton=1;";
        command.Parameters.AddWithValue("$path", fullDirectory);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (savePassword)
            await _passwordStore.SaveAsync(password, cancellationToken).ConfigureAwait(false);
        else
            await _passwordStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ForgetPasswordAsync(CancellationToken cancellationToken) =>
        _passwordStore.DeleteAsync(cancellationToken);

    public async Task<BackupStatusView> ReadStatusAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var passwordStored = !string.IsNullOrEmpty(
            await _passwordStore.ReadAsync(cancellationToken).ConfigureAwait(false));
        var running = _baiduClientProbe.IsRunning();
        var unavailableSince = settings.LastBaiduClientSeenAt ?? settings.ClientWaitingSinceAt;
        var attention = !running && settings.LastSuccessAt is not null && unavailableSince is { } since &&
                        now - since >= TimeSpan.FromHours(24);
        var cloudStatus = settings.DirectoryPath is null
            ? "未配置本地同步目录；云端状态未知。"
            : running
                ? "百度网盘客户端已运行，等待其处理；Jarvis 无法确认云端上传。"
                : attention
                    ? "本地备份已验证，但百度网盘客户端连续 24 小时未运行；云端状态未知。"
                    : "本地备份状态可查；百度网盘云端状态未知。";
        return new(
            settings.DirectoryPath, passwordStored, settings.LastSuccessAt,
            settings.LastBackupPath, settings.LastValidatedAt, running,
            cloudStatus, attention, settings.LastError,
            settings.DailyRetention, settings.MonthlyRetention, settings.UpgradeRetention);
    }

    public async Task AdvanceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.DirectoryPath)) return;
        var running = _baiduClientProbe.IsRunning();
        if (running)
        {
            await UpdateBaiduSeenAsync(now, cancellationToken).ConfigureAwait(false);
        }

        var password = await _passwordStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(password) &&
            (settings.LastAutoAttemptAt is null ||
             now - settings.LastAutoAttemptAt.Value >= TimeSpan.FromHours(1)))
        {
            await UpdateAutoAttemptAsync(now, cancellationToken).ConfigureAwait(false);
            try
            {
                var localNow = now.ToLocalTime();
                if (!Directory.EnumerateFiles(
                        settings.DirectoryPath, $"jarvis-daily-{localNow:yyyyMMdd}-*.jarvis-backup")
                    .Any())
                    await CreateAsync(BackupKind.Daily, null, now, cancellationToken).ConfigureAwait(false);
                if (!Directory.EnumerateFiles(
                        settings.DirectoryPath, $"jarvis-monthly-{localNow:yyyyMM}*.jarvis-backup")
                    .Any())
                    await CreateAsync(BackupKind.Monthly, null, now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await SaveErrorAsync(
                    $"自动备份失败：{exception.Message}", CancellationToken.None).ConfigureAwait(false);
            }
            settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }

        var unavailableSince = settings.LastBaiduClientSeenAt ?? settings.ClientWaitingSinceAt;
        if (!running && settings.LastSuccessAt is not null && unavailableSince is { } since &&
            now - since >= TimeSpan.FromHours(24) &&
            (settings.LastBaiduWarningAt is null || now - settings.LastBaiduWarningAt.Value >= TimeSpan.FromHours(24)))
            await UpdateBaiduWarningAsync(now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupOperationView> CreateAsync(
        BackupKind kind,
        string? suppliedPassword,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.DirectoryPath))
            throw new InvalidOperationException("请先配置百度网盘客户端同步的本地备份目录。");
        var password = suppliedPassword;
        if (string.IsNullOrEmpty(password))
            password = await _passwordStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("当前电脑未保存备份密码；请本次输入密码或选择保存。");
        ValidatePassword(password);

        Directory.CreateDirectory(settings.DirectoryPath);
        var scratch = Path.Combine(_dataDirectory, ".backup-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var snapshotPath = Path.Combine(scratch, "snapshot.sqlite");
        var token = KindToken(kind);
        var fileName = $"jarvis-{token}-{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jarvis-backup";
        var destination = Path.Combine(settings.DirectoryPath, fileName);
        var temporaryArchive = destination + ".tmp";
        try
        {
            await CreateConsistentSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var databaseVersion = await ValidateDatabaseAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            string digest;
            await using (var snapshotStream = File.OpenRead(snapshotPath))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(
                    snapshotStream, cancellationToken).ConfigureAwait(false));
            var manifest = new BackupManifest(
                FormatName, FormatVersion, now, kind, databaseVersion,
                typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0.0.0", digest);
            CreateArchive(snapshotPath, temporaryArchive, password, manifest);
            var validation = await ValidateArchiveAsync(
                temporaryArchive, password, scratch, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryArchive, destination, overwrite: false);
            await SaveSuccessAsync(destination, now, cancellationToken).ConfigureAwait(false);
            var cleanupFailures = await CleanupRetentionAsync(
                settings.DirectoryPath, settings, cancellationToken).ConfigureAwait(false);
            var message = cleanupFailures.Count == 0
                ? "密码保护备份已生成并通过完整性、版本和可打开性校验。"
                : $"密码保护备份已验证，但有 {cleanupFailures.Count} 个过期文件无法清理；请检查磁盘空间或文件占用。";
            if (cleanupFailures.Count > 0)
                await SaveErrorAsync(message, CancellationToken.None).ConfigureAwait(false);
            return new(true, message,
                destination, kind, now, validation.DatabaseVersion, true);
        }
        catch (Exception exception)
        {
            await SaveErrorAsync(exception.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDelete(temporaryArchive);
            TryDeleteDirectory(scratch);
        }
    }

    public async Task<BackupOperationView> TestRestoreAsync(
        string backupPath,
        string password,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var scratch = Path.Combine(_dataDirectory, ".restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var result = await ValidateArchiveAsync(
                Path.GetFullPath(backupPath), password, scratch, cancellationToken).ConfigureAwait(false);
            return new(true, "备份已在隔离目录解密并校验；正式数据没有被覆盖。",
                Path.GetFullPath(backupPath), result.Manifest.Kind, result.Manifest.CreatedAt,
                result.DatabaseVersion, true);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    public async Task<BackupOperationView> ScheduleRestoreAsync(
        string backupPath,
        string password,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var scratch = Path.Combine(_dataDirectory, ".restore-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var result = await ValidateArchiveAsync(
                Path.GetFullPath(backupPath), password, scratch, cancellationToken).ConfigureAwait(false);
            var stagingPath = Path.Combine(
                _dataDirectory, ".pending-restore-" + Guid.NewGuid().ToString("N") + ".sqlite");
            File.Move(result.DatabasePath, stagingPath, overwrite: false);
            try
            {
                await PendingRestoreCoordinator.ScheduleAsync(
                    _dataDirectory, _databasePath, stagingPath, result.Manifest.DatabaseSha256,
                    result.DatabaseVersion, now, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(stagingPath);
                throw;
            }
            return new(true,
                "备份已隔离校验并排队；完全退出并重新打开 Jarvis 后恢复。供应商凭据不会从备份恢复。",
                Path.GetFullPath(backupPath), result.Manifest.Kind, result.Manifest.CreatedAt,
                result.DatabaseVersion, true, RestoreScheduled: true);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private async Task CreateConsistentSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var destination = new SqliteConnection(destinationConnectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static void CreateArchive(
        string snapshotPath,
        string archivePath,
        string password,
        BackupManifest manifest)
    {
        using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipOutputStream(file) { IsStreamOwner = false, Password = password };
        zip.SetLevel(6);
        WriteEncryptedEntry(zip, ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest));
        using var database = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        WriteEncryptedEntry(zip, DatabaseEntryName, database);
        zip.Finish();
        file.Flush(flushToDisk: true);
    }

    private static async Task<ValidatedBackup> ValidateArchiveAsync(
        string archivePath,
        string password,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipFile(file) { IsStreamOwner = false, Password = password };
        var entries = GetStrictEntries(zip) ??
                      throw new InvalidDataException("备份必须且只能包含两个 WinZip AES-256 条目。");
        BackupManifest manifest;
        using (var manifestStream = zip.GetInputStream(entries.Manifest))
        {
            var bytes = await ReadWithLimitAsync(
                manifestStream, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<BackupManifest>(bytes) ??
                       throw new InvalidDataException("备份 manifest 无效。");
        }
        if (manifest.Format != FormatName || manifest.Version != FormatVersion || !Enum.IsDefined(manifest.Kind))
            throw new InvalidDataException("备份格式或版本不兼容。");
        var extracted = Path.Combine(scratchDirectory, "validated.sqlite");
        TryDelete(extracted);
        using (var input = zip.GetInputStream(entries.Database))
        await using (var output = new FileStream(extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await CopyWithLimitAsync(input, output, MaximumDatabaseBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        await using var databaseStream = File.OpenRead(extracted);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(databaseStream, cancellationToken)
            .ConfigureAwait(false));
        if (!string.Equals(digest, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份数据库摘要校验失败。");
        var databaseVersion = await ValidateDatabaseAsync(extracted, cancellationToken).ConfigureAwait(false);
        if (databaseVersion != manifest.DatabaseVersion)
            throw new InvalidDataException("备份 manifest 与数据库版本不一致。");
        return new(manifest, extracted, databaseVersion);
    }

    internal static async Task<int> ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string,
                "ok", StringComparison.Ordinal))
            throw new InvalidDataException("备份数据库完整性校验失败。");
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var value = Convert.ToInt32(
            await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (value is < 1 or > CurrentDatabaseVersion)
            throw new InvalidDataException($"备份数据库版本 {value} 不受当前程序支持。");
        return value;
    }

    private async Task<StoredBackupSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT directory_path,last_success_at_utc,last_backup_path,last_validated_at_utc,
                   last_baidu_client_seen_at_utc,client_waiting_since_at_utc,
                   last_baidu_warning_at_utc,last_auto_attempt_at_utc,last_error,
                   daily_retention,monthly_retention,upgrade_retention
              FROM backup_settings WHERE singleton=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new();
        return new(
            NullableString(reader, 0), NullableTimestamp(reader, 1), NullableString(reader, 2),
            NullableTimestamp(reader, 3), NullableTimestamp(reader, 4), NullableTimestamp(reader, 5),
            NullableTimestamp(reader, 6), NullableTimestamp(reader, 7), NullableString(reader, 8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11));
    }

    private async Task SaveSuccessAsync(string path, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_settings SET last_success_at_utc=$now,last_backup_path=$path,
                   last_validated_at_utc=$now,client_waiting_since_at_utc=COALESCE(client_waiting_since_at_utc,$now),
                   last_error=NULL WHERE singleton=1;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveErrorAsync(string message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_settings SET last_error=$error WHERE singleton=1;";
        command.Parameters.AddWithValue("$error", message.Length <= 500 ? message : message[..500]);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateBaiduSeenAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_settings SET last_baidu_client_seen_at_utc=$now,
                   client_waiting_since_at_utc=NULL,last_baidu_warning_at_utc=NULL WHERE singleton=1;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateBaiduWarningAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_settings SET last_baidu_warning_at_utc=$now WHERE singleton=1;";
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAutoAttemptAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_settings SET last_auto_attempt_at_utc=$now WHERE singleton=1;";
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<IReadOnlyList<string>> CleanupRetentionAsync(
        string directory,
        StoredBackupSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        DeleteExcess(directory, "daily", settings.DailyRetention, failures);
        DeleteExcess(directory, "monthly", settings.MonthlyRetention, failures);
        DeleteExcess(directory, "upgrade", settings.UpgradeRetention, failures);
        return Task.FromResult<IReadOnlyList<string>>(failures);
    }

    private static void DeleteExcess(
        string directory,
        string token,
        int keep,
        ICollection<string> failures)
    {
        foreach (var path in Directory.EnumerateFiles(
                     directory, $"jarvis-{token}-*.jarvis-backup", SearchOption.TopDirectoryOnly)
                 .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                 .Skip(keep))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(path);
            }
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static StrictEntries? GetStrictEntries(ZipFile zip)
    {
        var entries = zip.Cast<ZipEntry>().ToArray();
        if (entries.Length != 2 || entries.Any(entry => !entry.IsFile || !entry.IsCrypted || entry.AESKeySize != 256))
            return null;
        var manifest = entries.Where(entry => entry.Name == ManifestEntryName).ToArray();
        var database = entries.Where(entry => entry.Name == DatabaseEntryName).ToArray();
        return manifest.Length == 1 && database.Length == 1 ? new(manifest[0], database[0]) : null;
    }

    private static void WriteEncryptedEntry(ZipOutputStream zip, string name, ReadOnlySpan<byte> content)
    {
        zip.PutNextEntry(NewEntry(name));
        zip.Write(content);
        zip.CloseEntry();
    }

    private static void WriteEncryptedEntry(ZipOutputStream zip, string name, Stream content)
    {
        zip.PutNextEntry(NewEntry(name));
        content.CopyTo(zip);
        zip.CloseEntry();
    }

    private static ZipEntry NewEntry(string name) => new(name)
    {
        AESKeySize = 256,
        CompressionMethod = CompressionMethod.Deflated,
        DateTime = DateTime.Now
    };

    private static async Task<byte[]> ReadWithLimitAsync(
        Stream input,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await CopyWithLimitAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("备份条目超过当前程序的安全上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 12) throw new ArgumentException("备份密码至少需要 12 个字符。");
    }

    private static string KindToken(BackupKind kind) => kind switch
    {
        BackupKind.Daily => "daily",
        BackupKind.Monthly => "monthly",
        BackupKind.UpgradeOrMigration => "upgrade",
        _ => "manual"
    };

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(
            reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed record BackupManifest(
        string Format,
        int Version,
        DateTimeOffset CreatedAt,
        BackupKind Kind,
        int DatabaseVersion,
        string ApplicationVersion,
        string DatabaseSha256);

    private sealed record StrictEntries(ZipEntry Manifest, ZipEntry Database);
    private sealed record ValidatedBackup(BackupManifest Manifest, string DatabasePath, int DatabaseVersion);
    private sealed record StoredBackupSettings(
        string? DirectoryPath = null,
        DateTimeOffset? LastSuccessAt = null,
        string? LastBackupPath = null,
        DateTimeOffset? LastValidatedAt = null,
        DateTimeOffset? LastBaiduClientSeenAt = null,
        DateTimeOffset? ClientWaitingSinceAt = null,
        DateTimeOffset? LastBaiduWarningAt = null,
        DateTimeOffset? LastAutoAttemptAt = null,
        string? LastError = null,
        int DailyRetention = 30,
        int MonthlyRetention = 12,
        int UpgradeRetention = 3);
}

internal static class PendingRestoreCoordinator
{
    private const string MarkerFileName = "pending-restore.json";

    public static async Task ScheduleAsync(
        string dataDirectory,
        string targetDatabasePath,
        string stagingPath,
        string sha256,
        int databaseVersion,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var markerPath = Path.Combine(dataDirectory, MarkerFileName);
        if (File.Exists(markerPath))
        {
            var old = await ReadMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false);
            if (old is not null && IsInside(dataDirectory, old.StagingPath)) TryDelete(old.StagingPath);
        }
        var marker = new PendingRestoreMarker(
            Path.GetFullPath(targetDatabasePath), Path.GetFullPath(stagingPath),
            sha256, databaseVersion, scheduledAt);
        var temporary = markerPath + ".tmp";
        await File.WriteAllBytesAsync(
            temporary, JsonSerializer.SerializeToUtf8Bytes(marker), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, markerPath, overwrite: true);
    }

    public static async Task<bool> ApplyIfPendingAsync(
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var fullDirectory = Path.GetFullPath(dataDirectory);
        var markerPath = Path.Combine(fullDirectory, MarkerFileName);
        if (!File.Exists(markerPath)) return false;
        var marker = await ReadMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false) ??
                     throw new InvalidDataException("待恢复标记无效；正式数据未变更。");
        if (!IsInside(fullDirectory, marker.StagingPath) || !IsInside(fullDirectory, marker.TargetDatabasePath) ||
            !File.Exists(marker.StagingPath))
            throw new InvalidDataException("待恢复数据库不在 Jarvis 专用目录内或已丢失。");
        var version = await BackupService.ValidateDatabaseAsync(marker.StagingPath, cancellationToken)
            .ConfigureAwait(false);
        if (version != marker.DatabaseVersion)
            throw new InvalidDataException("待恢复数据库版本已改变。");
        await using (var stream = File.OpenRead(marker.StagingPath))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            if (!string.Equals(digest, marker.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("待恢复数据库摘要已改变。");
        }

        var databasePath = marker.TargetDatabasePath;
        var rollbackPath = Path.Combine(fullDirectory, "restore-rollback.db");
        var oldPath = Path.Combine(fullDirectory, ".restore-old-" + Guid.NewGuid().ToString("N") + ".db");
        if (File.Exists(databasePath))
            await SnapshotDatabaseAsync(databasePath, rollbackPath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(databasePath)) File.Move(databasePath, oldPath, overwrite: false);
            File.Move(marker.StagingPath, databasePath, overwrite: false);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
            TryDelete(oldPath);
            TryDelete(markerPath);
            return true;
        }
        catch
        {
            if (!File.Exists(databasePath) && File.Exists(oldPath)) File.Move(oldPath, databasePath);
            throw;
        }
    }

    private static async Task SnapshotDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var temporary = destinationPath + ".tmp";
        TryDelete(temporary);
        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = temporary,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        await using (var destination = new SqliteConnection(destinationBuilder.ToString()))
        {
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }
        await BackupService.ValidateDatabaseAsync(temporary, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destinationPath, overwrite: true);
    }

    private static async Task<PendingRestoreMarker?> ReadMarkerAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingRestoreMarker>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsInside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed record PendingRestoreMarker(
        string TargetDatabasePath,
        string StagingPath,
        string Sha256,
        int DatabaseVersion,
        DateTimeOffset ScheduledAt);
}

internal sealed class NullBackupPasswordStore : IBackupPasswordStore
{
    public ValueTask SaveAsync(string password, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class NullBaiduClientProbe : IBaiduClientProbe
{
    public bool IsRunning() => false;
}
