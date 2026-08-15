using Jarvis.Contracts;

namespace Jarvis.Desktop;

public static class NaturalLanguageCandidatePresentation
{
    public const string BusyText = "正在生成候选操作，请稍后…\n尚未创建或启动正式监督。";

    public static string FormatFailure(CompanionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var missing = outcome.MissingInformation?
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var message = string.IsNullOrWhiteSpace(outcome.Message)
            ? "还不能生成候选。"
            : outcome.Message.Trim();
        return missing.Length == 0
            ? $"{message}\n\n修改描述后再次点击“生成候选操作”。"
            : $"{message}\n\n缺少：\n{string.Join("\n", missing.Select(item => $"• {item}"))}" +
              "\n\n补充后再次点击“生成候选操作”。";
    }
}
