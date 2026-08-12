using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class LocalReminderSoundGateScenarios
{
    [Fact]
    public void Only_a_fresh_current_notice_gets_one_sound_opportunity()
    {
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var commitmentId = Guid.NewGuid();
        var gate = new LocalReminderSoundGate();

        var expired = Notice(commitmentId, now.AddSeconds(-1));
        Assert.False(gate.Consume(expired, commitmentId, now, presentationSuppressesSound: false));

        var otherCommitment = Notice(Guid.NewGuid(), now.AddSeconds(10));
        Assert.False(gate.Consume(otherCommitment, commitmentId, now, presentationSuppressesSound: false));

        var muted = Notice(commitmentId, now.AddSeconds(10));
        Assert.False(gate.Consume(muted, commitmentId, now, presentationSuppressesSound: true));
        Assert.False(gate.Consume(muted, commitmentId, now, presentationSuppressesSound: false));

        var fresh = Notice(commitmentId, now.AddSeconds(10));
        Assert.True(gate.Consume(fresh, commitmentId, now, presentationSuppressesSound: false));
        Assert.False(gate.Consume(fresh, commitmentId, now, presentationSuppressesSound: false));
    }

    private static ReminderNotice Notice(Guid commitmentId, DateTimeOffset expiresAt) =>
        new(
            commitmentId,
            "test",
            expiresAt.AddSeconds(-10),
            ReminderKind.LocalDeviation,
            Guid.NewGuid(),
            expiresAt,
            PlaySound: true,
            PersistentMarker: true);
}
