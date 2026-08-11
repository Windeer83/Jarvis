using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class OneTimeCommitmentScenarios
{
    private static readonly DateTimeOffset Baseline = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Candidate_is_not_formal_until_confirmed_and_card_shows_complete_defaults()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var prepared = await module.PrepareAsync(ComputerDraft(
            startAt: Baseline.AddHours(1),
            inputGoal: "专注整理交易日志",
            outcomeGoal: "完成今天的复盘条目"));

        Assert.True(prepared.Success);
        var card = Assert.IsType<CommitmentCard>(prepared.Value);
        Assert.Equal("专注整理交易日志", card.InputGoal);
        Assert.Equal("完成今天的复盘条目", card.OutcomeGoal);
        Assert.Equal(SupervisionMode.Interactive, card.SupervisionMode);
        Assert.Equal(5, card.ReminderSettings.LocalDeviationMinutes);
        Assert.Equal(20, card.ReminderSettings.FirstMobileDeviationMinutes);
        Assert.Equal(20, card.ReminderSettings.MobileRepeatMinutes);
        Assert.Equal(3, card.ReminderSettings.MaxMobileReminders);
        Assert.Contains("尚未正式成立", card.ConfirmationNotice, StringComparison.Ordinal);
        Assert.Empty((await module.GetSnapshotAsync()).Commitments);

        var confirmed = await module.ConfirmAsync(card.CandidateId);

        Assert.True(confirmed.Success);
        Assert.Single((await module.GetSnapshotAsync()).Commitments);
    }

    [Fact]
    public async Task Confirmed_computer_commitment_recovers_from_sqlite_and_derives_time_phase()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        var activity = new FakeActivitySource();
        activity.Next = activity.Next with { ObservedAt = Baseline.AddMinutes(10) };
        var reminders = new FakeReminderSink();

        Guid commitmentId;
        await using (var firstRun = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders))
        {
            var prepared = await firstRun.PrepareAsync(ComputerDraft(Baseline.AddMinutes(10)));
            var confirmed = await firstRun.ConfirmAsync(prepared.Value!.CandidateId);
            commitmentId = confirmed.Value!.Id;
            Assert.Equal(CommitmentPhase.Scheduled, confirmed.Value.Phase);

            clock.Now = Baseline.AddMinutes(10);
            await firstRun.TickAsync();
            Assert.Equal(CommitmentPhase.PreparationBuffer,
                (await firstRun.GetSnapshotAsync()).Commitments.Single().Phase);
            Assert.Equal(1, activity.ObservationCount);
            Assert.Equal(Baseline.AddMinutes(10),
                (await firstRun.GetSnapshotAsync()).LatestActivity!.ObservedAt);
            Assert.Single(reminders.Notices);

            await firstRun.TickAsync();
            Assert.Single(reminders.Notices);
        }

        clock.Now = Baseline.AddMinutes(15);
        await using (var secondRun = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            var recovered = await secondRun.GetSnapshotAsync();
            Assert.Equal(commitmentId, recovered.ActiveComputerCommitmentId);
            Assert.Equal(CommitmentPhase.Supervising, recovered.Commitments.Single().Phase);

            clock.Now = Baseline.AddMinutes(70);
            await secondRun.TickAsync();
            var ended = await secondRun.GetSnapshotAsync();
            Assert.Null(ended.ActiveComputerCommitmentId);
            Assert.Null(ended.LatestActivity);
            Assert.Equal(CommitmentPhase.AwaitingReview, ended.Commitments.Single().Phase);
        }
    }

    [Fact]
    public async Task Computer_conflicts_are_explicit_and_use_half_open_intervals()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var first = await module.PrepareAsync(ComputerDraft(Baseline.AddHours(1), durationMinutes: 60));
        Assert.True((await module.ConfirmAsync(first.Value!.CandidateId)).Success);

        var overlapping = await module.PrepareAsync(ComputerDraft(
            Baseline.AddMinutes(90), durationMinutes: 60, inputGoal: "冲突承诺"));
        var rejected = await module.ConfirmAsync(overlapping.Value!.CandidateId);
        Assert.False(rejected.Success);
        Assert.Equal("computer_commitment_conflict", rejected.ErrorCode);

        var adjacent = await module.PrepareAsync(ComputerDraft(
            Baseline.AddHours(2), durationMinutes: 60, inputGoal: "相邻承诺"));
        Assert.True((await module.ConfirmAsync(adjacent.Value!.CandidateId)).Success);

        var offline = await module.PrepareAsync(OfflineDraft(
            Baseline.AddMinutes(90), durationMinutes: 30));
        Assert.True((await module.ConfirmAsync(offline.Value!.CandidateId)).Success);

        Assert.Equal(3, (await module.GetSnapshotAsync()).Commitments.Count);
    }

    [Fact]
    public async Task Offline_commitment_reminds_and_accepts_manual_confirmation_without_activity_evidence()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();

        Guid commitmentId;
        await using (var firstRun = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders))
        {
            var prepared = await firstRun.PrepareAsync(OfflineDraft(Baseline.AddMinutes(-5), 60));
            var confirmed = await firstRun.ConfirmAsync(prepared.Value!.CandidateId);
            commitmentId = confirmed.Value!.Id;

            await firstRun.TickAsync();

            Assert.Equal(0, activity.ObservationCount);
            Assert.Single(reminders.Notices);
            Assert.Equal(CommitmentPhase.ActiveUnsupervised,
                (await firstRun.GetSnapshotAsync()).Commitments.Single().Phase);

            var manual = await firstRun.ConfirmOfflineStartedAsync(commitmentId);
            Assert.True(manual.Success);
            Assert.Equal(Baseline, manual.Value!.OfflineManuallyConfirmedAt);
        }

        await using var secondRun = await OpenAsync(database.Path, clock);
        var recovered = (await secondRun.GetSnapshotAsync()).Commitments.Single();
        Assert.Equal(Baseline, recovered.OfflineManuallyConfirmedAt);
    }

    [Fact]
    public async Task Prepared_candidates_do_not_reserve_the_computer_supervision_slot()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var first = await module.PrepareAsync(ComputerDraft(Baseline.AddHours(1)));
        var second = await module.PrepareAsync(ComputerDraft(
            Baseline.AddHours(1), inputGoal: "另一个候选"));

        Assert.Empty((await module.GetSnapshotAsync()).Commitments);
        Assert.True((await module.ConfirmAsync(second.Value!.CandidateId)).Success);
        Assert.Equal("candidate_not_found",
            (await module.ConfirmAsync(first.Value!.CandidateId)).ErrorCode);
        Assert.Single((await module.GetSnapshotAsync()).Commitments);
    }

    [Fact]
    public async Task Unknown_commitment_kind_and_supervision_mode_fail_closed()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var badKind = ComputerDraft(Baseline.AddHours(1)) with
        {
            Kind = (CommitmentKind)99,
            RelatedAppsOrSites = null
        };
        var badMode = ComputerDraft(Baseline.AddHours(2)) with
        {
            SupervisionMode = (SupervisionMode)99
        };

        Assert.Equal("commitment_kind_invalid", (await module.PrepareAsync(badKind)).ErrorCode);
        Assert.Equal("supervision_mode_invalid", (await module.PrepareAsync(badMode)).ErrorCode);
        Assert.Empty((await module.GetSnapshotAsync()).Commitments);
    }

    [Fact]
    public async Task Repeated_concurrent_confirmation_can_only_form_one_offline_commitment()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);
        var prepared = await module.PrepareAsync(OfflineDraft(Baseline.AddHours(1), 60));

        var confirmations = await Task.WhenAll(
            module.ConfirmAsync(prepared.Value!.CandidateId),
            module.ConfirmAsync(prepared.Value.CandidateId));

        Assert.Single(confirmations, result => result.Success);
        Assert.Single(confirmations, result => result.ErrorCode == "candidate_not_found");
        Assert.Single((await module.GetSnapshotAsync()).Commitments);
    }

    private static Task<SupervisionModule> OpenAsync(string path, FakeClock clock) =>
        SupervisionModule.OpenAsync(path, clock, new FakeActivitySource(), new FakeReminderSink());

    private static CommitmentDraft ComputerDraft(
        DateTimeOffset startAt,
        int durationMinutes = 60,
        string? inputGoal = "完成交易日志",
        string? outcomeGoal = null) => new(
        CommitmentKind.Computer,
        startAt,
        EndAt: null,
        durationMinutes,
        inputGoal,
        outcomeGoal,
        [
            new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"),
            new CommitmentTarget(CommitmentTargetKind.Website, "tradingview.com")
        ],
        SupervisionMode: null,
        ReminderSettings: null);

    private static CommitmentDraft OfflineDraft(
        DateTimeOffset startAt,
        int durationMinutes) => new(
        CommitmentKind.Offline,
        startAt,
        EndAt: null,
        durationMinutes,
        InputGoal: "阅读纸质资料",
        OutcomeGoal: null,
        RelatedAppsOrSites: null,
        SupervisionMode: null,
        ReminderSettings: null);
}
