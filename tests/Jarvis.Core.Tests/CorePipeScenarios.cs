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

    private static async Task<CoreResponse> SendAsync(string pipeName, CoreRequest request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);

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

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, CoreProtocol.Json));
        var response = await reader.ReadLineAsync();
        return JsonSerializer.Deserialize<CoreResponse>(response!, CoreProtocol.Json)!;
    }
}
