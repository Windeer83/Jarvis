using Jarvis.Contracts;

namespace Jarvis.Desktop;

public sealed class LocalReminderSoundGate
{
    private Guid? _consumedNoticeId;

    public bool Consume(
        ReminderNotice? reminder,
        Guid currentCommitmentId,
        DateTimeOffset now,
        bool presentationSuppressesSound)
    {
        if (reminder is not { PlaySound: true } ||
            reminder.CommitmentId != currentCommitmentId ||
            reminder.BubbleExpiresAt is not { } expiresAt ||
            expiresAt <= now ||
            reminder.NoticeId == _consumedNoticeId)
        {
            return false;
        }

        // Seeing a fresh notice consumes its one sound opportunity even when presentation is muted.
        // Leaving a quiet/full-screen mode must never replay a sound that was intentionally skipped.
        _consumedNoticeId = reminder.NoticeId;
        return !presentationSuppressesSound;
    }
}
