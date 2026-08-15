using Jarvis.Contracts;
using System.Net.Http;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class AiReviewAssistanceScenarios
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Daily_review_ai_uses_only_current_review_facts_and_requires_confirmation()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider
        {
            NextReviewDraft = new AiReviewDraftPayload(
                "今天完成了主要工作，但切换成本偏高。",
                ["相关工作时段集中"],
                ["明天先完成最重要的一项"])
        };
        var credentials = new FakeCredentialStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-review-test-1234"));

        Assert.True((await companion.DispatchAsync(new StartDailyReviewCommand())).Success);
        var active = (await companion.SnapshotAsync()).DailyReview;
        Assert.NotNull(active.SessionId);
        Assert.True((await companion.DispatchAsync(new RespondDailyReviewCommand(
            active.SessionId!.Value, "今天按计划完成了交易复盘。"))).Success);

        var generated = await companion.DispatchAsync(
            new GenerateAiReviewDraftCommand(AiReviewKind.Daily));

        Assert.True(generated.Success, generated.Message);
        Assert.Equal(AiRequestPurpose.DailyReviewAssist, provider.LastRequest!.Purpose);
        Assert.Null(provider.LastRequest.Supervision);
        Assert.NotNull(provider.LastRequest.ReviewFacts);
        Assert.Equal(AiReviewKind.Daily, provider.LastRequest.ReviewFacts!.Kind);
        Assert.Contains(
            provider.LastRequest.ReviewFacts.DailyAnswers,
            item => item.RawText == "今天按计划完成了交易复盘。");
        var pending = generated.Snapshot!.PendingAiReviewDraft;
        Assert.NotNull(pending);
        Assert.Equal(AiReviewDraftState.Pending, pending!.State);
        Assert.Empty(generated.Snapshot.ConfirmedAiReviewDrafts);

        var confirmed = await companion.DispatchAsync(new ConfirmAiReviewDraftCommand(
            pending.DraftId,
            "今天完成交易复盘；明天先处理最重要的一项。",
            QualityRating: 4,
            StructureReliable: true,
            AmbiguityHandled: true,
            NoOverreach: true,
            PrivacyScopeConfirmed: true,
            Note: "事实与原始回答一致"));

        Assert.True(confirmed.Success, confirmed.Message);
        Assert.Null(confirmed.Snapshot!.PendingAiReviewDraft);
        var formal = Assert.Single(confirmed.Snapshot.ConfirmedAiReviewDrafts);
        Assert.Equal(AiReviewDraftState.Confirmed, formal.State);
        Assert.True(formal.UserModified);
        Assert.Equal(4, formal.Evaluation!.QualityRating);
        Assert.Equal(1, confirmed.Snapshot.AiTrialEvidence.ConfirmedDrafts);
        Assert.Equal(1, confirmed.Snapshot.AiTrialEvidence.ModifiedDrafts);

        clock.Now = clock.Now.AddDays(14);
        var completedWindow = await companion.SnapshotAsync();
        Assert.True(completedWindow.AiTrialEvidence.TrialWindowComplete);
        Assert.Equal(1, completedWindow.AiTrialEvidence.TotalRequests);

        Assert.True((await companion.DispatchAsync(
            new GenerateAiReviewDraftCommand(AiReviewKind.Daily))).Success);
        Assert.Equal(1, (await companion.SnapshotAsync()).AiTrialEvidence.TotalRequests);
    }

    [Fact]
    public async Task Cycle_review_ai_is_traceable_across_restart_and_manual_qwen_comparison_never_calls_cloud()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider
        {
            NextReviewDraft = new AiReviewDraftPayload(
                "本周期投入稳定。", ["偏离时段下降"], ["保持固定复盘时段"])
        };
        var credentials = new FakeCredentialStore();
        await credentials.SaveAsync("siliconflow", "sk-cycle-test-1234", CancellationToken.None);
        Guid draftId;

        await using (var companion = await CompanionModule.OpenAsync(
                         database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials))
        {
            Assert.True((await companion.DispatchAsync(new StartCycleReviewCommand())).Success);
            var generated = await companion.DispatchAsync(
                new GenerateAiReviewDraftCommand(AiReviewKind.Cycle));
            Assert.True(generated.Success, generated.Message);
            var draft = generated.Snapshot!.PendingAiReviewDraft!;
            draftId = draft.DraftId;
            Assert.Equal(AiRequestPurpose.CycleReviewAssist, provider.LastRequest!.Purpose);
            Assert.Null(provider.LastRequest.Supervision);
            Assert.NotNull(provider.LastRequest.ReviewFacts!.CycleTrends);
            Assert.False(string.IsNullOrWhiteSpace(draft.AnonymizedComparisonPrompt));
            Assert.True((await companion.DispatchAsync(new ConfirmAiReviewDraftCommand(
                draftId, draft.DraftText, 5, true, true, true, true))).Success);

            var callsBeforeComparison = provider.CallCount;
            var privacyRejected = await companion.DispatchAsync(new RecordManualAiComparisonCommand(
                draftId,
                "qwen3.7-flash",
                "unchecked output",
                3,
                StructureReliable: true,
                AmbiguityHandled: false,
                NoOverreach: true,
                PrivacyScopeConfirmed: false));
            Assert.False(privacyRejected.Success);
            Assert.Equal("manual_comparison_privacy_unconfirmed", privacyRejected.ErrorCode);
            var comparison = await companion.DispatchAsync(new RecordManualAiComparisonCommand(
                draftId,
                "qwen3.7-flash",
                "手动脱敏对照结果",
                3,
                StructureReliable: true,
                AmbiguityHandled: false,
                NoOverreach: true,
                PrivacyScopeConfirmed: true));
            Assert.True(comparison.Success, comparison.Message);
            Assert.Equal(callsBeforeComparison, provider.CallCount);
            Assert.Equal(1, comparison.Snapshot!.AiTrialEvidence.ManualComparisonCount);
            Assert.Equal(3, comparison.Snapshot.AiTrialEvidence.ManualAverageQualityRating);
            Assert.Equal(0, comparison.Snapshot.AiTrialEvidence.ManualAmbiguityHandledRate);
        }

        await using var restarted = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        var persisted = Assert.Single((await restarted.SnapshotAsync()).ConfirmedAiReviewDrafts);
        Assert.Equal(draftId, persisted.DraftId);
        Assert.Equal("本周期投入稳定。", persisted.ConfirmedText);
        Assert.Equal(1, (await restarted.SnapshotAsync()).AiTrialEvidence.ManualComparisonCount);
    }

    [Fact]
    public async Task Ai_review_failure_leaves_deterministic_review_usable_and_records_trial_failure()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(Start);
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var provider = new FakeAiProvider { NextException = new HttpRequestException("offline") };
        var credentials = new FakeCredentialStore();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path, supervision, clock, new FakeWorktimeChannel(), provider, credentials);
        await companion.DispatchAsync(new SaveAiCredentialCommand("sk-review-test-1234"));
        Assert.True((await companion.DispatchAsync(new StartDailyReviewCommand())).Success);
        var before = (await companion.SnapshotAsync()).DailyReview;

        var outcome = await companion.DispatchAsync(
            new GenerateAiReviewDraftCommand(AiReviewKind.Daily));

        Assert.False(outcome.Success);
        Assert.Equal("ai_provider_unavailable", outcome.ErrorCode);
        var after = await companion.SnapshotAsync();
        Assert.Equal(before.SessionId, after.DailyReview.SessionId);
        Assert.Equal(ReviewSessionState.InProgress, after.DailyReview.State);
        Assert.Null(after.PendingAiReviewDraft);
        Assert.Equal(1, after.AiTrialEvidence.FailedRequests);
    }
}
