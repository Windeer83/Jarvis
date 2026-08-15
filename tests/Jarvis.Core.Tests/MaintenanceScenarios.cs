using Jarvis.Contracts;
using System.Text.Json;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class MaintenanceScenarios
{
    [Fact]
    public async Task New_supervision_after_update_preview_makes_confirmation_stale()
    {
        using var database = new TemporaryDatabase();
        var root = Path.GetDirectoryName(database.Path)!;
        var installer = Path.Combine(root, "Jarvis-new.msi");
        await File.WriteAllTextAsync(installer, "installer-v2");
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.FromHours(8)));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var launcher = new FakeMaintenanceWorkerLauncher();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore(), new FakeBackupPasswordStore(),
            new FakeBaiduClientProbe(), launcher);
        Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
            Path.Combine(root, "backups"), "update backup password", "update backup password",
            SavePassword: true))).Success);
        var preview = await companion.DispatchAsync(new PrepareProductUpdateCommand(
            installer, StopActiveSupervision: false));
        var card = Assert.IsType<ProductUpdateCard>(preview.ProductUpdate);

        var commitment = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer, clock.Now, null, 30, "new active work", null,
            [new(CommitmentTargetKind.Application, "notepad.exe")],
            SupervisionMode.Interactive, null));
        Assert.True((await supervision.ConfirmAsync(commitment.Value!.CandidateId)).Success);

        var confirmed = await companion.DispatchAsync(new ConfirmProductUpdateCommand(
            card.CandidateId, card.ConfirmationPhrase));
        Assert.False(confirmed.Success);
        Assert.Equal("update_active_supervision_changed", confirmed.ErrorCode);
        Assert.Null(launcher.Request);
    }

    [Fact]
    public async Task Active_supervision_blocks_update_until_explicit_stop_then_verified_backup_precedes_worker()
    {
        using var database = new TemporaryDatabase();
        var root = Path.GetDirectoryName(database.Path)!;
        var installer = Path.Combine(root, "Jarvis-new.msi");
        await File.WriteAllTextAsync(installer, "installer-v2");
        var backupDirectory = Path.Combine(root, "configured-backups");
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.FromHours(8)));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var prepared = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer, clock.Now, null, 60, "更新拦截", null,
            [new(CommitmentTargetKind.Application, "notepad.exe")],
            SupervisionMode.Interactive, null));
        Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
        var launcher = new FakeMaintenanceWorkerLauncher();
        var passwordStore = new FakeBackupPasswordStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(),
            new FakeAiProvider(), new FakeCredentialStore(), passwordStore,
            new FakeBaiduClientProbe(), launcher);
        const string password = "verified update backup password";
        Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
            backupDirectory, password, password, SavePassword: true))).Success);

        var blocked = await companion.DispatchAsync(new PrepareProductUpdateCommand(
            installer, StopActiveSupervision: false));
        Assert.False(blocked.Success);
        Assert.Equal("update_active_supervision", blocked.ErrorCode);
        Assert.Empty(Directory.Exists(backupDirectory)
            ? Directory.EnumerateFiles(backupDirectory, "jarvis-upgrade-*.jarvis-backup")
            : []);

        var ready = await companion.DispatchAsync(new PrepareProductUpdateCommand(
            installer, StopActiveSupervision: true));
        var card = Assert.IsType<ProductUpdateCard>(ready.ProductUpdate);
        Assert.True(File.Exists(card.VerifiedBackupPath));
        Assert.True(File.Exists(card.DatabaseRollbackPath));
        Assert.Equal(CommitmentPhase.AwaitingReview,
            Assert.Single((await supervision.GetSnapshotAsync()).Commitments).Phase);
        var wrong = await companion.DispatchAsync(new ConfirmProductUpdateCommand(
            card.CandidateId, "wrong"));
        Assert.False(wrong.Success);
        Assert.Null(launcher.Request);
        var confirmed = await companion.DispatchAsync(new ConfirmProductUpdateCommand(
            card.CandidateId, card.ConfirmationPhrase));
        Assert.True(confirmed.Success, confirmed.Message);
        Assert.True(confirmed.MaintenanceOperation!.RequiresProductExit);
        Assert.NotNull(launcher.Request);
        var serialized = JsonSerializer.Serialize(launcher.Request, CoreProtocol.Json);
        Assert.DoesNotContain(password, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("更新拦截", serialized, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(root, "last-maintenance-status.json"),
            JsonSerializer.Serialize(new
            {
                format = "jarvis-maintenance-status",
                version = 1,
                operation = "ProductUpdate",
                status = "rolled_back",
                manualRecoveryDirectory = launcher.Request!.WorkingDirectory
            }, CoreProtocol.Json));
        var recoveredStatus = (await companion.SnapshotAsync()).Maintenance;
        Assert.NotNull(recoveredStatus);
        Assert.Contains("已自动回滚", recoveredStatus.Status, StringComparison.Ordinal);
        Assert.Equal(launcher.Request.WorkingDirectory, recoveredStatus.ManualRecoveryPath);
    }

    [Fact]
    public async Task Safe_erase_requires_external_verified_backup_and_two_step_confirmation()
    {
        using var database = new TemporaryDatabase();
        var root = Path.GetDirectoryName(database.Path)!;
        var configuredBackups = Path.Combine(root, "configured-backups");
        var external = Path.Combine(Path.GetTempPath(), "Jarvis-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 16, 14, 0, 0, TimeSpan.FromHours(8)));
        var launcher = new FakeMaintenanceWorkerLauncher();
        try
        {
            await using var supervision = await SupervisionModule.OpenAsync(
                database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
            await using var companion = await CompanionModule.OpenAsync(
                database.Path, supervision, clock, new FakeWorktimeChannel(),
                new FakeAiProvider(), new FakeCredentialStore(), new FakeBackupPasswordStore(),
                new FakeBaiduClientProbe(), launcher);
            const string password = "final external backup password";
            Assert.True((await companion.DispatchAsync(new ConfigureBackupCommand(
                configuredBackups, password, password, SavePassword: false))).Success);

            var inside = await companion.DispatchAsync(new PrepareSafeEraseCommand(
                Path.Combine(root, "final"), password, password));
            Assert.False(inside.Success);
            Assert.Equal("safe_erase_backup_inside_scope", inside.ErrorCode);

            var preview = await companion.DispatchAsync(new PrepareSafeEraseCommand(
                external, password, password));
            var card = Assert.IsType<SafeEraseCard>(preview.SafeErase);
            Assert.True(File.Exists(card.FinalBackupPath));
            Assert.Contains(Path.GetFullPath(root), card.LocalScopes);
            var expectedMaintenanceRoot = Path.GetFullPath(root + "-Maintenance");
            Assert.Contains(expectedMaintenanceRoot, card.LocalScopes);
            Assert.DoesNotContain(card.FinalBackupPath, card.LocalScopes);
            var wrong = await companion.DispatchAsync(new ConfirmSafeEraseCommand(
                card.CandidateId, "wrong"));
            Assert.False(wrong.Success);
            Assert.Null(launcher.Request);

            clock.Now = clock.Now.AddMinutes(1);
            var confirmed = await companion.DispatchAsync(new ConfirmSafeEraseCommand(
                card.CandidateId, card.ConfirmationPhrase));
            Assert.True(confirmed.Success, confirmed.Message);
            Assert.True(confirmed.MaintenanceOperation!.RequiresProductExit);
            Assert.Equal(MaintenanceOperationKind.SafeErase, launcher.Request!.Operation);
            Assert.NotEqual(card.FinalBackupPath, launcher.Request.FinalBackupPath);
            Assert.True(File.Exists(launcher.Request.FinalBackupPath));
            Assert.Equal(expectedMaintenanceRoot, launcher.Request.MaintenanceRoot);
            Assert.DoesNotContain("Baidu", launcher.Request.CredentialTargets, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(password,
                JsonSerializer.Serialize(launcher.Request, CoreProtocol.Json), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }
}

internal sealed class FakeMaintenanceWorkerLauncher : IMaintenanceWorkerLauncher
{
    public MaintenanceWorkerRequest? Request { get; private set; }

    public void Launch(MaintenanceWorkerRequest request) => Request = request;
}
