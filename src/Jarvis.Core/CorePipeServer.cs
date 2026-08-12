using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class CoreCommandHandler(
    SupervisionModule supervision,
    Func<CancellationToken, Task<SupervisionSnapshot>>? projectionReader = null)
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

                    var projection = await TryReadProjectionAfterMutationAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return new CoreResponse(
                        true,
                        Message: projection.IsAvailable
                            ? "工作承诺已正式成立。"
                            : "工作承诺已正式成立；当前状态暂时无法刷新，请稍后刷新。",
                        Snapshot: projection.Snapshot);
                }

            case CoreOperations.ConfirmOfflineStarted when request.CommitmentId is not null:
                {
                    var result = await supervision.ConfirmOfflineStartedAsync(
                            request.CommitmentId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Success)
                    {
                        return Failure(result.ErrorCode, result.Message);
                    }

                    var projection = await TryReadProjectionAfterMutationAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return new CoreResponse(
                        true,
                        Message: projection.IsAvailable
                            ? "已记录线下工作开始确认。"
                            : "已记录线下工作开始确认；当前状态暂时无法刷新，请稍后刷新。",
                        Snapshot: projection.Snapshot);
                }

            case CoreOperations.CreateTemplate when request.TemplateDraft is not null:
                {
                    var result = await supervision.CreateTemplateAsync(
                            request.TemplateDraft, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                                "模板已保存；保存模板不会创建工作承诺。",
                                cancellationToken,
                                template: result.Value)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.UpdateTemplate
                when request.TemplateId is not null && request.TemplateDraft is not null:
                {
                    var result = await supervision.UpdateTemplateAsync(
                            request.TemplateId.Value, request.TemplateDraft, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                                "模板已更新；既有承诺和历史发生项没有改变。",
                                cancellationToken,
                                template: result.Value)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.ArchiveTemplate when request.TemplateId is not null:
                {
                    var result = await supervision.ArchiveTemplateAsync(
                            request.TemplateId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                                "模板已归档；由它生成的承诺仍然保留。",
                                cancellationToken,
                                template: result.Value)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.PrepareFromTemplate when request.TemplateCommitmentDraft is not null:
                {
                    var result = await supervision.PrepareFromTemplateAsync(
                            request.TemplateCommitmentDraft, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? new CoreResponse(true, Card: result.Value)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.PrepareRecurrence when request.RecurrenceDraft is not null:
                {
                    var result = await supervision.PrepareRecurrenceAsync(
                            request.RecurrenceDraft, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? new CoreResponse(true, RecurrenceCard: result.Value)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.ConfirmRecurrence when request.CandidateId is not null:
                {
                    var result = await supervision.ConfirmRecurrenceAsync(
                            request.CandidateId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                                "重复安排已确认，每个日期都已生成独立工作承诺。",
                                cancellationToken,
                                recurrencePlan: result.Value)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.PrepareRecurrenceChange when request.RecurrenceChange is not null:
                {
                    var result = await supervision.PrepareRecurrenceChangeAsync(
                            request.RecurrenceChange, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? new CoreResponse(true, RecurrenceChangeCard: result.Value)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.ConfirmRecurrenceChange when request.CandidateId is not null:
                {
                    var result = await supervision.ConfirmRecurrenceChangeAsync(
                            request.CandidateId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                                "重复安排已按所选作用范围更新，历史记录仍保留。",
                                cancellationToken,
                                recurrencePlan: result.Value)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.SaveActivityRule when request.ActivityRule is not null:
                {
                    var result = await supervision.SaveActivityRuleAsync(
                        request.ActivityRule, cancellationToken).ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync("活动分类规则已保存。", cancellationToken)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.ClassifyCurrentActivity
                when request.CommitmentId is not null &&
                     request.Classification is not null && request.RuleScope is not null:
                {
                    var result = await supervision.ClassifyCurrentActivityAsync(
                        request.CommitmentId.Value,
                        request.Classification.Value,
                        request.RuleScope.Value,
                        request.Note,
                        cancellationToken).ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync("已保存分类并纠正本次活动记录。", cancellationToken)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.RecordReturnIntent when request.CommitmentId is not null:
                {
                    var result = await supervision.RecordReturnIntentAsync(
                        request.CommitmentId.Value, cancellationToken).ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                            "已记录马上回去；偏离计时会在稳定相关两分钟后清零。", cancellationToken)
                            .ConfigureAwait(false)
                        : Failure(result.ErrorCode, result.Message);
                }

            case CoreOperations.RespondToRestPrompt
                when request.CommitmentId is not null && request.IsResting is not null:
                {
                    var result = await supervision.RespondToRestPromptAsync(
                        request.CommitmentId.Value, request.IsResting.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Success && result.ErrorCode != "rest_denied")
                    {
                        return Failure(result.ErrorCode, result.Message);
                    }

                    return await SuccessAfterMutationAsync(
                        result.Success
                            ? $"已确认限时休息至 {result.Value!.EndAt.ToLocalTime():HH:mm}。"
                            : result.Message!,
                        cancellationToken).ConfigureAwait(false);
                }

            case CoreOperations.StartTimedRest when request.CommitmentId is not null:
                {
                    var result = await supervision.StartTimedRestAsync(
                        request.CommitmentId.Value, request.RestEndAt, cancellationToken)
                        .ConfigureAwait(false);
                    return result.Success
                        ? await SuccessAfterMutationAsync(
                            $"已开始限时休息，{result.Value!.EndAt.ToLocalTime():HH:mm} 自动恢复监督。",
                            cancellationToken).ConfigureAwait(false)
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

    private async Task<CoreResponse> SuccessAfterMutationAsync(
        string message,
        CancellationToken cancellationToken,
        CommitmentTemplateView? template = null,
        RecurrencePlanView? recurrencePlan = null)
    {
        var projection = await TryReadProjectionAfterMutationAsync(cancellationToken)
            .ConfigureAwait(false);
        return new CoreResponse(
            true,
            Message: projection.IsAvailable
                ? message
                : $"{message} 正式写入已成功，但当前状态暂时无法刷新，请立即刷新状态。",
            Snapshot: projection.Snapshot,
            Template: template,
            RecurrencePlan: recurrencePlan);
    }

    private async Task<(bool IsAvailable, SupervisionSnapshot? Snapshot)>
        TryReadProjectionAfterMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (
                true,
                await (projectionReader is null
                    ? supervision.GetSnapshotAsync(cancellationToken)
                    : projectionReader(cancellationToken)).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (false, null);
        }
    }
}

internal sealed class CorePipeServer(
    string pipeName,
    CoreCommandHandler handler) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;
    private Exception? _fatalError;

    public Exception? FatalError => Volatile.Read(ref _fatalError);

    public void Start()
    {
        _runTask ??= RunGuardedAsync(_shutdown.Token);
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

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fatalError, exception);
        }
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
            try
            {
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client may close after connecting or before reading its response.
                // That failure belongs to this connection; the Core accept loop stays alive.
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
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
