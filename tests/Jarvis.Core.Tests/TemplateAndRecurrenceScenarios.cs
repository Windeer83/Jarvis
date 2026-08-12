using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class TemplateAndRecurrenceScenarios
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

    private static async Task<SupervisionResult<RecurrencePlanView>> ChangeAsync(
        SupervisionModule module,
        RecurrenceChangeRequest request)
    {
        var prepared = await module.PrepareRecurrenceChangeAsync(request);
        Assert.True(prepared.Success, prepared.Message);
        return await module.ConfirmRecurrenceChangeAsync(prepared.Value!.CandidateId);
    }

    [Fact]
    public async Task Template_crud_persists_explicit_defaults_without_creating_a_commitment()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);

        Guid templateId;
        await using (var module = await OpenAsync(database.Path, clock))
        {
            var created = await module.CreateTemplateAsync(TemplateDraft());

            Assert.True(created.Success);
            templateId = created.Value!.Id;
            Assert.Empty((await module.GetSnapshotAsync()).Commitments);
            Assert.Equal(180, created.Value.DurationMinutes);
            Assert.Equal(new RestSettings(10, 15), created.Value.RestSettings);
            Assert.Equal(ActivityClassification.Related,
                Assert.Single(created.Value.ActivityRules).Classification);
        }

        await using (var restarted = await OpenAsync(database.Path, clock))
        {
            var template = Assert.Single((await restarted.GetSnapshotAsync()).Templates);
            Assert.Equal(templateId, template.Id);
            Assert.Equal("交易日志", template.Name);

            var archived = await restarted.ArchiveTemplateAsync(templateId);
            Assert.True(archived.Success);
            Assert.True(archived.Value!.IsArchived);
            Assert.Empty((await restarted.GetSnapshotAsync()).Commitments);
        }
    }

    [Fact]
    public async Task Template_and_frozen_commitment_keep_non_default_supervision_settings_across_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        var reminders = new ReminderSettings(
            StartReminderEnabled: false,
            LocalDeviationMinutes: 7,
            FirstMobileDeviationMinutes: 25,
            MobileRepeatMinutes: 12,
            MaxMobileReminders: 2,
            SoundEnabled: false,
            QuietPresentation: true);
        var rest = new RestSettings(12, 18);
        Guid templateId;
        Guid commitmentId;

        await using (var module = await OpenAsync(database.Path, clock))
        {
            var created = await module.CreateTemplateAsync(TemplateDraft() with
            {
                ReminderSettings = reminders,
                RestSettings = rest,
                ActivityRules =
                [
                    new ActivityRule(
                        new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"),
                        ActivityClassification.Distracting)
                ]
            });
            Assert.True(created.Success, created.Message);
            templateId = created.Value!.Id;

            var prepared = await module.PrepareFromTemplateAsync(new TemplateCommitmentDraft(
                templateId,
                Baseline.AddDays(1)));
            Assert.True(prepared.Success, prepared.Message);
            var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);
            Assert.True(confirmed.Success, confirmed.Message);
            commitmentId = confirmed.Value!.Id;
        }

        await using var restarted = await OpenAsync(database.Path, clock);
        var snapshot = await restarted.GetSnapshotAsync();
        var template = Assert.Single(snapshot.Templates);
        var commitment = Assert.Single(snapshot.Commitments, item => item.Id == commitmentId);
        Assert.Equal(reminders, template.ReminderSettings);
        Assert.Equal(reminders, commitment.ReminderSettings);
        Assert.Equal(rest, template.RestSettings);
        Assert.Equal(rest, commitment.RestSettings);
        Assert.Equal(ActivityClassification.Distracting,
            Assert.Single(template.ActivityRules).Classification);
        Assert.Equal(ActivityClassification.Distracting,
            Assert.Single(commitment.ActivityRules).Classification);
    }

    [Fact]
    public async Task Template_candidate_allows_one_time_overrides_and_later_template_edits_do_not_change_it()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);
        var template = (await module.CreateTemplateAsync(TemplateDraft())).Value!;

        var prepared = await module.PrepareFromTemplateAsync(new TemplateCommitmentDraft(
            template.Id,
            Baseline.AddDays(1).AddHours(1),
            DurationMinutes: 90,
            InputGoal: "只覆盖本次输入目标"));

        var card = prepared.Value!;
        Assert.Equal(template.Id, card.TemplateId);
        Assert.Equal(90, (card.EndAt - card.StartAt).TotalMinutes);
        Assert.Equal("只覆盖本次输入目标", card.InputGoal);
        Assert.Equal("完成当天复盘", card.OutcomeGoal);
        Assert.Equal(new RestSettings(10, 15), card.RestSettings);
        Assert.Equal(template.ActivityRules, card.ActivityRules);
        Assert.Equal(template.ReminderSettings, card.ReminderSettings);
        var confirmed = (await module.ConfirmAsync(card.CandidateId)).Value!;

        var updated = await module.UpdateTemplateAsync(template.Id, TemplateDraft() with
        {
            DurationMinutes = 240,
            InputGoal = "模板后来被修改"
        });
        Assert.True(updated.Success);

        var persisted = Assert.Single((await module.GetSnapshotAsync()).Commitments);
        Assert.Equal(confirmed.Id, persisted.Id);
        Assert.Equal(90, (persisted.EndAt - persisted.StartAt).TotalMinutes);
        Assert.Equal("只覆盖本次输入目标", persisted.InputGoal);

        var next = (await module.PrepareFromTemplateAsync(new TemplateCommitmentDraft(
            template.Id,
            Baseline.AddDays(2).AddHours(1)))).Value!;
        Assert.Equal(240, (next.EndAt - next.StartAt).TotalMinutes);
        Assert.Equal("模板后来被修改", next.InputGoal);
    }

    [Fact]
    public async Task Daily_weekly_and_selected_dates_create_independent_occurrences_and_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);

        Guid[] planIds;
        await using (var module = await OpenAsync(database.Path, clock))
        {
            var daily = await CreatePlanAsync(module, new RecurrencePattern(
                RecurrenceKind.Daily,
                new DateOnly(2026, 8, 13),
                new DateOnly(2026, 8, 15)));
            var weekly = await CreatePlanAsync(module, new RecurrencePattern(
                RecurrenceKind.Weekly,
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 30),
                Weekdays: [DayOfWeek.Monday, DayOfWeek.Wednesday]));
            var selected = await CreatePlanAsync(module, new RecurrencePattern(
                RecurrenceKind.SelectedDates,
                SelectedDates:
                [
                    new DateOnly(2026, 9, 1),
                    new DateOnly(2026, 9, 4),
                    new DateOnly(2026, 9, 9)
                ]));

            Assert.Equal([13, 14, 15], daily.Occurrences.Select(x => x.Date.Day));
            Assert.Equal([17, 19, 24, 26], weekly.Occurrences.Select(x => x.Date.Day));
            Assert.Equal([1, 4, 9], selected.Occurrences.Select(x => x.Date.Day));

            var occurrenceIds = daily.Occurrences.Concat(weekly.Occurrences)
                .Concat(selected.Occurrences).Select(x => x.CommitmentId).ToArray();
            Assert.Equal(10, occurrenceIds.Distinct().Count());
            planIds = [daily.Id, weekly.Id, selected.Id];
        }

        await using var restarted = await OpenAsync(database.Path, clock);
        var snapshot = await restarted.GetSnapshotAsync();
        Assert.Equal(planIds.Order(), snapshot.RecurrencePlans.Select(x => x.Id).Order());
        Assert.Equal(10, snapshot.Commitments.Count);
        Assert.Equal(10, snapshot.RecurrencePlans.Sum(x => x.Occurrences.Count));
    }

    [Fact]
    public async Task Existing_cross_midnight_conflict_rejects_the_entire_recurrence_batch_with_a_date()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var existing = await module.PrepareAsync(ComputerDraft(
            new DateTimeOffset(2026, 8, 14, 0, 30, 0, TimeSpan.FromHours(8)), 60));
        Assert.True((await module.ConfirmAsync(existing.Value!.CandidateId)).Success);

        var candidate = await module.PrepareRecurrenceAsync(new RecurrenceDraft(
            ComputerDraft(new DateTimeOffset(2026, 8, 13, 23, 30, 0, TimeSpan.FromHours(8)), 120),
            new RecurrencePattern(
                RecurrenceKind.Daily,
                new DateOnly(2026, 8, 13),
                new DateOnly(2026, 8, 14))));
        Assert.True(candidate.Success);

        var rejected = await module.ConfirmRecurrenceAsync(candidate.Value!.CandidateId);

        Assert.False(rejected.Success);
        Assert.Equal("recurrence_computer_conflict", rejected.ErrorCode);
        Assert.Contains("2026-08-13", rejected.Message, StringComparison.Ordinal);
        Assert.Single((await module.GetSnapshotAsync()).Commitments);
        Assert.Empty((await module.GetSnapshotAsync()).RecurrencePlans);
    }

    [Fact]
    public async Task Conflict_between_two_generated_occurrences_writes_nothing()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        var candidate = await module.PrepareRecurrenceAsync(new RecurrenceDraft(
            ComputerDraft(new DateTimeOffset(2026, 8, 13, 23, 30, 0, TimeSpan.FromHours(8)), 1500),
            new RecurrencePattern(
                RecurrenceKind.Daily,
                new DateOnly(2026, 8, 13),
                new DateOnly(2026, 8, 14))));
        var rejected = await module.ConfirmRecurrenceAsync(candidate.Value!.CandidateId);

        Assert.False(rejected.Success);
        Assert.Equal("recurrence_computer_conflict", rejected.ErrorCode);
        Assert.Contains("2026-08-14", rejected.Message, StringComparison.Ordinal);
        Assert.Empty((await module.GetSnapshotAsync()).Commitments);
        Assert.Empty((await module.GetSnapshotAsync()).RecurrencePlans);
    }

    [Fact]
    public async Task Skipping_an_occurrence_preserves_its_identity_and_status_across_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        Guid planId;
        Guid occurrenceId;

        await using (var module = await OpenAsync(database.Path, clock))
        {
            var plan = await CreateThreeDatePlanAsync(module, hour: 10);
            planId = plan.Id;
            occurrenceId = plan.Occurrences[1].CommitmentId;
            var changed = await ChangeAsync(module, new RecurrenceChangeRequest(
                plan.Id,
                occurrenceId,
                RecurrenceChangeKind.Skip,
                RecurrenceChangeScope.ThisOccurrence));

            Assert.True(changed.Success);
            Assert.Equal(3, changed.Value!.Occurrences.Count);
            Assert.Equal(RecurrenceOccurrenceStatus.Skipped,
                changed.Value.Occurrences.Single(x => x.CommitmentId == occurrenceId).Status);
        }

        await using var restarted = await OpenAsync(database.Path, clock);
        var recovered = (await restarted.GetSnapshotAsync()).RecurrencePlans.Single(x => x.Id == planId);
        Assert.Equal(3, recovered.Occurrences.Count);
        Assert.Equal(occurrenceId,
            recovered.Occurrences.Single(x => x.Status == RecurrenceOccurrenceStatus.Skipped).CommitmentId);
        Assert.Equal(CommitmentPhase.Skipped,
            (await restarted.GetSnapshotAsync()).Commitments.Single(x => x.Id == occurrenceId).Phase);
    }

    [Fact]
    public async Task Skipped_computer_occurrence_does_not_observe_activity_or_send_its_start_reminder()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, reminders);
        var prepared = await module.PrepareRecurrenceAsync(new RecurrenceDraft(
            ComputerDraft(Baseline.AddHours(1), 60),
            new RecurrencePattern(
                RecurrenceKind.SelectedDates,
                SelectedDates: [DateOnly.FromDateTime(Baseline.Date)])));
        var plan = (await module.ConfirmRecurrenceAsync(prepared.Value!.CandidateId)).Value!;

        var skipped = await ChangeAsync(module, new RecurrenceChangeRequest(
            plan.Id,
            plan.Occurrences.Single().CommitmentId,
            RecurrenceChangeKind.Skip,
            RecurrenceChangeScope.ThisOccurrence));
        Assert.True(skipped.Success);
        clock.Now = Baseline.AddHours(1);
        await module.TickAsync();

        Assert.Equal(0, activity.ObservationCount);
        Assert.Empty(reminders.Notices);
        Assert.Null((await module.GetSnapshotAsync()).ActiveComputerCommitmentId);
    }

    [Fact]
    public async Task Skip_and_adjust_scopes_have_distinct_observable_reach()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);

        foreach (var (scope, expected) in new[]
                 {
                     (RecurrenceChangeScope.ThisOccurrence, 1),
                     (RecurrenceChangeScope.ThisAndFuture, 2),
                     (RecurrenceChangeScope.EntirePlan, 3)
                 })
        {
            var skipPlan = await CreateThreeDatePlanAsync(module, hour: 10);
            var skipped = (await ChangeAsync(module, new RecurrenceChangeRequest(
                skipPlan.Id,
                skipPlan.Occurrences[1].CommitmentId,
                RecurrenceChangeKind.Skip,
                scope))).Value!;
            Assert.Equal(expected,
                skipped.Occurrences.Count(x => x.Status == RecurrenceOccurrenceStatus.Skipped));

            var adjustPlan = await CreateThreeDatePlanAsync(module, hour: 14);
            var anchor = adjustPlan.Occurrences[1];
            var startsBefore = adjustPlan.Occurrences.ToDictionary(x => x.CommitmentId, x => x.StartAt);
            var adjusted = (await ChangeAsync(module, new RecurrenceChangeRequest(
                adjustPlan.Id,
                anchor.CommitmentId,
                RecurrenceChangeKind.Adjust,
                scope,
                NewStartAt: anchor.StartAt.AddHours(1),
                NewDurationMinutes: 45))).Value!;
            Assert.Equal(expected,
                adjusted.Occurrences.Count(x => x.StartAt == startsBefore[x.CommitmentId].AddHours(1) &&
                                                (x.EndAt - x.StartAt).TotalMinutes == 45));
        }
    }

    [Fact]
    public async Task Started_occurrence_is_immutable_and_whole_plan_adjustment_preserves_history()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);
        var plan = await CreatePlanAsync(module, new RecurrencePattern(
            RecurrenceKind.SelectedDates,
            SelectedDates:
            [
                new DateOnly(2026, 8, 13),
                new DateOnly(2026, 8, 14),
                new DateOnly(2026, 8, 15),
                new DateOnly(2026, 8, 16)
            ]));
        var original = plan.Occurrences.ToDictionary(x => x.CommitmentId, x => x.StartAt);
        clock.Now = plan.Occurrences[0].StartAt;

        var rejected = await module.PrepareRecurrenceChangeAsync(new RecurrenceChangeRequest(
            plan.Id,
            plan.Occurrences[0].CommitmentId,
            RecurrenceChangeKind.Adjust,
            RecurrenceChangeScope.ThisOccurrence,
            NewStartAt: plan.Occurrences[0].StartAt.AddHours(1)));
        Assert.False(rejected.Success);
        Assert.Equal("recurrence_history_immutable", rejected.ErrorCode);

        var adjusted = await ChangeAsync(module, new RecurrenceChangeRequest(
            plan.Id,
            plan.Occurrences[2].CommitmentId,
            RecurrenceChangeKind.Adjust,
            RecurrenceChangeScope.EntirePlan,
            NewStartAt: plan.Occurrences[2].StartAt.AddHours(1)));
        Assert.True(adjusted.Success);
        Assert.Equal(original[plan.Occurrences[0].CommitmentId], adjusted.Value!.Occurrences[0].StartAt);
        Assert.All(adjusted.Value.Occurrences.Skip(1), occurrence =>
            Assert.Equal(original[occurrence.CommitmentId].AddHours(1), occurrence.StartAt));
    }

    [Fact]
    public async Task Recurrence_change_preview_writes_nothing_and_confirm_revalidates_current_time()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);
        var plan = await CreateThreeDatePlanAsync(module, hour: 10);
        var anchor = plan.Occurrences[1];

        var preview = await module.PrepareRecurrenceChangeAsync(new RecurrenceChangeRequest(
            plan.Id,
            anchor.CommitmentId,
            RecurrenceChangeKind.Skip,
            RecurrenceChangeScope.ThisAndFuture));

        Assert.True(preview.Success, preview.Message);
        Assert.Equal(2, preview.Value!.AffectedOccurrences.Count);
        var unchanged = (await module.GetSnapshotAsync()).RecurrencePlans.Single(item => item.Id == plan.Id);
        Assert.All(unchanged.Occurrences, item =>
            Assert.Equal(RecurrenceOccurrenceStatus.Active, item.Status));

        clock.Now = anchor.StartAt;
        var rejected = await module.ConfirmRecurrenceChangeAsync(preview.Value.CandidateId);
        Assert.False(rejected.Success);
        Assert.Equal("recurrence_history_immutable", rejected.ErrorCode);
        var stillUnchanged = (await module.GetSnapshotAsync()).RecurrencePlans.Single(item => item.Id == plan.Id);
        Assert.All(stillUnchanged.Occurrences, item =>
            Assert.Equal(RecurrenceOccurrenceStatus.Active, item.Status));
    }

    [Fact]
    public async Task Moving_a_later_anchor_back_rejects_atomically_when_an_earlier_affected_item_becomes_past()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Baseline);
        await using var module = await OpenAsync(database.Path, clock);
        var plan = await CreateThreeDatePlanAsync(module, hour: 10);
        var before = plan.Occurrences.ToDictionary(item => item.CommitmentId, item => item.StartAt);
        var anchor = plan.Occurrences[2];

        var rejected = await module.PrepareRecurrenceChangeAsync(new RecurrenceChangeRequest(
            plan.Id,
            anchor.CommitmentId,
            RecurrenceChangeKind.Adjust,
            RecurrenceChangeScope.EntirePlan,
            NewStartAt: Baseline.AddHours(1)));

        Assert.False(rejected.Success);
        Assert.Equal("recurrence_history_immutable", rejected.ErrorCode);
        var unchanged = (await module.GetSnapshotAsync()).RecurrencePlans.Single(item => item.Id == plan.Id);
        Assert.All(unchanged.Occurrences, item => Assert.Equal(before[item.CommitmentId], item.StartAt));
    }

    private static async Task<RecurrencePlanView> CreatePlanAsync(
        SupervisionModule module,
        RecurrencePattern pattern)
    {
        var plansBeforePreview = (await module.GetSnapshotAsync()).RecurrencePlans.Count;
        var prepared = await module.PrepareRecurrenceAsync(new RecurrenceDraft(
            OfflineDraft(new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(8)), 60),
            pattern));
        Assert.True(prepared.Success);
        Assert.Equal(plansBeforePreview, (await module.GetSnapshotAsync()).RecurrencePlans.Count);
        var confirmed = await module.ConfirmRecurrenceAsync(prepared.Value!.CandidateId);
        Assert.True(confirmed.Success);
        return confirmed.Value!;
    }

    private static Task<RecurrencePlanView> CreateThreeDatePlanAsync(
        SupervisionModule module,
        int hour) => CreatePlanAsync(module, new RecurrencePattern(
            RecurrenceKind.SelectedDates,
            SelectedDates:
            [
                new DateOnly(2026, 9, 10),
                new DateOnly(2026, 9, 11),
                new DateOnly(2026, 9, 12)
            ]), hour);

    private static async Task<RecurrencePlanView> CreatePlanAsync(
        SupervisionModule module,
        RecurrencePattern pattern,
        int hour)
    {
        var prepared = await module.PrepareRecurrenceAsync(new RecurrenceDraft(
            OfflineDraft(new DateTimeOffset(2026, 9, 10, hour, 0, 0, TimeSpan.FromHours(8)), 60),
            pattern));
        var confirmed = await module.ConfirmRecurrenceAsync(prepared.Value!.CandidateId);
        return confirmed.Value!;
    }

    private static CommitmentTemplateDraft TemplateDraft() => new(
        Name: "交易日志",
        Kind: CommitmentKind.Computer,
        DurationMinutes: 180,
        InputGoal: "整理交易日志",
        OutcomeGoal: "完成当天复盘",
        RelatedAppsOrSites:
        [
            new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")
        ],
        SupervisionMode: SupervisionMode.Interactive,
        ReminderSettings: null,
        ActivityRules:
        [
            new ActivityRule(
                new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"),
                ActivityClassification.Related)
        ],
        RestSettings: new RestSettings(10, 15));

    private static CommitmentDraft ComputerDraft(DateTimeOffset startAt, int durationMinutes) => new(
        CommitmentKind.Computer,
        startAt,
        EndAt: null,
        durationMinutes,
        InputGoal: "整理交易日志",
        OutcomeGoal: null,
        RelatedAppsOrSites:
        [
            new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")
        ],
        SupervisionMode: null,
        ReminderSettings: null);

    private static CommitmentDraft OfflineDraft(DateTimeOffset startAt, int durationMinutes) => new(
        CommitmentKind.Offline,
        startAt,
        EndAt: null,
        durationMinutes,
        InputGoal: "离线复盘",
        OutcomeGoal: null,
        RelatedAppsOrSites: null,
        SupervisionMode: null,
        ReminderSettings: null);

    private static Task<SupervisionModule> OpenAsync(string path, FakeClock clock) =>
        SupervisionModule.OpenAsync(path, clock, new FakeActivitySource(), new FakeReminderSink());
}
