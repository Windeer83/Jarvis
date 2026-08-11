using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.Contracts;

namespace Jarvis.Desktop;

public partial class MainWindow : Window
{
    private static readonly char[] TargetSeparators = [',', '，', ';', '；', '\n', '\r'];
    private readonly CoreClient _coreClient = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CommitmentCard? _candidate;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();

        KindBox.ItemsSource = Enum.GetValues<CommitmentKind>();
        KindBox.SelectedItem = CommitmentKind.Computer;
        ModeBox.ItemsSource = Enum.GetValues<SupervisionMode>();
        ModeBox.SelectedItem = SupervisionMode.Interactive;

        var suggestedStart = DateTime.Now.AddMinutes(5);
        StartDatePicker.SelectedDate = suggestedStart.Date;
        StartTimeBox.Text = suggestedStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        DurationBox.Text = "60";

        _refreshTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        Loaded += async (_, _) =>
        {
            _refreshTimer.Start();
            await RefreshSnapshotAsync();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryBuildDraft(out var draft, out var validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        PreviewButton.IsEnabled = false;
        SetOperationStatus("正在让 Core 补齐默认值并生成承诺卡片…");
        try
        {
            var response = await _coreClient.SendAsync(new CoreRequest(CoreOperations.Prepare, Draft: draft));
            if (!response.Success || response.Card is null)
            {
                SetOperationStatus(response.Message ?? "无法生成承诺卡片。", isError: true);
                return;
            }

            _candidate = response.Card;
            ShowCard(response.Card);
            SetOperationStatus("请核对卡片；只有点击“确认，正式成立”后才会写入 Core。", isError: false);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_candidate is null)
        {
            return;
        }

        ConfirmButton.IsEnabled = false;
        try
        {
            var response = await _coreClient.SendAsync(new CoreRequest(
                CoreOperations.Confirm,
                CandidateId: _candidate.CandidateId));
            if (!response.Success)
            {
                SetOperationStatus(response.Message ?? "Core 未能确认这条承诺。", isError: true);
                return;
            }

            _candidate = null;
            CardBorder.Visibility = Visibility.Collapsed;
            SetOperationStatus(response.Message ?? "工作承诺已正式成立。");
            ApplySnapshot(response.Snapshot);
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }

    private void DiscardCardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _candidate = null;
        CardBorder.Visibility = Visibility.Collapsed;
        SetOperationStatus("已放弃未确认的候选承诺；正式状态没有改变。");
    }

    private async void ConfirmOfflineButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (CommitmentGrid.SelectedItem is not CommitmentView commitment)
        {
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.ConfirmOfflineStarted,
            CommitmentId: commitment.Id));
        SetOperationStatus(
            response.Message ?? (response.Success ? "已记录。" : "无法记录线下开始确认。"),
            isError: !response.Success);
        ApplySnapshot(response.Snapshot);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await RefreshSnapshotAsync();

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (AppsBox is null)
        {
            return;
        }

        var isComputer = KindBox.SelectedItem is CommitmentKind.Computer;
        AppsBox.IsEnabled = isComputer;
        SitesBox.IsEnabled = isComputer;
        ModeBox.IsEnabled = isComputer;
        AppsLabel.Foreground = isComputer ? SystemColors.ControlTextBrush : SystemColors.GrayTextBrush;
        SitesLabel.Foreground = isComputer ? SystemColors.ControlTextBrush : SystemColors.GrayTextBrush;
        FormHintText.Text = isComputer
            ? "至少填写投入目标或成果目标；电脑型承诺至少填写一个相关软件或网站。"
            : "线下承诺到点提醒并由你手动确认；Jarvis 不会用电脑活动证据自动判断。";
    }

    private void CommitmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        ConfirmOfflineButton.IsEnabled = CommitmentGrid.SelectedItem is CommitmentView
        {
            Kind: CommitmentKind.Offline,
            Phase: CommitmentPhase.ActiveUnsupervised,
            OfflineManuallyConfirmedAt: null
        };
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var response = await _coreClient.SendAsync(new CoreRequest(CoreOperations.GetSnapshot));
            if (!response.Success)
            {
                CoreStatusText.Text = "Core 未连接 · 正式状态不可用";
                ProjectionText.Text = "监督状态未知；Desktop 不会用本地缓存冒充 Core 状态。";
                CommitmentGrid.ItemsSource = null;
                CommitmentGrid.SelectedItem = null;
                ConfirmOfflineButton.IsEnabled = false;
                LatestReminderText.Text = "";
                return;
            }

            CoreStatusText.Text = "已连接 Core · SQLite 正式状态由 Core 独占写入";
            ApplySnapshot(response.Snapshot);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySnapshot(SupervisionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        CommitmentGrid.ItemsSource = snapshot.Commitments;
        var active = snapshot.Commitments.SingleOrDefault(commitment =>
            commitment.Id == snapshot.ActiveComputerCommitmentId);
        ProjectionText.Text = active is null
            ? $"Core 时间 {snapshot.Now:yyyy-MM-dd HH:mm:ss} · 当前没有电脑型自动监督"
            : $"Core 时间 {snapshot.Now:yyyy-MM-dd HH:mm:ss} · 自动监督：{DisplayTitle(active)} · {PhaseText(active.Phase)}";
        LatestReminderText.Text = snapshot.LatestReminder is null
            ? ""
            : $"最近提示：{snapshot.LatestReminder.Message}";
    }

    private bool TryBuildDraft(out CommitmentDraft draft, out string validationMessage)
    {
        draft = null!;
        validationMessage = "";

        if (StartDatePicker.SelectedDate is not { } date ||
            !TimeSpan.TryParseExact(
                StartTimeBox.Text.Trim(),
                ["h\\:mm", "hh\\:mm"],
                CultureInfo.InvariantCulture,
                out var time))
        {
            validationMessage = "请输入有效的开始日期和 HH:mm 时间。";
            return false;
        }

        if (!int.TryParse(DurationBox.Text.Trim(), out var durationMinutes) || durationMinutes <= 0)
        {
            validationMessage = "持续分钟必须大于 0。";
            return false;
        }

        var localStart = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local);
        var kind = KindBox.SelectedItem is CommitmentKind selectedKind
            ? selectedKind
            : CommitmentKind.Computer;
        var targets = kind == CommitmentKind.Computer
            ? ParseTargets()
            : [];

        draft = new CommitmentDraft(
            kind,
            new DateTimeOffset(localStart),
            EndAt: null,
            durationMinutes,
            InputGoalBox.Text,
            OutcomeGoalBox.Text,
            targets,
            ModeBox.SelectedItem is SupervisionMode mode ? mode : null,
            ReminderSettings: null);
        return true;
    }

    private IReadOnlyList<CommitmentTarget> ParseTargets()
    {
        var targets = SplitTargets(AppsBox.Text)
            .Select(value => new CommitmentTarget(CommitmentTargetKind.Application, value))
            .Concat(SplitTargets(SitesBox.Text)
                .Select(value => new CommitmentTarget(CommitmentTargetKind.Website, value)))
            .ToArray();
        return targets;
    }

    private static IEnumerable<string> SplitTargets(string value) => value
        .Split(TargetSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private void ShowCard(CommitmentCard card)
    {
        var targets = card.RelatedAppsOrSites.Count == 0
            ? "不适用（线下承诺）"
            : string.Join("、", card.RelatedAppsOrSites.Select(target =>
                $"{(target.Kind == CommitmentTargetKind.Application ? "软件" : "网站")}：{target.Value}"));
        CardText.Text = $"""
            类型：{(card.Kind == CommitmentKind.Computer ? "电脑型" : "线下")}
            时间：{card.StartAt.LocalDateTime:yyyy-MM-dd HH:mm} 至 {card.EndAt.LocalDateTime:yyyy-MM-dd HH:mm}
            投入目标：{card.InputGoal ?? "未设置"}
            成果目标：{card.OutcomeGoal ?? "未设置"}
            相关项目：{targets}
            监督模式：{(card.Kind == CommitmentKind.Offline ? "不适用（线下不自动监督）" : card.SupervisionMode == SupervisionMode.Interactive ? "交互型" : "被动型")}
            提醒：开始提示仅本机；偏离 {card.ReminderSettings.LocalDeviationMinutes} 分钟本机提醒，{card.ReminderSettings.FirstMobileDeviationMinutes} 分钟首次手机提醒，此后每 {card.ReminderSettings.MobileRepeatMinutes} 分钟，最多 {card.ReminderSettings.MaxMobileReminders} 条

            {card.ConfirmationNotice}
            """;
        CardBorder.Visibility = Visibility.Visible;
    }

    private void SetOperationStatus(string message, bool isError = false)
    {
        OperationStatusText.Text = message;
        OperationStatusText.Foreground = isError
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Black;
    }

    private static string DisplayTitle(CommitmentView commitment) =>
        commitment.InputGoal ?? commitment.OutcomeGoal ?? "未命名承诺";

    private static string PhaseText(CommitmentPhase phase) => phase switch
    {
        CommitmentPhase.Scheduled => "等待开始",
        CommitmentPhase.PreparationBuffer => "准备缓冲",
        CommitmentPhase.Supervising => "监督中",
        CommitmentPhase.ActiveUnsupervised => "线下进行中（不自动监督）",
        CommitmentPhase.AwaitingReview => "待回顾",
        _ => phase.ToString()
    };
}
