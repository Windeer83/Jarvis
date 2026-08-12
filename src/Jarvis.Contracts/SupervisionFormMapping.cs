namespace Jarvis.Contracts;

public sealed record SupervisionFormValues(
    bool StartReminderEnabled,
    int LocalDeviationMinutes,
    int FirstMobileDeviationMinutes,
    int MobileRepeatMinutes,
    int MaxMobileReminders,
    bool SoundEnabled,
    bool QuietPresentation,
    int RestIdlePromptMinutes,
    int RestTotalMinutes,
    string? RelatedApplications,
    string? RelatedDomains,
    string? DistractingApplications,
    string? DistractingDomains,
    string? UnknownApplications,
    string? UnknownDomains);

public sealed record SupervisionFormSettings(
    ReminderSettings Reminders,
    IReadOnlyList<ActivityRule> ActivityRules,
    RestSettings Rest);

public static class SupervisionFormMapping
{
    private static readonly char[] Separators = [',', '，', ';', '；', '\n', '\r'];

    public static bool TryToSettings(
        SupervisionFormValues values,
        out SupervisionFormSettings settings,
        out string validationMessage)
    {
        settings = null!;
        if (values.LocalDeviationMinutes <= 0 ||
            values.FirstMobileDeviationMinutes < values.LocalDeviationMinutes ||
            values.MobileRepeatMinutes <= 0 ||
            values.MaxMobileReminders <= 0)
        {
            validationMessage = "提醒阈值必须为正数，且首次手机提醒不得早于本机提醒。";
            return false;
        }

        if (values.RestIdlePromptMinutes <= 0 ||
            values.RestTotalMinutes < values.RestIdlePromptMinutes)
        {
            validationMessage = "休息分钟必须为正数，且默认总休息不得短于闲置询问时间。";
            return false;
        }

        settings = new SupervisionFormSettings(
            new ReminderSettings(
                values.StartReminderEnabled,
                values.LocalDeviationMinutes,
                values.FirstMobileDeviationMinutes,
                values.MobileRepeatMinutes,
                values.MaxMobileReminders,
                values.SoundEnabled,
                values.QuietPresentation),
            ParseRules(values),
            new RestSettings(values.RestIdlePromptMinutes, values.RestTotalMinutes));
        validationMessage = "";
        return true;
    }

    public static SupervisionFormValues FromSettings(
        ReminderSettings reminders,
        IReadOnlyList<ActivityRule> rules,
        RestSettings rest) => new(
        reminders.StartReminderEnabled,
        reminders.LocalDeviationMinutes,
        reminders.FirstMobileDeviationMinutes,
        reminders.MobileRepeatMinutes,
        reminders.MaxMobileReminders,
        reminders.SoundEnabled,
        reminders.QuietPresentation,
        rest.IdlePromptMinutes,
        rest.DefaultTotalRestMinutes,
        FormatRules(rules, ActivityClassification.Related, CommitmentTargetKind.Application),
        FormatRules(rules, ActivityClassification.Related, CommitmentTargetKind.Website),
        FormatRules(rules, ActivityClassification.Distracting, CommitmentTargetKind.Application),
        FormatRules(rules, ActivityClassification.Distracting, CommitmentTargetKind.Website),
        FormatRules(rules, ActivityClassification.Unknown, CommitmentTargetKind.Application),
        FormatRules(rules, ActivityClassification.Unknown, CommitmentTargetKind.Website));

    private static IReadOnlyList<ActivityRule> ParseRules(SupervisionFormValues values) =>
    [
        .. ParseRules(values.RelatedApplications, values.RelatedDomains, ActivityClassification.Related),
        .. ParseRules(values.DistractingApplications, values.DistractingDomains,
            ActivityClassification.Distracting),
        .. ParseRules(values.UnknownApplications, values.UnknownDomains, ActivityClassification.Unknown)
    ];

    private static IEnumerable<ActivityRule> ParseRules(
        string? applications,
        string? domains,
        ActivityClassification classification) => Split(applications)
        .Select(value => new ActivityRule(
            new CommitmentTarget(CommitmentTargetKind.Application, value), classification))
        .Concat(Split(domains).Select(value => new ActivityRule(
            new CommitmentTarget(CommitmentTargetKind.Website, value), classification)));

    private static IEnumerable<string> Split(string? value) => (value ?? "")
        .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private static string FormatRules(
        IReadOnlyList<ActivityRule> rules,
        ActivityClassification classification,
        CommitmentTargetKind kind) => string.Join(", ", rules
        .Where(rule => rule.Classification == classification && rule.Target.Kind == kind)
        .Select(rule => rule.Target.Value));
}
