using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class SupervisionFormMappingScenarios
{
    [Fact]
    public void Compact_supervision_form_round_trips_every_user_reachable_setting()
    {
        var input = new SupervisionFormValues(
            StartReminderEnabled: false,
            LocalDeviationMinutes: 7,
            FirstMobileDeviationMinutes: 25,
            MobileRepeatMinutes: 12,
            MaxMobileReminders: 2,
            SoundEnabled: false,
            QuietPresentation: true,
            RestIdlePromptMinutes: 11,
            RestTotalMinutes: 17,
            RelatedApplications: "Excel.exe",
            RelatedDomains: "tradingview.com",
            DistractingApplications: "Steam.exe",
            DistractingDomains: "bilibili.com",
            UnknownApplications: "Custom.exe",
            UnknownDomains: "unknown.example");

        var valid = SupervisionFormMapping.TryToSettings(input, out var settings, out var message);

        Assert.True(valid, message);
        Assert.Equal(new ReminderSettings(false, 7, 25, 12, 2, false, true), settings.Reminders);
        Assert.Equal(new RestSettings(11, 17), settings.Rest);
        Assert.Equal(6, settings.ActivityRules.Count);
        Assert.Contains(settings.ActivityRules, rule =>
            rule.Classification == ActivityClassification.Related &&
            rule.Target == new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"));
        Assert.Contains(settings.ActivityRules, rule =>
            rule.Classification == ActivityClassification.Distracting &&
            rule.Target == new CommitmentTarget(CommitmentTargetKind.Website, "bilibili.com"));
        Assert.Contains(settings.ActivityRules, rule =>
            rule.Classification == ActivityClassification.Unknown &&
            rule.Target == new CommitmentTarget(CommitmentTargetKind.Application, "Custom.exe"));

        Assert.Equal(input, SupervisionFormMapping.FromSettings(
            settings.Reminders, settings.ActivityRules, settings.Rest));
    }

    [Fact]
    public void Compact_supervision_form_rejects_invalid_threshold_order_without_partial_settings()
    {
        var invalid = new SupervisionFormValues(
            true, 20, 5, 20, 3, true, false, 10, 15,
            null, null, null, null, null, null);

        Assert.False(SupervisionFormMapping.TryToSettings(invalid, out _, out var message));
        Assert.Contains("不得早于", message, StringComparison.Ordinal);
    }
}
