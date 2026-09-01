using Jarvis.Contracts;

namespace Jarvis.Desktop;

public readonly record struct LocalReminderPresentation(
    bool ShowBubble,
    bool ShowMarker,
    bool SuppressSound)
{
    public bool ShowOverlay => ShowBubble || ShowMarker;

    public static LocalReminderPresentation Evaluate(
        ReminderNotice? reminder,
        ActiveSupervisionView state,
        Guid currentCommitmentId,
        DateTimeOffset now,
        ReminderSettings reminderSettings,
        bool quietPresentation,
        bool foregroundIsFullscreen,
        bool muted)
    {
        quietPresentation |= reminderSettings.QuietPresentation;
        muted |= !reminderSettings.SoundEnabled;
        var freshCurrentBubble = reminder is not null &&
                                 reminder.CommitmentId == currentCommitmentId &&
                                 reminder.CommitmentVersion == state.CommitmentVersion &&
                                 reminder.BubbleExpiresAt > now;
        var fullscreenDeviationBubble = freshCurrentBubble &&
                                        reminder!.Kind == ReminderKind.LocalDeviation &&
                                        state.Classification == ActivityClassification.Distracting;

        return new LocalReminderPresentation(
            ShowBubble: !quietPresentation &&
                        (foregroundIsFullscreen ? fullscreenDeviationBubble : freshCurrentBubble),
            ShowMarker: !quietPresentation &&
                        !foregroundIsFullscreen &&
                        state.ReminderMarkerActive,
            SuppressSound: muted || quietPresentation || foregroundIsFullscreen);
    }
}
