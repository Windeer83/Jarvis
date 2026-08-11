using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class CoreCommandHandler(SupervisionModule supervision)
{
    public async Task<CoreResponse> HandleAsync(
        CoreRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case CoreOperations.Prepare when request.Draft is not null:
                {
                    var result = await supervision.PrepareAsync(request.Draft, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? new CoreResponse(true, Card: result.Value)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.Confirm when request.CandidateId is not null:
                {
                    var result = await supervision.ConfirmAsync(request.CandidateId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Success)
                    {
                        return Failure(result.ErrorCode, result.Message);
                    }

                    await supervision.TickAsync(cancellationToken).ConfigureAwait(false);
                    return new CoreResponse(
                        true,
                        Message: "工作承诺已正式成立。",
                        Snapshot: await supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));
                }

            case CoreOperations.ConfirmOfflineStarted when request.CommitmentId is not null:
                {
                    var result = await supervision.ConfirmOfflineStartedAsync(
                            request.CommitmentId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? new CoreResponse(
                            true,
                            Message: "已记录线下工作开始确认。",
                            Snapshot: await supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false))
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.GetSnapshot:
                return new CoreResponse(
                    true,
                    Snapshot: await supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));

            default:
                return Failure("invalid_request", "Core 无法识别这项操作或请求缺少必要内容。");
        }
    }

    private static CoreResponse Failure(string? errorCode, string? message) =>
        new(false, errorCode ?? "operation_failed", message ?? "操作失败。");
}

internal sealed class CorePipeServer(
    string pipeName,
    CoreCommandHandler handler) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;

    public void Start()
    {
        _runTask ??= RunAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
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

        CoreResponse response;
        try
        {
            var json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var request = json is null
                ? null
                : JsonSerializer.Deserialize<CoreRequest>(json, CoreProtocol.Json);
            response = request is null
                ? new CoreResponse(false, "invalid_json", "Core 收到的请求不是有效 JSON。")
                : await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = new CoreResponse(false, "invalid_json", "Core 收到的请求不是有效 JSON。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new CoreResponse(false, "core_error", $"Core 操作失败：{exception.Message}");
        }

        var responseJson = JsonSerializer.Serialize(response, CoreProtocol.Json);
        await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
