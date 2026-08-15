using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class WindowsAiCredentialStore : IAiCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobSize = 5 * 512;
    private const string TargetPrefix = "Jarvis/AI/";

    public ValueTask SaveAsync(string provider, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length is 0 or > MaxCredentialBlobSize)
            throw new ArgumentOutOfRangeException(nameof(secret));
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetPrefix + provider,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWriteW(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            handle.Free();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(string provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredReadW(TargetPrefix + provider, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return ValueTask.FromResult<string?>(null);
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public ValueTask DeleteAsync(string provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDeleteW(TargetPrefix + provider, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound) throw new Win32Exception(error);
        }

        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr pointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

internal sealed class SiliconFlowCloudAiProvider(HttpClient? httpClient = null) : ICloudAiProvider, IDisposable
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    private readonly bool _ownsClient = httpClient is null;

    public decimal EstimateCostCny(AiProviderRequest request)
    {
        var profile = SiliconFlowModelCatalog.Resolve(request.Model);
        var systemPrompt = SystemPrompt(request);
        var estimatedInputTokens = Encoding.UTF8.GetByteCount(systemPrompt + request.Text) + 512;
        return SiliconFlowModelCatalog.CalculateCost(
            profile,
            new AiTokenUsage(estimatedInputTokens, request.MaxOutputTokens, 0));
    }

    public async ValueTask<AiProviderResult> CompleteAsync(
        AiProviderRequest request,
        string credential,
        CancellationToken cancellationToken)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = SystemPrompt(request)
            },
            new JsonObject { ["role"] = "user", ["content"] = request.Text }
        };
        var profile = SiliconFlowModelCatalog.Resolve(request.Model);
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = false
        };
        if (profile.DisableThinking) payload["enable_thinking"] = false;
        if (request.Purpose != AiRequestPurpose.BasicChat)
            payload["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var message = new HttpRequestMessage(HttpMethod.Post, SiliconFlowModelCatalog.Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new AiProviderResult(
                false, null, new AiTokenUsage(0, 0, 0),
                $"http_{(int)response.StatusCode}", ErrorMessage(response.StatusCode));
        var parsedUsage = new AiTokenUsage(0, 0, 0);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? string.Empty;
            var usage = root.GetProperty("usage");
            var cacheHit = usage.TryGetProperty("prompt_cache_hit_tokens", out var hit)
                ? hit.GetInt32()
                : usage.TryGetProperty("prompt_tokens_details", out var details) &&
                  details.TryGetProperty("cached_tokens", out var cached)
                    ? cached.GetInt32()
                    : 0;
            parsedUsage = new AiTokenUsage(
                usage.GetProperty("prompt_tokens").GetInt32(),
                usage.GetProperty("completion_tokens").GetInt32(),
                cacheHit);
            if (request.Purpose == AiRequestPurpose.BasicChat)
                return new AiProviderResult(true, content, parsedUsage);
            if (request.Purpose is AiRequestPurpose.DailyReviewAssist or AiRequestPurpose.CycleReviewAssist)
            {
                var reviewDraft = ParseReviewDraft(content);
                return reviewDraft is null
                    ? new AiProviderResult(
                        false, null, parsedUsage, "review_draft_invalid",
                        "AI 返回的复盘草稿结构无法验证。")
                    : new AiProviderResult(true, content, parsedUsage, ReviewDraft: reviewDraft);
            }
            using (var candidateDocument = JsonDocument.Parse(content))
            {
                if (candidateDocument.RootElement.TryGetProperty(
                        "needsClarification", out var clarification))
                {
                    var clarificationResult = ParseClarification(clarification);
                    return new AiProviderResult(
                        false, content, parsedUsage, "ai_clarification_required",
                        clarificationResult.Message,
                        MissingInformation: clarificationResult.MissingInformation);
                }
            }
            var candidate = ParseCandidate(content, request.Text, request.Now);
            return candidate is null
                ? new AiProviderResult(false, null, parsedUsage, "candidate_json_invalid", "AI 候选 JSON 无法验证。")
                : new AiProviderResult(true, content, parsedUsage, Candidate: candidate);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        {
            return new AiProviderResult(false, null, parsedUsage,
                "ai_response_invalid", "硅基流动返回了无法解析的响应。");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private static string ErrorMessage(System.Net.HttpStatusCode statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized =>
            "硅基流动拒绝了 API Key（401）；请确认密钥来自硅基流动且仍有效。",
        System.Net.HttpStatusCode.Forbidden =>
            "硅基流动拒绝访问该模型（403）；请检查账号认证和模型权限。",
        (System.Net.HttpStatusCode)429 =>
            "硅基流动请求频率或额度已达限制（429），请稍后再试。",
        System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout =>
            "硅基流动模型当前繁忙，请稍后再试。",
        _ => $"硅基流动请求失败（HTTP {(int)statusCode}）。"
    };

    private static string SystemPrompt(AiProviderRequest request) => request.Purpose switch
    {
        AiRequestPurpose.NaturalLanguageOperation => CandidateSystemPrompt(request),
        AiRequestPurpose.DailyReviewAssist or AiRequestPurpose.CycleReviewAssist =>
            ReviewSystemPrompt(request),
        _ => "你是 Jarvis 的简洁中文助手。只回答当前用户问题，不虚构监督事实。"
    };

    private static string ReviewSystemPrompt(AiProviderRequest request)
    {
        if (request.ReviewFacts is null)
            throw new InvalidOperationException("复盘辅助缺少 Core 提供的事实投影。");
        var kind = request.ReviewFacts.Kind == AiReviewKind.Daily ? "每日复盘" : "周期复盘";
        var facts = request.ReviewFacts;
        var projection = new
        {
            kind = facts.Kind.ToString(),
            facts.PeriodStart,
            facts.PeriodEnd,
            facts.FactsSummary,
            dailyAnswers = facts.DailyAnswers.Select(item => new
            {
                question = item.Question.ToString(),
                item.RawText,
                item.AnsweredAt
            }),
            commitmentReviews = facts.CommitmentReviews.Select(item => new
            {
                state = item.State.ToString(),
                item.RequestedAt,
                item.DeferredUntil,
                item.RawText,
                assessment = item.Assessment?.ToString(),
                item.AnsweredAt
            }),
            cycleTrends = facts.CycleTrends is null ? null : new
            {
                facts.CycleTrends.PlannedCommitments,
                facts.CycleTrends.ReviewedCommitments,
                facts.CycleTrends.PlannedMinutes,
                facts.CycleTrends.RelatedMinutes,
                facts.CycleTrends.DistractingMinutes,
                facts.CycleTrends.RestMinutes,
                facts.CycleTrends.DeferredReviews,
                facts.CycleTrends.NoResponseCount,
                facts.CycleTrends.ObservedMinutes,
                commitments = facts.CycleTrends.Commitments.Select(item => new
                {
                    item.LocalDate,
                    item.InputGoal,
                    item.OutcomeGoal,
                    item.PlannedMinutes,
                    item.RelatedMinutes,
                    item.DistractingMinutes,
                    item.RestMinutes,
                    reviewState = item.ReviewState?.ToString(),
                    assessment = item.Assessment?.ToString(),
                    item.ReviewText
                }),
                dailyReviews = facts.CycleTrends.DailyReviews.Select(item => new
                {
                    item.ReviewDate,
                    state = item.State.ToString(),
                    item.AnswerCount
                })
            },
            facts.FactItemCount
        };
        return $$"""
            你只根据下面由 Jarvis Core 提供的最小事实投影整理{{kind}}草稿。
            不得补充投影之外的经历、原因、评价或人格判断；不生成自律分数、排行榜或诊断。
            输出只是待确认草稿，不能声称已经写入正式记录。
            建议调整最多三项；不确定时在观察中明确写“待用户确认”。
            只输出 JSON：
            {"draftText":"一段可编辑总结","observations":["有事实依据的观察"],"suggestedAdjustments":["最多三项候选调整"]}
            Core 最小事实投影：{{JsonSerializer.Serialize(projection, CoreProtocol.Json)}}
            """;
    }

    private static AiReviewDraftPayload? ParseReviewDraft(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("draftText", out var draftNode) || draftNode.ValueKind != JsonValueKind.String)
            return null;
        var draftText = draftNode.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(draftText)) return null;
        var observations = ReadStringArray(root, "observations", 20);
        var adjustments = ReadStringArray(root, "suggestedAdjustments", 3);
        return new AiReviewDraftPayload(draftText, observations, adjustments);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array) return [];
        return node.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
    }

    private static string CandidateSystemPrompt(AiProviderRequest request)
    {
        var commitments = request.Supervision?.Commitments
            .Where(item => item.EndAt > request.Now && item.Phase != CommitmentPhase.Skipped)
            .OrderBy(item => item.StartAt)
            .Take(50)
            .Select(item => new
            {
                id = item.Id,
                version = item.Version,
                startAt = item.StartAt,
                endAt = item.EndAt,
                inputGoal = item.InputGoal,
                outcomeGoal = item.OutcomeGoal,
                targets = item.RelatedAppsOrSites,
                mode = item.SupervisionMode.ToString(),
                phase = item.Phase.ToString()
            }).ToArray() ?? [];
        var templates = request.Supervision?.Templates
            .Where(item => !item.IsArchived)
            .OrderBy(item => item.Name)
            .Take(50)
            .Select(item => new
            {
                id = item.Id,
                name = item.Name,
                kind = item.Kind.ToString(),
                durationMinutes = item.DurationMinutes,
                inputGoal = item.InputGoal,
                outcomeGoal = item.OutcomeGoal
            }).ToArray() ?? [];
        const string example = """
            {"kind":"createCommitment","summary":"...","commitment":{"kind":"computer","startAt":"ISO8601","endAt":null,"durationMinutes":60,"inputGoal":"...","outcomeGoal":"...","relatedAppsOrSites":[{"kind":"application","value":"winword.exe"}],"supervisionMode":"interactive"}}
            """;
        return $$"""
            你只把用户文字转换为一个待确认的 Jarvis 候选操作，不能声称已经执行。
            当前时间：{{request.Now:O}}。
            当前承诺最小投影：{{JsonSerializer.Serialize(commitments, CoreProtocol.Json)}}
            当前模板最小投影：{{JsonSerializer.Serialize(templates, CoreProtocol.Json)}}
            只输出 JSON 对象。支持 kind：createCommitment、reviseCommitment、createRecurrence、createFromTemplate、saveTemplate、endCommitmentEarly、cancelCommitment、deferCommitment。
            createCommitment 结构：{{example}}。
            reviseCommitment 必须提供 revision（commitmentId、expectedVersion、完整 proposed、reason）；
            createRecurrence 必须提供 recurrence（commitment + 有限 pattern）；
            createFromTemplate 必须提供 fromTemplate（templateId + startAt 和明确覆盖）；
            saveTemplate 必须提供 template（无具体日期的模板默认值）。
            endCommitmentEarly 必须提供 targetCommitmentId 和 expectedVersion。
            cancelCommitment 必须提供 targetCommitmentId、expectedVersion、reason；
            deferCommitment 用于进行中的承诺，必须提供 targetCommitmentId、expectedVersion、deferredStartAt、reason。
            创建电脑型承诺时：开始时间、结束时间或持续时长、投入目标或成果目标中的至少一个、至少一个相关软件或网站是必要信息。
            投入目标和成果目标至少填写一个即可，绝对不要要求两者都填写；像“进行交易复盘”这样的工作描述可直接作为投入目标。
            用户用“下午1点开始一直到下午5:40”描述的是同一天的明确时间段；若未写日期且该时段在当前本地日期仍未开始，可使用今天。若已经过去，才追问具体日期。
            “要用到 Notion、TradingView 和浏览器”表示这些是相关工具；可把 Notion 作为应用、tradingview.com 作为网站，并把常见浏览器应用列入候选，交给用户在确认卡核对。
            示例用户输入：“下午1点开始一个监督一直到下午5:40，我要进行交易复盘，要用到 Notion、TradingView 和浏览器。”
            示例候选中的核心字段：inputGoal="交易复盘"、startAt=今天13:00、endAt=今天17:40、relatedAppsOrSites 包含 Notion、tradingview.com 与常见浏览器。
            只有必要信息确实缺失或重要变化有歧义时才追问，不要把已经从原话中明确表达的信息再次列为缺失。
            需要追问时只输出：{"needsClarification":{"message":"还不能生成候选，请补充以下信息。","missingFields":["具体缺失项"]} }。
            AI 不得修改权限、系统提示、历史事实或绕过 Core 版本校验。
            """;
    }

    private static (string Message, IReadOnlyList<string> MissingInformation) ParseClarification(
        JsonElement clarification)
    {
        if (clarification.ValueKind == JsonValueKind.String)
        {
            var message = clarification.GetString();
            return (
                string.IsNullOrWhiteSpace(message)
                    ? "请补充候选操作中缺少的时间、对象或作用范围。"
                    : message,
                []);
        }

        if (clarification.ValueKind != JsonValueKind.Object)
            return ("请补充候选操作中缺少的时间、对象或作用范围。", []);

        var messageText = clarification.TryGetProperty("message", out var messageNode) &&
                          messageNode.ValueKind == JsonValueKind.String
            ? messageNode.GetString()
            : null;
        var missing = clarification.TryGetProperty("missingFields", out var fields) &&
                      fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
        return (
            string.IsNullOrWhiteSpace(messageText)
                ? "还不能生成候选，请补充以下必要信息。"
                : messageText,
            missing);
    }

    private static NaturalLanguageOperationCandidate? ParseCandidate(
        string json,
        string originalText,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("needsClarification", out _)) return null;
        var kind = root.GetProperty("kind").GetString();
        var summary = root.TryGetProperty("summary", out var summaryNode)
            ? summaryNode.GetString() ?? ""
            : "";
        var candidate = kind switch
        {
            "createCommitment" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CreateCommitment, originalText,
                Commitment: Deserialize<CommitmentDraft>(root, "commitment"),
                Summary: summary, CreatedAt: now),
            "reviseCommitment" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.ReviseCommitment, originalText,
                Revision: Deserialize<CommitmentRevisionDraft>(root, "revision"),
                Summary: summary, CreatedAt: now),
            "createRecurrence" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CreateRecurrence, originalText,
                Summary: summary, Recurrence: Deserialize<RecurrenceDraft>(root, "recurrence"),
                CreatedAt: now),
            "createFromTemplate" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CreateFromTemplate, originalText,
                Summary: summary, FromTemplate: Deserialize<TemplateCommitmentDraft>(root, "fromTemplate"),
                CreatedAt: now),
            "saveTemplate" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.SaveTemplate, originalText,
                Summary: summary, CreatedAt: now,
                Template: Deserialize<CommitmentTemplateDraft>(root, "template")),
            "endCommitmentEarly" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.EndCommitmentEarly, originalText,
                Summary: summary, CreatedAt: now,
                TargetCommitmentId: root.TryGetProperty("targetCommitmentId", out var id) &&
                                    Guid.TryParse(id.GetString(), out var parsedId)
                    ? parsedId
                    : null,
                ExpectedVersion: root.TryGetProperty("expectedVersion", out var version) &&
                                 version.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : null),
            "cancelCommitment" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.CancelCommitment, originalText,
                Summary: summary, CreatedAt: now,
                TargetCommitmentId: ReadGuid(root, "targetCommitmentId"),
                ExpectedVersion: ReadInt(root, "expectedVersion"),
                Reason: ReadString(root, "reason")),
            "deferCommitment" => new NaturalLanguageOperationCandidate(
                Guid.NewGuid(), CandidateOperationKind.DeferCommitment, originalText,
                Summary: summary, CreatedAt: now,
                TargetCommitmentId: ReadGuid(root, "targetCommitmentId"),
                ExpectedVersion: ReadInt(root, "expectedVersion"),
                DeferredStartAt: ReadDateTimeOffset(root, "deferredStartAt"),
                Reason: ReadString(root, "reason")),
            _ => null
        };
        return candidate switch
        {
            { Kind: CandidateOperationKind.CreateCommitment, Commitment: null } => null,
            { Kind: CandidateOperationKind.ReviseCommitment, Revision: null } => null,
            { Kind: CandidateOperationKind.CreateRecurrence, Recurrence: null } => null,
            { Kind: CandidateOperationKind.CreateFromTemplate, FromTemplate: null } => null,
            { Kind: CandidateOperationKind.SaveTemplate, Template: null } => null,
            { Kind: CandidateOperationKind.EndCommitmentEarly, TargetCommitmentId: null } => null,
            { Kind: CandidateOperationKind.EndCommitmentEarly, ExpectedVersion: null } => null,
            { Kind: CandidateOperationKind.CancelCommitment, TargetCommitmentId: null } => null,
            { Kind: CandidateOperationKind.CancelCommitment, ExpectedVersion: null } => null,
            { Kind: CandidateOperationKind.CancelCommitment, Reason: null } => null,
            { Kind: CandidateOperationKind.DeferCommitment, TargetCommitmentId: null } => null,
            { Kind: CandidateOperationKind.DeferCommitment, ExpectedVersion: null } => null,
            { Kind: CandidateOperationKind.DeferCommitment, DeferredStartAt: null } => null,
            { Kind: CandidateOperationKind.DeferCommitment, Reason: null } => null,
            _ => candidate
        };
    }

    private static Guid? ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && DateTimeOffset.TryParse(
            value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static T? Deserialize<T>(JsonElement root, string propertyName) where T : class =>
        root.TryGetProperty(propertyName, out var value)
            ? JsonSerializer.Deserialize<T>(value.GetRawText(), CoreProtocol.Json)
            : null;
}
