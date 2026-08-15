using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class CorePipeScenarios
{
    [Fact]
    public async Task Desktop_operations_round_trip_through_core_pipe_and_return_core_projection()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var clock = new FakeClock(now);
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        var pipeName = $"Jarvis.Core.Tests.{Guid.NewGuid():N}";
        await using var server = new CorePipeServer(pipeName, new CoreCommandHandler(module));
        server.Start();

        var prepare = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Prepare,
            Draft: new CommitmentDraft(
                CommitmentKind.Computer,
                now.AddMinutes(10),
                EndAt: null,
                DurationMinutes: 60,
                InputGoal: "整理交易日志",
                OutcomeGoal: "完成一份复盘",
                RelatedAppsOrSites:
                [
                    new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")
                ],
                SupervisionMode: null,
                ReminderSettings: null)));

        Assert.True(prepare.Success);
        Assert.NotNull(prepare.Card);

        var confirm = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Confirm,
            CandidateId: prepare.Card.CandidateId));

        Assert.True(confirm.Success);
        Assert.Single(confirm.Snapshot!.Commitments);

        var snapshot = await SendAsync(pipeName, new CoreRequest(CoreOperations.GetSnapshot));
        Assert.Equal(confirm.Snapshot.Commitments.Single().Id, snapshot.Snapshot!.Commitments.Single().Id);
    }

    [Fact]
    public async Task Client_disconnect_before_response_does_not_stop_accept_loop()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8)));
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        var pipeName = $"Jarvis.Core.Tests.{Guid.NewGuid():N}";
        await using var server = new CorePipeServer(pipeName, new CoreCommandHandler(module));
        server.Start();

        await using (var abandoned = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await abandoned.ConnectAsync(1000);
        }

        await Task.Delay(100);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var snapshot = await SendAsync(
            pipeName,
            new CoreRequest(CoreOperations.GetSnapshot),
            timeout.Token);

        Assert.True(snapshot.Success);
        Assert.Empty(snapshot.Snapshot!.Commitments);
    }

    [Fact]
    public async Task Reminder_delivery_failure_does_not_repeat_the_formal_reminder()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var clock = new FakeClock(now);
        var reminderSink = new ThrowingReminderSink();
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            reminderSink);
        var pipeName = $"Jarvis.Core.Tests.{Guid.NewGuid():N}";
        await using var server = new CorePipeServer(pipeName, new CoreCommandHandler(module));
        server.Start();

        var prepare = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Prepare,
            Draft: new CommitmentDraft(
                CommitmentKind.Computer,
                now,
                EndAt: null,
                DurationMinutes: 60,
                InputGoal: "验证提交边界",
                OutcomeGoal: null,
                RelatedAppsOrSites:
                [
                    new CommitmentTarget(CommitmentTargetKind.Application, "notepad.exe")
                ],
                SupervisionMode.Interactive,
                ReminderSettings: null)));

        var confirm = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Confirm,
            CandidateId: prepare.Card!.CandidateId));

        Assert.True(confirm.Success);
        Assert.Equal(0, reminderSink.AttemptCount);
        await module.TickAsync();
        Assert.Equal(1, reminderSink.AttemptCount);
        await module.TickAsync();
        Assert.Equal(1, reminderSink.AttemptCount);
        Assert.Single((await module.GetSnapshotAsync()).Commitments);
    }

    [Fact]
    public async Task Desktop_supervision_actions_reach_the_authoritative_core_projection()
    {
        using var database = new TemporaryDatabase();
        var start = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var clock = new FakeClock(start);
        var activity = new FakeActivitySource
        {
            Next = new ActivityObservation(
                ActivityAvailability.Available,
                IsUserActive: true,
                ForegroundProcess: "games.exe",
                start,
                IdleDuration: TimeSpan.Zero)
        };
        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, activity, new FakeReminderSink());
        var pipeName = $"Jarvis.Core.Tests.{Guid.NewGuid():N}";
        await using var server = new CorePipeServer(pipeName, new CoreCommandHandler(module));
        server.Start();

        var prepare = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Prepare,
            Draft: new CommitmentDraft(
                CommitmentKind.Computer, start, EndAt: null, DurationMinutes: 60,
                InputGoal: "IPC监督动作", OutcomeGoal: null,
                RelatedAppsOrSites:
                [
                    new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe")
                ],
                SupervisionMode.Interactive, ReminderSettings: null)));
        var confirm = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.Confirm, CandidateId: prepare.Card!.CandidateId));
        var commitmentId = confirm.Snapshot!.Commitments.Single().Id;

        var saved = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.SaveActivityRule,
            ActivityRule: new ActivityRuleBinding(
                ActivityRuleScope.Commitment,
                commitmentId,
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Application, "games.exe"),
                    ActivityClassification.Distracting)),
            ExpectedVersion: 1));
        Assert.True(saved.Success);

        await module.TickAsync();
        var version = (await module.GetSnapshotAsync()).Commitments.Single().Version;
        var returned = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.RecordReturnIntent,
            CommitmentId: commitmentId,
            ExpectedVersion: version));
        Assert.True(returned.Success);
        Assert.Equal(start, returned.Snapshot!.ActiveSupervision!.ReturnIntentAt);

        var missingEnd = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.StartTimedRest,
            CommitmentId: commitmentId,
            ExpectedVersion: version));
        Assert.False(missingEnd.Success);
        Assert.Equal("rest_end_required", missingEnd.ErrorCode);

        var startedRest = await SendAsync(pipeName, new CoreRequest(
            CoreOperations.StartTimedRest,
            CommitmentId: commitmentId,
            RestMinutes: 10,
            ExpectedVersion: version));
        Assert.True(startedRest.Success);
        Assert.Equal(start.AddMinutes(10),
            startedRest.Snapshot!.ActiveSupervision!.ActiveRest!.EndAt);
    }

    [Fact]
    public async Task Fatal_accept_loop_failure_is_exposed_to_the_core_host()
    {
        using var database = new TemporaryDatabase();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8)));
        await using var module = await SupervisionModule.OpenAsync(
            database.Path,
            clock,
            new FakeActivitySource(),
            new FakeReminderSink());
        await using var server = new CorePipeServer(string.Empty, new CoreCommandHandler(module));

        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (server.FatalError is null)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.NotNull(server.FatalError);
    }

    private static async Task<CoreResponse> SendAsync(
        string pipeName,
        CoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, cancellationToken);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request, CoreProtocol.Json).AsMemory(),
            cancellationToken);
        var response = await reader.ReadLineAsync(cancellationToken);
        return JsonSerializer.Deserialize<CoreResponse>(response!, CoreProtocol.Json)!;
    }
}
