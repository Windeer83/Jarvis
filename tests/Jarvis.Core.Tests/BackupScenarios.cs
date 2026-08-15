using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class BackupScenarios
{
    [Fact]
    public async Task Manual_backup_is_verified_and_test_restore_isolated_while_bad_archives_fail_closed()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.FromHours(8)));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        await CreateCommitmentAsync(supervision, clock);
        var backupDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "BaiduSync", "Jarvis");
        var passwordStore = new FakeBackupPasswordStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore(), passwordStore, new FakeBaiduClientProbe());

        const string password = "correct horse battery staple";
        var configured = await companion.DispatchAsync(new ConfigureBackupCommand(
            backupDirectory, password, password, SavePassword: false));
        Assert.True(configured.Success, configured.Message);
        Assert.Null(await passwordStore.ReadAsync(CancellationToken.None));
        passwordStore.ThrowOnRead = true;
        var created = await companion.DispatchAsync(new CreateBackupCommand(BackupKind.Manual, password));

        Assert.True(created.Success, created.Message);
        Assert.Contains("正式操作已成功", created.Message, StringComparison.Ordinal);
        var operation = Assert.IsType<BackupOperationView>(created.BackupOperation);
        Assert.True(operation.IntegrityVerified);
        Assert.Equal(BackupKind.Manual, operation.Kind);
        Assert.True(File.Exists(operation.BackupPath));
        passwordStore.ThrowOnRead = false;
        var tested = await companion.DispatchAsync(new TestBackupRestoreCommand(operation.BackupPath!, password));
        Assert.True(tested.Success, tested.Message);
        Assert.True(tested.BackupOperation!.IntegrityVerified);

        var wrongPassword = await companion.DispatchAsync(new TestBackupRestoreCommand(
            operation.BackupPath!, "wrong password value"));
        Assert.False(wrongPassword.Success);
        var corruptedPath = Path.Combine(backupDirectory, "corrupted.jarvis-backup");
        var bytes = await File.ReadAllBytesAsync(operation.BackupPath!);
        bytes[^20] ^= 0x55;
        await File.WriteAllBytesAsync(corruptedPath, bytes);
        var corrupted = await companion.DispatchAsync(new TestBackupRestoreCommand(corruptedPath, password));
        Assert.False(corrupted.Success);

        var snapshot = await supervision.GetSnapshotAsync();
        Assert.Single(snapshot.Commitments);
        Assert.DoesNotContain(Directory.EnumerateFiles(backupDirectory), path =>
            path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Saved_password_drives_daily_and_monthly_backup_retention_and_baidu_attention_without_cloud_claims()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.FromHours(8)));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        await CreateCommitmentAsync(supervision, clock);
        var backupDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "BaiduSync", "Jarvis");
        Directory.CreateDirectory(backupDirectory);
        SeedExcessFiles(backupDirectory, "daily", 31);
        SeedExcessFiles(backupDirectory, "monthly", 13);
        SeedExcessFiles(backupDirectory, "upgrade", 4);
        var passwordStore = new FakeBackupPasswordStore();
        var baidu = new FakeBaiduClientProbe();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore(), passwordStore, baidu);
        const string password = "saved correct horse battery staple";
        Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
            backupDirectory, password, password, SavePassword: true))).Success);

        await companion.AdvanceAsync();
        Assert.Equal(password, await passwordStore.ReadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(backupDirectory, "jarvis-daily-20260815-*.jarvis-backup"));
        Assert.Single(Directory.EnumerateFiles(backupDirectory, "jarvis-monthly-20260815-*.jarvis-backup"));
        Assert.Equal(30, Directory.EnumerateFiles(backupDirectory, "jarvis-daily-*.jarvis-backup").Count());
        Assert.Equal(12, Directory.EnumerateFiles(backupDirectory, "jarvis-monthly-*.jarvis-backup").Count());
        Assert.Equal(3, Directory.EnumerateFiles(backupDirectory, "jarvis-upgrade-*.jarvis-backup").Count());
        var initial = (await companion.SnapshotAsync()).BackupProjection;
        Assert.False(initial.AttentionRequired);
        Assert.Contains("云端状态未知", initial.CloudStatus, StringComparison.Ordinal);

        clock.Now = clock.Now.AddHours(25);
        await companion.AdvanceAsync();
        var warning = (await companion.SnapshotAsync()).BackupProjection;
        Assert.True(warning.AttentionRequired);
        Assert.Contains("24 小时未运行", warning.CloudStatus, StringComparison.Ordinal);

        baidu.Running = true;
        await companion.AdvanceAsync();
        var running = (await companion.SnapshotAsync()).BackupProjection;
        Assert.False(running.AttentionRequired);
        Assert.True(running.BaiduClientRunning);
        Assert.Contains("无法确认云端上传", running.CloudStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verified_restore_is_applied_only_on_next_start_and_preserves_a_local_rollback_snapshot()
    {
        using var database = new TemporaryDatabase();
        var dataDirectory = Path.GetDirectoryName(database.Path)!;
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.FromHours(8)));
        string backupPath;
        await using (var supervision = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            await CreateCommitmentAsync(supervision, clock);
            var backupDirectory = Path.Combine(dataDirectory, "BaiduSync", "Jarvis");
            await using (var companion = await CompanionModule.OpenAsync(
                             database.Path, supervision, clock, new FakeWorktimeChannel(),
                             new FakeAiProvider(), new FakeCredentialStore(),
                             new FakeBackupPasswordStore(), new FakeBaiduClientProbe()))
            {
                const string password = "correct horse battery staple";
                Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
                    backupDirectory, password, password, SavePassword: false))).Success);
                var created = await companion.DispatchAsync(new CreateBackupCommand(BackupKind.Manual, password));
                backupPath = created.BackupOperation!.BackupPath!;

                clock.Now = clock.Now.AddHours(2);
                await CreateCommitmentAsync(supervision, clock);
                Assert.Equal(2, (await supervision.GetSnapshotAsync()).Commitments.Count);
                var wrong = await companion.DispatchAsync(new ScheduleBackupRestoreCommand(
                    backupPath, "wrong password value"));
                Assert.False(wrong.Success);
                Assert.False(File.Exists(Path.Combine(dataDirectory, "pending-restore.json")));
                var scheduled = await companion.DispatchAsync(new ScheduleBackupRestoreCommand(
                    backupPath, password));
                Assert.True(scheduled.Success, scheduled.Message);
                Assert.True(scheduled.BackupOperation!.RestoreScheduled);
                Assert.Equal(2, (await supervision.GetSnapshotAsync()).Commitments.Count);
            }
        }

        Assert.True(await PendingRestoreCoordinator.ApplyIfPendingAsync(dataDirectory));
        Assert.True(File.Exists(Path.Combine(dataDirectory, "restore-rollback.db")));
        Assert.False(File.Exists(Path.Combine(dataDirectory, "pending-restore.json")));
        await using var restored = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        Assert.Single((await restored.GetSnapshotAsync()).Commitments);
    }

    [Fact]
    public async Task Automatic_failure_is_reported_and_throttled_before_a_later_retry()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.FromHours(8)));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var backupDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "BaiduSync", "Jarvis");
        var passwordStore = new FakeBackupPasswordStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore(), passwordStore, new FakeBaiduClientProbe());
        const string password = "saved correct horse battery staple";
        Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
            backupDirectory, password, password, SavePassword: true))).Success);
        Directory.Delete(backupDirectory, recursive: true);
        await File.WriteAllTextAsync(backupDirectory, "not a directory");

        await companion.AdvanceAsync();
        var failed = (await companion.SnapshotAsync()).BackupProjection;
        Assert.Contains("自动备份失败", failed.LastError, StringComparison.Ordinal);

        File.Delete(backupDirectory);
        Directory.CreateDirectory(backupDirectory);
        clock.Now = clock.Now.AddMinutes(30);
        await companion.AdvanceAsync();
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.jarvis-backup"));

        clock.Now = clock.Now.AddMinutes(31);
        await companion.AdvanceAsync();
        Assert.NotEmpty(Directory.EnumerateFiles(backupDirectory, "jarvis-daily-*.jarvis-backup"));
    }

    private static async Task CreateCommitmentAsync(SupervisionModule supervision, FakeClock clock)
    {
        var prepared = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Offline, clock.Now.AddHours(1), null, 30,
            "备份内的承诺", null, null, null, null));
        Assert.True(prepared.Success, prepared.Message);
        Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
    }

    private static void SeedExcessFiles(string directory, string kind, int count)
    {
        for (var index = 0; index < count; index++)
            File.WriteAllText(
                Path.Combine(directory, $"jarvis-{kind}-202501{index:00}-000000-000-{index:D32}.jarvis-backup"),
                "retention fixture");
    }
}

internal sealed class FakeBackupPasswordStore : IBackupPasswordStore
{
    private string? _password;
    public bool ThrowOnRead { get; set; }

    public ValueTask SaveAsync(string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _password = password;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnRead) throw new InvalidOperationException("injected credential read failure");
        return ValueTask.FromResult(_password);
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _password = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeBaiduClientProbe(bool running = false) : IBaiduClientProbe
{
    public bool Running { get; set; } = running;
    public bool IsRunning() => Running;
}
