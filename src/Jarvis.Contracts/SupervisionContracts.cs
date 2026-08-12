using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace Jarvis.Contracts;

public enum CommitmentKind
{
    Computer,
    Offline
}

public enum CommitmentTargetKind
{
    Application,
    Website
}

public enum SupervisionMode
{
    Interactive,
    Passive
}

public enum CommitmentPhase
{
    Scheduled,
    PreparationBuffer,
    Supervising,
    ActiveUnsupervised,
    AwaitingReview,
    Skipped
}

public enum ActivityAvailability
{
    Available,
    Unobservable
}

public enum ActivityClassification
{
    Related,
    Distracting,
    Unknown
}

public enum ActivityRuleScope
{
    Global,
    Template,
    Commitment
}

public enum DeviationReason
{
    DistractingActivity,
    InteractiveIdle,
    UnknownActivity
}

public enum SupervisionPromptKind
{
    UnknownClassification,
    ConfirmRest
}

public enum TimedRestSource
{
    IdleConfirmation,
    Proactive
}

public enum ReminderKind
{
    CommitmentStarted,
    LocalDeviation,
    UnknownClassificationQuestion,
    RestQuestion,
    RestEnded
}

public enum RecurrenceKind
{
    Daily,
    Weekly,
    SelectedDates
}

public enum RecurrenceOccurrenceStatus
{
    Active,
    Skipped
}

public enum RecurrenceChangeKind
{
    Skip,
    Adjust
}

public enum RecurrenceChangeScope
{
    ThisOccurrence,
    ThisAndFuture,
    EntirePlan
}

public sealed record ReminderSettings(
    bool StartReminderEnabled,
    int LocalDeviationMinutes,
    int FirstMobileDeviationMinutes,
    int MobileRepeatMinutes,
    int MaxMobileReminders,
    bool SoundEnabled = true,
    bool QuietPresentation = false);

public sealed record RestSettings(
    int IdlePromptMinutes,
    int DefaultTotalRestMinutes);

public sealed record CommitmentTarget(CommitmentTargetKind Kind, string Value);

public sealed record ActivityRule(
    CommitmentTarget Target,
    ActivityClassification Classification);

public sealed record ActivityRuleBinding(
    ActivityRuleScope Scope,
    Guid? ScopeId,
    ActivityRule Rule);

public sealed record CommitmentDraft(
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    int? DurationMinutes,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget>? RelatedAppsOrSites,
    SupervisionMode? SupervisionMode,
    ReminderSettings? ReminderSettings,
    IReadOnlyList<ActivityRule>? ActivityRules = null,
    RestSettings? RestSettings = null,
    Guid? TemplateId = null);

public sealed record CommitmentCard(
    Guid CandidateId,
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget> RelatedAppsOrSites,
    SupervisionMode SupervisionMode,
    ReminderSettings ReminderSettings,
    string ConfirmationNotice,
    IReadOnlyList<ActivityRule> ActivityRules,
    RestSettings RestSettings,
    Guid? TemplateId = null);

public sealed record ActivityObservation(
    ActivityAvailability Availability,
    bool IsUserActive,
    string? ForegroundProcess,
    DateTimeOffset ObservedAt,
    string? ForegroundWebsiteDomain = null,
    TimeSpan? IdleDuration = null);

public sealed record ReminderNotice(
    Guid CommitmentId,
    string Message,
    DateTimeOffset CreatedAt,
    ReminderKind Kind = ReminderKind.CommitmentStarted,
    Guid NoticeId = default,
    DateTimeOffset? BubbleExpiresAt = null,
    bool PlaySound = false,
    bool PersistentMarker = false,
    int CommitmentVersion = 1);

public sealed record ActivityCorrectionView(
    CommitmentTarget Target,
    ActivityClassification OriginalClassification,
    ActivityClassification CorrectedClassification,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset CorrectedAt,
    ActivityRuleScope Scope,
    string? Note,
    int CommitmentVersion = 1,
    long? ActivitySegmentId = null);

public sealed record TimedRestView(
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    TimedRestSource Source);

public sealed record ActiveSupervisionView(
    Guid CommitmentId,
    ActivityClassification? Classification,
    bool IsIdle,
    DeviationReason? DeviationReason,
    DateTimeOffset? DeviationStartedAt,
    TimeSpan CountedDeviation,
    DateTimeOffset? RelatedStableSince,
    bool ReminderMarkerActive,
    DateTimeOffset? ReturnIntentAt,
    SupervisionPromptKind? PendingPrompt,
    TimedRestView? ActiveRest,
    DateTimeOffset? LastUnobservableStartedAt,
    DateTimeOffset? LastUnobservableEndedAt,
    IReadOnlyList<ActivityCorrectionView> RecentCorrections,
    int CommitmentVersion = 1,
    CommitmentTarget? ActionableTarget = null,
    DateTimeOffset? ActivityStateStartedAt = null);

public sealed record CommitmentView(
    Guid Id,
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget> RelatedAppsOrSites,
    SupervisionMode SupervisionMode,
    ReminderSettings ReminderSettings,
    CommitmentPhase Phase,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset? OfflineManuallyConfirmedAt,
    IReadOnlyList<ActivityRule> ActivityRules,
    RestSettings RestSettings,
    Guid? TemplateId = null,
    int Version = 1);

public sealed record CommitmentRevisionDraft(
    Guid CommitmentId,
    int ExpectedVersion,
    CommitmentDraft Proposed,
    string Reason);

public sealed record CommitmentRevisionVersionView(
    Guid CommitmentId,
    int Version,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset ConfirmedAt,
    string Reason,
    CommitmentCard Snapshot);

public sealed record CommitmentRevisionCard(
    Guid CandidateId,
    Guid CommitmentId,
    int FromVersion,
    int ToVersion,
    DateTimeOffset EffectiveFrom,
    CommitmentCard Before,
    CommitmentCard After,
    string Reason,
    string ConfirmationNotice);

public sealed record ActivitySegmentView(
    long Id,
    Guid CommitmentId,
    int CommitmentVersion,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    ActivityAvailability Availability,
    CommitmentTarget? Target,
    ActivityClassification? OriginalClassification,
    ActivityClassification? EffectiveClassification,
    bool IsIdle,
    DeviationReason? DeviationReason,
    DateTimeOffset? CorrectedAt = null,
    string? CorrectionNote = null);

public sealed record SupervisionResponseView(
    long Id,
    Guid CommitmentId,
    int CommitmentVersion,
    string Kind,
    DateTimeOffset RecordedAt,
    string? Note = null);

public sealed record CommitmentHistoryView(
    Guid CommitmentId,
    int CurrentVersion,
    IReadOnlyList<CommitmentRevisionVersionView> Versions,
    IReadOnlyList<ActivitySegmentView> ActivitySegments,
    IReadOnlyList<ReminderNotice> Reminders,
    IReadOnlyList<ActivityCorrectionView> Corrections,
    IReadOnlyList<SupervisionResponseView> Responses);

public sealed record CommitmentTemplateDraft(
    string Name,
    CommitmentKind Kind,
    int DurationMinutes,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget>? RelatedAppsOrSites,
    SupervisionMode? SupervisionMode,
    ReminderSettings? ReminderSettings,
    IReadOnlyList<ActivityRule>? ActivityRules,
    RestSettings? RestSettings);

public sealed record CommitmentTemplateView(
    Guid Id,
    string Name,
    CommitmentKind Kind,
    int DurationMinutes,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget> RelatedAppsOrSites,
    SupervisionMode SupervisionMode,
    ReminderSettings ReminderSettings,
    IReadOnlyList<ActivityRule> ActivityRules,
    RestSettings RestSettings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsArchived);

public sealed record TemplateCommitmentDraft(
    Guid TemplateId,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt = null,
    int? DurationMinutes = null,
    string? InputGoal = null,
    string? OutcomeGoal = null,
    IReadOnlyList<CommitmentTarget>? RelatedAppsOrSites = null,
    SupervisionMode? SupervisionMode = null,
    ReminderSettings? ReminderSettings = null,
    IReadOnlyList<ActivityRule>? ActivityRules = null,
    RestSettings? RestSettings = null);

public sealed record RecurrencePattern(
    RecurrenceKind Kind,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    IReadOnlyList<DayOfWeek>? Weekdays = null,
    IReadOnlyList<DateOnly>? SelectedDates = null);

public sealed record RecurrenceDraft(
    CommitmentDraft Commitment,
    RecurrencePattern Pattern);

public sealed record RecurrenceOccurrenceView(
    Guid CommitmentId,
    DateOnly Date,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    RecurrenceOccurrenceStatus Status);

public sealed record RecurrenceCard(
    Guid CandidateId,
    RecurrencePattern Pattern,
    IReadOnlyList<CommitmentCard> Occurrences,
    string ConfirmationNotice);

public sealed record RecurrencePlanView(
    Guid Id,
    Guid? TemplateId,
    RecurrencePattern Pattern,
    IReadOnlyList<RecurrenceOccurrenceView> Occurrences,
    DateTimeOffset ConfirmedAt);

public sealed record RecurrenceChangeRequest(
    Guid PlanId,
    Guid AnchorCommitmentId,
    RecurrenceChangeKind Kind,
    RecurrenceChangeScope Scope,
    DateTimeOffset? NewStartAt = null,
    int? NewDurationMinutes = null,
    string? Reason = null);

public sealed record RecurrenceChangeOccurrencePreview(
    Guid CommitmentId,
    DateOnly Date,
    DateTimeOffset BeforeStartAt,
    DateTimeOffset BeforeEndAt,
    RecurrenceOccurrenceStatus BeforeStatus,
    DateTimeOffset AfterStartAt,
    DateTimeOffset AfterEndAt,
    RecurrenceOccurrenceStatus AfterStatus,
    int BeforeVersion = 1,
    int AfterVersion = 1);

public sealed record RecurrenceChangeCard(
    Guid CandidateId,
    Guid PlanId,
    RecurrenceChangeKind Kind,
    RecurrenceChangeScope Scope,
    IReadOnlyList<RecurrenceChangeOccurrencePreview> AffectedOccurrences,
    string ConfirmationNotice,
    string? Reason = null);

[method: JsonConstructor]
public sealed record SupervisionSnapshot(
    DateTimeOffset Now,
    Guid? ActiveComputerCommitmentId,
    IReadOnlyList<CommitmentView> Commitments,
    ActivityObservation? LatestActivity,
    ReminderNotice? LatestReminder,
    ActiveSupervisionView? ActiveSupervision = null)
{
    public SupervisionSnapshot(
        DateTimeOffset Now,
        Guid? ActiveComputerCommitmentId,
        IReadOnlyList<CommitmentView> Commitments,
        ActivityObservation? LatestActivity,
        ReminderNotice? LatestReminder,
        ActiveSupervisionView? ActiveSupervision,
        IReadOnlyList<CommitmentTemplateView> Templates,
        IReadOnlyList<RecurrencePlanView> RecurrencePlans)
        : this(
            Now,
            ActiveComputerCommitmentId,
            Commitments,
            LatestActivity,
            LatestReminder,
            ActiveSupervision)
    {
        this.Templates = Templates;
        this.RecurrencePlans = RecurrencePlans;
    }

    public IReadOnlyList<CommitmentTemplateView> Templates { get; init; } = [];
    public IReadOnlyList<RecurrencePlanView> RecurrencePlans { get; init; } = [];
}

public sealed record CoreRequest(
    string Operation,
    CommitmentDraft? Draft = null,
    Guid? CandidateId = null,
    Guid? CommitmentId = null,
    CommitmentTemplateDraft? TemplateDraft = null,
    Guid? TemplateId = null,
    TemplateCommitmentDraft? TemplateCommitmentDraft = null,
    RecurrenceDraft? RecurrenceDraft = null,
    RecurrenceChangeRequest? RecurrenceChange = null,
    ActivityRuleBinding? ActivityRule = null,
    ActivityClassification? Classification = null,
    ActivityRuleScope? RuleScope = null,
    bool? IsResting = null,
    DateTimeOffset? RestEndAt = null,
    string? Note = null,
    CommitmentRevisionDraft? RevisionDraft = null,
    int? ExpectedVersion = null,
    CommitmentTarget? ActivityTarget = null,
    DateTimeOffset? ActivityStateStartedAt = null);

public sealed record CoreResponse(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    CommitmentCard? Card = null,
    SupervisionSnapshot? Snapshot = null,
    CommitmentTemplateView? Template = null,
    RecurrenceCard? RecurrenceCard = null,
    RecurrencePlanView? RecurrencePlan = null,
    RecurrenceChangeCard? RecurrenceChangeCard = null,
    CommitmentRevisionCard? CommitmentRevisionCard = null,
    CommitmentHistoryView? CommitmentHistory = null);

public static class CoreProtocol
{
    public static readonly JsonSerializerOptions Json = CreateOptions();

    public static string PipeName
    {
        get
        {
            var user = string.Concat(Environment.UserName.Select(character =>
                char.IsLetterOrDigit(character) ? character : '_'));
            return $"Jarvis.Core.{user}.{Process.GetCurrentProcess().SessionId}";
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class CoreOperations
{
    public const string Prepare = "prepare";
    public const string Confirm = "confirm";
    public const string ConfirmOfflineStarted = "confirmOfflineStarted";
    public const string CreateTemplate = "createTemplate";
    public const string UpdateTemplate = "updateTemplate";
    public const string ArchiveTemplate = "archiveTemplate";
    public const string PrepareFromTemplate = "prepareFromTemplate";
    public const string PrepareRecurrence = "prepareRecurrence";
    public const string ConfirmRecurrence = "confirmRecurrence";
    public const string PrepareRecurrenceChange = "prepareRecurrenceChange";
    public const string ConfirmRecurrenceChange = "confirmRecurrenceChange";
    public const string PrepareCommitmentRevision = "prepareCommitmentRevision";
    public const string ConfirmCommitmentRevision = "confirmCommitmentRevision";
    public const string GetCommitmentHistory = "getCommitmentHistory";
    public const string SaveActivityRule = "saveActivityRule";
    public const string ClassifyCurrentActivity = "classifyCurrentActivity";
    public const string RecordReturnIntent = "recordReturnIntent";
    public const string RespondToRestPrompt = "respondToRestPrompt";
    public const string StartTimedRest = "startTimedRest";
    public const string GetSnapshot = "getSnapshot";
}
