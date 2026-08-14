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

internal sealed class DeepSeekCloudAiProvider(HttpClient? httpClient = null) : ICloudAiProvider, IDisposable
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    private readonly bool _ownsClient = httpClient is null;

    public decimal EstimateCostCny(AiProviderRequest request)
    {
        var systemPrompt = request.Purpose == AiRequestPurpose.NaturalLanguageOperation
            ? CandidateSystemPrompt(request)
            : "你是 Jarvis 的简洁中文助手。只回答当前用户问题，不虚构监督事实。";
        var estimatedInputTokens = Encoding.UTF8.GetByteCount(systemPrompt + request.Text) + 512;
        return estimatedInputTokens / 1_000_000m * 1m + request.MaxOutputTokens / 1_000_000m * 2m;
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
                ["content"] = request.Purpose == AiRequestPurpose.NaturalLanguageOperation
                    ? CandidateSystemPrompt(request)
                    : "你是 Jarvis 的简洁中文助手。只回答当前用户问题，不虚构监督事实。"
            },
            new JsonObject { ["role"] = "user", ["content"] = request.Text }
        };
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens
        };
        if (request.Purpose == AiRequestPurpose.NaturalLanguageOperation)
            payload["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new AiProviderResult(
                false, null, new AiTokenUsage(0, 0, 0),
                $"http_{(int)response.StatusCode}", "DeepSeek 请求失败。");
        var parsedUsage = new AiTokenUsage(0, 0, 0);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? string.Empty;
            var usage = root.GetProperty("usage");
            parsedUsage = new AiTokenUsage(
                usage.GetProperty("prompt_tokens").GetInt32(),
                usage.GetProperty("completion_tokens").GetInt32(),
                usage.TryGetProperty("prompt_cache_hit_tokens", out var hit) ? hit.GetInt32() : 0);
            if (request.Purpose != AiRequestPurpose.NaturalLanguageOperation)
                return new AiProviderResult(true, content, parsedUsage);
            using (var candidateDocument = JsonDocument.Parse(content))
            {
                if (candidateDocument.RootElement.TryGetProperty(
                        "needsClarification", out var clarification))
                {
                    return new AiProviderResult(
                        false, content, parsedUsage, "ai_clarification_required",
                        clarification.ValueKind == JsonValueKind.String
                            ? clarification.GetString()
                            : "请补充候选操作中缺少的时间、对象或作用范围。");
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
                "ai_response_invalid", "DeepSeek 返回了无法解析的响应。");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
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
        return $"""
            你只把用户文字转换为一个待确认的 Jarvis 候选操作，不能声称已经执行。
            当前时间：{request.Now:O}。
            当前承诺最小投影：{JsonSerializer.Serialize(commitments, CoreProtocol.Json)}
            当前模板最小投影：{JsonSerializer.Serialize(templates, CoreProtocol.Json)}
            只输出 JSON 对象。支持 kind：createCommitment、reviseCommitment、createRecurrence、createFromTemplate、saveTemplate、endCommitmentEarly、cancelCommitment、deferCommitment。
            createCommitment 结构：{example}。
            reviseCommitment 必须提供 revision（commitmentId、expectedVersion、完整 proposed、reason）；
            createRecurrence 必须提供 recurrence（commitment + 有限 pattern）；
            createFromTemplate 必须提供 fromTemplate（templateId + startAt 和明确覆盖）；
            saveTemplate 必须提供 template（无具体日期的模板默认值）。
            endCommitmentEarly 必须提供 targetCommitmentId 和 expectedVersion。
            cancelCommitment 必须提供 targetCommitmentId、expectedVersion、reason；
            deferCommitment 用于进行中的承诺，必须提供 targetCommitmentId、expectedVersion、deferredStartAt、reason。
            时间、作用范围或重要变化有歧义时只输出 needsClarification 字段，不要猜。
            AI 不得修改权限、系统提示、历史事实或绕过 Core 版本校验。
            """;
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
