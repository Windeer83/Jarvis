using Jarvis.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class SupervisionBehaviorScenarios
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Distracting_activity_reminds_once_and_only_stable_related_activity_recovers()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using var module = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders);
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");
        Assert.True((await module.SaveActivityRuleAsync(new ActivityRuleBinding(
            ActivityRuleScope.Commitment,
            commitment.Id,
            new ActivityRule(
                new CommitmentTarget(CommitmentTargetKind.Application, "games.exe"),
                ActivityClassification.Distracting)), commitment.Version)).Success);

        Observe(activity, clock, "games.exe", active: true);
        await module.TickAsync();
        clock.Now = Start.AddMinutes(5); // preparation buffer only suppresses presentation
        Observe(activity, clock, "games.exe", active: true);
        await module.TickAsync();
        clock.Now = Start.AddMinutes(10);
        Observe(activity, clock, "games.exe", active: true);
        await module.TickAsync();

        var reminded = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(ActivityClassification.Distracting, reminded.Classification);
        Assert.Equal(Start, reminded.DeviationStartedAt);
        Assert.True(reminded.ReminderMarkerActive);
        Assert.Single(reminders.Notices, notice => notice.Kind == ReminderKind.LocalDeviation);
        Assert.True(reminders.Notices.Single(notice => notice.Kind == ReminderKind.LocalDeviation).PlaySound);

        await module.RecordReturnIntentAsync(commitment.Id);
        Assert.Equal(Start,
            (await module.GetSnapshotAsync()).ActiveSupervision!.DeviationStartedAt);

        clock.Now = Start.AddMinutes(11);
        Observe(activity, clock, "Excel.exe", active: true);
        await module.TickAsync();
        Assert.True((await module.GetSnapshotAsync()).ActiveSupervision!.ReminderMarkerActive);

        clock.Now = Start.AddMinutes(13);
        Observe(activity, clock, "Excel.exe", active: true);
        await module.TickAsync();
        var recovered = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Null(recovered.DeviationStartedAt);
        Assert.False(recovered.ReminderMarkerActive);

        clock.Now = Start.AddMinutes(14);
        Observe(activity, clock, "games.exe", active: true);
        await module.TickAsync();
        clock.Now = Start.AddMinutes(19);
        Observe(activity, clock, "games.exe", active: true);
        await module.TickAsync();
        Assert.Equal(2, reminders.Notices.Count(notice => notice.Kind == ReminderKind.LocalDeviation));
    }

    [Fact]
    public async Task Rules_use_commitment_then_template_then_global_precedence_and_passive_idle_stays_related()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-10));
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var templateId = Guid.NewGuid();
        var commitment = await ConfirmComputerAsync(
            module, Start, "reader.exe", SupervisionMode.Passive, templateId);
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "reader.exe");

        Assert.True((await module.SaveActivityRuleAsync(new ActivityRuleBinding(
            ActivityRuleScope.Global, null,
            new ActivityRule(target, ActivityClassification.Distracting)))).Success);
        Assert.True((await module.SaveActivityRuleAsync(new ActivityRuleBinding(
            ActivityRuleScope.Template, templateId,
            new ActivityRule(target, ActivityClassification.Unknown)))).Success);
        Assert.True((await module.SaveActivityRuleAsync(new ActivityRuleBinding(
            ActivityRuleScope.Commitment, commitment.Id,
            new ActivityRule(target, ActivityClassification.Related)), commitment.Version)).Success);

        clock.Now = Start.AddMinutes(6);
        Observe(activity, clock, "reader.exe", active: false, idle: TimeSpan.FromMinutes(20));
        await module.TickAsync();

        var state = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(ActivityClassification.Related, state.Classification);
        Assert.Null(state.DeviationStartedAt);

        await using var restarted = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await restarted.TickAsync();
        Assert.Equal(ActivityClassification.Related,
            (await restarted.GetSnapshotAsync()).ActiveSupervision!.Classification);
    }

    [Fact]
    public async Task Frozen_card_rules_and_rest_settings_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-10));
        var activity = new FakeActivitySource();
        Guid commitmentId;
        await using (var module = await SupervisionModule.OpenAsync(
                         database.Path, clock, activity, new FakeReminderSink()))
        {
            var prepared = await module.PrepareAsync(new CommitmentDraft(
                CommitmentKind.Computer,
                Start,
                EndAt: null,
                DurationMinutes: 90,
                InputGoal: "冻结模板值",
                OutcomeGoal: null,
                RelatedAppsOrSites:
                [
                    new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")
                ],
                SupervisionMode.Interactive,
                ReminderSettings: null,
                ActivityRules:
                [
                    new ActivityRule(
                        new CommitmentTarget(CommitmentTargetKind.Application, "research.exe"),
                        ActivityClassification.Related)
                ],
                RestSettings: new RestSettings(7, 12),
                TemplateId: Guid.NewGuid()));
            var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);
            commitmentId = confirmed.Value!.Id;
        }

        clock.Now = Start.AddMinutes(6);
        Observe(activity, clock, "research.exe", active: true);
        await using var restarted = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await restarted.TickAsync();
        var snapshot = await restarted.GetSnapshotAsync();
        Assert.Equal(new RestSettings(7, 12), snapshot.Commitments.Single().RestSettings);
        Assert.Contains(snapshot.Commitments.Single().ActivityRules, rule =>
            rule.Target.Value == "research.exe" &&
            rule.Classification == ActivityClassification.Related);
        Assert.Equal(commitmentId, snapshot.ActiveComputerCommitmentId);
        Assert.Equal(ActivityClassification.Related, snapshot.ActiveSupervision!.Classification);
    }

    [Fact]
    public async Task Unknown_question_can_correct_from_original_activity_start()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using var module = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders);
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");

        clock.Now = Start.AddMinutes(5);
        Observe(activity, clock, "mystery.exe", active: true);
        await module.TickAsync();
        clock.Now = Start.AddMinutes(10);
        Observe(activity, clock, "mystery.exe", active: true);
        await module.TickAsync();

        Assert.Equal(SupervisionPromptKind.UnknownClassification,
            (await module.GetSnapshotAsync()).ActiveSupervision!.PendingPrompt);
        Assert.Single(reminders.Notices, notice => notice.Kind == ReminderKind.UnknownClassificationQuestion);

        var corrected = await module.ClassifyCurrentActivityAsync(
            commitment.Id,
            ActivityClassification.Distracting,
            ActivityRuleScope.Commitment,
            "这是交易之外的软件");
        Assert.True(corrected.Success);
        var state = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(Start.AddMinutes(5), state.DeviationStartedAt);
        Assert.True(state.ReminderMarkerActive);
        Assert.Contains(state.RecentCorrections,
            correction => correction.OriginalClassification == ActivityClassification.Unknown &&
                          correction.CorrectedClassification == ActivityClassification.Distracting &&
                          correction.EffectiveFrom == Start.AddMinutes(5));
    }

    [Theory]
    [InlineData(ActivityRuleScope.Template)]
    [InlineData(ActivityRuleScope.Global)]
    public async Task Wider_scope_correction_also_overrides_the_frozen_rule_for_the_current_commitment(
        ActivityRuleScope scope)
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        Guid commitmentId;

        await using (var module = await SupervisionModule.OpenAsync(
                         database.Path, clock, activity, new FakeReminderSink()))
        {
            var template = await module.CreateTemplateAsync(new CommitmentTemplateDraft(
                "current correction",
                CommitmentKind.Computer,
                90,
                "supervise",
                null,
                [new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")],
                SupervisionMode.Interactive,
                null,
                [
                    new ActivityRule(
                        new CommitmentTarget(CommitmentTargetKind.Application, "mystery.exe"),
                        ActivityClassification.Distracting)
                ],
                new RestSettings(10, 15)));
            Assert.True(template.Success, template.Message);
            var prepared = await module.PrepareFromTemplateAsync(new TemplateCommitmentDraft(
                template.Value!.Id,
                Start));
            var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);
            commitmentId = confirmed.Value!.Id;

            Observe(activity, clock, "mystery.exe", active: true);
            await module.TickAsync();
            Assert.Equal(ActivityClassification.Distracting,
                (await module.GetSnapshotAsync()).ActiveSupervision!.Classification);

            var corrected = await module.ClassifyCurrentActivityAsync(
                commitmentId,
                ActivityClassification.Related,
                scope);
            Assert.True(corrected.Success, corrected.Message);
            clock.Now = Start.AddMinutes(1);
            Observe(activity, clock, "mystery.exe", active: true);
            await module.TickAsync();
            Assert.Equal(ActivityClassification.Related,
                (await module.GetSnapshotAsync()).ActiveSupervision!.Classification);
        }

        await using var restarted = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await restarted.TickAsync();
        var snapshot = await restarted.GetSnapshotAsync();
        Assert.Equal(ActivityClassification.Related, snapshot.ActiveSupervision!.Classification);
        Assert.Contains(
            snapshot.Commitments.Single(item => item.Id == commitmentId).ActivityRules,
            rule => rule.Target.Value == "mystery.exe" &&
                    rule.Classification == ActivityClassification.Related);
    }

    [Fact]
    public async Task Invalid_rule_scope_fails_before_any_classification_change()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");
        Observe(activity, clock, "mystery.exe", active: true);
        await module.TickAsync();

        var result = await module.ClassifyCurrentActivityAsync(
            commitment.Id,
            ActivityClassification.Related,
            (ActivityRuleScope)99);

        Assert.Equal("activity_rule_scope_invalid", result.ErrorCode);
        Assert.Equal(ActivityClassification.Unknown,
            (await module.GetSnapshotAsync()).ActiveSupervision!.Classification);
    }

    [Fact]
    public async Task Classifying_after_the_first_tick_materializes_and_corrects_the_pending_segment()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");

        Observe(activity, clock, "mystery.exe", active: true);
        await module.TickAsync();
        var captured = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(ActivityClassification.Unknown, captured.Classification);

        clock.Now = Start.AddSeconds(30);
        var corrected = await module.ClassifyActivityAsync(
            commitment.Id,
            commitment.Version,
            captured.ActionableTarget!,
            captured.ActivityStateStartedAt!.Value,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment,
            "research is related");

        Assert.True(corrected.Success, corrected.Message);
        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        var segment = Assert.Single(history.ActivitySegments);
        Assert.Equal(Start, segment.StartAt);
        Assert.Equal(Start.AddSeconds(30), segment.EndAt);
        Assert.Equal(1, segment.CommitmentVersion);
        Assert.Equal(ActivityAvailability.Available, segment.Availability);
        Assert.Equal("mystery.exe", segment.Target!.Value);
        Assert.Equal(ActivityClassification.Unknown, segment.OriginalClassification);
        Assert.Equal(ActivityClassification.Related, segment.EffectiveClassification);
        Assert.Equal(segment.Id, Assert.Single(history.Corrections).ActivitySegmentId);
    }

    [Fact]
    public async Task Failed_classification_transaction_leaves_no_rule_correction_runtime_or_notice()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "mystery.exe");
        var binding = new ActivityRuleBinding(
            ActivityRuleScope.Commitment,
            commitment.Id,
            new ActivityRule(target, ActivityClassification.Distracting));
        var globalBinding = new ActivityRuleBinding(
            ActivityRuleScope.Global,
            null,
            new ActivityRule(target, ActivityClassification.Distracting));
        var correction = new ActivityCorrectionView(
            target,
            ActivityClassification.Unknown,
            ActivityClassification.Distracting,
            Start,
            Start.AddMinutes(5),
            ActivityRuleScope.Commitment,
            null);
        var runtime = new StoredSupervisionRuntime(
            commitment.Id,
            ActivityClassification.Distracting,
            target,
            Start,
            DeviationStartedAt: Start,
            CountedDeviation: TimeSpan.FromMinutes(5),
            DeviationCountingSince: Start.AddMinutes(5),
            DeviationReason: DeviationReason.DistractingActivity,
            LocalReminderSentAt: Start.AddMinutes(5),
            ReminderMarkerActive: true);
        var notice = new ReminderNotice(
            commitment.Id,
            "test",
            Start.AddMinutes(5),
            ReminderKind.LocalDeviation,
            Guid.NewGuid(),
            Start.AddMinutes(5).AddSeconds(10),
            PlaySound: true,
            PersistentMarker: true);
        var store = new SqliteCommitmentStore(database.Path);

        await Assert.ThrowsAsync<SqliteException>(() => store.PersistClassificationForTestAsync(
            [binding, globalBinding],
            correction,
            pendingSegment: null,
            runtime,
            notice,
            async (connection, transaction, cancellationToken) =>
            {
                await using var fail = connection.CreateCommand();
                fail.Transaction = transaction;
                fail.CommandText = "INSERT INTO table_that_does_not_exist VALUES (1);";
                await fail.ExecuteNonQueryAsync(cancellationToken);
            }));

        Assert.Null(await store.FindActivityRuleAsync(
            ActivityRuleScope.Commitment, commitment.Id, target, CancellationToken.None));
        Assert.Null(await store.FindActivityRuleAsync(
            ActivityRuleScope.Global, null, target, CancellationToken.None));
        Assert.Empty(await store.ReadCorrectionsAsync(commitment.Id, CancellationToken.None));
        Assert.Null(await store.ReadLatestReminderAsync(CancellationToken.None));
        var unchanged = await store.ReadRuntimeAsync(commitment.Id, CancellationToken.None);
        Assert.Null(unchanged.Classification);
        Assert.False(unchanged.ReminderMarkerActive);
    }

    [Fact]
    public async Task Unobservable_time_does_not_count_and_recovery_does_not_replay_stale_reminders()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using (var module = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders))
        {
            var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");
            Assert.True((await module.SaveActivityRuleAsync(new ActivityRuleBinding(
                ActivityRuleScope.Commitment,
                commitment.Id,
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Application, "games.exe"),
                    ActivityClassification.Distracting)), commitment.Version)).Success);
            clock.Now = Start.AddMinutes(5);
            Observe(activity, clock, "games.exe", active: true);
            await module.TickAsync();
            clock.Now = Start.AddMinutes(8);
            ObserveUnobservable(activity, clock);
            await module.TickAsync();
        }

        clock.Now = Start.AddMinutes(30);
        Observe(activity, clock, "games.exe", active: true);
        await using var restarted = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders);
        await restarted.TickAsync();
        var state = (await restarted.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(TimeSpan.FromMinutes(3), state.CountedDeviation);
        Assert.False(state.ReminderMarkerActive);
        Assert.DoesNotContain(reminders.Notices, notice => notice.Kind == ReminderKind.LocalDeviation);
    }

    [Fact]
    public async Task Interactive_idle_confirmation_uses_total_rest_from_idle_start_and_expires_automatically()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using var module = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders);
        var commitment = await ConfirmComputerAsync(module, Start, "Excel.exe");

        clock.Now = Start.AddMinutes(5);
        Observe(activity, clock, "Excel.exe", active: false, idle: TimeSpan.Zero);
        await module.TickAsync();
        clock.Now = Start.AddMinutes(15);
        Observe(activity, clock, "Excel.exe", active: false, idle: TimeSpan.FromMinutes(10));
        await module.TickAsync();
        Assert.Equal(SupervisionPromptKind.ConfirmRest,
            (await module.GetSnapshotAsync()).ActiveSupervision!.PendingPrompt);

        var accepted = await module.RespondToRestPromptAsync(commitment.Id, isResting: true);
        Assert.True(accepted.Success);
        Assert.Equal(Start.AddMinutes(5), accepted.Value!.StartAt);
        Assert.Equal(Start.AddMinutes(20), accepted.Value.EndAt);

        clock.Now = Start.AddMinutes(20);
        Observe(activity, clock, "Excel.exe", active: false, idle: TimeSpan.FromMinutes(15));
        await module.TickAsync();
        Assert.Null((await module.GetSnapshotAsync()).ActiveSupervision!.ActiveRest);
        Assert.Single(reminders.Notices, notice => notice.Kind == ReminderKind.RestEnded);

        var missingEnd = await module.StartTimedRestAsync(commitment.Id, endAt: null);
        Assert.Equal("rest_end_required", missingEnd.ErrorCode);
    }

    private static async Task<CommitmentView> ConfirmComputerAsync(
        SupervisionModule module,
        DateTimeOffset start,
        string relatedProcess,
        SupervisionMode mode = SupervisionMode.Interactive,
        Guid? templateId = null)
    {
        var prepared = await module.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer,
            start,
            EndAt: null,
            DurationMinutes: 90,
            InputGoal: "完成监督测试",
            OutcomeGoal: null,
            RelatedAppsOrSites:
            [
                new CommitmentTarget(CommitmentTargetKind.Application, relatedProcess)
            ],
            SupervisionMode: mode,
            ReminderSettings: null,
            TemplateId: templateId,
            RestSettings: null));
        Assert.True(prepared.Success);
        var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);
        Assert.True(confirmed.Success);
        return confirmed.Value!;
    }

    private static void Observe(
        FakeActivitySource activity,
        FakeClock clock,
        string process,
        bool active,
        TimeSpan? idle = null) => activity.Next = new ActivityObservation(
        ActivityAvailability.Available,
        active,
        process,
        clock.Now,
        ForegroundWebsiteDomain: null,
        IdleDuration: idle ?? (active ? TimeSpan.Zero : TimeSpan.FromMinutes(1)));

    private static void ObserveUnobservable(FakeActivitySource activity, FakeClock clock) =>
        activity.Next = new ActivityObservation(
            ActivityAvailability.Unobservable,
            IsUserActive: false,
            ForegroundProcess: null,
            clock.Now,
            ForegroundWebsiteDomain: null,
            IdleDuration: null);
}
