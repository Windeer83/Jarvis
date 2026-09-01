using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal interface IMaintenanceWorkerLauncher
{
    void Launch(MaintenanceWorkerRequest request);
}

internal sealed record MaintenanceWorkerRequest(
    MaintenanceOperationKind Operation,
    Guid RequestId,
    int ParentProcessId,
    string WorkingDirectory,
    string MaintenanceRoot,
    string ProgramDirectory,
    string DataDirectory,
    string DatabasePath,
    string? InstallerPath,
    string? InstallerSha256,
    string? DatabaseRollbackPath,
    string? ConfiguredBackupDirectory,
    string? FinalBackupPath,
    IReadOnlyList<string> CredentialTargets,
    string LoginStartupValueName,
    string StatusPath,
    DateTimeOffset CreatedAt,
    bool RestartAfterMaintenance = true,
    bool InstallMainProgramOnly = false);

internal sealed class PowerShellMaintenanceWorkerLauncher : IMaintenanceWorkerLauncher
{
    private readonly string _sourceScript;

    public PowerShellMaintenanceWorkerLauncher(string? sourceScript = null)
    {
        _sourceScript = sourceScript ?? Path.Combine(AppContext.BaseDirectory, "apply-t28-maintenance.ps1");
    }

    public void Launch(MaintenanceWorkerRequest request)
    {
        if (!File.Exists(_sourceScript))
            throw new FileNotFoundException("安装目录缺少 Jarvis 维护脚本，无法启动维护操作。", _sourceScript);
        Directory.CreateDirectory(request.WorkingDirectory);
        var script = Path.Combine(request.WorkingDirectory, "apply-maintenance.ps1");
        var requestPath = Path.Combine(request.WorkingDirectory, "request.json");
        File.Copy(_sourceScript, script, overwrite: true);
        File.WriteAllText(
            requestPath,
            JsonSerializer.Serialize(request, CoreProtocol.Json),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-RequestPath", requestPath
            }
        }) ?? throw new InvalidOperationException("无法启动 Jarvis 维护进程。");
    }
}

internal sealed class MaintenanceService
{
    private const string MaintenanceRootMarker = ".jarvis-maintenance-root";
    private static readonly string[] CredentialTargets =
    [
        "Jarvis/AI/siliconflow",
        "Jarvis/AI/deepseek",
        "Jarvis/Backup/password"
    ];
    private readonly string _databasePath;
    private readonly string _dataDirectory;
    private readonly string _programDirectory;
    private readonly string _maintenanceRoot;
    private readonly BackupService _backup;
    private readonly IMaintenanceWorkerLauncher _launcher;
    private ProductUpdateCandidate? _updateCandidate;
    private SafeEraseCandidate? _eraseCandidate;

    public MaintenanceService(
        string databasePath,
        BackupService backup,
        IMaintenanceWorkerLauncher launcher,
        string? maintenanceRoot = null,
        string? programDirectory = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _dataDirectory = Path.GetDirectoryName(_databasePath)!;
        _programDirectory = Path.GetFullPath(programDirectory ?? AppContext.BaseDirectory);
        _maintenanceRoot = Path.GetFullPath(maintenanceRoot ?? DefaultMaintenanceRoot(_dataDirectory));
        _backup = backup;
        _launcher = launcher;
    }

    public async Task<ProductUpdateCard> PrepareUpdateAsync(
        string installerPath,
        bool activeSupervisionStopped,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var installer = Path.GetFullPath(installerPath);
        if (!File.Exists(installer) ||
            !string.Equals(Path.GetExtension(installer), ".msi", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择已经下载到本机的新版 Jarvis .msi 安装包。");
        var hash = await HashFileAsync(installer, cancellationToken).ConfigureAwait(false);
        var portable = await _backup.CreateAsync(
            BackupKind.UpgradeOrMigration, null, now, cancellationToken).ConfigureAwait(false);
        if (!portable.IntegrityVerified || string.IsNullOrWhiteSpace(portable.BackupPath))
            throw new InvalidOperationException("升级前密码保护备份未通过校验，更新已中止。");
        var candidateId = Guid.NewGuid();
        EnsureMaintenanceRoot();
        var work = Path.Combine(_maintenanceRoot, "update-" + candidateId.ToString("N"));
        Directory.CreateDirectory(work);
        var rollback = Path.Combine(work, "database-rollback.sqlite3");
        await _backup.CreateRollbackSnapshotAsync(rollback, cancellationToken).ConfigureAwait(false);
        var phrase = "确认更新 Jarvis";
        var card = new ProductUpdateCard(
            candidateId, installer, hash, portable.BackupPath, rollback,
            phrase, now.AddMinutes(10), activeSupervisionStopped);
        _updateCandidate = new(card, work);
        _eraseCandidate = null;
        return card;
    }

    public async Task<MaintenanceOperationView> ConfirmUpdateAsync(
        Guid candidateId,
        string confirmationPhrase,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = _updateCandidate;
        ValidateCandidate(candidate?.Card.CandidateId, candidateId, candidate?.Card.ExpiresAt, now,
            candidate?.Card.ConfirmationPhrase, confirmationPhrase, "更新");
        var card = candidate!.Card;
        if (!File.Exists(card.InstallerPath) ||
            !string.Equals(await HashFileAsync(card.InstallerPath, cancellationToken).ConfigureAwait(false),
                card.InstallerSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("安装包在预览后发生变化；更新已中止，请重新选择并预览。");
        await _backup.CreateRollbackSnapshotAsync(
            card.DatabaseRollbackPath, cancellationToken).ConfigureAwait(false);
        var configuredBackup = await _backup.ReadConfiguredDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var request = BuildRequest(
            MaintenanceOperationKind.ProductUpdate, candidate.WorkingDirectory, now,
            card.InstallerPath, card.InstallerSha256, card.DatabaseRollbackPath,
            configuredBackup, null);
        _launcher.Launch(request);
        _updateCandidate = null;
        return new(
            MaintenanceOperationKind.ProductUpdate,
            "更新已排队；Jarvis 完全退出后才运行本机安装包。失败时会恢复程序与数据库并保留手动恢复位置。",
            RequiresProductExit: true,
            ManualRecoveryPath: candidate.WorkingDirectory);
    }

    public async Task<SafeEraseCard> PrepareSafeEraseAsync(
        string finalBackupDirectory,
        string password,
        string confirmPassword,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            throw new ArgumentException("两次输入的最终备份密码不一致。");
        var finalDirectory = Path.GetFullPath(finalBackupDirectory);
        var configuredBackup = await _backup.ReadConfiguredDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (IsSameOrChild(finalDirectory, _dataDirectory) ||
            IsSameOrChild(finalDirectory, _maintenanceRoot) ||
            configuredBackup is not null && IsSameOrChild(finalDirectory, configuredBackup))
            throw new SafeEraseScopeException("最终备份目录位于即将清除的范围内；请选择范围之外的位置。");
        var finalBackup = await _backup.CreateExternalAsync(
            finalDirectory, password, now, cancellationToken).ConfigureAwait(false);
        if (!finalBackup.IntegrityVerified || string.IsNullOrWhiteSpace(finalBackup.BackupPath))
            throw new InvalidOperationException("最终密码保护备份没有通过校验，安全清除已中止。");
        var candidateId = Guid.NewGuid();
        EnsureMaintenanceRoot();
        var work = Path.Combine(_maintenanceRoot, "erase-" + candidateId.ToString("N"));
        Directory.CreateDirectory(work);
        var scopes = new List<string> { _dataDirectory };
        if (!string.IsNullOrWhiteSpace(configuredBackup) &&
            !string.Equals(configuredBackup, _dataDirectory, StringComparison.OrdinalIgnoreCase))
            scopes.Add(Path.GetFullPath(configuredBackup));
        scopes.Add(_maintenanceRoot);
        scopes.Add("Windows 登录启动项：Jarvis Core");
        scopes.AddRange(CredentialTargets.Select(target => "Windows 凭据：" + target));
        var card = new SafeEraseCard(
            candidateId, finalBackup.BackupPath, scopes,
            "永久清除本机 Jarvis 数据", now.AddMinutes(10),
            "只清除上列本机范围；不会调用百度 API、删除百度云端文件，也不会查找 Jarvis 之外保存的密码。");
        _eraseCandidate = new(card, work, configuredBackup, finalDirectory, password);
        _updateCandidate = null;
        return card;
    }

    public async Task<MaintenanceOperationView> ConfirmSafeEraseAsync(
        Guid candidateId,
        string confirmationPhrase,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = _eraseCandidate;
        ValidateCandidate(candidate?.Card.CandidateId, candidateId, candidate?.Card.ExpiresAt, now,
            candidate?.Card.ConfirmationPhrase, confirmationPhrase, "安全清除");
        var finalBackup = await _backup.CreateExternalAsync(
            candidate!.FinalBackupDirectory, candidate.Password, now, cancellationToken).ConfigureAwait(false);
        if (!finalBackup.IntegrityVerified || string.IsNullOrWhiteSpace(finalBackup.BackupPath))
            throw new InvalidOperationException("最终密码保护备份在确认时未通过校验，安全清除已中止。");
        var request = BuildRequest(
            MaintenanceOperationKind.SafeErase, candidate.WorkingDirectory, now,
            null, null, null, candidate.ConfiguredBackupDirectory, finalBackup.BackupPath);
        _launcher.Launch(request);
        _eraseCandidate = null;
        return new MaintenanceOperationView(
            MaintenanceOperationKind.SafeErase,
            "安全清除已排队；Jarvis 完全退出后才删除已核对的本机范围。最终密码保护备份位于清除范围之外。",
            RequiresProductExit: true,
            ManualRecoveryPath: finalBackup.BackupPath);
    }

    public async Task<MaintenanceOperationView?> ReadLastOperationAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_dataDirectory, "last-maintenance-status.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) ||
                format.GetString() != "jarvis-maintenance-status" ||
                !root.TryGetProperty("operation", out var operationText) ||
                !Enum.TryParse<MaintenanceOperationKind>(operationText.GetString(), ignoreCase: true, out var operation))
                return new(MaintenanceOperationKind.ProductUpdate, "上次维护状态文件不可识别。", false);
            var status = root.TryGetProperty("status", out var statusText)
                ? statusText.GetString() ?? "unknown"
                : "unknown";
            var recovery = root.TryGetProperty("manualRecoveryDirectory", out var recoveryText)
                ? recoveryText.GetString()
                : null;
            return new(
                operation,
                status == "completed"
                    ? "上次手动更新已完成，并通过程序/数据库健康检查。"
                    : status == "rolled_back"
                        ? "上次更新失败，程序与数据库已自动回滚；如需人工核查请使用下列恢复位置。"
                        : $"上次维护状态：{status}",
                false,
                recovery);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new(MaintenanceOperationKind.ProductUpdate,
                $"上次维护状态暂时无法读取：{exception.GetType().Name}", false);
        }
    }

    private MaintenanceWorkerRequest BuildRequest(
        MaintenanceOperationKind operation,
        string work,
        DateTimeOffset now,
        string? installer,
        string? installerHash,
        string? rollback,
        string? configuredBackup,
        string? finalBackup) => new(
            operation, Guid.NewGuid(), Environment.ProcessId, work, _maintenanceRoot, _programDirectory,
            _dataDirectory, _databasePath, installer, installerHash, rollback,
            configuredBackup is null ? null : Path.GetFullPath(configuredBackup), finalBackup,
            CredentialTargets, "Jarvis Core",
            operation == MaintenanceOperationKind.ProductUpdate
                ? Path.Combine(_dataDirectory, "last-maintenance-status.json")
                : Path.Combine(work, "status.json"),
            now);

    private static void ValidateCandidate(
        Guid? actualId,
        Guid requestedId,
        DateTimeOffset? expiresAt,
        DateTimeOffset now,
        string? expectedPhrase,
        string suppliedPhrase,
        string operation)
    {
        if (actualId is null || actualId != requestedId || expiresAt is null || now >= expiresAt)
            throw new InvalidOperationException($"{operation}候选已过期，请重新预览。 ");
        if (!string.Equals(expectedPhrase, suppliedPhrase.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"请输入完整确认短语：{expectedPhrase}");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool IsSameOrChild(string candidate, string root)
    {
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultMaintenanceRoot(string dataDirectory)
    {
        var trimmed = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        return parent is not null && !string.IsNullOrWhiteSpace(name)
            ? Path.Combine(parent, name + "-Maintenance")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jarvis-Maintenance");
    }

    private void EnsureMaintenanceRoot()
    {
        Directory.CreateDirectory(_maintenanceRoot);
        var marker = Path.Combine(_maintenanceRoot, MaintenanceRootMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "Jarvis maintenance root");
    }

    private sealed record ProductUpdateCandidate(ProductUpdateCard Card, string WorkingDirectory);
    private sealed record SafeEraseCandidate(
        SafeEraseCard Card,
        string WorkingDirectory,
        string? ConfiguredBackupDirectory,
        string FinalBackupDirectory,
        string Password);
}

internal sealed class SafeEraseScopeException(string message) : InvalidOperationException(message);
