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

public sealed record ReminderSettings(
    bool StartReminderEnabled,
    int LocalDeviationMinutes,
    int FirstMobileDeviationMinutes,
    int MobileRepeatMinutes,
    int MaxMobileReminders);

public sealed record CommitmentTarget(CommitmentTargetKind Kind, string Value);

public sealed record CommitmentDraft(
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    int? DurationMinutes,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget>? RelatedAppsOrSites,
    SupervisionMode? SupervisionMode,
    ReminderSettings? ReminderSettings);

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
    string ConfirmationNotice);

public sealed record ActivityObservation(
    ActivityAvailability Availability,
    bool IsUserActive,
    string? ForegroundProcess,
    DateTimeOffset ObservedAt);

public sealed record ReminderNotice(
    Guid CommitmentId,
    string Message,
    DateTimeOffset CreatedAt);

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
    DateTimeOffset? OfflineManuallyConfirmedAt);

public sealed record SupervisionSnapshot(
    DateTimeOffset Now,
    Guid? ActiveComputerCommitmentId,
    IReadOnlyList<CommitmentView> Commitments,
    ActivityObservation? LatestActivity,
    ReminderNotice? LatestReminder);

public sealed record CoreRequest(
    string Operation,
    CommitmentDraft? Draft = null,
    Guid? CandidateId = null,
    Guid? CommitmentId = null);

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
    public const string GetSnapshot = "getSnapshot";
}
