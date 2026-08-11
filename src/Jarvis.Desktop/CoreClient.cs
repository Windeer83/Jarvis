using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Desktop;

internal sealed class CoreClient
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task<CoreResponse> SendAsync(
        CoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                CoreProtocol.PipeName,
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

            var json = JsonSerializer.Serialize(request, CoreProtocol.Json);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            var responseJson = await reader.ReadLineAsync(cancellationToken);
            return responseJson is null
                ? new CoreResponse(false, "core_disconnected", "Jarvis Core 在返回状态前断开连接。")
                : JsonSerializer.Deserialize<CoreResponse>(responseJson, CoreProtocol.Json)
                  ?? new CoreResponse(false, "invalid_response", "Jarvis Core 返回了无法读取的状态。");
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or OperationCanceledException or JsonException)
        {
            return new CoreResponse(
                false,
                "core_unavailable",
                exception is OperationCanceledException
                    ? "连接 Jarvis Core 的操作已取消。"
                    : "无法连接 Jarvis Core，请先启动 Core。");
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
