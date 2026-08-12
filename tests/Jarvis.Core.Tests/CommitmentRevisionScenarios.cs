using Jarvis.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class CommitmentRevisionScenarios
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Confirmed_revision_applies_forward_and_preserves_both_versions_after_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-10));
        Guid id;

        await using (var module = await OpenAsync(database.Path, clock))
        {
            var original = await ConfirmAsync(module, Draft(Start, 60, "写初稿"));
            id = original.Id;
            Assert.Equal(1, original.Version);

            clock.Now = Start.AddMinutes(15);
            var preview = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
                id,
                original.Version,
                Draft(Start, 90, "写完初稿") with
                {
                    ReminderSettings = new ReminderSettings(true, 8, 20, 20, 3)
                },
                "原定一小时不够，需要延长并降低打扰"));

            Assert.True(preview.Success, preview.Message);
            Assert.Equal(1, preview.Value!.FromVersion);
            Assert.Equal(2, preview.Value.ToVersion);
            Assert.Equal(clock.Now, preview.Value.EffectiveFrom);
            Assert.Equal("写初稿", preview.Value.Before.InputGoal);
            Assert.Equal("写完初稿", preview.Value.After.InputGoal);

            var confirmed = await module.ConfirmCommitmentRevisionAsync(preview.Value.CandidateId);
            Assert.True(confirmed.Success, confirmed.Message);
            Assert.Equal(2, confirmed.Value!.Version);
            Assert.Equal(Start.AddMinutes(90), confirmed.Value.EndAt);
        }

        await using var restarted = await OpenAsync(database.Path, clock);
        var current = Assert.Single((await restarted.GetSnapshotAsync()).Commitments);
        Assert.Equal(id, current.Id);
        Assert.Equal(2, current.Version);
        Assert.Equal("写完初稿", current.InputGoal);
        var history = await restarted.GetCommitmentHistoryAsync(id);
        Assert.True(history.Success, history.Message);
        Assert.Equal([1, 2], history.Value!.Versions.Select(item => item.Version));
        Assert.Equal("建立工作承诺", history.Value.Versions[0].Reason);
        Assert.Equal("原定一小时不够，需要延长并降低打扰", history.Value.Versions[1].Reason);
        Assert.Equal(Start.AddMinutes(15), history.Value.Versions[1].EffectiveFrom);
        Assert.Equal("写初稿", history.Value.Versions[0].Snapshot.InputGoal);
    }

    [Fact]
    public async Task History_uses_one_read_snapshot_while_a_revision_commits_concurrently()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var original = await ConfirmAsync(module, Draft(Start, 60, "snapshot v1"));
        clock.Now = Start.AddMinutes(10);
        var revision = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, original.Version, Draft(Start, 90, "snapshot v2"), "concurrent revision"));
        Assert.True(revision.Success, revision.Message);

        await using (var setup = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await setup.OpenAsync();
            await using var wal = setup.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", (string)(await wal.ExecuteScalarAsync())!);
        }

        var store = new SqliteCommitmentStore(database.Path);
        Task<SupervisionResult<CommitmentView>>? confirmation = null;
        var history = await store.ReadHistoryForTestAsync(
            original.Id,
            cancellationToken =>
            {
                confirmation = module.ConfirmCommitmentRevisionAsync(
                    revision.Value!.CandidateId, cancellationToken);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(history);
        Assert.Equal(1, history.CurrentVersion);
        Assert.Collection(history.Versions, version => Assert.Equal(1, version.Version));
        var confirmed = await confirmation!;
        Assert.True(confirmed.Success, confirmed.Message);
        var after = (await module.GetCommitmentHistoryAsync(original.Id)).Value!;
        Assert.Equal(2, after.CurrentVersion);
        Assert.Equal([1, 2], after.Versions.Select(version => version.Version));
    }

    [Fact]
    public async Task Revision_requires_reason_and_cannot_move_an_active_start_backwards()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var original = await ConfirmAsync(module, Draft(Start, 60, "写初稿"));
        clock.Now = Start.AddMinutes(10);

        var noReason = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, 1, Draft(Start, 90, "写完初稿"), "   "));
        Assert.False(noReason.Success);
        Assert.Equal("revision_reason_required", noReason.ErrorCode);

        var movedStart = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id,
            1,
            Draft(Start.AddMinutes(-30), 120, "写完初稿"),
            "把已经发生的开始时间倒改"));
        Assert.False(movedStart.Success);
        Assert.Equal("revision_history_immutable", movedStart.ErrorCode);

        var history = await module.GetCommitmentHistoryAsync(original.Id);
        Assert.Single(history.Value!.Versions);
    }

    [Fact]
    public async Task Stale_revision_candidate_and_stale_action_are_rejected_without_writes()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-10));
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var original = await ConfirmAsync(module, Draft(Start, 60, "第一版"));

        var first = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, 1, Draft(Start, 70, "候选甲"), "采用候选甲"));
        var second = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, 1, Draft(Start, 80, "候选乙"), "采用候选乙"));
        Assert.True(second.Success);
        Assert.Equal("candidate_not_found",
            (await module.ConfirmCommitmentRevisionAsync(first.Value!.CandidateId)).ErrorCode);
        Assert.True((await module.ConfirmCommitmentRevisionAsync(second.Value!.CandidateId)).Success);

        clock.Now = Start.AddMinutes(10);
        activity.Next = activity.Next with
        {
            ForegroundProcess = "unknown.exe",
            ObservedAt = clock.Now
        };
        await module.TickAsync();
        var active = (await module.GetSnapshotAsync()).ActiveSupervision!;
        Assert.Equal(2, active.CommitmentVersion);
        var stale = await module.ClassifyActivityAsync(
            original.Id,
            expectedVersion: 1,
            active.ActionableTarget!,
            active.ActivityStateStartedAt!.Value,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment);
        Assert.False(stale.Success);
        Assert.Equal("commitment_version_stale", stale.ErrorCode);
        Assert.Empty((await module.GetCommitmentHistoryAsync(original.Id)).Value!.Corrections);
    }

    [Fact]
    public async Task Revision_preview_becomes_stale_when_a_commitment_rule_changes_before_confirmation()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var original = await ConfirmAsync(module, Draft(Start, 60, "original rules"));

        clock.Now = Start;
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "research.exe");
        activity.Next = activity.Next with
        {
            ForegroundProcess = target.Value,
            ObservedAt = clock.Now
        };
        await module.TickAsync();
        var active = (await module.GetSnapshotAsync()).ActiveSupervision!;
        var revision = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, original.Version, Draft(Start, 90, "revised goal"), "extend the work"));
        Assert.True(revision.Success, revision.Message);

        clock.Now = Start.AddSeconds(1);
        var classified = await module.ClassifyActivityAsync(
            original.Id,
            original.Version,
            target,
            active.ActivityStateStartedAt!.Value,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment,
            "research is part of this commitment");
        Assert.True(classified.Success, classified.Message);

        var stale = await module.ConfirmCommitmentRevisionAsync(revision.Value!.CandidateId);

        Assert.False(stale.Success);
        Assert.Equal("commitment_version_stale", stale.ErrorCode);
        var store = new SqliteCommitmentStore(database.Path);
        Assert.Equal(
            ActivityClassification.Related,
            await store.FindActivityRuleAsync(
                ActivityRuleScope.Commitment, original.Id, target, CancellationToken.None));
        var history = (await module.GetCommitmentHistoryAsync(original.Id)).Value!;
        Assert.Equal(1, history.CurrentVersion);
        Assert.Single(history.Versions);
    }

    [Fact]
    public async Task Any_new_candidate_invalidates_an_older_revision_candidate()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-10));
        await using var module = await OpenAsync(database.Path, clock);
        var original = await ConfirmAsync(module, Draft(Start, 60, "original"));
        var revision = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, 1, Draft(Start, 70, "revision"), "prepare a revision"));
        Assert.True(revision.Success, revision.Message);

        Assert.True((await module.PrepareAsync(Draft(Start.AddHours(2), 30, "new candidate"))).Success);

        var stale = await module.ConfirmCommitmentRevisionAsync(revision.Value!.CandidateId);
        Assert.False(stale.Success);
        Assert.Equal("candidate_not_found", stale.ErrorCode);
        Assert.Single((await module.GetCommitmentHistoryAsync(original.Id)).Value!.Versions);
    }

    [Fact]
    public async Task Persisted_activity_segments_and_responses_keep_the_version_that_applied()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var reminders = new FakeReminderSink();
        await using var module = await SupervisionModule.OpenAsync(database.Path, clock, activity, reminders);
        var original = await ConfirmAsync(module, Draft(Start, 60, "有监督历史"));

        activity.Next = activity.Next with
        {
            ForegroundProcess = "unknown.exe",
            ObservedAt = clock.Now
        };
        await module.TickAsync();
        await module.RecordReturnIntentAsync(original.Id);

        clock.Now = Start.AddMinutes(10);
        var revision = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            original.Id, 1, Draft(Start, 90, "修订后"), "延长承诺"));
        Assert.True((await module.ConfirmCommitmentRevisionAsync(revision.Value!.CandidateId)).Success);

        activity.Next = activity.Next with
        {
            ForegroundProcess = "Excel.exe",
            ObservedAt = clock.Now
        };
        await module.TickAsync();
        clock.Now = clock.Now.AddMinutes(1);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await module.TickAsync();
        clock.Now = clock.Now.AddMinutes(1);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await module.TickAsync();

        var history = (await module.GetCommitmentHistoryAsync(original.Id)).Value!;
        Assert.Contains(history.ActivitySegments, item =>
            item.CommitmentVersion == 1 && item.Target!.Value == "unknown.exe");
        Assert.Contains(history.ActivitySegments, item =>
            item.CommitmentVersion == 2 && item.Target!.Value == "Excel.exe");
        Assert.Contains(history.Responses, item =>
            item.CommitmentVersion == 1 && item.Kind == "return_intent");
    }

    [Fact]
    public async Task Explicit_correction_reclassifies_only_the_bound_segment_and_keeps_original_fact()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmAsync(module, Draft(Start, 60, "纠正误判"));
        activity.Next = activity.Next with { ForegroundProcess = "research.exe", ObservedAt = clock.Now };
        await module.TickAsync();
        var captured = (await module.GetSnapshotAsync()).ActiveSupervision!;
        clock.Now = Start.AddMinutes(3);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await module.TickAsync();

        var corrected = await module.ClassifyActivityAsync(
            commitment.Id, commitment.Version, captured.ActionableTarget!,
            captured.ActivityStateStartedAt!.Value, ActivityClassification.Related,
            ActivityRuleScope.Commitment, "这是查资料，不是分心");

        Assert.True(corrected.Success, corrected.Message);
        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        var correction = Assert.Single(history.Corrections);
        Assert.NotNull(correction.ActivitySegmentId);
        var segment = Assert.Single(history.ActivitySegments);
        Assert.Equal(ActivityClassification.Unknown, segment.OriginalClassification);
        Assert.Equal(ActivityClassification.Related, segment.EffectiveClassification);
        Assert.Equal("这是查资料，不是分心", segment.CorrectionNote);
        Assert.Equal(segment.Id, correction.ActivitySegmentId);
    }

    [Fact]
    public async Task Revision_invalidates_old_transient_prompt_and_marker_but_preserves_deviation_history()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var commitment = await ConfirmAsync(module, Draft(Start, 60, "version boundary"));
        var store = new SqliteCommitmentStore(database.Path);
        var deviationStartedAt = Start.AddMinutes(1);
        var runtime = new StoredSupervisionRuntime(
            commitment.Id,
            ActivityClassification.Distracting,
            new CommitmentTarget(CommitmentTargetKind.Application, "chat.exe"),
            deviationStartedAt,
            IsIdle: true,
            IdleStartedAt: deviationStartedAt,
            DeviationStartedAt: deviationStartedAt,
            CountedDeviation: TimeSpan.FromMinutes(8),
            DeviationCountingSince: Start.AddMinutes(9),
            DeviationReason: DeviationReason.InteractiveIdle,
            LocalReminderSentAt: Start.AddMinutes(6),
            ReminderMarkerActive: true,
            ReturnIntentAt: Start.AddMinutes(7),
            PendingPrompt: SupervisionPromptKind.ConfirmRest,
            LastObservedAt: Start.AddMinutes(9),
            RestPromptedForIdleStart: deviationStartedAt);
        await store.PersistRuntimeAndRemindersAsync(
            runtime,
            commitment.Version,
            [new ReminderNotice(
                commitment.Id,
                "old-version reminder",
                Start.AddMinutes(6),
                ReminderKind.RestQuestion,
                Guid.NewGuid(),
                Start.AddMinutes(20),
                PersistentMarker: true,
                CommitmentVersion: 1)],
            CancellationToken.None);
        await store.AppendResponseAsync(
            commitment.Id, 1, "return_intent", Start.AddMinutes(7), null, CancellationToken.None);

        clock.Now = Start.AddMinutes(10);
        var preview = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            commitment.Id, 1, Draft(Start, 90, "revised version"), "change supervision forward"));
        Assert.True(preview.Success, preview.Message);
        var confirmed = await module.ConfirmCommitmentRevisionAsync(preview.Value!.CandidateId);
        Assert.True(confirmed.Success, confirmed.Message);

        var after = await store.ReadRuntimeAsync(commitment.Id, CancellationToken.None);
        Assert.Null(after.PendingPrompt);
        Assert.False(after.ReminderMarkerActive);
        Assert.Null(after.LocalReminderSentAt);
        Assert.Null(after.UnknownPromptedForStart);
        Assert.Null(after.RestPromptedForIdleStart);
        Assert.Equal(deviationStartedAt, after.DeviationStartedAt);
        Assert.Equal(TimeSpan.FromMinutes(8), after.CountedDeviation);
        Assert.Equal(Start.AddMinutes(7), after.ReturnIntentAt);

        var oldPromptAction = await module.RespondToRestPromptAsync(commitment.Id, 2, isResting: true);
        Assert.False(oldPromptAction.Success);
        Assert.Equal("rest_prompt_not_active", oldPromptAction.ErrorCode);
        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        Assert.Contains(history.Reminders, item => item.CommitmentVersion == 1);
        Assert.Contains(history.Responses, item => item.CommitmentVersion == 1);
    }

    [Fact]
    public async Task Activity_segment_is_split_exactly_at_a_revision_boundary()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var commitment = await ConfirmAsync(module, Draft(Start, 60, "segment boundary"));

        clock.Now = Start.AddMinutes(10).AddSeconds(30);
        var preview = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            commitment.Id, 1, Draft(Start, 90, "segment boundary v2"), "split at confirmation"));
        Assert.True((await module.ConfirmCommitmentRevisionAsync(preview.Value!.CandidateId)).Success);

        var store = new SqliteCommitmentStore(database.Path);
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe");
        await store.AppendActivitySegmentAsync(
            commitment.Id,
            commitmentVersion: 1,
            new ActivityObservation(
                ActivityAvailability.Available,
                true,
                target.Value,
                Start.AddMinutes(11)),
            target,
            ActivityClassification.Related,
            isIdle: false,
            deviationReason: null,
            Start.AddMinutes(10),
            Start.AddMinutes(11),
            CancellationToken.None);

        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        Assert.Collection(
            history.ActivitySegments,
            first =>
            {
                Assert.Equal(1, first.CommitmentVersion);
                Assert.Equal(Start.AddMinutes(10), first.StartAt);
                Assert.Equal(Start.AddMinutes(10).AddSeconds(30), first.EndAt);
            },
            second =>
            {
                Assert.Equal(2, second.CommitmentVersion);
                Assert.Equal(Start.AddMinutes(10).AddSeconds(30), second.StartAt);
                Assert.Equal(Start.AddMinutes(11), second.EndAt);
            });
    }

    [Fact]
    public async Task Segment_split_at_revision_uses_each_versions_activity_rule()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        var activity = new FakeActivitySource();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var observedTarget = new CommitmentTarget(CommitmentTargetKind.Application, "chat");
        var configuredTarget = new CommitmentTarget(CommitmentTargetKind.Application, "chat.exe");
        var originalDraft = Draft(Start, 60, "versioned rules") with
        {
            RelatedAppsOrSites =
            [
                new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")
            ],
            ActivityRules = [new ActivityRule(configuredTarget, ActivityClassification.Distracting)]
        };
        var commitment = await ConfirmAsync(module, originalDraft);

        clock.Now = Start;
        activity.Next = activity.Next with
        {
            ForegroundProcess = observedTarget.Value,
            ObservedAt = clock.Now
        };
        await module.TickAsync();
        Assert.Equal(
            ActivityClassification.Distracting,
            (await module.GetSnapshotAsync()).ActiveSupervision!.Classification);

        clock.Now = Start.AddSeconds(30);
        var revision = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            commitment.Id,
            commitment.Version,
            originalDraft with
            {
                ActivityRules = [new ActivityRule(configuredTarget, ActivityClassification.Related)]
            },
            "chat is part of the revised work"));
        Assert.True(revision.Success, revision.Message);
        Assert.True((await module.ConfirmCommitmentRevisionAsync(revision.Value!.CandidateId)).Success);

        clock.Now = Start.AddMinutes(1);
        activity.Next = activity.Next with { ObservedAt = clock.Now };
        await module.TickAsync();

        var segments = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!.ActivitySegments;
        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1, first.CommitmentVersion);
                Assert.Equal(Start, first.StartAt);
                Assert.Equal(Start.AddSeconds(30), first.EndAt);
                Assert.Equal(ActivityClassification.Distracting, first.OriginalClassification);
                Assert.Equal(ActivityClassification.Distracting, first.EffectiveClassification);
            },
            second =>
            {
                Assert.Equal(2, second.CommitmentVersion);
                Assert.Equal(Start.AddSeconds(30), second.StartAt);
                Assert.Equal(Start.AddMinutes(1), second.EndAt);
                Assert.Equal(ActivityClassification.Related, second.OriginalClassification);
                Assert.Equal(ActivityClassification.Related, second.EffectiveClassification);
            });
    }

    [Fact]
    public async Task Correction_after_revision_binds_to_the_current_version_segment_without_rewriting_v1()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var commitment = await ConfirmAsync(module, Draft(Start, 60, "correction boundary"));
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "research.exe");
        var activityStartedAt = Start.AddMinutes(10);
        var revisionAt = Start.AddMinutes(10).AddSeconds(30);
        clock.Now = revisionAt;
        var preview = await module.PrepareCommitmentRevisionAsync(new CommitmentRevisionDraft(
            commitment.Id, 1, Draft(Start, 90, "correction boundary v2"), "change rules forward"));
        Assert.True((await module.ConfirmCommitmentRevisionAsync(preview.Value!.CandidateId)).Success);

        var store = new SqliteCommitmentStore(database.Path);
        await store.AppendActivitySegmentAsync(
            commitment.Id,
            commitmentVersion: 1,
            new ActivityObservation(ActivityAvailability.Available, true, target.Value, Start.AddMinutes(11)),
            target,
            ActivityClassification.Unknown,
            isIdle: false,
            deviationReason: DeviationReason.UnknownActivity,
            activityStartedAt,
            Start.AddMinutes(11),
            CancellationToken.None);
        await store.WriteRuntimeAsync(new StoredSupervisionRuntime(
            commitment.Id,
            ActivityClassification.Unknown,
            target,
            activityStartedAt,
            DeviationStartedAt: activityStartedAt,
            CountedDeviation: TimeSpan.FromMinutes(1),
            DeviationCountingSince: Start.AddMinutes(11),
            DeviationReason: DeviationReason.UnknownActivity,
            LastObservedAt: Start.AddMinutes(11)), CancellationToken.None);
        clock.Now = Start.AddMinutes(11);

        var corrected = await module.ClassifyActivityAsync(
            commitment.Id,
            expectedVersion: 2,
            target,
            activityStartedAt,
            ActivityClassification.Related,
            ActivityRuleScope.Commitment,
            "current version research is related");

        Assert.True(corrected.Success, corrected.Message);
        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        var correction = Assert.Single(history.Corrections);
        Assert.Equal(revisionAt, correction.EffectiveFrom);
        Assert.Equal(2, correction.CommitmentVersion);
        var first = Assert.Single(history.ActivitySegments, item => item.CommitmentVersion == 1);
        var second = Assert.Single(history.ActivitySegments, item => item.CommitmentVersion == 2);
        Assert.Equal(ActivityClassification.Unknown, first.EffectiveClassification);
        Assert.Equal(ActivityClassification.Related, second.EffectiveClassification);
        Assert.Equal(second.Id, correction.ActivitySegmentId);
    }

    [Fact]
    public async Task Full_history_returns_more_than_the_twenty_recent_corrections()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start.AddMinutes(-5));
        await using var module = await OpenAsync(database.Path, clock);
        var commitment = await ConfirmAsync(module, Draft(Start, 60, "full correction history"));
        var store = new SqliteCommitmentStore(database.Path);
        var target = new CommitmentTarget(CommitmentTargetKind.Application, "history.exe");
        var runtime = new StoredSupervisionRuntime(commitment.Id);
        for (var index = 0; index < 21; index++)
        {
            var at = Start.AddSeconds(index);
            await store.PersistClassificationAsync(
                [],
                new ActivityCorrectionView(
                    target,
                    ActivityClassification.Unknown,
                    ActivityClassification.Related,
                    at,
                    at,
                    ActivityRuleScope.Commitment,
                    $"correction {index}",
                    1),
                expectedVersion: 1,
                pendingSegment: null,
                runtime,
                notice: null,
                CancellationToken.None);
        }

        var history = (await module.GetCommitmentHistoryAsync(commitment.Id)).Value!;
        Assert.Equal(21, history.Corrections.Count);
        Assert.Equal("correction 0", history.Corrections[0].Note);
        Assert.Equal("correction 20", history.Corrections[^1].Note);
        var active = await store.ReadRecentCorrectionsAsync(commitment.Id, CancellationToken.None);
        Assert.Equal(20, active.Count);
        Assert.Equal("correction 20", active[0].Note);
    }

    private static async Task<CommitmentView> ConfirmAsync(
        SupervisionModule module,
        CommitmentDraft draft)
    {
        var prepared = await module.PrepareAsync(draft);
        Assert.True(prepared.Success, prepared.Message);
        var confirmed = await module.ConfirmAsync(prepared.Value!.CandidateId);
        Assert.True(confirmed.Success, confirmed.Message);
        return confirmed.Value!;
    }

    private static CommitmentDraft Draft(
        DateTimeOffset startAt,
        int durationMinutes,
        string goal) => new(
        CommitmentKind.Computer,
        startAt,
        EndAt: null,
        durationMinutes,
        goal,
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
                new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"),
                ActivityClassification.Related)
        ],
        RestSettings: new RestSettings(10, 15));

    private static Task<SupervisionModule> OpenAsync(string path, FakeClock clock) =>
        SupervisionModule.OpenAsync(path, clock, new FakeActivitySource(), new FakeReminderSink());
}
