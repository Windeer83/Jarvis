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

    public static SiliconFlowModelProfile Select(
        AiRequestPurpose purpose,
        AiModelPreference preference = AiModelPreference.Flash)
    {
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "未知的 AI 请求用途。");
        return preference switch
        {
            AiModelPreference.Flash => Flash,
            AiModelPreference.Pro => Pro,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "未知的 AI 模型选择。")
        };
    }

    public static string Describe(AiModelPreference preference) =>
        preference switch
        {
            AiModelPreference.Flash => "DeepSeek-V4-Flash（全局）",
            AiModelPreference.Pro => "DeepSeek-V4-Pro（全局）",
            _ => "未知模型"
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
