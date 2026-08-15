using System.IO;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Desktop;

public enum DesktopPetVisualState
{
    Idle,
    Working,
    Reminder,
    Happy,
    Caring
}

public enum DesktopPetOverlayState
{
    None,
    Resting,
    Talking,
    Sleeping
}

public sealed record DesktopPetProjection(
    DesktopPetVisualState VisualState,
    DesktopPetOverlayState OverlayState,
    string Status,
    string Detail,
    string? ForegroundProcess = null,
    Guid? ProactivePromptId = null)
{
    public static DesktopPetProjection Disconnected { get; } = new(
        DesktopPetVisualState.Caring,
        DesktopPetOverlayState.None,
        "Core 暂时未连接",
        "监督数据仍以 Core 为准；打开面板可查看连接状态。");
}

public sealed class CompanionPersonaSettingsChangedEventArgs(
    CompanionPersonaSettingsView settings) : EventArgs
{
    public CompanionPersonaSettingsView Settings { get; } = settings;
}

public sealed class DesktopPetProfessionalModeChangedEventArgs(bool professionalMode) : EventArgs
{
    public bool ProfessionalMode { get; } = professionalMode;
}

public sealed class ProactivePromptPresentedEventArgs(Guid promptId) : EventArgs
{
    public Guid PromptId { get; } = promptId;
}

public static class DesktopPetProjectionBuilder
{
    public static DesktopPetProjection Build(
        SupervisionSnapshot? supervision,
        CompanionSnapshot? companion,
        DateTimeOffset now)
    {
        if (supervision is null)
        {
            return DesktopPetProjection.Disconnected;
        }

        var active = supervision.Commitments.SingleOrDefault(item =>
            item.Id == supervision.ActiveComputerCommitmentId);
        var state = supervision.ActiveSupervision;
        var foreground = supervision.LatestActivity?.ForegroundProcess;
        var persona = companion?.PersonaProjection ?? CompanionPersonaView.Default;
        var overlay = state?.ActiveRest is not null
            ? DesktopPetOverlayState.Resting
            : companion?.Ai.IsRequestInProgress == true
                ? DesktopPetOverlayState.Talking
                : supervision.LatestActivity?.Availability == ActivityAvailability.Unobservable
                    ? DesktopPetOverlayState.Sleeping
                    : DesktopPetOverlayState.None;

        if (active is not null && state is not null)
        {
            var title = Title(active);
            if (state.ReminderMarkerActive)
            {
                return new(
                    DesktopPetVisualState.Reminder,
                    overlay,
                    "该回到当前承诺了",
                    $"{title} · 连续偏离 {Duration(state.CountedDeviation)}",
                    foreground);
            }

            if (state.Classification == ActivityClassification.Unknown)
            {
                return new(
                    DesktopPetVisualState.Caring,
                    overlay,
                    "这个活动还未确定",
                    $"{title} · 打开面板可将当前软件或网站标为相关或分心",
                    foreground);
            }

            if (state.ActiveRest is not null)
            {
                return new(
                    DesktopPetVisualState.Idle,
                    overlay,
                    "限时休息中",
                    $"{title} · {state.ActiveRest.EndAt.ToLocalTime():HH:mm} 自动恢复监督",
                    foreground);
            }

            return new(
                DesktopPetVisualState.Working,
                overlay,
                active.Phase == CommitmentPhase.PreparationBuffer ? "准备缓冲" : "专注监督中",
                $"{title} · {Activity(state.Classification)}",
                foreground);
        }

        if (companion?.BackupProjection.AttentionRequired == true)
        {
            return new(
                DesktopPetVisualState.Caring,
                overlay,
                "本地备份等待网盘处理",
                companion.BackupProjection.CloudStatus,
                foreground);
        }

        if (persona.CurrentPrompt is { } prompt &&
            (prompt.ExpiresAt is null || prompt.ExpiresAt > now))
        {
            return new(
                DesktopPetVisualState.Caring,
                overlay,
                prompt.Text,
                "点击 Jarvis 可以回应；不想回应时忽略即可，不会追问。",
                foreground,
                prompt.PromptId);
        }

        var recentCompletion = companion?.CommitmentReviews
            .Where(item => item.State == CommitmentReviewState.Completed && item.AnsweredAt is not null)
            .OrderByDescending(item => item.AnsweredAt)
            .FirstOrDefault();
        if (recentCompletion?.AnsweredAt is { } answeredAt && now - answeredAt <= TimeSpan.FromMinutes(5))
        {
            return new(
                DesktopPetVisualState.Happy,
                overlay,
                "做得很好",
                "这次承诺已经完成回顾，记录已由 Core 保存。",
                foreground);
        }

        var awaitingReview = supervision.Commitments.Count(item => item.Phase == CommitmentPhase.AwaitingReview);
        if (awaitingReview > 0 || companion?.PendingCandidate is not null ||
            companion?.DailyReview.State == ReviewSessionState.InProgress)
        {
            return new(
                DesktopPetVisualState.Caring,
                overlay,
                "有一件事等你确认",
                awaitingReview > 0 ? $"{awaitingReview} 项承诺等待回顾。" : "打开面板查看待确认内容。",
                foreground);
        }

        var next = supervision.Commitments
            .Where(item => item.Phase == CommitmentPhase.Scheduled)
            .OrderBy(item => item.StartAt)
            .FirstOrDefault();
        return new(
            DesktopPetVisualState.Idle,
            overlay,
            "Jarvis 已就绪",
            next is null
                ? "当前没有进行中的监督。"
                : $"下一项 {next.StartAt.ToLocalTime():HH:mm} · {Title(next)}",
            foreground);
    }

    private static string Title(CommitmentView commitment) =>
        commitment.InputGoal ?? commitment.OutcomeGoal ?? "未命名承诺";

    private static string Activity(ActivityClassification? classification) => classification switch
    {
        ActivityClassification.Related => "活动相关",
        ActivityClassification.Distracting => "活动分心",
        ActivityClassification.Unknown => "活动未确定",
        _ => "等待活动证据"
    };

    private static string Duration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{(int)value.TotalMinutes}分{value.Seconds}秒"
        : $"{Math.Max(0, value.Seconds)}秒";
}

public sealed record DesktopPetSettings(
    double? Left = null,
    double? Top = null,
    double Scale = 1,
    bool ClickThrough = false,
    bool AutoMove = false,
    bool ProfessionalMode = true,
    IReadOnlyList<string>? AutoHideProcesses = null)
{
    public IReadOnlyList<string> HiddenProcesses => AutoHideProcesses ?? [];

    public DesktopPetSettings Normalize() => this with
    {
        Scale = Math.Clamp(Scale, 0.7, 1.4),
        AutoHideProcesses = HiddenProcesses
            .Select(NormalizeProcess)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    };

    public static string NormalizeProcess(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}

public sealed class DesktopPetSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jarvis",
        "desktop-pet.json");

    public DesktopPetSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new DesktopPetSettings();
            }

            return (JsonSerializer.Deserialize<DesktopPetSettings>(File.ReadAllText(_path)) ??
                    new DesktopPetSettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DesktopPetSettings();
        }
    }

    public void Save(DesktopPetSettings settings)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize()));
        File.Move(temporary, _path, overwrite: true);
    }
}

public static class DesktopPetSnap
{
    public static (double Left, double Top) ConstrainAndSnap(
        double left,
        double top,
        double width,
        double height,
        double workLeft,
        double workTop,
        double workRight,
        double workBottom,
        double snapDistance = 28)
    {
        left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - width));
        top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - height));
        var rightDistance = Math.Abs(workRight - (left + width));
        var leftDistance = Math.Abs(left - workLeft);
        var bottomDistance = Math.Abs(workBottom - (top + height));
        var topDistance = Math.Abs(top - workTop);
        var minimum = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        if (minimum > snapDistance)
        {
            return (left, top);
        }

        if (minimum == leftDistance) left = workLeft;
        else if (minimum == rightDistance) left = workRight - width;
        else if (minimum == topDistance) top = workTop;
        else top = workBottom - height;
        return (left, top);
    }
}
