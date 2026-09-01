namespace Jarvis.Contracts;

public enum MaintenanceOperationKind
{
    ProductUpdate,
    SafeErase
}

public sealed record ProductUpdateCard(
    Guid CandidateId,
    string InstallerPath,
    string InstallerSha256,
    string VerifiedBackupPath,
    string DatabaseRollbackPath,
    string ConfirmationPhrase,
    DateTimeOffset ExpiresAt,
    bool ActiveSupervisionStopped);

public sealed record SafeEraseCard(
    Guid CandidateId,
    string FinalBackupPath,
    IReadOnlyList<string> LocalScopes,
    string ConfirmationPhrase,
    DateTimeOffset ExpiresAt,
    string BoundaryNotice);

public sealed record MaintenanceOperationView(
    MaintenanceOperationKind Kind,
    string Status,
    bool RequiresProductExit,
    string? ManualRecoveryPath = null);

public sealed record PrepareProductUpdateCommand(
    string InstallerPath,
    bool StopActiveSupervision) : CompanionCommand;

public sealed record ConfirmProductUpdateCommand(
    Guid CandidateId,
    string ConfirmationPhrase) : CompanionCommand;

public sealed record PrepareSafeEraseCommand(
    string FinalBackupDirectory,
    string Password,
    string ConfirmPassword) : CompanionCommand;

public sealed record ConfirmSafeEraseCommand(
    Guid CandidateId,
    string ConfirmationPhrase) : CompanionCommand;
