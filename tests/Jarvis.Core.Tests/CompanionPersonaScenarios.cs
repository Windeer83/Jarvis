using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class CompanionPersonaScenarios
{
    [Fact]
    public async Task Persona_boundaries_persist_and_reach_chat_without_changing_formal_state()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        var ai = new FakeAiProvider();
        var credentials = new FakeCredentialStore();
        await using (var companion = await CompanionModule.OpenAsync(
                         database.Path,
                         supervision,
                         clock,
                         new FakeWorktimeChannel(),
                         ai,
                         credentials))
        {
            var configured = await companion.DispatchAsync(new ConfigureCompanionPersonaCommand(new(
                ProfessionalMode: false,
                ProactiveEnabled: true,
                PreferredAddress: "小岚",
                DisallowedAddresses: ["主人", "宝宝"],
                DislikedTone: "不要撒娇，不要使用夸张感叹号",
                InteractionBoundary: "工作时简短；忽略后立即停止")));
            Assert.True(configured.Success, configured.Message);
            Assert.Empty((await supervision.GetSnapshotAsync()).Commitments);
            await companion.DispatchAsync(new SaveAiCredentialCommand("sk-test-persona"));

            var chat = await companion.DispatchAsync(new RequestAiChatCommand("今天有点累"));
            Assert.True(chat.Success, chat.Message);
            Assert.Contains("小岚", ai.LastRequest!.PersonaInstructions, StringComparison.Ordinal);
            Assert.Contains("主人", ai.LastRequest.PersonaInstructions, StringComparison.Ordinal);
            Assert.Contains("不制造亲密度", ai.LastRequest.PersonaInstructions, StringComparison.Ordinal);
            Assert.Contains("不暗示用户亏欠", ai.LastRequest.PersonaInstructions, StringComparison.Ordinal);
        }

        await using var restarted = await CompanionModule.OpenAsync(
            database.Path,
            supervision,
            clock,
            new FakeWorktimeChannel(),
            new FakeAiProvider(),
            new FakeCredentialStore());
        var persisted = (await restarted.SnapshotAsync()).PersonaProjection.Settings;
        Assert.Equal("小岚", persisted.PreferredAddress);
        Assert.Contains("宝宝", persisted.DisallowedAddresses);
        Assert.Contains("忽略后立即停止", persisted.InteractionBoundary);
    }

    [Fact]
    public async Task Proactive_chat_is_low_frequency_ends_on_ignore_and_reduces_after_repeated_ignores()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        await using var companion = await CompanionModule.OpenAsync(
            database.Path,
            supervision,
            clock,
            new FakeWorktimeChannel(),
            new FakeAiProvider(),
            new FakeCredentialStore());

        await companion.AdvanceAsync();
        var first = Assert.IsType<ProactiveCompanionPromptView>(
            (await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
        Assert.DoesNotContain("失望", first.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True((await companion.DispatchAsync(
            new DismissProactiveCompanionCommand(first.PromptId))).Success);
        Assert.Null((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
        await companion.AdvanceAsync();
        Assert.Null((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);

        clock.Now = clock.Now.AddDays(1);
        await companion.AdvanceAsync();
        var second = Assert.IsType<ProactiveCompanionPromptView>(
            (await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
        await companion.DispatchAsync(new DismissProactiveCompanionCommand(second.PromptId));
        Assert.Equal(2, (await companion.SnapshotAsync()).PersonaProjection.ConsecutiveIgnores);

        clock.Now = clock.Now.AddDays(1);
        await companion.AdvanceAsync();
        Assert.Null((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
        clock.Now = clock.Now.AddDays(1);
        await companion.AdvanceAsync();
        Assert.NotNull((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
    }

    [Fact]
    public async Task Active_work_and_sleep_hours_suppress_proactive_chat()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        var activity = new FakeActivitySource();
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            activity,
            new FakeReminderSink());
        var candidate = await supervision.PrepareAsync(new CommitmentDraft(
            CommitmentKind.Computer,
            clock.Now,
            null,
            60,
            "验证主动问候边界",
            null,
            [new CommitmentTarget(CommitmentTargetKind.Application, "work.exe")],
            SupervisionMode.Interactive,
            null));
        Assert.True(candidate.Success, candidate.Message);
        Assert.True((await supervision.ConfirmAsync(candidate.Value!.CandidateId)).Success);
        activity.Next = new ActivityObservation(
            ActivityAvailability.Available,
            true,
            "work",
            clock.Now,
            IdleDuration: TimeSpan.Zero);
        await supervision.TickAsync();
        await using var companion = await CompanionModule.OpenAsync(
            database.Path,
            supervision,
            clock,
            new FakeWorktimeChannel(),
            new FakeAiProvider(),
            new FakeCredentialStore());

        await companion.AdvanceAsync();
        Assert.Null((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);

        clock.Now = new DateTimeOffset(2026, 8, 15, 23, 30, 0, TimeSpan.Zero);
        await companion.AdvanceAsync();
        Assert.Null((await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
    }

    [Fact]
    public async Task Responding_records_only_the_prompt_and_user_response_and_ends_the_turn()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        await using var companion = await CompanionModule.OpenAsync(
            database.Path,
            supervision,
            clock,
            new FakeWorktimeChannel(),
            new FakeAiProvider(),
            new FakeCredentialStore());
        await companion.AdvanceAsync();
        var prompt = (await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt!;

        var result = await companion.DispatchAsync(new RespondProactiveCompanionCommand(
            prompt.PromptId,
            "今天先休息，明天再继续。"));

        Assert.True(result.Success, result.Message);
        var snapshot = await companion.SnapshotAsync();
        Assert.Null(snapshot.PersonaProjection.CurrentPrompt);
        Assert.Equal(1, snapshot.PersonaProjection.TotalResponses);
        Assert.Equal(0, snapshot.PersonaProjection.ConsecutiveIgnores);
        Assert.Equal(["assistant", "user"], snapshot.RecentChat.Select(item => item.Role));
    }

    [Fact]
    public async Task Hidden_prompt_is_not_an_ignore_until_the_desktop_acknowledges_presentation()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero));
        await using var supervision = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        await using var companion = await CompanionModule.OpenAsync(
            database.Path,
            supervision,
            clock,
            new FakeWorktimeChannel(),
            new FakeAiProvider(),
            new FakeCredentialStore());

        await companion.AdvanceAsync();
        var hidden = Assert.IsType<ProactiveCompanionPromptView>(
            (await companion.SnapshotAsync()).PersonaProjection.CurrentPrompt);
        Assert.Null(hidden.PresentedAt);
        Assert.Null(hidden.ExpiresAt);
        Assert.Equal(0, (await companion.SnapshotAsync()).PersonaProjection.TodayPromptCount);

        clock.Now = clock.Now.AddHours(3);
        await companion.AdvanceAsync();
        var stillPending = (await companion.SnapshotAsync()).PersonaProjection;
        Assert.Equal(hidden.PromptId, stillPending.CurrentPrompt?.PromptId);
        Assert.Equal(0, stillPending.TotalIgnores);

        var acknowledged = await companion.DispatchAsync(
            new AcknowledgeProactiveCompanionCommand(hidden.PromptId));
        Assert.True(acknowledged.Success, acknowledged.Message);
        var visible = (await companion.SnapshotAsync()).PersonaProjection;
        Assert.Equal(clock.Now, visible.CurrentPrompt?.PresentedAt);
        Assert.Equal(clock.Now.AddHours(2), visible.CurrentPrompt?.ExpiresAt);
        Assert.Equal(1, visible.TodayPromptCount);

        clock.Now = clock.Now.AddHours(2).AddSeconds(1);
        await companion.AdvanceAsync();
        var expired = (await companion.SnapshotAsync()).PersonaProjection;
        Assert.Null(expired.CurrentPrompt);
        Assert.Equal(1, expired.TotalIgnores);
    }
}
