namespace Jarvis.Contracts;

public enum DataDeletionScope
{
    DetailedTimelineOnly,
    TimelineAndDailySummaries,
    AllSupervisionRecords
}

public sealed record DataGovernanceStatusView(
    int DetailedTimelineRetentionDays,
    DateTimeOffset? LastRetentionAppliedAt,
    int DefaultRetentionDays = 90)
{
    public static DataGovernanceStatusView Default { get; } = new(90, null);
}

public sealed record DataTimelineEntryView(
    DateTimeOffset At,
    DateTimeOffset? EndAt,
    string Category,
    string Summary,
    Guid? CommitmentId = null,
    int? CommitmentVersion = null);

public sealed record DailyActivitySummaryView(
    DateOnly Date,
    double ObservedSeconds,
    double RelatedSeconds,
    double DistractingSeconds,
    double UnknownSeconds,
    double UnobservableSeconds,
    double IdleSeconds,
    int ReminderCount,
    int ResponseCount);

public sealed record DataCommitmentRecordView(
    Guid CommitmentId,
    int CurrentVersion,
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? InputGoal,
    string? OutcomeGoal,
    bool IsSkipped,
    string? ReviewText,
    CompletionAssessment? Assessment);

public sealed record DataRangeView(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<DataTimelineEntryView> Timeline,
    IReadOnlyList<DailyActivitySummaryView> DailySummaries,
    IReadOnlyList<DataCommitmentRecordView> Commitments,
    bool IsTruncated);

public sealed record DataDeletionCard(
    Guid CandidateId,
    DateOnly StartDate,
    DateOnly EndDate,
    DataDeletionScope Scope,
    int EstimatedRecordCount,
    string ConfirmationPhrase,
    DateTimeOffset ExpiresAt,
    string ScopeDescription);

public sealed record SetDetailedTimelineRetentionCommand(int Days) : CompanionCommand;

public sealed record QueryDataRangeCommand(DateOnly StartDate, DateOnly EndDate) : CompanionCommand;

public sealed record ExportDataRangeCommand(
    DateOnly StartDate,
    DateOnly EndDate,
    string DestinationPath,
    string Password) : CompanionCommand;

public sealed record PreparePermanentDataDeletionCommand(
    DateOnly StartDate,
    DateOnly EndDate,
    DataDeletionScope Scope) : CompanionCommand;

public sealed record ConfirmPermanentDataDeletionCommand(
    Guid CandidateId,
    string ConfirmationPhrase) : CompanionCommand;
