using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class ClassificationTargetCaptureScenarios
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Opening_Jarvis_does_not_replace_the_external_activity_awaiting_classification()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());

        var prepared = await module.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer,
            Start.AddMinutes(-10),
            EndAt: null,
            DurationMinutes: 60,
            InputGoal: "核对提醒活动",
            OutcomeGoal: null,
            RelatedAppsOrSites:
            [
                new CommitmentTarget(CommitmentTargetKind.Application, "notepad.exe")
            ],
            SupervisionMode.Interactive,
            ReminderSettings: null));
        var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);

        clock.Now = Start;
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available,
            IsUserActive: true,
            ForegroundProcess: "ChatGPT",
            Start);
        await module.TickAsync();

        clock.Now = Start.AddSeconds(2);
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available,
            IsUserActive: true,
            ForegroundProcess: "Jarvis.Desktop",
            clock.Now);
        await module.TickAsync();

        var snapshot = await module.GetSnapshotAsync();
        Assert.Equal("ChatGPT", snapshot.ActiveSupervision!.ActionableTarget!.Value);

        var result = await module.ClassifyActivityAsync(
            confirmed.Value!.Id,
            confirmed.Value.Version,
            snapshot.ActiveSupervision.ActionableTarget,
            snapshot.ActiveSupervision.ActivityStateStartedAt!.Value,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment);

        Assert.True(result.Success, result.Message);
        Assert.Contains(
            (await module.GetSnapshotAsync()).Commitments.Single().ActivityRules,
            rule => rule.Target.Value == "ChatGPT" &&
                    rule.Classification == ActivityClassification.Related);
        Assert.DoesNotContain(
            (await module.GetSnapshotAsync()).Commitments.Single().ActivityRules,
            rule => rule.Target.Value == "Jarvis.Desktop");
    }

    [Fact]
    public async Task Classification_rejects_a_stale_activity_token_without_writing_a_rule()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var prepared = await module.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer,
            Start.AddMinutes(-10),
            EndAt: null,
            DurationMinutes: 60,
            InputGoal: "核对过期操作",
            OutcomeGoal: null,
            RelatedAppsOrSites:
            [
                new CommitmentTarget(CommitmentTargetKind.Application, "notepad.exe")
            ],
            SupervisionMode.Interactive,
            ReminderSettings: null));
        var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);

        clock.Now = Start;
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available, true, "ChatGPT", Start);
        await module.TickAsync();
        var captured = (await module.GetSnapshotAsync()).ActiveSupervision!;

        clock.Now = Start.AddMinutes(1);
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available, true, "notepad", clock.Now);
        await module.TickAsync();

        var result = await module.ClassifyActivityAsync(
            confirmed.Value!.Id,
            confirmed.Value.Version,
            captured.ActionableTarget!,
            captured.ActivityStateStartedAt!.Value,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment);

        Assert.Equal("activity_changed", result.ErrorCode);
        Assert.DoesNotContain(
            (await module.GetSnapshotAsync()).Commitments.Single().ActivityRules,
            rule => rule.Target.Value == "ChatGPT");
    }
}
