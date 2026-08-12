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
    AwaitingReview
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

public sealed record ReminderSettings(
    bool StartReminderEnabled,
    int LocalDeviationMinutes,
    int FirstMobileDeviationMinutes,
    int MobileRepeatMinutes,
    int MaxMobileReminders);

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
    IReadOnlyList<ActivityRule>? ActivityRules = null,
    RestSettings? RestSettings = null,
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
    bool PersistentMarker = false);

public sealed record ActivityCorrectionView(
    CommitmentTarget Target,
    ActivityClassification OriginalClassification,
    ActivityClassification CorrectedClassification,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset CorrectedAt,
    ActivityRuleScope Scope,
    string? Note);

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
    IReadOnlyList<ActivityCorrectionView> RecentCorrections);

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
    IReadOnlyList<ActivityRule>? ActivityRules = null,
    RestSettings? RestSettings = null,
    Guid? TemplateId = null);

public sealed record SupervisionSnapshot(
    DateTimeOffset Now,
    Guid? ActiveComputerCommitmentId,
    IReadOnlyList<CommitmentView> Commitments,
    ActivityObservation? LatestActivity,
    ReminderNotice? LatestReminder,
    ActiveSupervisionView? ActiveSupervision = null);

public sealed record CoreRequest(
    string Operation,
    CommitmentDraft? Draft = null,
    Guid? CandidateId = null,
    Guid? CommitmentId = null,
    ActivityRuleBinding? ActivityRule = null,
    ActivityClassification? Classification = null,
    ActivityRuleScope? RuleScope = null,
    bool? IsResting = null,
    DateTimeOffset? RestEndAt = null,
    string? Note = null);

public sealed record CoreResponse(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    CommitmentCard? Card = null,
    SupervisionSnapshot? Snapshot = null);

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
    public const string SaveActivityRule = "saveActivityRule";
    public const string ClassifyCurrentActivity = "classifyCurrentActivity";
    public const string RecordReturnIntent = "recordReturnIntent";
    public const string RespondToRestPrompt = "respondToRestPrompt";
    public const string StartTimedRest = "startTimedRest";
    public const string GetSnapshot = "getSnapshot";
}
