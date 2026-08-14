using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class CompanionWorkflowScenarios
{
    private static readonly DateTimeOffset Start = new(2026, 8, 14, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mobile_escalation_starts_at_twenty_minutes_replaces_old_card_and_stops_at_three()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        var local = new FakeReminderSink();
        await using var supervision = await SupervisionModule.OpenAsync(database.Path, clock, activity, local);
        var commitment = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());

        Assert.True((await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(
            Enabled: true, CliPath: "lark-cli", Profile: "jarvis-t04"))).Success);
        Assert.True((await companion.DispatchAsync(new BindWorktimeUserCommand(
            "evt-bind", "ou_test", "oc_test", "om_bind"))).Success);

        for (var minute = 20; minute <= 80; minute += 20)
        {
            clock.Now = Start.AddMinutes(minute);
            activity.Next = Distracting(clock.Now);
            await supervision.TickAsync();
            await companion.AdvanceAsync();
        }

        Assert.Equal(3, channel.Sent.Count);
        Assert.Equal([1, 2, 3], channel.Sent.Select(card => card.Sequence));
        Assert.Equal(channel.Sent.Take(2).Select(card => card.CardId), channel.Invalidated);
        Assert.All(channel.Sent, card =>
        {
            Assert.Equal(commitment.Id, card.CommitmentId);
            Assert.DoesNotContain("ChatGPT", card.PrivacyPreview, StringComparison.OrdinalIgnoreCase);
        });

        var stale = await companion.DispatchAsync(new HandleWorktimeActionCommand(
            "evt-old", "ou_test", channel.Sent[0].CardId, commitment.Id, commitment.Version,
            WorktimeActionKind.ReturnNow, null));
        Assert.False(stale.Success);
        Assert.Equal("mobile_card_stale", stale.ErrorCode);

        var accepted = await companion.DispatchAsync(new HandleWorktimeActionCommand(
            "evt-current", "ou_test", channel.Sent[^1].CardId, commitment.Id, commitment.Version,
            WorktimeActionKind.ReturnNow, null));
        Assert.True(accepted.Success);
        Assert.Contains(channel.Sent[^1].CardId, channel.Invalidated);
    }

    [Fact]
    public async Task Planned_or_early_end_enters_review_and_raw_text_survives_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmActiveComputerAsync(supervision, clock, activity);
        await using (var companion = await OpenCompanionAsync(database.Path, supervision, clock))
        {
            var ended = await companion.DispatchAsync(
                new EndCommitmentEarlyCommand(commitment.Id, commitment.Version));
            Assert.True(ended.Success);
            var endedSnapshot = await supervision.GetSnapshotAsync();
            Assert.Null(endedSnapshot.ActiveComputerCommitmentId);
            Assert.Equal(CommitmentPhase.AwaitingReview, Assert.Single(endedSnapshot.Commitments).Phase);
            var pending = Assert.Single((await companion.SnapshotAsync()).CommitmentReviews);
            Assert.Equal(CommitmentReviewState.Pending, pending.State);

            var replacement = await supervision.PrepareAsync(new CommitmentDraft(
                CommitmentKind.Computer, clock.Now, null, 30, "接续工作", null,
                [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
                Jarvis.Contracts.SupervisionMode.Interactive, null));
            Assert.True(replacement.Success, replacement.Message);
            Assert.True((await supervision.ConfirmAsync(replacement.Value!.CandidateId)).Success);

            var reviewed = await companion.DispatchAsync(new SubmitCommitmentReviewCommand(
                commitment.Id, "完成了主要部分，最后一段留到明天。", CompletionAssessment.Partial));
            Assert.True(reviewed.Success);
        }

        await using var restarted = await OpenCompanionAsync(database.Path, supervision, clock);
        var persisted = Assert.Single((await restarted.SnapshotAsync()).CommitmentReviews);
        Assert.Equal(CommitmentReviewState.Completed, persisted.State);
        Assert.Equal("完成了主要部分，最后一段留到明天。", persisted.RawText);
        Assert.Equal(CompletionAssessment.Partial, persisted.Assessment);
    }

    [Fact]
    public async Task Daily_review_is_pending_offline_asks_one_question_at_a_time_and_only_follows_up_once()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 59, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await using var companion = await OpenCompanionAsync(database.Path, supervision, clock);

        Assert.True((await companion.DispatchAsync(
            new ConfigureDailyReviewCommand(new TimeOnly(23, 0)))).Success);
        clock.Now = clock.Now.AddMinutes(2);
        await companion.AdvanceAsync();
        var due = (await companion.SnapshotAsync()).DailyReview;
        Assert.Equal(ReviewSessionState.Pending, due.State);
        Assert.False(due.MobileInviteSent);

        Assert.True((await companion.DispatchAsync(new StartDailyReviewCommand())).Success);
        var first = (await companion.SnapshotAsync()).DailyReview;
        Assert.Equal(ReviewQuestionKind.WhatWentWell, first.CurrentQuestion);
        Assert.Contains("Core 客观事实", first.FactsSummary, StringComparison.Ordinal);
        Assert.Contains("休息", first.FactsSummary, StringComparison.Ordinal);
        Assert.Contains("已确认完成结果", first.FactsSummary, StringComparison.Ordinal);
        Assert.True((await companion.DispatchAsync(
            new RespondDailyReviewCommand(first.SessionId!.Value, "整理资料的做法有效。"))).Success);
        var second = (await companion.SnapshotAsync()).DailyReview;
        Assert.Equal(ReviewQuestionKind.WhatWentPoorly, second.CurrentQuestion);

        Assert.True((await companion.DispatchAsync(new SnoozeDailyReviewCommand(30))).Success);
        clock.Now = clock.Now.AddMinutes(30);
        await companion.AdvanceAsync();
        Assert.True((await companion.SnapshotAsync()).DailyReview.FollowUpUsed);
        var repeatedSnooze = await companion.DispatchAsync(new SnoozeDailyReviewCommand(30));
        Assert.False(repeatedSnooze.Success);
        Assert.Equal("daily_follow_up_exhausted", repeatedSnooze.ErrorCode);
        clock.Now = clock.Now.AddMinutes(30);
        await companion.AdvanceAsync();
        Assert.True((await companion.SnapshotAsync()).DailyReview.FollowUpUsed);
    }

    [Fact]
    public async Task Cycle_review_uses_traceable_aggregates_and_keeps_one_to_three_confirmed_focuses()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await using var companion = await OpenCompanionAsync(database.Path, supervision, clock);

        Assert.True((await companion.DispatchAsync(new ConfigureCycleReviewCommand(
            DateOnly.FromDateTime(clock.Now.LocalDateTime.Date), 14, new TimeOnly(20, 0)))).Success);
        clock.Now = clock.Now.AddDays(14).AddHours(8);
        await companion.AdvanceAsync();
        Assert.True((await companion.DispatchAsync(new StartCycleReviewCommand())).Success);
        var review = (await companion.SnapshotAsync()).CycleReview;
        Assert.Equal(ReviewSessionState.InProgress, review.State);
        Assert.NotNull(review.Trends);
        Assert.DoesNotContain("自律", review.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.True((await companion.DispatchAsync(new ConfirmCycleFocusesCommand(
            ["减少无目标浏览", "把重要承诺放到上午"]))).Success);
        Assert.Equal(2, (await companion.SnapshotAsync()).CycleReview.ConfirmedFocuses.Count);
    }

    [Fact]
    public async Task Cloud_ai_uses_credential_manager_budget_gates_and_never_writes_full_key_to_sqlite()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var provider = new FakeAiProvider { NextUsage = new AiTokenUsage(1_000, 1_000, 0) };
        var credentials = new FakeCredentialStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        const string key = "sk-this-must-never-enter-sqlite-9876";

        Assert.True((await companion.DispatchAsync(new SaveAiCredentialCommand(key))).Success);
        var status = (await companion.SnapshotAsync()).Ai;
        Assert.Equal("deepseek-v4-flash", status.Model);
        Assert.Equal("9876", status.CredentialLastFour);

        var chat = await companion.DispatchAsync(new RequestAiChatCommand("你好，帮我简短整理今天的重点。"));
        Assert.True(chat.Success);
        Assert.Single((await companion.SnapshotAsync()).RecentAiRequests);
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database.Path)));

        provider.EstimatedCostCny = 1.01m;
        var needsApproval = await companion.DispatchAsync(new RequestAiChatCommand("进行较长分析"));
        Assert.False(needsApproval.Success);
        Assert.Equal("ai_cost_confirmation_required", needsApproval.ErrorCode);
    }

    [Fact]
    public async Task Cloud_ai_hard_cap_resumes_only_after_the_user_explicitly_raises_it()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider();
        var credentials = new FakeCredentialStore();
        await using (var companion = await CompanionModule.OpenAsync(
                         database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials))
        {
            await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-1234"));
            var store = new SqliteCompanionStore(database.Path);
            Assert.True(await store.TryReserveAiRequestAsync(new AiRequestRecordView(
                Guid.NewGuid(), clock.Now, AiRequestPurpose.BasicChat, "DeepSeek", "deepseek-v4-flash",
                0, 0, 0, "test", 30m, false), CancellationToken.None));

            var blocked = await companion.DispatchAsync(new RequestAiChatCommand("先不要调用云端"));
            Assert.False(blocked.Success);
            Assert.Equal("ai_monthly_cap_reached", blocked.ErrorCode);
            var atLimit = await companion.SnapshotAsync();
            Assert.Equal(30m, atLimit.Ai.MonthlyHardCapCny);
            Assert.True(atLimit.Ai.Alert15Reached);
            Assert.True(atLimit.Ai.Alert24Reached);
            Assert.Equal(0, provider.CallCount);

            Assert.True((await companion.DispatchAsync(new SetAiMonthlyHardCapCommand(35m))).Success);
            var resumed = await companion.DispatchAsync(new RequestAiChatCommand("现在可以调用云端"));
            Assert.True(resumed.Success, resumed.Message);
            Assert.Equal(1, provider.CallCount);
        }

        await using var restarted = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        Assert.Equal(35m, (await restarted.SnapshotAsync()).Ai.MonthlyHardCapCny);
    }

    [Fact]
    public async Task Cloud_ai_processing_state_is_visible_while_the_provider_call_is_in_flight()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider
        {
            Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, new FakeCredentialStore());
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-1234"));

        var pending = companion.DispatchAsync(new RequestAiChatCommand("等待云端回复"));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await companion.SnapshotAsync()).Ai.IsRequestInProgress);
        provider.Release.SetResult(true);
        Assert.True((await pending).Success);
        Assert.False((await companion.SnapshotAsync()).Ai.IsRequestInProgress);
    }

    [Fact]
    public async Task Natural_language_creates_only_a_candidate_then_core_confirms_it()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var provider = new FakeAiProvider
        {
            NextCandidate = new NaturalLanguageOperationCandidate(
                Guid.NewGuid(),
                CandidateOperationKind.CreateCommitment,
                "明天下午三点监督我写报告一小时，只允许 Word。",
                new CommitmentDraft(
                    Kind: CommitmentKind.Computer,
                    StartAt: Start.AddDays(1).AddHours(6),
                    EndAt: null,
                    DurationMinutes: 60,
                    InputGoal: "写报告",
                    OutcomeGoal: "形成报告初稿",
                    RelatedAppsOrSites:
                        [new CommitmentTarget(CommitmentTargetKind.Application, "winword.exe")],
                    SupervisionMode: Jarvis.Contracts.SupervisionMode.Interactive,
                    ReminderSettings: null),
                null,
                "明天 15:00–16:00 · Word · 交互型")
        };
        var credentials = new FakeCredentialStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-1234"));

        var interpreted = await companion.DispatchAsync(new InterpretNaturalLanguageCommand(
            "明天下午三点监督我写报告一小时，只允许 Word。", CandidateSource.Desktop));
        Assert.True(interpreted.Success);
        Assert.Empty((await supervision.GetSnapshotAsync()).Commitments);
        var pending = (await companion.SnapshotAsync()).PendingCandidate;
        Assert.NotNull(pending);
        Assert.NotNull(pending.Commitment!.EndAt);
        Assert.Equal(20, pending.Commitment.ReminderSettings!.FirstMobileDeviationMinutes);
        Assert.Contains("手机 20/20 分钟", pending.Summary, StringComparison.Ordinal);

        var confirmed = await companion.DispatchAsync(new ConfirmNaturalLanguageCandidateCommand(
            interpreted.Candidate!.CandidateId));
        Assert.True(confirmed.Success);
        Assert.Single((await supervision.GetSnapshotAsync()).Commitments);
        Assert.Null((await companion.SnapshotAsync()).PendingCandidate);
    }

    [Fact]
    public async Task Recovery_cancels_the_old_card_and_a_new_deviation_restarts_at_sequence_one()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await ConfirmActiveComputerAsync(supervision, clock, activity);
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind", "ou_test", "oc", "om"));

        clock.Now = Start.AddMinutes(20);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.Single(channel.Sent);

        clock.Now = Start.AddMinutes(21);
        activity.Next = Related(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        clock.Now = Start.AddMinutes(23);
        activity.Next = Related(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.Contains(channel.Sent[0].CardId, channel.Invalidated);

        clock.Now = Start.AddMinutes(24);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        clock.Now = Start.AddMinutes(44);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();

        Assert.Equal([1, 1], channel.Sent.Select(card => card.Sequence));
        Assert.NotEqual(channel.Sent[0].DeviationStartedAt, channel.Sent[1].DeviationStartedAt);
    }

    [Fact]
    public async Task Daily_review_invites_once_when_online_and_requires_one_to_three_adjustments()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 59, 30, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind", "ou_test", "oc", "om"));
        await companion.DispatchAsync(new ConfigureDailyReviewCommand(new TimeOnly(23, 0)));

        await companion.AdvanceAsync();
        Assert.Empty(channel.DailyReviewInvitations);
        clock.Now = clock.Now.AddSeconds(31);
        await companion.AdvanceAsync();
        await companion.AdvanceAsync();
        Assert.Single(channel.DailyReviewInvitations);
        Assert.True((await companion.SnapshotAsync()).DailyReview.MobileInviteSent);

        Assert.True((await companion.DispatchAsync(new StartDailyReviewCommand())).Success);
        var review = (await companion.SnapshotAsync()).DailyReview;
        foreach (var answer in new[] { "做得好", "待改善", "原因" })
        {
            Assert.True((await companion.DispatchAsync(
                new RespondDailyReviewCommand(review.SessionId!.Value, answer))).Success);
            review = (await companion.SnapshotAsync()).DailyReview;
        }
        Assert.Equal(ReviewQuestionKind.TomorrowAdjustments, review.CurrentQuestion);
        var tooMany = await companion.DispatchAsync(new RespondDailyReviewCommand(
            review.SessionId!.Value, "一\n二\n三\n四"));
        Assert.False(tooMany.Success);
        var accepted = await companion.DispatchAsync(new RespondDailyReviewCommand(
            review.SessionId.Value, "上午先做报告\n下午减少无目标浏览"));
        Assert.True(accepted.Success);
        Assert.Equal(ReviewSessionState.Completed, (await companion.SnapshotAsync()).DailyReview.State);
    }

    [Fact]
    public async Task Cycle_trends_include_confirmed_timed_rest_minutes()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var rest = await supervision.StartTimedRestAsync(
            commitment.Id, commitment.Version, Start.AddMinutes(12));
        Assert.True(rest.Success);
        await using var companion = await OpenCompanionAsync(database.Path, supervision, clock);

        Assert.True((await companion.DispatchAsync(new StartCycleReviewCommand())).Success);
        var trends = (await companion.SnapshotAsync()).CycleReview.Trends;
        Assert.NotNull(trends);
        Assert.Equal(12d, trends.RestMinutes, precision: 2);
        var trace = Assert.Single(trends.Commitments);
        Assert.Equal(commitment.Id, trace.CommitmentId);
        Assert.Equal("验证监督闭环", trace.InputGoal);
        Assert.Equal("留下正式记录", trace.OutcomeGoal);
        Assert.Equal(12d, trace.RestMinutes, precision: 2);
    }

    [Fact]
    public void Feishu_card_is_compact_private_and_contains_four_versioned_actions()
    {
        var card = new MobileEscalationCard(
            Guid.NewGuid(), Guid.NewGuid(), 3, 2, Start.AddMinutes(40), Start,
            Start.AddHours(2), Start, ActivityClassification.Distracting,
            "写报告", "Jarvis 工作提醒：请解锁飞书查看详情。");
        var json = LarkEscalationCardJson.Build(card, interactive: true);

        Assert.Contains("\"schema\":\"2.0\"", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            card.PrivacyPreview,
            document.RootElement.GetProperty("config").GetProperty("summary")
                .GetProperty("content").GetString());
        Assert.DoesNotContain("ChatGPT", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, Enum.GetValues<WorktimeActionKind>().Count(action =>
            json.Contains($"\"action\":\"{action}\"", StringComparison.Ordinal)));
        Assert.Contains("\"rest_minutes\":15", json, StringComparison.Ordinal);
        Assert.DoesNotContain("corner_radius", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_commands_round_trip_as_a_closed_typed_union()
    {
        var request = new CoreRequest(
            CoreOperations.DispatchCompanion,
            Companion: new InterpretNaturalLanguageCommand("明天上午写报告", CandidateSource.Feishu));

        var json = JsonSerializer.Serialize(request, CoreProtocol.Json);
        var roundTrip = JsonSerializer.Deserialize<CoreRequest>(json, CoreProtocol.Json);
        var command = Assert.IsType<InterpretNaturalLanguageCommand>(roundTrip!.Companion);
        Assert.Equal(CandidateSource.Feishu, command.Source);
        Assert.Equal("明天上午写报告", command.Text);

        var capJson = JsonSerializer.Serialize(new CoreRequest(
            CoreOperations.DispatchCompanion,
            Companion: new SetAiMonthlyHardCapCommand(35m)), CoreProtocol.Json);
        var capRoundTrip = JsonSerializer.Deserialize<CoreRequest>(capJson, CoreProtocol.Json);
        Assert.Equal(35m, Assert.IsType<SetAiMonthlyHardCapCommand>(capRoundTrip!.Companion).HardCapCny);
    }

    [Fact]
    public async Task DeepSeek_structured_output_can_create_a_template_candidate_without_executing_it()
    {
        var template = new CommitmentTemplateDraft(
            "报告模板", CommitmentKind.Computer, 60, "写报告", "形成初稿",
            [new CommitmentTarget(CommitmentTargetKind.Application, "winword.exe")],
            Jarvis.Contracts.SupervisionMode.Interactive,
            new ReminderSettings(true, 5, 20, 20, 3),
            [new ActivityRule(
                new CommitmentTarget(CommitmentTargetKind.Application, "winword.exe"),
                ActivityClassification.Related)],
            new RestSettings(10, 15));
        var content = JsonSerializer.Serialize(new
        {
            kind = "saveTemplate",
            summary = "保存无日期的报告模板",
            template
        }, CoreProtocol.Json);
        var handler = new StubHttpHandler(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            usage = new { prompt_tokens = 120, completion_tokens = 80, prompt_cache_hit_tokens = 20 }
        }));
        using var provider = new DeepSeekCloudAiProvider(new HttpClient(handler));

        var result = await provider.CompleteAsync(
            new AiProviderRequest(
                AiRequestPurpose.NaturalLanguageOperation,
                "保存一个报告模板",
                "deepseek-v4-flash",
                2048,
                Start),
            "sk-test-secret",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CandidateOperationKind.SaveTemplate, result.Candidate!.Kind);
        Assert.Equal("报告模板", result.Candidate.Template!.Name);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.DoesNotContain("sk-test-secret", handler.RequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeek_ambiguity_is_returned_as_a_clarification_instead_of_an_invalid_candidate()
    {
        var content = JsonSerializer.Serialize(new
        {
            needsClarification = "你说的下午是 15:00 吗？持续多久？"
        });
        using var provider = new DeepSeekCloudAiProvider(new HttpClient(new StubHttpHandler(
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = 30, completion_tokens = 20 }
            }))));

        var result = await provider.CompleteAsync(
            new AiProviderRequest(
                AiRequestPurpose.NaturalLanguageOperation, "下午写报告", "deepseek-v4-flash", 500, Start),
            "sk-test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ai_clarification_required", result.ErrorCode);
        Assert.Contains("持续多久", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mobile_delivery_retries_the_same_persisted_card_and_invalidation_is_retryable()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        await ConfirmActiveComputerAsync(supervision, clock, activity);
        var channel = new FakeWorktimeChannel { FailNextSend = true };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind-retry", "ou_test", "oc", "om"));

        clock.Now = Start.AddMinutes(20);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.Equal(MobileCardState.PendingDelivery, Assert.Single((await companion.SnapshotAsync()).MobileCards).State);

        clock.Now = Start.AddMinutes(21);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.Equal(2, channel.SendAttempts.Count);
        Assert.Equal(channel.SendAttempts[0].CardId, channel.SendAttempts[1].CardId);
        Assert.Equal(MobileCardState.Active, Assert.Single((await companion.SnapshotAsync()).MobileCards).State);

        channel.FailNextInvalidation = true;
        clock.Now = Start.AddMinutes(40);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.Contains((await companion.SnapshotAsync()).MobileCards,
            card => card.State == MobileCardState.SupersedePending);
        clock.Now = Start.AddMinutes(41);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        Assert.DoesNotContain((await companion.SnapshotAsync()).MobileCards,
            card => card.State == MobileCardState.SupersedePending);
    }

    [Fact]
    public async Task Worktime_action_retry_does_not_repeat_the_supervision_response_after_companion_commit_fails()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var commitment = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind-action", "ou_test", "oc", "om"));
        clock.Now = Start.AddMinutes(20);
        activity.Next = Distracting(clock.Now);
        await supervision.TickAsync();
        await companion.AdvanceAsync();
        var card = Assert.Single((await companion.SnapshotAsync()).MobileCards);
        var action = new HandleWorktimeActionCommand(
            "stable-action", "ou_test", card.CardId, commitment.Id, commitment.Version,
            WorktimeActionKind.StartRest, clock.Now.AddMinutes(5));

        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER fail_card_outcome BEFORE UPDATE OF state ON mobile_escalation_cards
                WHEN NEW.state={(int)MobileCardState.ResponsePending}
                BEGIN SELECT RAISE(ABORT,'injected'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => companion.DispatchAsync(action));
        clock.Now = clock.Now.AddMinutes(10);
        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER fail_card_outcome;";
            await drop.ExecuteNonQueryAsync();
        }

        var retried = await companion.DispatchAsync(action);

        Assert.True(retried.Success, retried.Message);
        await using var verify = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM supervision_responses WHERE kind='timed_rest_started';";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Daily_review_sends_exactly_one_automatic_follow_up_after_thirty_minutes()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 59, 30, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind-follow", "ou_test", "oc", "om"));
        await companion.DispatchAsync(new ConfigureDailyReviewCommand(new TimeOnly(23, 0)));
        clock.Now = clock.Now.AddSeconds(31);
        await companion.AdvanceAsync();
        Assert.Single(channel.DailyReviewInvitations);
        clock.Now = clock.Now.AddMinutes(30);
        await companion.AdvanceAsync();
        Assert.Equal(2, channel.DailyReviewInvitations.Count);
        Assert.True((await companion.SnapshotAsync()).DailyReview.FollowUpUsed);
        clock.Now = clock.Now.AddMinutes(30);
        await companion.AdvanceAsync();
        Assert.Equal(2, channel.DailyReviewInvitations.Count);
    }

    [Fact]
    public async Task Daily_review_blocked_by_active_commitment_invites_when_that_commitment_ends()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 50, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var draft = new CommitmentDraft(
            CommitmentKind.Computer, clock.Now, null, 30, "晚间收尾", null,
            [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
            Jarvis.Contracts.SupervisionMode.Interactive, null);
        var prepared = await supervision.PrepareAsync(draft);
        Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind-deferred-review", "ou_test", "oc", "om"));
        await companion.DispatchAsync(new ConfigureDailyReviewCommand(new TimeOnly(23, 0)));

        clock.Now = new DateTimeOffset(2026, 8, 14, 15, 1, 0, TimeSpan.Zero);
        await companion.AdvanceAsync();
        Assert.Empty(channel.DailyReviewInvitations);
        clock.Now = new DateTimeOffset(2026, 8, 14, 15, 21, 0, TimeSpan.Zero);
        await companion.AdvanceAsync();

        Assert.Single(channel.DailyReviewInvitations);
        Assert.Equal(new DateOnly(2026, 8, 14), channel.DailyReviewInvitations[0]);
        Assert.True((await companion.SnapshotAsync()).DailyReview.MobileInviteSent);
    }

    [Fact]
    public async Task Deferred_daily_review_does_not_send_an_old_mobile_invite_after_core_restart()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 50, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using (var supervision = await SupervisionModule.OpenAsync(
                         database.Path, clock, activity, new FakeReminderSink()))
        {
            var draft = new CommitmentDraft(
                CommitmentKind.Computer, clock.Now, null, 30, "晚间收尾", null,
                [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
                Jarvis.Contracts.SupervisionMode.Interactive, null);
            var prepared = await supervision.PrepareAsync(draft);
            Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
            await using var companion = await CompanionModule.OpenAsync(
                database.Path, supervision, clock, new FakeWorktimeChannel(),
                new FakeAiProvider(), new FakeCredentialStore());
            await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
            await companion.DispatchAsync(new BindWorktimeUserCommand("bind-restart-review", "ou_test", "oc", "om"));
            await companion.DispatchAsync(new ConfigureDailyReviewCommand(new TimeOnly(23, 0)));
            clock.Now = new DateTimeOffset(2026, 8, 14, 15, 1, 0, TimeSpan.Zero);
            await companion.AdvanceAsync();
        }

        clock.Now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        await using var restartedSupervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var restartedChannel = new FakeWorktimeChannel();
        await using var restarted = await CompanionModule.OpenAsync(
            database.Path, restartedSupervision, clock, restartedChannel,
            new FakeAiProvider(), new FakeCredentialStore());
        await restarted.AdvanceAsync();

        Assert.Empty(restartedChannel.DailyReviewInvitations);
        Assert.Equal(new DateOnly(2026, 8, 14), (await restarted.SnapshotAsync()).DailyReview.ReviewDate);
    }

    [Fact]
    public async Task Feishu_natural_language_returns_a_visible_candidate_and_can_confirm_it_in_chat()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var channel = new FakeWorktimeChannel();
        var ai = new FakeAiProvider
        {
            NextCandidate = new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CreateCommitment, "一小时写报告",
                Commitment: new CommitmentDraft(
                    CommitmentKind.Computer, Start.AddHours(1), null, 60, "写报告", "形成初稿",
                    [new CommitmentTarget(CommitmentTargetKind.Application, "winword.exe")],
                    Jarvis.Contracts.SupervisionMode.Interactive, null))
        };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, ai, new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-123"));
        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "bind-chat", "ou_test", clock.Now, "oc", "om1", "绑定 Jarvis"));
        channel.FailNextText = true;
        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "nl-chat", "ou_test", clock.Now, "oc", "om2", "一小时写报告"));
        Assert.DoesNotContain(channel.TextReplies, reply => reply.Contains("确认候选", StringComparison.Ordinal));
        await companion.AdvanceAsync();
        Assert.Contains(channel.TextReplies, reply => reply.Contains("确认候选", StringComparison.Ordinal));
        Assert.Empty((await supervision.GetSnapshotAsync()).Commitments);
        Assert.Equal(channel.TextReplyAttempts[^2], channel.TextReplyAttempts[^1]);
        var successfulReplies = channel.TextReplies.Count;
        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "nl-chat", "ou_test", clock.Now, "oc", "om2", "一小时写报告"));
        Assert.Equal(successfulReplies, channel.TextReplies.Count);
        Assert.Equal(1, ai.CallCount);
        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "confirm-chat", "ou_test", clock.Now, "oc", "om3", "确认候选"));
        Assert.Single((await supervision.GetSnapshotAsync()).Commitments);
        Assert.Contains(channel.TextReplies, reply => reply.Contains("正式确认", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Feishu_confirmation_retry_finishes_a_candidate_whose_official_action_was_committed()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand(
            "bind-committed-candidate", "ou_test", "oc", "om-bind"));

        var candidate = new NaturalLanguageOperationCandidate(
            Guid.NewGuid(), CandidateOperationKind.CreateCommitment, "一小时写报告",
            Commitment: new CommitmentDraft(
                CommitmentKind.Computer, Start.AddHours(1), null, 60, "写报告", "形成初稿",
                [new CommitmentTarget(CommitmentTargetKind.Application, "winword.exe")],
                Jarvis.Contracts.SupervisionMode.Interactive, null));
        var store = new SqliteCompanionStore(database.Path);
        await store.SaveCandidateAsync(candidate, null, null, CancellationToken.None);
        Assert.True(await store.TryBeginWorktimeEventAsync(
            "retry-committed-candidate", clock.Now, CancellationToken.None));
        await store.BindWorktimeCandidateAsync(
            "retry-committed-candidate", candidate.CandidateId, "confirm", CancellationToken.None);
        Assert.True(await store.TryBeginCandidateConfirmationAsync(candidate.CandidateId, CancellationToken.None));
        var prepared = await supervision.PrepareAsync(candidate.Commitment!);
        Assert.True(prepared.Success);
        Assert.True((await supervision.ConfirmAsync(prepared.Value!.CandidateId)).Success);
        await store.MarkCandidateOfficialActionCommittedAsync(candidate.CandidateId, CancellationToken.None);

        var newerCandidate = candidate with
        {
            CandidateId = Guid.NewGuid(),
            OriginalText = "稍后再写另一份报告",
            Commitment = candidate.Commitment! with { StartAt = Start.AddHours(3) }
        };
        await store.SaveCandidateAsync(newerCandidate, null, null, CancellationToken.None);

        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "retry-committed-candidate", "ou_test", clock.Now, "oc", "om-confirm", "确认候选"));

        Assert.Single((await supervision.GetSnapshotAsync()).Commitments);
        Assert.Equal("confirmed", await store.ReadCandidateStateAsync(
            candidate.CandidateId, CancellationToken.None));
        Assert.Equal("pending", await store.ReadCandidateStateAsync(
            newerCandidate.CandidateId, CancellationToken.None));
        Assert.Contains(channel.TextReplies, reply => reply.Contains("正式确认", StringComparison.Ordinal));

        Assert.True(await store.TryBeginWorktimeEventAsync(
            "retry-discarded-candidate", clock.Now, CancellationToken.None));
        await store.BindWorktimeCandidateAsync(
            "retry-discarded-candidate", newerCandidate.CandidateId, "discard", CancellationToken.None);
        var newestCandidate = newerCandidate with
        {
            CandidateId = Guid.NewGuid(),
            OriginalText = "保留这条新候选",
            Commitment = newerCandidate.Commitment! with { StartAt = Start.AddHours(5) }
        };
        await store.SaveCandidateAsync(newestCandidate, null, null, CancellationToken.None);
        await channel.EmitAsync(new WorktimeTextInboundEvent(
            "retry-discarded-candidate", "ou_test", clock.Now, "oc", "om-discard", "放弃候选"));

        Assert.Equal("discarded", await store.ReadCandidateStateAsync(
            newerCandidate.CandidateId, CancellationToken.None));
        Assert.Equal("pending", await store.ReadCandidateStateAsync(
            newestCandidate.CandidateId, CancellationToken.None));
    }

    [Fact]
    public async Task DeepSeek_invalid_candidate_still_reports_the_billable_usage()
    {
        using var provider = new DeepSeekCloudAiProvider(new HttpClient(new StubHttpHandler(
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "not-json" } } },
                usage = new { prompt_tokens = 321, completion_tokens = 45, prompt_cache_hit_tokens = 12 }
            }))));
        var result = await provider.CompleteAsync(
            new AiProviderRequest(
                AiRequestPurpose.NaturalLanguageOperation, "创建承诺", "deepseek-v4-flash", 500, Start),
            "sk-test", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(321, result.Usage.InputTokens);
        Assert.Equal(45, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Interrupted_ai_call_keeps_its_budget_reservation_and_same_event_is_not_recharged()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider { NextException = new HttpRequestException("connection lost") };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, new FakeCredentialStore());
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-123"));
        var command = new InterpretNaturalLanguageCommand(
            "创建一条承诺", CandidateSource.Feishu, "stable-ai-event");

        var first = await companion.DispatchAsync(command);
        var second = await companion.DispatchAsync(command);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Equal("ai_previous_result_uncertain", second.ErrorCode);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0.01m, (await companion.SnapshotAsync()).Ai.MonthSpendCny);
    }

    [Fact]
    public async Task Missed_review_while_core_was_off_is_asked_once_on_the_next_day()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var channel = new FakeWorktimeChannel();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, channel, new FakeAiProvider(), new FakeCredentialStore());
        await companion.DispatchAsync(new ConfigureWorktimeChannelCommand(true, "lark-cli", "test"));
        await companion.DispatchAsync(new BindWorktimeUserCommand("bind-missed", "ou_test", "oc", "om"));
        await companion.DispatchAsync(new ConfigureDailyReviewCommand(new TimeOnly(23, 0)));

        clock.Now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        await companion.AdvanceAsync();
        var missed = (await companion.SnapshotAsync()).DailyReview;
        Assert.Equal(new DateOnly(2026, 8, 14), missed.ReviewDate);
        Assert.Equal(ReviewSessionState.Pending, missed.State);
        Assert.False(missed.FollowUpUsed);
        Assert.Empty(channel.DailyReviewInvitations);
        await companion.AdvanceAsync();
        Assert.Empty(channel.DailyReviewInvitations);
    }

    [Fact]
    public async Task Default_daily_review_time_is_effective_without_opening_settings()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 14, 59, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        await using var companion = await OpenCompanionAsync(database.Path, supervision, clock);

        clock.Now = clock.Now.AddMinutes(2);
        await companion.AdvanceAsync();

        var review = (await companion.SnapshotAsync()).DailyReview;
        Assert.Equal(new DateOnly(2026, 8, 14), review.ReviewDate);
        Assert.Equal(ReviewSessionState.Pending, review.State);
    }

    [Fact]
    public async Task Confirmed_ai_defer_ends_the_current_supervision_and_creates_a_future_commitment()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var current = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var credential = new FakeCredentialStore();
        var ai = new FakeAiProvider
        {
            NextCandidate = new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.DeferCommitment, "推迟到下午",
                TargetCommitmentId: current.Id, ExpectedVersion: current.Version,
                DeferredStartAt: current.EndAt.AddHours(1), Reason: "临时会议")
        };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), ai, credential);
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-123"));
        var interpreted = await companion.DispatchAsync(
            new InterpretNaturalLanguageCommand("把当前承诺推迟到下午", CandidateSource.Desktop));
        Assert.True(interpreted.Success, interpreted.Message);
        Assert.Contains("剩余时长", interpreted.Candidate!.Summary, StringComparison.Ordinal);
        var confirmed = await companion.DispatchAsync(
            new ConfirmNaturalLanguageCandidateCommand(interpreted.Candidate.CandidateId));
        Assert.True(confirmed.Success, confirmed.Message);
        var commitments = (await supervision.GetSnapshotAsync()).Commitments;
        Assert.Equal(2, commitments.Count);
        Assert.Equal(CommitmentPhase.AwaitingReview, commitments.Single(item => item.Id == current.Id).Phase);
        Assert.Equal(CommitmentPhase.Scheduled, commitments.Single(item => item.Id != current.Id).Phase);
    }

    [Fact]
    public async Task Confirmed_ai_cancel_marks_the_target_commitment_skipped_with_no_replacement()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var current = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var ai = new FakeAiProvider
        {
            NextCandidate = new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CancelCommitment, "取消当前承诺",
                TargetCommitmentId: current.Id, ExpectedVersion: current.Version,
                Reason: "计划已经改变")
        };
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), ai, new FakeCredentialStore());
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-123"));

        var interpreted = await companion.DispatchAsync(
            new InterpretNaturalLanguageCommand("取消当前承诺", CandidateSource.Desktop));
        Assert.True(interpreted.Success, interpreted.Message);
        var confirmed = await companion.DispatchAsync(
            new ConfirmNaturalLanguageCandidateCommand(interpreted.Candidate!.CandidateId));

        Assert.True(confirmed.Success, confirmed.Message);
        var only = Assert.Single((await supervision.GetSnapshotAsync()).Commitments);
        Assert.Equal(current.Id, only.Id);
        Assert.Equal(CommitmentPhase.Skipped, only.Phase);
    }

    [Fact]
    public async Task Cancel_and_defer_have_deterministic_commands_when_ai_is_unavailable()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var current = await ConfirmActiveComputerAsync(supervision, clock, activity);
        var ai = new FakeAiProvider();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), ai, new FakeCredentialStore());

        var deferred = await companion.DispatchAsync(new DeferActiveCommitmentCommand(
            current.Id, current.Version, current.EndAt.AddHours(1), "改到下午"));
        Assert.True(deferred.Success, deferred.Message);
        var replacement = (await supervision.GetSnapshotAsync()).Commitments.Single(item => item.Id != current.Id);
        var cancelled = await companion.DispatchAsync(new CancelCommitmentCommand(
            replacement.Id, replacement.Version, "不再需要"));

        Assert.True(cancelled.Success, cancelled.Message);
        Assert.Equal(CommitmentPhase.Skipped,
            (await supervision.GetSnapshotAsync()).Commitments.Single(item => item.Id == replacement.Id).Phase);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public void Live_lark_processes_that_never_become_ready_time_out_for_restart()
    {
        const long started = 1;
        var beforeTimeout = started + (long)(Stopwatch.Frequency * 19d);
        var afterTimeout = started + (long)(Stopwatch.Frequency * 21d);

        Assert.False(LarkCliWorktimeChannel.IsReadyTimedOut(1, started, beforeTimeout));
        Assert.True(LarkCliWorktimeChannel.IsReadyTimedOut(1, started, afterTimeout));
        Assert.False(LarkCliWorktimeChannel.IsReadyTimedOut(2, started, afterTimeout));
        Assert.True(LarkCliWorktimeChannel.IsReadyDiagnostic(
            "card.action.trigger", "[event] ready event_key=card.action.trigger transport=websocket"));
        Assert.False(LarkCliWorktimeChannel.IsReadyDiagnostic(
            "card.action.trigger", "[event] ready event_key=im.message.receive_v1"));
    }

    private static async Task<CompanionModule> OpenCompanionAsync(
        string path, SupervisionModule supervision, FakeClock clock) =>
        await CompanionModule.OpenAsync(
            path, supervision, clock, new FakeWorktimeChannel(), new FakeAiProvider(),
            new FakeCredentialStore());

    private static async Task<CommitmentView> ConfirmActiveComputerAsync(
        SupervisionModule module, FakeClock clock, FakeActivitySource activity)
    {
        var draft = new CommitmentDraft(
            Kind: CommitmentKind.Computer,
            StartAt: clock.Now,
            EndAt: null,
            DurationMinutes: 120,
            InputGoal: "验证监督闭环",
            OutcomeGoal: "留下正式记录",
            RelatedAppsOrSites:
                [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
            SupervisionMode: Jarvis.Contracts.SupervisionMode.Interactive,
            ReminderSettings: new ReminderSettings(true, 5, 20, 20, 3),
            ActivityRules: [new ActivityRule(
                new CommitmentTarget(CommitmentTargetKind.Application, "chat.exe"),
                ActivityClassification.Distracting)]);
        var card = await module.PrepareAsync(draft);
        Assert.True(card.Success);
        Assert.True((await module.ConfirmAsync(card.Value!.CandidateId)).Success);
        activity.Next = Distracting(clock.Now);
        await module.TickAsync();
        return Assert.Single((await module.GetSnapshotAsync()).Commitments);
    }

    private static ActivityObservation Distracting(DateTimeOffset at) => new(
        ActivityAvailability.Available,
        IsUserActive: true,
        ForegroundProcess: "chat",
        ObservedAt: at,
        IdleDuration: TimeSpan.Zero);

    private static ActivityObservation Related(DateTimeOffset at) => new(
        ActivityAvailability.Available,
        IsUserActive: true,
        ForegroundProcess: "work",
        ObservedAt: at,
        IdleDuration: TimeSpan.Zero);
}

internal sealed class FakeWorktimeChannel : IWorktimeChannel
{
    public bool IsHealthy { get; set; } = true;
    public bool NeedsRestart => false;
    public string? LastError => null;
    public List<MobileEscalationCard> Sent { get; } = [];
    public List<MobileEscalationCard> SendAttempts { get; } = [];
    public List<Guid> Invalidated { get; } = [];
    public List<string> InvalidationResults { get; } = [];
    public List<DateOnly> DailyReviewInvitations { get; } = [];
    public List<string> TextReplies { get; } = [];
    public List<Guid> TextReplyKeys { get; } = [];
    public List<Guid> TextReplyAttempts { get; } = [];
    public bool FailNextSend { get; set; }
    public bool FailNextInvalidation { get; set; }
    public bool FailNextText { get; set; }
    private Func<WorktimeInboundEvent, CancellationToken, Task>? _onEvent;

    public ValueTask ConfigureAsync(
        WorktimeChannelConfiguration configuration,
        Func<WorktimeInboundEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        IsHealthy = configuration.Enabled;
        _onEvent = configuration.Enabled ? onEvent : null;
        return ValueTask.CompletedTask;
    }

    public Task EmitAsync(WorktimeInboundEvent inbound) =>
        _onEvent is null
            ? throw new InvalidOperationException("Fake worktime listener is not configured.")
            : _onEvent(inbound, CancellationToken.None);

    public ValueTask<WorktimeDeliveryResult> SendAsync(
        MobileEscalationCard card, CancellationToken cancellationToken)
    {
        SendAttempts.Add(card);
        if (FailNextSend)
        {
            FailNextSend = false;
            return ValueTask.FromResult(new WorktimeDeliveryResult(false, ErrorCode: "temporary"));
        }
        Sent.Add(card);
        return ValueTask.FromResult(new WorktimeDeliveryResult(true, $"om_{Sent.Count}"));
    }

    public ValueTask<bool> InvalidateAsync(
        Guid cardId, string platformMessageId, string resultText, CancellationToken cancellationToken)
    {
        if (FailNextInvalidation)
        {
            FailNextInvalidation = false;
            return ValueTask.FromResult(false);
        }
        Invalidated.Add(cardId);
        InvalidationResults.Add(resultText);
        return ValueTask.FromResult(true);
    }

    public ValueTask<WorktimeDeliveryResult> SendDailyReviewInvitationAsync(
        Guid sessionId, DateOnly reviewDate, bool followUp, CancellationToken cancellationToken)
    {
        DailyReviewInvitations.Add(reviewDate);
        return ValueTask.FromResult(new WorktimeDeliveryResult(true, $"om_review_{sessionId:N}"));
    }

    public ValueTask<WorktimeDeliveryResult> SendTextAsync(
        string recipientOpenId, string text, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        TextReplyAttempts.Add(idempotencyKey);
        if (FailNextText)
        {
            FailNextText = false;
            return ValueTask.FromResult(new WorktimeDeliveryResult(false, ErrorCode: "temporary"));
        }
        TextReplies.Add(text);
        TextReplyKeys.Add(idempotencyKey);
        return ValueTask.FromResult(new WorktimeDeliveryResult(true, $"om_text_{TextReplies.Count}"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeCredentialStore : IAiCredentialStore
{
    private string? _value;

    public ValueTask SaveAsync(string provider, string secret, CancellationToken cancellationToken)
    {
        _value = secret;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(string provider, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_value);

    public ValueTask DeleteAsync(string provider, CancellationToken cancellationToken)
    {
        _value = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAiProvider : ICloudAiProvider
{
    public AiTokenUsage NextUsage { get; set; } = new(100, 50, 0);
    public decimal? EstimatedCostCny { get; set; }
    public NaturalLanguageOperationCandidate? NextCandidate { get; set; }
    public int CallCount { get; private set; }
    public Exception? NextException { get; set; }
    public TaskCompletionSource<bool>? Started { get; set; }
    public TaskCompletionSource<bool>? Release { get; set; }

    public decimal EstimateCostCny(AiProviderRequest request) =>
        EstimatedCostCny ?? 0.01m;

    public async ValueTask<AiProviderResult> CompleteAsync(
        AiProviderRequest request, string credential, CancellationToken cancellationToken)
    {
        CallCount++;
        if (NextException is not null) throw NextException;
        Started?.TrySetResult(true);
        if (Release is not null)
            await Release.Task.WaitAsync(cancellationToken);
        return new AiProviderResult(
            true,
            request.Purpose == AiRequestPurpose.NaturalLanguageOperation
                ? "candidate"
                : "今天的重点已经整理好了。",
            NextUsage,
            Candidate: NextCandidate);
    }
}

internal sealed class StubHttpHandler(string responseJson) : HttpMessageHandler
{
    public string? RequestBody { get; private set; }
    public string? AuthorizationScheme { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }
}
