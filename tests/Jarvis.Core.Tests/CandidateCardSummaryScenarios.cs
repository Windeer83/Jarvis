using Jarvis.Contracts;
using Jarvis.Desktop;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class CandidateCardSummaryScenarios
{
    [Fact]
    public void Summary_shows_all_rule_states_and_presentation_settings_as_frozen()
    {
        var card = Card(
            new ReminderSettings(false, 5, 20, 20, 3, SoundEnabled: false, QuietPresentation: true),
            [
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Application, "devenv.exe"),
                    ActivityClassification.Related),
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Website, "video.example"),
                    ActivityClassification.Distracting),
                new ActivityRule(
                    new CommitmentTarget(CommitmentTargetKind.Application, "other.exe"),
                    ActivityClassification.Unknown)
            ]);

        var summary = CandidateCardSummary.Format(card);

        Assert.Contains("Related：软件：devenv.exe", summary);
        Assert.Contains("Distracting：网站：video.example", summary);
        Assert.Contains("Unknown：软件：other.exe", summary);
        Assert.Contains("开始时提醒 关", summary);
        Assert.Contains("声音 关", summary);
        Assert.Contains("安静呈现 开", summary);
        Assert.Contains("冻结为该次单次承诺", summary);
    }

    [Fact]
    public void Summary_names_missing_rule_states_instead_of_hiding_them()
    {
        var summary = CandidateCardSummary.Format(Card(
            new ReminderSettings(true, 5, 20, 20, 3),
            []));

        Assert.Contains("Related：未设置", summary);
        Assert.Contains("Distracting：未设置", summary);
        Assert.Contains("Unknown：未设置", summary);
    }

    [Fact]
    public void Inherited_template_preview_sends_only_identity_and_start_time()
    {
        var draft = TemplatePreviewDraft.CreateInherited(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        Assert.Null(draft.EndAt);
        Assert.Null(draft.DurationMinutes);
        Assert.Null(draft.InputGoal);
        Assert.Null(draft.OutcomeGoal);
        Assert.Null(draft.RelatedAppsOrSites);
        Assert.Null(draft.SupervisionMode);
        Assert.Null(draft.ReminderSettings);
        Assert.Null(draft.ActivityRules);
        Assert.Null(draft.RestSettings);
    }

    private static CommitmentCard Card(
        ReminderSettings reminders,
        IReadOnlyList<ActivityRule> rules) => new(
            Guid.NewGuid(),
            CommitmentKind.Computer,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            "专注编码",
            "完成功能",
            [new CommitmentTarget(CommitmentTargetKind.Application, "devenv.exe")],
            SupervisionMode.Interactive,
            reminders,
            "确认后写入",
            rules,
            new RestSettings(10, 15));
}
