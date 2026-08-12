using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class LocalReminderPresentationScenarios
{
    [Fact]
    public void Fullscreen_allows_only_a_silent_fresh_explicit_distraction_bubble()
    {
        var now = DateTimeOffset.UnixEpoch;
        var commitmentId = Guid.NewGuid();
        var distracting = State(commitmentId, ActivityClassification.Distracting, marker: true);
        var related = State(commitmentId, ActivityClassification.Related, marker: true);
        var deviation = Notice(commitmentId, now, ReminderKind.LocalDeviation);
        var question = Notice(commitmentId, now, ReminderKind.UnknownClassificationQuestion);

        var allowed = LocalReminderPresentation.Evaluate(
            deviation, distracting, commitmentId, now, false, true, false);
        Assert.True(allowed.ShowBubble);
        Assert.False(allowed.ShowMarker);
        Assert.True(allowed.SuppressSound);

        Assert.False(LocalReminderPresentation.Evaluate(
            deviation, related, commitmentId, now, false, true, false).ShowOverlay);
        Assert.False(LocalReminderPresentation.Evaluate(
            question, distracting, commitmentId, now, false, true, false).ShowOverlay);
    }

    [Fact]
    public void Quiet_presentation_hides_everything_without_changing_the_state()
    {
        var now = DateTimeOffset.UnixEpoch;
        var commitmentId = Guid.NewGuid();
        var state = State(commitmentId, ActivityClassification.Distracting, marker: true);

        var presentation = LocalReminderPresentation.Evaluate(
            Notice(commitmentId, now, ReminderKind.LocalDeviation),
            state,
            commitmentId,
            now,
            quietPresentation: true,
            foregroundIsFullscreen: false,
            muted: false);

        Assert.False(presentation.ShowOverlay);
        Assert.True(presentation.SuppressSound);
        Assert.True(state.ReminderMarkerActive);
    }

    [Theory]
    [InlineData(-1, -1, 1921, 1081, true)]
    [InlineData(0, 0, 1920, 1080, true)]
    [InlineData(100, 100, 1800, 900, false)]
    public void Fullscreen_geometry_is_detected_with_small_border_tolerance(
        int left, int top, int right, int bottom, bool expected)
    {
        var window = new ForegroundPresentationDetector.NativeRect
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
        var monitor = new ForegroundPresentationDetector.NativeRect
        {
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1080
        };

        Assert.Equal(expected, ForegroundPresentationDetector.CoversMonitor(window, monitor));
    }

    private static ActiveSupervisionView State(
        Guid commitmentId,
        ActivityClassification classification,
        bool marker) =>
        new(
            commitmentId,
            classification,
            IsIdle: false,
            DeviationReason.DistractingActivity,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(5),
            RelatedStableSince: null,
            marker,
            ReturnIntentAt: null,
            PendingPrompt: null,
            ActiveRest: null,
            LastUnobservableStartedAt: null,
            LastUnobservableEndedAt: null,
            RecentCorrections: []);

    private static ReminderNotice Notice(
        Guid commitmentId,
        DateTimeOffset now,
        ReminderKind kind) =>
        new(
            commitmentId,
            "test",
            now,
            kind,
            Guid.NewGuid(),
            now.AddSeconds(10),
            PlaySound: true,
            PersistentMarker: true);
}
