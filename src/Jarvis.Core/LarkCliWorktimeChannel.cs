using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class LarkCliWorktimeChannel : IWorktimeChannel
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WorktimeChannelConfiguration? _configuration;
    private Func<WorktimeInboundEvent, CancellationToken, Task>? _onEvent;
    private CancellationTokenSource? _listenersCancellation;
    private readonly List<Process> _listeners = [];
    private readonly List<Task> _listenerTasks = [];
    private readonly HashSet<string> _readyEventKeys = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private string? _lastError;
    private long _listenersStartedTimestamp;

    public bool IsHealthy
    {
        get
        {
            lock (_stateLock)
                return _configuration?.Enabled == true && _listeners.Count == 2 &&
                       _readyEventKeys.Count == 2 && _listeners.All(process => !process.HasExited) &&
                       _listenerTasks.All(task => !task.IsFaulted && !task.IsCompleted);
        }
    }

    public bool NeedsRestart
    {
        get
        {
            lock (_stateLock)
            {
                var readyTimedOut = IsReadyTimedOut(
                    _readyEventKeys.Count, _listenersStartedTimestamp, Stopwatch.GetTimestamp());
                if (readyTimedOut)
                    _lastError ??= "飞书事件监听在 20 秒内没有全部就绪，Core 将重新启动监听。";
                return _configuration?.Enabled == true &&
                       (_listeners.Count != 2 || _listeners.Any(process => process.HasExited) ||
                        _listenerTasks.Any(task => task.IsFaulted || task.IsCompleted) || readyTimedOut);
            }
        }
    }

    public string? LastError { get { lock (_stateLock) return _lastError; } }

    internal static bool IsReadyTimedOut(int readyCount, long startedTimestamp, long nowTimestamp) =>
        readyCount != 2 && startedTimestamp != 0 &&
        Stopwatch.GetElapsedTime(startedTimestamp, nowTimestamp) >= ReadyTimeout;

    internal static bool IsReadyDiagnostic(string eventKey, string line) =>
        line.StartsWith($"[event] ready event_key={eventKey}", StringComparison.Ordinal);

    public async ValueTask ConfigureAsync(
        WorktimeChannelConfiguration configuration,
        Func<WorktimeInboundEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_configuration?.Enabled == true && configuration.Enabled &&
                string.Equals(_configuration.CliPath, configuration.CliPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_configuration.Profile, configuration.Profile, StringComparison.Ordinal) &&
                IsHealthy)
            {
                // Binding only changes the target IDs. Keep the current callback task alive;
                // restarting it from inside its own callback would wait on itself.
                _configuration = configuration;
                _onEvent = onEvent;
                return;
            }
            await StopListenersAsync().ConfigureAwait(false);
            _configuration = configuration;
            _onEvent = onEvent;
            if (!configuration.Enabled) return;
            lock (_stateLock)
            {
                _lastError = null;
                _readyEventKeys.Clear();
                _listenersStartedTimestamp = Stopwatch.GetTimestamp();
            }
            var executable = ResolveExecutable(configuration.CliPath);
            // Listener lifetime belongs to Core, not to the short-lived IPC request that enabled it.
            _listenersCancellation = new CancellationTokenSource();
            StartListener(executable, configuration.Profile, "im.message.receive_v1", _listenersCancellation.Token);
            StartListener(executable, configuration.Profile, "card.action.trigger", _listenersCancellation.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<WorktimeDeliveryResult> SendAsync(
        MobileEscalationCard card,
        CancellationToken cancellationToken)
    {
        if (_configuration?.Enabled != true || string.IsNullOrWhiteSpace(_configuration.BoundUserId))
            return new WorktimeDeliveryResult(false, ErrorCode: "lark_not_bound", Message: "飞书用户尚未绑定。");
        var cardJson = LarkEscalationCardJson.Build(card, interactive: true);
        var payload = JsonSerializer.Serialize(new
        {
            receive_id = _configuration.BoundUserId,
            msg_type = "interactive",
            content = cardJson,
            uuid = card.CardId.ToString("N")
        });
        var result = await RunCliAsync(
            [
                "--profile", _configuration.Profile,
                "api", "POST", "/open-apis/im/v1/messages",
                "--as", "bot", "--params", "{\"receive_id_type\":\"open_id\"}",
                "--data", "-"
            ],
            payload,
            cancellationToken).ConfigureAwait(false);
        var messageId = result.ExitCode == 0 ? FindStringProperty(result.StandardOutput, "message_id") : null;
        return messageId is null
            ? new WorktimeDeliveryResult(
                false, ErrorCode: result.ExitCode == 0 ? "lark_message_id_missing" : "lark_send_failed",
                Message: SafeError(result.StandardError))
            : new WorktimeDeliveryResult(true, messageId);
    }

    public async ValueTask<WorktimeDeliveryResult> SendDailyReviewInvitationAsync(
        Guid sessionId,
        DateOnly reviewDate,
        bool followUp,
        CancellationToken cancellationToken)
    {
        if (_configuration?.Enabled != true || string.IsNullOrWhiteSpace(_configuration.BoundUserId))
            return new WorktimeDeliveryResult(false, ErrorCode: "lark_not_bound", Message: "飞书用户尚未绑定。");
        var payload = JsonSerializer.Serialize(new
        {
            receive_id = _configuration.BoundUserId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new
            {
                text = $"Jarvis 每日复盘（{reviewDate:yyyy-MM-dd}）{(followUp ? "还在等你处理" : "已准备好")}。" +
                       "可回复：现在复盘 / 30分钟后复盘 / 60分钟后复盘 / 跳过复盘。"
            }),
            uuid = followUp ? $"{sessionId:N}-followup" : sessionId.ToString("N")
        });
        var result = await RunCliAsync(
            [
                "--profile", _configuration.Profile,
                "api", "POST", "/open-apis/im/v1/messages",
                "--as", "bot", "--params", "{\"receive_id_type\":\"open_id\"}",
                "--data", "-"
            ],
            payload,
            cancellationToken).ConfigureAwait(false);
        var messageId = result.ExitCode == 0 ? FindStringProperty(result.StandardOutput, "message_id") : null;
        return messageId is null
            ? new WorktimeDeliveryResult(false, ErrorCode: "lark_review_invite_failed", Message: SafeError(result.StandardError))
            : new WorktimeDeliveryResult(true, messageId);
    }

    public async ValueTask<WorktimeDeliveryResult> SendTextAsync(
        string recipientOpenId,
        string text,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (_configuration?.Enabled != true)
            return new WorktimeDeliveryResult(false, ErrorCode: "lark_not_enabled", Message: "飞书通道尚未启用。");
        var payload = JsonSerializer.Serialize(new
        {
            receive_id = recipientOpenId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text }),
            uuid = idempotencyKey.ToString("N")
        });
        var result = await RunCliAsync(
            [
                "--profile", _configuration.Profile,
                "api", "POST", "/open-apis/im/v1/messages",
                "--as", "bot", "--params", "{\"receive_id_type\":\"open_id\"}",
                "--data", "-"
            ],
            payload,
            cancellationToken).ConfigureAwait(false);
        var messageId = result.ExitCode == 0 ? FindStringProperty(result.StandardOutput, "message_id") : null;
        return messageId is null
            ? new WorktimeDeliveryResult(false, ErrorCode: "lark_text_send_failed", Message: SafeError(result.StandardError))
            : new WorktimeDeliveryResult(true, messageId);
    }

    public async ValueTask<bool> InvalidateAsync(
        Guid cardId,
        string platformMessageId,
        string resultText,
        CancellationToken cancellationToken)
    {
        if (_configuration?.Enabled != true) return false;
        var payload = JsonSerializer.Serialize(new
        {
            content = LarkEscalationCardJson.BuildExpired(cardId, resultText)
        });
        var result = await RunCliAsync(
            [
                "--profile", _configuration.Profile,
                "api", "PATCH", $"/open-apis/im/v1/messages/{platformMessageId}",
                "--as", "bot", "--data", "-"
            ],
            payload,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListenersAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private void StartListener(
        string executable,
        string profile,
        string eventKey,
        CancellationToken cancellationToken)
    {
        var startInfo = NewStartInfo(executable, redirectInput: true);
        foreach (var argument in new[]
                 {
                     "--profile", profile, "event", "consume", eventKey, "--as", "bot"
                 })
            startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"无法启动飞书事件监听：{eventKey}");
        lock (_stateLock)
        {
            _listeners.Add(process);
            _listenerTasks.Add(ReadListenerAsync(process, eventKey, cancellationToken));
        }
    }

    private async Task ReadListenerAsync(
        Process process,
        string eventKey,
        CancellationToken cancellationToken)
    {
        var stderr = DrainStderrAsync(process, eventKey, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (IsReadyDiagnostic(eventKey, line))
                {
                    lock (_stateLock) _readyEventKeys.Add(eventKey);
                    continue;
                }
                try
                {
                    var inbound = ParseInbound(eventKey, line);
                    if (inbound is not null && _onEvent is not null)
                        await _onEvent(inbound, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    // One malformed event must not stop the long-lived listener.
                }
                catch (InvalidOperationException)
                {
                    // A stale or invalid callback is isolated to that callback.
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (_stateLock) _lastError = $"飞书事件处理失败：{exception.GetType().Name}";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await stderr.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                lock (_stateLock) _lastError ??= $"飞书监听进程已退出：{eventKey}";
        }
    }

    private async Task DrainStderrAsync(
        Process process,
        string eventKey,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (IsReadyDiagnostic(eventKey, line))
                {
                    lock (_stateLock) _readyEventKeys.Add(eventKey);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private WorktimeInboundEvent? ParseInbound(string eventKey, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventId = String(root, "event_id");
        var sender = eventKey == "card.action.trigger"
            ? String(root, "operator_id")
            : String(root, "sender_id");
        if (eventId is null || sender is null) return null;
        var receivedAt = ParseTimestamp(String(root, "timestamp")) ?? DateTimeOffset.UtcNow;
        if (eventKey == "im.message.receive_v1")
        {
            if (!string.Equals(String(root, "message_type"), "text", StringComparison.Ordinal) ||
                !string.Equals(String(root, "chat_type"), "p2p", StringComparison.Ordinal))
                return null;
            return new WorktimeTextInboundEvent(
                eventId, sender, receivedAt, String(root, "chat_id") ?? "",
                String(root, "message_id") ?? "", String(root, "content") ?? "");
        }

        var actionValue = String(root, "action_value");
        if (actionValue is null) return null;
        using var actionDocument = JsonDocument.Parse(actionValue);
        var action = actionDocument.RootElement;
        if (!Guid.TryParse(String(action, "card_id"), out var cardId) ||
            !Guid.TryParse(String(action, "commitment_id"), out var commitmentId) ||
            !action.TryGetProperty("version", out var versionNode) ||
            !Enum.TryParse<WorktimeActionKind>(String(action, "action"), true, out var actionKind))
            return null;
        var restEnd = action.TryGetProperty("rest_end_at", out var restNode) &&
                      DateTimeOffset.TryParse(restNode.GetString(), CultureInfo.InvariantCulture,
                          DateTimeStyles.RoundtripKind, out var parsedRest)
            ? parsedRest
            : action.TryGetProperty("rest_minutes", out var minutesNode) &&
              minutesNode.TryGetInt32(out var restMinutes) && restMinutes > 0
                ? receivedAt.AddMinutes(restMinutes)
                : (DateTimeOffset?)null;
        return new WorktimeActionInboundEvent(
            eventId, sender, receivedAt, String(root, "token") ?? "", cardId, commitmentId,
            versionNode.GetInt32(), actionKind, restEnd);
    }

    private async Task StopListenersAsync()
    {
        if (_listenersCancellation is not null)
        {
            await _listenersCancellation.CancelAsync().ConfigureAwait(false);
            _listenersCancellation.Dispose();
            _listenersCancellation = null;
        }

        Process[] listeners;
        Task[] listenerTasks;
        lock (_stateLock)
        {
            listeners = [.. _listeners];
            listenerTasks = [.. _listenerTasks];
            _listeners.Clear();
            _listenerTasks.Clear();
            _readyEventKeys.Clear();
            _listenersStartedTimestamp = 0;
        }
        foreach (var process in listeners)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        try { await Task.WhenAll(listenerTasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task<CliResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable(_configuration!.CliPath);
        var startInfo = NewStartInfo(executable, redirectInput: true);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return new CliResult(-1, "", "timeout");
        }

        return new CliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static ProcessStartInfo NewStartInfo(string executable, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (redirectInput) startInfo.StandardInputEncoding = new UTF8Encoding(false);
        startInfo.Environment["LARKSUITE_CLI_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["LARKSUITE_CLI_NO_SKILLS_NOTIFIER"] = "1";
        return startInfo;
    }

    private static string ResolveExecutable(string configured)
    {
        if (File.Exists(configured)) return Path.GetFullPath(configured);
        if (!string.Equals(configured, "lark-cli", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configured, "lark-cli.cmd", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("找不到配置的 lark-cli。", configured);
        var packaged = @"D:\Application\nodejs\node_global\node_modules\@larksuite\cli\bin\lark-cli.exe";
        if (File.Exists(packaged)) return packaged;
        throw new FileNotFoundException("找不到 lark-cli；请在 Jarvis 中配置完整路径。");
    }

    private static string? FindStringProperty(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Find(document.RootElement, name);
        }
        catch (JsonException) { return null; }
    }

    private static string? Find(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = Find(property.Value, name);
                if (nested is not null) return nested;
            }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                var nested = Find(item, name);
                if (nested is not null) return nested;
            }
        return null;
    }

    private static string SafeError(string stderr)
    {
        try
        {
            var root = JsonNode.Parse(stderr) as JsonObject;
            var error = root?["error"] as JsonObject ?? root;
            var code = error?["code"]?.ToString();
            return string.IsNullOrWhiteSpace(code) ? "飞书 CLI 请求失败。" : $"飞书 CLI 请求失败（code={code}）。";
        }
        catch (JsonException) { return "飞书 CLI 请求失败。"; }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (long.TryParse(value, CultureInfo.InvariantCulture, out var number))
            return number >= 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number);
        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}

internal static class LarkEscalationCardJson
{
    public static string Build(MobileEscalationCard card, bool interactive)
    {
        var elapsed = card.CountedDeviation ?? (card.SentAt - card.DeviationStartedAt);
        var elements = new JsonArray
        {
            Highlight($"**已连续偏离 {Math.Floor(elapsed.TotalMinutes):0} 分钟**\n请决定下一步。", "yellow-50"),
            Highlight(
                $"**承诺**  {Escape(card.CommitmentSummary)}\n" +
                $"**计划**  {card.PlannedStartAt.ToLocalTime():MM-dd HH:mm} – {card.PlannedEndAt.ToLocalTime():HH:mm}\n" +
                $"**当前分类**  {Classification(card.Classification)}",
                "grey-50")
        };
        if (interactive)
        {
            elements.Add(new JsonObject
            {
                ["tag"] = "column_set",
                ["flex_mode"] = "flow",
                ["horizontal_spacing"] = "8px",
                ["columns"] = new JsonArray(
                    ButtonColumn(card, WorktimeActionKind.ReturnNow, "马上回去", "primary_filled"),
                    ButtonColumn(card, WorktimeActionKind.StartRest,
                        $"休息 {card.DefaultRestMinutes} 分钟", "default"),
                    ButtonColumn(card, WorktimeActionKind.AdjustCommitment, "调整承诺", "default"),
                    ButtonColumn(card, WorktimeActionKind.Misclassification, "误判", "default"))
            });
        }

        return new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject
            {
                ["update_multi"] = true,
                ["width_mode"] = "compact",
                ["enable_forward"] = false,
                ["summary"] = new JsonObject { ["content"] = card.PrivacyPreview }
            },
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = "Jarvis 工作提醒" },
                ["subtitle"] = new JsonObject { ["tag"] = "plain_text", ["content"] = $"第 {card.Sequence}/3 次手机提醒" },
                ["template"] = "yellow",
                ["icon"] = new JsonObject { ["tag"] = "standard_icon", ["token"] = "todo_colorful" }
            },
            ["body"] = new JsonObject
            {
                ["direction"] = "vertical",
                ["padding"] = "12px 12px 20px 12px",
                ["vertical_spacing"] = "12px",
                ["elements"] = elements
            }
        }.ToJsonString();
    }

    public static string BuildExpired(Guid cardId, string resultText) => new JsonObject
    {
        ["schema"] = "2.0",
        ["config"] = new JsonObject
        {
            ["update_multi"] = true,
            ["width_mode"] = "compact",
            ["enable_forward"] = false,
            ["summary"] = new JsonObject { ["content"] = "Jarvis 工作提醒：状态已更新" }
        },
        ["header"] = new JsonObject
        {
            ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = "提醒状态已更新" },
            ["template"] = "grey",
            ["icon"] = new JsonObject { ["tag"] = "standard_icon", ["token"] = "todo_colorful" }
        },
        ["body"] = new JsonObject
        {
            ["direction"] = "vertical",
            ["padding"] = "12px 12px 20px 12px",
            ["elements"] = new JsonArray(Highlight(
                $"**{Escape(resultText)}**\n这张卡已转为只读；请查看 Jarvis 当前状态。", "grey-50"))
        }
    }.ToJsonString();

    private static JsonObject Highlight(string content, string background) => new()
    {
        ["tag"] = "column_set",
        ["flex_mode"] = "none",
        ["columns"] = new JsonArray(new JsonObject
        {
            ["tag"] = "column",
            ["width"] = "weighted",
            ["weight"] = 1,
            ["background_style"] = background,
            ["padding"] = "12px",
            ["elements"] = new JsonArray(new JsonObject
            {
                ["tag"] = "markdown",
                ["content"] = content
            })
        })
    };

    private static JsonObject ButtonColumn(
        MobileEscalationCard card,
        WorktimeActionKind action,
        string text,
        string type)
    {
        var value = new JsonObject
        {
            ["card_id"] = card.CardId.ToString("D"),
            ["commitment_id"] = card.CommitmentId.ToString("D"),
            ["version"] = card.CommitmentVersion,
            ["action"] = action.ToString()
        };
        if (action == WorktimeActionKind.StartRest)
            value["rest_minutes"] = card.DefaultRestMinutes;
        return new JsonObject
        {
            ["tag"] = "column",
            ["width"] = "weighted",
            ["weight"] = 1,
            ["elements"] = new JsonArray(new JsonObject
            {
                ["tag"] = "button",
                ["text"] = new JsonObject { ["tag"] = "plain_text", ["content"] = text },
                ["type"] = type,
                ["size"] = "small",
                ["width"] = "fill",
                ["behaviors"] = new JsonArray(new JsonObject
                {
                    ["type"] = "callback",
                    ["value"] = value
                })
            })
        };
    }

    private static string Classification(ActivityClassification classification) => classification switch
    {
        ActivityClassification.Related => "相关",
        ActivityClassification.Distracting => "分心",
        _ => "未确定"
    };

    private static string Escape(string text) => text.Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
