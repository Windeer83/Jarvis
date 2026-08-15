using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed record SiliconFlowModelProfile(
    string Model,
    decimal InputCnyPerMillion,
    decimal OutputCnyPerMillion,
    decimal CacheHitInputCnyPerMillion,
    bool DisableThinking);

internal static class SiliconFlowModelCatalog
{
    public const string ProviderName = "SiliconFlow";
    public const string Endpoint = "https://api.siliconflow.cn/v1/chat/completions";
    public const string PriceVersion = "2026-08-15";
    public const string StatusModel = "DeepSeek-V4-Flash（普通） / DeepSeek-V4-Pro（复盘与复杂操作）";

    public static SiliconFlowModelProfile Select(AiRequestPurpose purpose) => purpose switch
    {
        AiRequestPurpose.BasicChat => Flash,
        AiRequestPurpose.NaturalLanguageOperation or
        AiRequestPurpose.DailyReviewAssist or
        AiRequestPurpose.CycleReviewAssist => Pro,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "未知的 AI 请求用途。")
    };

    public static SiliconFlowModelProfile Resolve(string model) =>
        string.Equals(model, Flash.Model, StringComparison.Ordinal) ? Flash :
        string.Equals(model, Pro.Model, StringComparison.Ordinal) ? Pro :
        throw new ArgumentException("只允许调用已核定的硅基流动 DeepSeek V4 模型。", nameof(model));

    public static decimal CalculateCost(SiliconFlowModelProfile profile, AiTokenUsage usage)
    {
        var cacheHit = Math.Min(usage.InputTokens, usage.CacheHitInputTokens);
        var cacheMiss = Math.Max(0, usage.InputTokens - cacheHit);
        return Math.Round(
            cacheHit / 1_000_000m * profile.CacheHitInputCnyPerMillion +
            cacheMiss / 1_000_000m * profile.InputCnyPerMillion +
            usage.OutputTokens / 1_000_000m * profile.OutputCnyPerMillion,
            6,
            MidpointRounding.AwayFromZero);
    }

    private static SiliconFlowModelProfile Flash { get; } = new(
        "deepseek-ai/DeepSeek-V4-Flash",
        InputCnyPerMillion: 1m,
        OutputCnyPerMillion: 2m,
        CacheHitInputCnyPerMillion: 0.02m,
        DisableThinking: true);

    private static SiliconFlowModelProfile Pro { get; } = new(
        "deepseek-ai/DeepSeek-V4-Pro",
        InputCnyPerMillion: 12m,
        OutputCnyPerMillion: 24m,
        CacheHitInputCnyPerMillion: 1m,
        DisableThinking: false);
}
