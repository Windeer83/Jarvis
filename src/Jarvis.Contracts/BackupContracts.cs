namespace Jarvis.Contracts;

public enum BackupKind
{
    Daily,
    Monthly,
    UpgradeOrMigration,
    Manual
}

public sealed record BackupStatusView(
    string? DirectoryPath,
    bool PasswordStored,
    DateTimeOffset? LastSuccessfulBackupAt,
    string? LastSuccessfulBackupPath,
    DateTimeOffset? LastValidatedAt,
    bool BaiduClientRunning,
    string CloudStatus,
    bool AttentionRequired,
    string? LastError,
    int DailyRetention = 30,
    int MonthlyRetention = 12,
    int UpgradeRetention = 3)
{
    public static BackupStatusView NotConfigured { get; } = new(
        null, false, null, null, null, false, "未配置本地同步目录；云端状态未知。", false, null);
}

public sealed record BackupOperationView(
    bool Success,
    string Message,
    string? BackupPath = null,
    BackupKind? Kind = null,
    DateTimeOffset? CreatedAt = null,
    int? DatabaseVersion = null,
    bool IntegrityVerified = false,
    bool RestoreScheduled = false);

public sealed record ConfigureBackupCommand(
    string DirectoryPath,
    string Password,
    string ConfirmPassword,
    bool SavePassword) : CompanionCommand;

public sealed record ForgetBackupPasswordCommand : CompanionCommand;

public sealed record CreateBackupCommand(
    BackupKind Kind = BackupKind.Manual,
    string? Password = null) : CompanionCommand;

public sealed record TestBackupRestoreCommand(
    string BackupPath,
    string Password) : CompanionCommand;

public sealed record ScheduleBackupRestoreCommand(
    string BackupPath,
    string Password) : CompanionCommand;
