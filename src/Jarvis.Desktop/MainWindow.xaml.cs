using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Jarvis.Contracts;

namespace Jarvis.Desktop;

public partial class MainWindow : Window
{
    private static readonly char[] TargetSeparators = [',', '，', ';', '；', '\n', '\r'];
    private readonly CoreClient _coreClient = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ReminderOverlayWindow _reminderOverlay = new();
    private CommitmentCard? _candidate;
    private RecurrenceCard? _recurrenceCandidate;
    private RecurrenceChangeCard? _recurrenceChangeCandidate;
    private CommitmentRevisionCard? _revisionCandidate;
    private Guid? _selectedTemplateId;
    private Guid? _selectedPlanId;
    private SupervisionSnapshot? _snapshot;
    private CompanionSnapshot? _companionSnapshot;
    private NaturalLanguageOperationCandidate? _naturalLanguageCandidate;
    private AiReviewDraftView? _activeAiReviewDraft;
    private Guid? _displayedAiReviewDraftId;
    private CommitmentView? _revisionSource;
    private readonly LocalReminderSoundGate _soundGate = new();
    private bool _refreshing;
    private bool _suppressSelectionEvents;
    private bool _naturalLanguageBusy;
    private bool _aiReviewBusy;

    public MainWindow()
    {
        InitializeComponent();

        KindBox.ItemsSource = Enum.GetValues<CommitmentKind>();
        KindBox.SelectedItem = CommitmentKind.Computer;
        ModeBox.ItemsSource = Enum.GetValues<SupervisionMode>();
        ModeBox.SelectedItem = SupervisionMode.Interactive;
        RuleScopeBox.ItemsSource = RuleScopeChoices;
        RuleScopeBox.DisplayMemberPath = nameof(RuleScopeChoice.Label);
        RuleScopeBox.SelectedIndex = 0;
        RecurrenceKindBox.ItemsSource = Enum.GetValues<RecurrenceKind>();
        RecurrenceKindBox.SelectedItem = RecurrenceKind.Daily;
        ChangeScopeBox.ItemsSource = Enum.GetValues<RecurrenceChangeScope>();
        ChangeScopeBox.SelectedItem = RecurrenceChangeScope.ThisOccurrence;
        CompletionAssessmentBox.ItemsSource = Enum.GetValues<CompletionAssessment>();
        CompletionAssessmentBox.SelectedItem = CompletionAssessment.Completed;
        AiModelPreferenceBox.ItemsSource = Enum.GetValues<AiModelPreference>();
        AiModelPreferenceBox.SelectedItem = AiModelPreference.Flash;

        var suggestedStart = DateTime.Now.AddMinutes(5);
        StartDatePicker.SelectedDate = suggestedStart.Date;
        StartTimeBox.Text = suggestedStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        DurationBox.Text = "60";
        StartReminderCheckBox.IsChecked = true;
        LocalDeviationBox.Text = "5";
        FirstMobileDeviationBox.Text = "20";
        MobileRepeatBox.Text = "20";
        MaxMobileRemindersBox.Text = "3";
        SoundEnabledCheckBox.IsChecked = true;
        QuietPresentationCheckBox.IsChecked = false;
        RestIdlePromptBox.Text = "10";
        RestTotalBox.Text = "15";
        RangeStartPicker.SelectedDate = suggestedStart.Date;
        RangeEndPicker.SelectedDate = suggestedStart.Date.AddDays(6);
        RecurrenceValuesBox.Text = "1,2,3,4,5";
        AdjustmentReasonPanel.Visibility = Visibility.Collapsed;

        _refreshTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _reminderOverlay.RestoreRequested += (_, _) => RestoreConfigurationWindow();
        Loaded += async (_, _) =>
        {
            _refreshTimer.Start();
            await RefreshSnapshotAsync();
        };
        Closing += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Hide();
        };
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_revisionSource is not null)
        {
            await PrepareRevisionAsync();
            return;
        }

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

    private async void CreateTemplateButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryBuildTemplateDraft(out var draft, out var validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.CreateTemplate,
            TemplateDraft: draft));
        await FinishTemplateMutationAsync(response);
    }

    private async void UpdateTemplateButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_selectedTemplateId is not { } templateId)
        {
            SetOperationStatus("请先选择要更新的模板。", isError: true);
            return;
        }

        if (!TryBuildTemplateDraft(out var draft, out var validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.UpdateTemplate,
            TemplateDraft: draft,
            TemplateId: templateId));
        await FinishTemplateMutationAsync(response);
    }

    private async void ArchiveTemplateButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_selectedTemplateId is not { } templateId)
        {
            SetOperationStatus("请先选择要归档的模板。", isError: true);
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.ArchiveTemplate,
            TemplateId: templateId));
        if (response.Success)
        {
            _selectedTemplateId = null;
        }

        await FinishTemplateMutationAsync(response);
    }

    private async void PrepareFromTemplateButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_selectedTemplateId is not { } templateId)
        {
            SetOperationStatus("请先选择一个模板。", isError: true);
            return;
        }

        if (!TryReadStart(out var startAt, out var validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        TemplateCommitmentDraft draft;
        if (OverrideTemplateSupervisionCheckBox.IsChecked != true)
        {
            draft = TemplatePreviewDraft.CreateInherited(templateId, startAt);
        }
        else
        {
            if (!TryReadDuration(out var durationMinutes, out validationMessage) ||
                !TryReadSupervisionDefaults(
                    out var parsedReminders,
                    out var parsedRules,
                    out var parsedRest,
                    out validationMessage))
            {
                SetOperationStatus(validationMessage, isError: true);
                return;
            }

            var kind = SelectedKind();
            draft = TemplatePreviewDraft.CreateOverridden(
                templateId,
                startAt,
                durationMinutes,
                NullIfWhiteSpace(InputGoalBox.Text),
                NullIfWhiteSpace(OutcomeGoalBox.Text),
                kind == CommitmentKind.Computer ? ParseTargets() : [],
                SelectedMode(),
                parsedReminders,
                parsedRules,
                parsedRest);
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.PrepareFromTemplate,
            TemplateCommitmentDraft: draft));
        AcceptPreparedCard(response);
    }

    private async void PreviewRecurrenceButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_revisionSource is not null)
        {
            SetOperationStatus("请先完成或取消当前修订。", isError: true);
            return;
        }

        if (!TryBuildDraft(out var commitment, out var validationMessage) ||
            !TryBuildRecurrencePattern(out var pattern, out validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.PrepareRecurrence,
            RecurrenceDraft: new RecurrenceDraft(commitment, pattern)));
        if (!response.Success || response.RecurrenceCard is null)
        {
            SetOperationStatus(response.Message ?? "无法生成重复安排候选卡片。", isError: true);
            return;
        }

        _candidate = null;
        _recurrenceChangeCandidate = null;
        _revisionCandidate = null;
        _recurrenceCandidate = response.RecurrenceCard;
        ShowRecurrenceCard(response.RecurrenceCard);
        SetOperationStatus("请核对所有日期；点击确认后才会一次性写入全部独立承诺。");
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_candidate is null && _recurrenceCandidate is null &&
            _recurrenceChangeCandidate is null && _revisionCandidate is null)
        {
            return;
        }

        ConfirmButton.IsEnabled = false;
        try
        {
            var request = _revisionCandidate is not null
                ? new CoreRequest(
                    CoreOperations.ConfirmCommitmentRevision,
                    CandidateId: _revisionCandidate.CandidateId)
                : _recurrenceCandidate is not null
                ? new CoreRequest(
                    CoreOperations.ConfirmRecurrence,
                    CandidateId: _recurrenceCandidate.CandidateId)
                : _recurrenceChangeCandidate is not null
                    ? new CoreRequest(
                        CoreOperations.ConfirmRecurrenceChange,
                        CandidateId: _recurrenceChangeCandidate.CandidateId)
                    : new CoreRequest(CoreOperations.Confirm, CandidateId: _candidate!.CandidateId);
            var response = await _coreClient.SendAsync(request);
            if (!response.Success)
            {
                SetOperationStatus(response.Message ?? "Core 未能确认这张候选卡片。", isError: true);
                if (_revisionCandidate is not null &&
                    response.ErrorCode is "commitment_version_stale" or "candidate_not_found")
                {
                    DiscardRevisionCandidate();
                    SetOperationStatus("这张修订候选已失效。请重新选择最新承诺并预览。", isError: true);
                    await RefreshSnapshotAsync();
                }
                return;
            }

            _candidate = null;
            _recurrenceCandidate = null;
            _recurrenceChangeCandidate = null;
            _revisionCandidate = null;
            CardBorder.Visibility = Visibility.Collapsed;
            SetOperationStatus(response.Message ?? "候选内容已正式写入。");
            if (response.RecurrencePlan is not null)
            {
                _selectedPlanId = response.RecurrencePlan.Id;
            }

            if (_revisionSource is not null)
            {
                LeaveRevisionMode();
            }

            await ApplyMutationProjectionAsync(response);
        }
        finally
        {
            ConfirmButton.IsEnabled = _candidate is not null ||
                                      _recurrenceCandidate is not null ||
                                      _recurrenceChangeCandidate is not null ||
                                      _revisionCandidate is not null;
        }
    }

    private void DiscardCardButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _candidate = null;
        _recurrenceCandidate = null;
        _recurrenceChangeCandidate = null;
        _revisionCandidate = null;
        CardBorder.Visibility = Visibility.Collapsed;
        ConfirmButton.Content = _revisionSource is null ? "确认，正式成立" : "确认修订";
        SetOperationStatus("已放弃候选卡片；Core 正式状态没有改变。");
    }

    private async void SkipOccurrenceButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        AdjustmentReasonPanel.Visibility = Visibility.Collapsed;
        await ChangeOccurrenceAsync(RecurrenceChangeKind.Skip);
    }

    private async void AdjustOccurrenceButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        AdjustmentReasonPanel.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(AdjustmentReasonBox.Text))
        {
            SetOperationStatus("请先填写调整原因，再点一次“调整”。", isError: true);
            AdjustmentReasonBox.Focus();
            return;
        }

        await ChangeOccurrenceAsync(RecurrenceChangeKind.Adjust);
    }

    private async Task ChangeOccurrenceAsync(RecurrenceChangeKind kind)
    {
        if (_revisionSource is not null)
        {
            SetOperationStatus("请先完成或取消当前修订。", isError: true);
            return;
        }

        if (PlanBox.SelectedItem is not PlanChoice choice ||
            OccurrenceGrid.SelectedItem is not RecurrenceOccurrenceView occurrence)
        {
            SetOperationStatus("请先选择一个重复计划和其中一个发生项。", isError: true);
            return;
        }

        var scope = ChangeScopeBox.SelectedItem is RecurrenceChangeScope selectedScope
            ? selectedScope
            : RecurrenceChangeScope.ThisOccurrence;
        DateTimeOffset? newStartAt = null;
        int? newDurationMinutes = null;
        string? reason = null;
        if (kind == RecurrenceChangeKind.Adjust)
        {
            if (!DateTime.TryParseExact(
                    AdjustmentStartBox.Text.Trim(),
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localStart) ||
                !int.TryParse(AdjustmentDurationBox.Text.Trim(), out var duration) || duration <= 0)
            {
                SetOperationStatus("调整需要填写 yyyy-MM-dd HH:mm 格式的新开始时间和大于 0 的分钟数。", isError: true);
                return;
            }

            reason = AdjustmentReasonBox.Text.Trim();
            if (reason.Length == 0)
            {
                SetOperationStatus("调整发生项需要填写原因。", isError: true);
                AdjustmentReasonBox.Focus();
                return;
            }

            newStartAt = new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Local));
            newDurationMinutes = duration;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.PrepareRecurrenceChange,
            RecurrenceChange: new RecurrenceChangeRequest(
                choice.Plan.Id,
                occurrence.CommitmentId,
                kind,
                scope,
                newStartAt,
                newDurationMinutes,
                reason)));
        if (!response.Success || response.RecurrenceChangeCard is null)
        {
            SetOperationStatus(response.Message ?? "无法生成修改候选卡片。", isError: true);
            return;
        }

        _candidate = null;
        _recurrenceCandidate = null;
        _revisionCandidate = null;
        _recurrenceChangeCandidate = response.RecurrenceChangeCard;
        ShowRecurrenceChangeCard(response.RecurrenceChangeCard);
        SetOperationStatus("请核对影响范围；确认后才会写入修改。");
    }

    private async void ConfirmOfflineButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            return;
        }

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.ConfirmOfflineStarted,
            CommitmentId: commitment.Id,
            ExpectedVersion: commitment.Version));
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

    private void RecurrenceKindBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (RecurrenceValuesBox is null)
        {
            return;
        }

        var selectedDates = RecurrenceKindBox.SelectedItem is RecurrenceKind.SelectedDates;
        RangeStartPicker.IsEnabled = !selectedDates;
        RangeEndPicker.IsEnabled = !selectedDates;
        RecurrenceValuesBox.IsEnabled = RecurrenceKindBox.SelectedItem is not RecurrenceKind.Daily;
        RecurrenceValuesBox.Text = selectedDates
            ? DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : RecurrenceKindBox.SelectedItem is RecurrenceKind.Weekly ? "1,2,3,4,5" : "";
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressSelectionEvents || TemplateBox.SelectedItem is not CommitmentTemplateView template)
        {
            return;
        }

        _selectedTemplateId = template.Id;
        LoadTemplateIntoForm(template);
    }

    private void PlanBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressSelectionEvents || PlanBox.SelectedItem is not PlanChoice choice)
        {
            return;
        }

        _selectedPlanId = choice.Plan.Id;
        OccurrenceGrid.ItemsSource = choice.Plan.Occurrences;
        OccurrenceGrid.SelectedItem = choice.Plan.Occurrences.FirstOrDefault();
    }

    private void OccurrenceGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (OccurrenceGrid.SelectedItem is not RecurrenceOccurrenceView occurrence)
        {
            return;
        }

        AdjustmentStartBox.Text = occurrence.StartAt.LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        AdjustmentDurationBox.Text = ((int)(occurrence.EndAt - occurrence.StartAt).TotalMinutes)
            .ToString(CultureInfo.InvariantCulture);
        AdjustmentReasonBox.Clear();
        AdjustmentReasonPanel.Visibility = Visibility.Collapsed;
    }

    private void CommitmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        var selected = SelectedCommitment();
        ConfirmOfflineButton.IsEnabled = selected is
        {
            Kind: CommitmentKind.Offline,
            Phase: CommitmentPhase.ActiveUnsupervised,
            OfflineManuallyConfirmedAt: null
        };
        ReviseCommitmentButton.IsEnabled = _revisionSource is null && selected is not null &&
                                           selected.Phase is not (CommitmentPhase.AwaitingReview or CommitmentPhase.Skipped);
        ViewHistoryButton.IsEnabled = selected is not null;
    }

    private void ReviseCommitmentButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先在正式状态中选择一条工作承诺。", isError: true);
            return;
        }

        EnterRevisionMode(commitment);
    }

    private void CancelRevisionButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        DiscardRevisionCandidate();
        LeaveRevisionMode("已取消修订；正式承诺没有改变。");
    }

    private async void ViewHistoryButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先在正式状态中选择一条工作承诺。", isError: true);
            return;
        }

        await ShowCommitmentHistoryAsync(commitment);
    }

    private void CloseHistoryButton_Click(object sender, RoutedEventArgs eventArgs) =>
        HistoryPanel.Visibility = Visibility.Collapsed;

    private void EnterRevisionMode(CommitmentView commitment)
    {
        _revisionSource = commitment;
        _candidate = null;
        _recurrenceCandidate = null;
        _recurrenceChangeCandidate = null;
        _revisionCandidate = null;
        CardBorder.Visibility = Visibility.Collapsed;
        RevisionReasonBox.Clear();
        RevisionModeText.Text = $"{DisplayTitle(commitment)} · {commitment.Id.ToString()[..8]}";
        RevisionModePanel.Visibility = Visibility.Visible;
        HistoryPanel.Visibility = Visibility.Collapsed;
        TemplateGroup.IsEnabled = false;
        RecurrenceGroup.IsEnabled = false;
        KindBox.IsEnabled = false;
        ReviseCommitmentButton.IsEnabled = false;
        var canMoveStart = commitment.Phase == CommitmentPhase.Scheduled;
        StartDatePicker.IsEnabled = canMoveStart;
        StartTimeBox.IsEnabled = canMoveStart;
        PreviewButton.Content = "预览修订";
        FormHintText.Text = "修改需要一句原因；确认前只生成候选卡，既有历史不会改变。";
        LoadCommitmentIntoForm(commitment);
        ContentScrollViewer.ScrollToTop();
        RevisionReasonBox.Focus();
        SetOperationStatus("已载入当前正式版本。修改需要的字段，填写原因后预览修订。");
    }

    private void LeaveRevisionMode(string? status = null)
    {
        _revisionSource = null;
        RevisionModePanel.Visibility = Visibility.Collapsed;
        RevisionReasonBox.Clear();
        TemplateGroup.IsEnabled = true;
        RecurrenceGroup.IsEnabled = true;
        KindBox.IsEnabled = true;
        StartDatePicker.IsEnabled = true;
        StartTimeBox.IsEnabled = true;
        PreviewButton.Content = "预览一次性承诺";
        ConfirmButton.Content = "确认，正式成立";
        KindBox_SelectionChanged(KindBox, null!);
        CommitmentGrid_SelectionChanged(CommitmentGrid, null!);
        if (status is not null)
        {
            SetOperationStatus(status);
        }
    }

    private void DiscardRevisionCandidate()
    {
        _revisionCandidate = null;
        CardText.Text = "";
        CardBorder.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
    }

    private void LoadCommitmentIntoForm(CommitmentView commitment)
    {
        KindBox.SelectedItem = commitment.Kind;
        StartDatePicker.SelectedDate = commitment.StartAt.LocalDateTime.Date;
        StartTimeBox.Text = commitment.StartAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        DurationBox.Text = Math.Max(1, (int)(commitment.EndAt - commitment.StartAt).TotalMinutes)
            .ToString(CultureInfo.InvariantCulture);
        InputGoalBox.Text = commitment.InputGoal ?? "";
        OutcomeGoalBox.Text = commitment.OutcomeGoal ?? "";
        ModeBox.SelectedItem = commitment.SupervisionMode;
        AppsBox.Text = string.Join(", ", commitment.RelatedAppsOrSites
            .Where(target => target.Kind == CommitmentTargetKind.Application)
            .Select(target => target.Value));
        SitesBox.Text = string.Join(", ", commitment.RelatedAppsOrSites
            .Where(target => target.Kind == CommitmentTargetKind.Website)
            .Select(target => target.Value));
        LoadSupervisionDefaults(
            commitment.ReminderSettings,
            commitment.ActivityRules,
            commitment.RestSettings);
    }

    private async Task PrepareRevisionAsync()
    {
        if (_revisionSource is not { } source)
        {
            return;
        }

        var reason = RevisionReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            SetOperationStatus("请先用一句话填写修改原因。", isError: true);
            RevisionReasonBox.Focus();
            return;
        }

        if (!TryBuildDraft(out var proposed, out var validationMessage))
        {
            SetOperationStatus(validationMessage, isError: true);
            return;
        }

        proposed = proposed with { TemplateId = source.TemplateId };

        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.PrepareCommitmentRevision,
            RevisionDraft: new CommitmentRevisionDraft(
                source.Id,
                source.Version,
                proposed,
                reason)));
        if (!response.Success || response.CommitmentRevisionCard is null)
        {
            SetOperationStatus(response.Message ?? "无法生成修订候选卡片。", isError: true);
            return;
        }

        _candidate = null;
        _recurrenceCandidate = null;
        _recurrenceChangeCandidate = null;
        _revisionCandidate = response.CommitmentRevisionCard;
        ShowRevisionCard(response.CommitmentRevisionCard);
        SetOperationStatus("请核对变更；只有确认修订后，新版本才会向后生效。");
    }

    private async Task ShowCommitmentHistoryAsync(CommitmentView commitment)
    {
        HistoryHeaderText.Text = $"{DisplayTitle(commitment)} · 历史";
        HistoryText.Text = "正在连接 Core 历史接口…";
        HistoryPanel.Visibility = Visibility.Visible;
        ContentScrollViewer.ScrollToEnd();
        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.GetCommitmentHistory,
            CommitmentId: commitment.Id));
        if (!response.Success || response.CommitmentHistory is null)
        {
            HistoryText.Text = response.Message ?? "无法读取这条承诺的历史。";
            SetOperationStatus(HistoryText.Text, isError: true);
            return;
        }

        HistoryText.Text = CommitmentHistorySummary.Format(response.CommitmentHistory);
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
                TemplateBox.ItemsSource = null;
                PlanBox.ItemsSource = null;
                OccurrenceGrid.ItemsSource = null;
                CommitmentGrid.SelectedItem = null;
                ConfirmOfflineButton.IsEnabled = false;
                LatestReminderText.Text = "";
                _snapshot = null;
                ClearCompanionProjection();
                ClearSupervisionProjection();
                return;
            }

            CoreStatusText.Text = "已连接 Core · SQLite 正式状态由 Core 独占写入";
            ApplySnapshot(response.Snapshot);
            ApplyCompanionSnapshot(response.CompanionOutcome?.Snapshot);
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

        _snapshot = snapshot;
        RefreshCommitmentGridRows();
        var active = snapshot.Commitments.SingleOrDefault(commitment =>
            commitment.Id == snapshot.ActiveComputerCommitmentId);
        ProjectionText.Text = active is null
            ? $"Core 时间 {snapshot.Now:yyyy-MM-dd HH:mm:ss} · 当前没有电脑型自动监督"
            : $"Core 时间 {snapshot.Now:yyyy-MM-dd HH:mm:ss} · 自动监督：{DisplayTitle(active)} · {PhaseText(active.Phase)}";
        LatestReminderText.Text = snapshot.LatestReminder is null
            ? ""
            : $"最近提示：{snapshot.LatestReminder.Message}";

        var templates = snapshot.Templates
            .Where(template => !template.IsArchived)
            .OrderBy(template => template.Name)
            .ToArray();
        var plans = snapshot.RecurrencePlans
            .OrderBy(plan => plan.ConfirmedAt)
            .Select(plan => new PlanChoice(
                plan,
                $"{PatternText(plan.Pattern)} · {plan.Occurrences.Count} 次 · {plan.Id.ToString()[..8]}"))
            .ToArray();

        _suppressSelectionEvents = true;
        try
        {
            TemplateBox.ItemsSource = templates;
            TemplateBox.SelectedItem = templates.SingleOrDefault(template => template.Id == _selectedTemplateId);
            PlanBox.ItemsSource = plans;
            var selectedPlan = plans.SingleOrDefault(choice => choice.Plan.Id == _selectedPlanId);
            PlanBox.SelectedItem = selectedPlan;
            OccurrenceGrid.ItemsSource = selectedPlan?.Plan.Occurrences;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        ApplyActiveSupervision(snapshot, active);
    }

    private async Task ApplyMutationProjectionAsync(CoreResponse response)
    {
        if (response.Snapshot is not null)
        {
            ApplySnapshot(response.Snapshot);
            return;
        }

        CoreStatusText.Text = "写入成功 · 当前显示可能陈旧，正在刷新";
        ProjectionText.Text = "正式写入已成功；当前投影暂时不可用，正在向 Core 重新读取。";
        await RefreshSnapshotAsync();
    }

    private async Task FinishTemplateMutationAsync(CoreResponse response)
    {
        SetOperationStatus(
            response.Message ?? (response.Success ? "模板操作完成。" : "模板操作失败。"),
            isError: !response.Success);
        if (!response.Success)
        {
            return;
        }

        _selectedTemplateId = response.Template is { IsArchived: false } template
            ? template.Id
            : null;
        await ApplyMutationProjectionAsync(response);
    }

    private void AcceptPreparedCard(CoreResponse response)
    {
        if (!response.Success || response.Card is null)
        {
            SetOperationStatus(response.Message ?? "无法生成承诺候选卡片。", isError: true);
            return;
        }

        _recurrenceCandidate = null;
        _recurrenceChangeCandidate = null;
        _revisionCandidate = null;
        _candidate = response.Card;
        ShowCard(response.Card);
        SetOperationStatus("请核对卡片；只有确认后才会写入 Core。");
    }

    private bool TryBuildDraft(out CommitmentDraft draft, out string validationMessage)
    {
        draft = null!;
        if (!TryReadStart(out var startAt, out validationMessage) ||
            !TryReadDuration(out var durationMinutes, out validationMessage) ||
            !TryReadSupervisionDefaults(
                out var reminderSettings,
                out var activityRules,
                out var restSettings,
                out validationMessage))
        {
            return false;
        }

        // Active revisions cannot edit this disabled field. Keep the Core timestamp exactly,
        // including seconds that the HH:mm form intentionally does not display.
        if (_revisionSource is { Phase: not CommitmentPhase.Scheduled } revisionSource)
        {
            startAt = revisionSource.StartAt;
        }

        var kind = SelectedKind();
        var targets = kind == CommitmentKind.Computer
            ? ParseTargets()
            : [];
        if (kind == CommitmentKind.Computer && targets.Count == 0)
        {
            validationMessage = "电脑型承诺至少需要一个相关软件或网站。";
            return false;
        }

        draft = new CommitmentDraft(
            kind,
            startAt,
            EndAt: null,
            durationMinutes,
            NullIfWhiteSpace(InputGoalBox.Text),
            NullIfWhiteSpace(OutcomeGoalBox.Text),
            targets,
            SelectedMode(),
            reminderSettings,
            activityRules,
            restSettings,
            TemplateId: _selectedTemplateId);
        return true;
    }

    private bool TryBuildTemplateDraft(
        out CommitmentTemplateDraft draft,
        out string validationMessage)
    {
        draft = null!;
        var name = TemplateNameBox.Text.Trim();
        if (name.Length == 0)
        {
            validationMessage = "模板名称不能为空。";
            return false;
        }

        if (!TryReadDuration(out var durationMinutes, out validationMessage) ||
            !TryReadSupervisionDefaults(
                out var reminderSettings,
                out var activityRules,
                out var restSettings,
                out validationMessage))
        {
            return false;
        }

        var kind = SelectedKind();
        var targets = kind == CommitmentKind.Computer ? ParseTargets() : [];
        if (kind == CommitmentKind.Computer && targets.Count == 0)
        {
            validationMessage = "电脑型模板至少需要一个相关软件或网站。";
            return false;
        }

        draft = new CommitmentTemplateDraft(
            name,
            kind,
            durationMinutes,
            NullIfWhiteSpace(InputGoalBox.Text),
            NullIfWhiteSpace(OutcomeGoalBox.Text),
            targets,
            SelectedMode(),
            reminderSettings,
            activityRules,
            restSettings);
        validationMessage = "";
        return true;
    }

    private bool TryBuildRecurrencePattern(
        out RecurrencePattern pattern,
        out string validationMessage)
    {
        pattern = null!;
        var kind = RecurrenceKindBox.SelectedItem is RecurrenceKind selected
            ? selected
            : RecurrenceKind.Daily;
        if (kind == RecurrenceKind.SelectedDates)
        {
            var dates = SplitTargets(RecurrenceValuesBox.Text)
                .Select(value => DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                    ? date
                    : (DateOnly?)null)
                .ToArray();
            if (dates.Length == 0 || dates.Any(date => date is null))
            {
                validationMessage = "指定日期请使用 yyyy-MM-dd，并用逗号分隔。";
                return false;
            }

            pattern = new RecurrencePattern(
                kind,
                SelectedDates: dates.Select(date => date!.Value).Distinct().Order().ToArray());
            validationMessage = "";
            return true;
        }

        if (RangeStartPicker.SelectedDate is not { } start ||
            RangeEndPicker.SelectedDate is not { } end || end.Date < start.Date)
        {
            validationMessage = "重复范围需要有效的开始和结束日期，且结束不得早于开始。";
            return false;
        }

        IReadOnlyList<DayOfWeek>? weekdays = null;
        if (kind == RecurrenceKind.Weekly)
        {
            var values = SplitTargets(RecurrenceValuesBox.Text).ToArray();
            if (values.Length == 0 || values.Any(value =>
                    !int.TryParse(value, out var day) || day is < 1 or > 7))
            {
                validationMessage = "每周重复请用 1-7 表示周一到周日，例如 1,3,5。";
                return false;
            }

            weekdays = values.Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .Select(day => day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day)
                .Distinct()
                .ToArray();
        }

        pattern = new RecurrencePattern(
            kind,
            DateOnly.FromDateTime(start),
            DateOnly.FromDateTime(end),
            weekdays);
        validationMessage = "";
        return true;
    }

    private bool TryReadStart(out DateTimeOffset startAt, out string validationMessage)
    {
        startAt = default;
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

        startAt = new DateTimeOffset(DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local));
        validationMessage = "";
        return true;
    }

    private bool TryReadDuration(out int durationMinutes, out string validationMessage)
    {
        if (!int.TryParse(DurationBox.Text.Trim(), out durationMinutes) || durationMinutes <= 0)
        {
            validationMessage = "持续分钟必须大于 0。";
            return false;
        }

        validationMessage = "";
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

    private bool TryReadSupervisionDefaults(
        out ReminderSettings reminders,
        out IReadOnlyList<ActivityRule> rules,
        out RestSettings rest,
        out string validationMessage)
    {
        reminders = null!;
        rules = [];
        rest = null!;
        if (!TryReadPositiveMinutes(LocalDeviationBox, out var localDeviation) ||
            !TryReadPositiveMinutes(FirstMobileDeviationBox, out var firstMobile) ||
            !TryReadPositiveMinutes(MobileRepeatBox, out var mobileRepeat) ||
            !TryReadPositiveMinutes(MaxMobileRemindersBox, out var maxMobile) ||
            !TryReadPositiveMinutes(RestIdlePromptBox, out var idlePrompt) ||
            !TryReadPositiveMinutes(RestTotalBox, out var totalRest))
        {
            validationMessage = "提醒和休息设置必须填写正整数。";
            return false;
        }

        var values = new SupervisionFormValues(
            StartReminderCheckBox.IsChecked == true,
            localDeviation,
            firstMobile,
            mobileRepeat,
            maxMobile,
            SoundEnabledCheckBox.IsChecked == true,
            QuietPresentationCheckBox.IsChecked == true,
            idlePrompt,
            totalRest,
            RelatedAppsRuleBox.Text,
            RelatedDomainsRuleBox.Text,
            DistractingAppsRuleBox.Text,
            DistractingDomainsRuleBox.Text,
            UnknownAppsRuleBox.Text,
            UnknownDomainsRuleBox.Text);
        if (!SupervisionFormMapping.TryToSettings(values, out var settings, out validationMessage))
        {
            return false;
        }

        reminders = settings.Reminders;
        rules = settings.ActivityRules;
        rest = settings.Rest;
        return true;
    }

    private static bool TryReadPositiveMinutes(TextBox textBox, out int value) =>
        int.TryParse(textBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static IEnumerable<string> SplitTargets(string value) => value
        .Split(TargetSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private CommitmentKind SelectedKind() => KindBox.SelectedItem is CommitmentKind kind
        ? kind
        : CommitmentKind.Computer;

    private SupervisionMode? SelectedMode() => SelectedKind() == CommitmentKind.Computer &&
                                                ModeBox.SelectedItem is SupervisionMode mode
        ? mode
        : null;

    private void LoadTemplateIntoForm(CommitmentTemplateView template)
    {
        TemplateNameBox.Text = template.Name;
        KindBox.SelectedItem = template.Kind;
        DurationBox.Text = template.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        InputGoalBox.Text = template.InputGoal ?? "";
        OutcomeGoalBox.Text = template.OutcomeGoal ?? "";
        ModeBox.SelectedItem = template.SupervisionMode;
        AppsBox.Text = string.Join(", ", template.RelatedAppsOrSites
            .Where(target => target.Kind == CommitmentTargetKind.Application)
            .Select(target => target.Value));
        SitesBox.Text = string.Join(", ", template.RelatedAppsOrSites
            .Where(target => target.Kind == CommitmentTargetKind.Website)
            .Select(target => target.Value));
        LoadSupervisionDefaults(template.ReminderSettings, template.ActivityRules, template.RestSettings);
        OverrideTemplateSupervisionCheckBox.IsChecked = false;
        SetOperationStatus("模板内容已载入；修改表单后可更新模板，或仅作为本次候选覆盖。");
    }

    private void LoadSupervisionDefaults(
        ReminderSettings reminders,
        IReadOnlyList<ActivityRule> rules,
        RestSettings rest)
    {
        var values = SupervisionFormMapping.FromSettings(reminders, rules, rest);
        StartReminderCheckBox.IsChecked = values.StartReminderEnabled;
        LocalDeviationBox.Text = values.LocalDeviationMinutes.ToString(CultureInfo.InvariantCulture);
        FirstMobileDeviationBox.Text = values.FirstMobileDeviationMinutes.ToString(CultureInfo.InvariantCulture);
        MobileRepeatBox.Text = values.MobileRepeatMinutes.ToString(CultureInfo.InvariantCulture);
        MaxMobileRemindersBox.Text = values.MaxMobileReminders.ToString(CultureInfo.InvariantCulture);
        SoundEnabledCheckBox.IsChecked = values.SoundEnabled;
        QuietPresentationCheckBox.IsChecked = values.QuietPresentation;
        RestIdlePromptBox.Text = values.RestIdlePromptMinutes.ToString(CultureInfo.InvariantCulture);
        RestTotalBox.Text = values.RestTotalMinutes.ToString(CultureInfo.InvariantCulture);
        RelatedAppsRuleBox.Text = values.RelatedApplications ?? "";
        RelatedDomainsRuleBox.Text = values.RelatedDomains ?? "";
        DistractingAppsRuleBox.Text = values.DistractingApplications ?? "";
        DistractingDomainsRuleBox.Text = values.DistractingDomains ?? "";
        UnknownAppsRuleBox.Text = values.UnknownApplications ?? "";
        UnknownDomainsRuleBox.Text = values.UnknownDomains ?? "";
    }

    private void ShowCard(CommitmentCard card)
    {
        CardHeaderText.Text = card.TemplateId is null
            ? "一次性候选卡片 · 等待确认"
            : "模板候选卡片 · 等待确认";
        CardText.Text = CandidateCardSummary.Format(card);
        CardBorder.Visibility = Visibility.Visible;
        ConfirmButton.Content = "确认，正式成立";
        ConfirmButton.IsEnabled = true;
    }

    private void ShowRecurrenceCard(RecurrenceCard card)
    {
        CardHeaderText.Text = $"重复安排候选卡片 · {card.Occurrences.Count} 个独立承诺";
        var preview = string.Join(Environment.NewLine, card.Occurrences.Take(12).Select(occurrence =>
            $"• {occurrence.StartAt.LocalDateTime:yyyy-MM-dd HH:mm} - {occurrence.EndAt.LocalDateTime:HH:mm}"));
        if (card.Occurrences.Count > 12)
        {
            preview += $"{Environment.NewLine}…另有 {card.Occurrences.Count - 12} 个日期";
        }

        var occurrenceSummary = card.Occurrences.Count == 0
            ? "没有发生项。"
            : CandidateCardSummary.Format(card.Occurrences[0], includeTime: false);
        CardText.Text = $"""
            方式：{PatternText(card.Pattern)}
            发生项：
            {preview}

            每个发生项的冻结内容：
            {occurrenceSummary}

            {card.ConfirmationNotice}
            """;
        CardBorder.Visibility = Visibility.Visible;
        ConfirmButton.Content = "确认，正式成立";
        ConfirmButton.IsEnabled = true;
    }

    private void ShowRecurrenceChangeCard(RecurrenceChangeCard card)
    {
        CardHeaderText.Text = $"重复安排修改候选 · {card.AffectedOccurrences.Count} 个发生项";
        CardText.Text = RecurrenceChangeSummary.Format(card);
        CardBorder.Visibility = Visibility.Visible;
        ConfirmButton.Content = "确认修改";
        ConfirmButton.IsEnabled = true;
    }

    private void ShowRevisionCard(CommitmentRevisionCard card)
    {
        CardHeaderText.Text = $"承诺修订候选 · v{card.FromVersion} → v{card.ToVersion}";
        CardText.Text = CommitmentRevisionSummary.Format(card);
        CardBorder.Visibility = Visibility.Visible;
        ConfirmButton.Content = "确认修订";
        ConfirmButton.IsEnabled = true;
        CardBorder.BringIntoView();
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

    private static string FormatActionableTarget(CommitmentTarget target)
    {
        const int maxLength = 26;
        return target.Value.Length <= maxLength
            ? target.Value
            : $"{target.Value[..(maxLength - 1)]}…";
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string PatternText(RecurrencePattern pattern) => pattern.Kind switch
    {
        RecurrenceKind.Daily => $"每天 {pattern.StartDate:yyyy-MM-dd} 至 {pattern.EndDate:yyyy-MM-dd}",
        RecurrenceKind.Weekly => $"每周 {pattern.StartDate:yyyy-MM-dd} 至 {pattern.EndDate:yyyy-MM-dd}",
        RecurrenceKind.SelectedDates => $"指定 {pattern.SelectedDates?.Count ?? 0} 个日期",
        _ => pattern.Kind.ToString()
    };

    private static string PhaseText(CommitmentPhase phase) => phase switch
    {
        CommitmentPhase.Scheduled => "等待开始",
        CommitmentPhase.PreparationBuffer => "准备缓冲",
        CommitmentPhase.Supervising => "监督中",
        CommitmentPhase.ActiveUnsupervised => "线下进行中（不自动监督）",
        CommitmentPhase.AwaitingReview => "待回顾",
        CommitmentPhase.Skipped => "已跳过（保留历史）",
        _ => phase.ToString()
    };

    private async void ReturnNowButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendActiveOperationAsync(new CoreRequest(
            CoreOperations.RecordReturnIntent,
            CommitmentId: _snapshot?.ActiveComputerCommitmentId,
            ExpectedVersion: _snapshot?.ActiveSupervision?.CommitmentVersion));

    private async void CurrentRelatedButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await ClassifyCurrentActivityAsync(ActivityClassification.Related);

    private async void CurrentDistractingButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await ClassifyCurrentActivityAsync(ActivityClassification.Distracting);

    private async Task ClassifyCurrentActivityAsync(ActivityClassification classification)
    {
        var scope = RuleScopeBox.SelectedItem is RuleScopeChoice selected
            ? selected.Scope
            : ActivityRuleScope.Commitment;
        if (scope == ActivityRuleScope.Template &&
            ActiveCommitment()?.TemplateId is null)
        {
            SetOperationStatus("这条承诺不来自模板，请选择“单次承诺”或“全局”。", isError: true);
            return;
        }

        var state = _snapshot?.ActiveSupervision;
        if (state?.ActionableTarget is null || state.ActivityStateStartedAt is null)
        {
            SetOperationStatus("当前没有可确认的外部活动；请回到要分类的软件后再试。", isError: true);
            return;
        }

        await SendActiveOperationAsync(new CoreRequest(
            CoreOperations.ClassifyCurrentActivity,
            CommitmentId: _snapshot?.ActiveComputerCommitmentId,
            Classification: classification,
            RuleScope: scope,
            ExpectedVersion: state.CommitmentVersion,
            ActivityTarget: state.ActionableTarget,
            ActivityStateStartedAt: state.ActivityStateStartedAt));
    }

    private async void AcceptRestButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendActiveOperationAsync(new CoreRequest(
            CoreOperations.RespondToRestPrompt,
            CommitmentId: _snapshot?.ActiveComputerCommitmentId,
            IsResting: true,
            ExpectedVersion: _snapshot?.ActiveSupervision?.CommitmentVersion));

    private async void DenyRestButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendActiveOperationAsync(new CoreRequest(
            CoreOperations.RespondToRestPrompt,
            CommitmentId: _snapshot?.ActiveComputerCommitmentId,
            IsResting: false,
            ExpectedVersion: _snapshot?.ActiveSupervision?.CommitmentVersion));

    private async void StartTimedRestButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(
                RestDurationMinutesBox.Text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var minutes) || minutes is <= 0 or > 1440)
        {
            SetOperationStatus("请输入 1–1440 的整数休息分钟数。", isError: true);
            return;
        }

        await SendActiveOperationAsync(new CoreRequest(
            CoreOperations.StartTimedRest,
            CommitmentId: _snapshot?.ActiveComputerCommitmentId,
            ExpectedVersion: _snapshot?.ActiveSupervision?.CommitmentVersion,
            RestMinutes: minutes));
    }

    private async void InterpretNaturalLanguageButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_naturalLanguageBusy) return;
        SetNaturalLanguageBusy(true);
        try
        {
            var outcome = await SendCompanionAsync(new InterpretNaturalLanguageCommand(
                NaturalLanguageBox.Text, CandidateSource.Desktop));
            if (outcome is { Success: false })
                NaturalLanguageCandidateText.Text = NaturalLanguageCandidatePresentation.FormatFailure(outcome);
        }
        finally
        {
            SetNaturalLanguageBusy(false);
        }
    }

    private async void ConfirmNaturalLanguageCandidateButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_naturalLanguageCandidate is null)
        {
            SetOperationStatus("当前没有可确认的自然语言候选操作。", isError: true);
            return;
        }

        await SendCompanionAsync(
            new ConfirmNaturalLanguageCandidateCommand(_naturalLanguageCandidate.CandidateId));
    }

    private async void DiscardNaturalLanguageCandidateButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_naturalLanguageCandidate is null)
        {
            SetOperationStatus("当前没有可放弃的自然语言候选操作。", isError: true);
            return;
        }

        await SendCompanionAsync(
            new DiscardNaturalLanguageCandidateCommand(_naturalLanguageCandidate.CandidateId));
    }

    private async void SaveAiCredentialButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var credential = AiCredentialBox.Password;
        await SendCompanionAsync(new SaveAiCredentialCommand(credential));
        AiCredentialBox.Clear();
    }

    private async void DeleteAiCredentialButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new DeleteAiCredentialCommand());

    private async void SetAiMonthlyHardCapButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!decimal.TryParse(
                AiHardCapBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var hardCap) &&
            !decimal.TryParse(
                AiHardCapBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out hardCap))
        {
            SetOperationStatus("请输入有效的 AI 月度硬上限。", isError: true);
            return;
        }
        var current = _companionSnapshot?.Ai.MonthlyHardCapCny ?? 30m;
        if (hardCap > current)
        {
            var confirmation = MessageBox.Show(
                $"确认把 AI 月度费用硬上限从 {current:F2} 元提高到 {hardCap:F2} 元吗？",
                "明确提高 AI 费用上限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
        }
        await SendCompanionAsync(new SetAiMonthlyHardCapCommand(hardCap));
    }

    private async void SetAiModelPreferenceButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (AiModelPreferenceBox.SelectedItem is not AiModelPreference preference)
        {
            SetOperationStatus("请先选择 Flash 或 Pro。", isError: true);
            return;
        }

        await SendCompanionAsync(new SetAiModelPreferenceCommand(preference));
    }

    private async void SendAiChatButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var command = new RequestAiChatCommand(AiChatBox.Text);
        var outcome = await SendCompanionAsync(command);
        if (outcome?.ErrorCode == "ai_cost_confirmation_required")
        {
            var confirmation = MessageBox.Show(
                $"{outcome.Message}\n\n仍要继续吗？",
                "确认本次 AI 费用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation == MessageBoxResult.Yes)
                await SendCompanionAsync(command with { ApprovedEstimatedCostOverOneCny = true });
        }
    }

    private async void ConfigureWorktimeButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new ConfigureWorktimeChannelCommand(
            WorktimeEnabledBox.IsChecked == true,
            LarkCliPathBox.Text,
            LarkProfileBox.Text,
            DetailedPreviewBox.IsChecked == true
                ? NotificationPreviewMode.Detailed
                : NotificationPreviewMode.Privacy));

    private async void EndSelectedCommitmentButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先在正式状态中选择要提前结束的承诺。", isError: true);
            return;
        }

        await SendCompanionAsync(new EndCommitmentEarlyCommand(commitment.Id, commitment.Version));
    }

    private async void CancelSelectedCommitmentButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先在正式状态中选择要取消的承诺。", isError: true);
            return;
        }
        var reason = CommitmentChangeReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            SetOperationStatus("取消承诺必须填写原因。", isError: true);
            return;
        }
        if (MessageBox.Show(
                $"确认取消所选承诺？\n\n原因：{reason}\n历史会保留，且不会标记完成。",
                "确认取消承诺", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await SendCompanionAsync(new CancelCommitmentCommand(commitment.Id, commitment.Version, reason));
    }

    private async void DeferSelectedCommitmentButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = SelectedCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先在正式状态中选择要推迟的进行中承诺。", isError: true);
            return;
        }
        var reason = CommitmentChangeReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            SetOperationStatus("推迟承诺必须填写原因。", isError: true);
            return;
        }
        if (!DateTime.TryParseExact(
                CommitmentDeferStartBox.Text.Trim(), "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStart))
        {
            SetOperationStatus("推迟时间请使用 yyyy-MM-dd HH:mm。", isError: true);
            return;
        }
        localStart = DateTime.SpecifyKind(localStart, DateTimeKind.Local);
        var newStart = new DateTimeOffset(localStart);
        if (newStart <= DateTimeOffset.Now)
        {
            SetOperationStatus("推迟后的开始时间必须在未来。", isError: true);
            return;
        }
        if (MessageBox.Show(
                $"确认把当前监督推迟到 {newStart:yyyy-MM-dd HH:mm}？\n\n" +
                "当前承诺将进入待回顾，并按剩余时长建立新承诺。",
                "确认推迟承诺", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await SendCompanionAsync(new DeferActiveCommitmentCommand(
            commitment.Id, commitment.Version, newStart, reason));
    }

    private async void SubmitCommitmentReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = ResolveSelectedReviewCommitment();
        if (commitment is null)
        {
            const string message = "请先在下方选择一条待回顾承诺；如果只有一条，Jarvis 会自动选中。";
            CommitmentReviewStatusText.Text = message;
            SetOperationStatus(message, isError: true);
            return;
        }

        SubmitCommitmentReviewButton.IsEnabled = false;
        CommitmentReviewStatusText.Text = "正在提交回顾，请稍候……";
        try
        {
            var outcome = await SendCompanionAsync(new SubmitCommitmentReviewCommand(
                commitment.Id,
                CommitmentReviewTextBox.Text,
                CompletionAssessmentBox.SelectedItem is CompletionAssessment assessment
                    ? assessment
                    : null));
            if (outcome?.Success == true)
            {
                CommitmentReviewTextBox.Clear();
                CommitmentReviewStatusText.Text = outcome.Message ?? "回顾已保存。";
            }
            else
            {
                CommitmentReviewStatusText.Text = outcome?.Message ?? "回顾未提交，请按提示修改后重试。";
            }
        }
        finally
        {
            SubmitCommitmentReviewButton.IsEnabled = true;
        }
    }

    private async void DeferCommitmentReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = ResolveSelectedReviewCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先选择一条待回顾承诺。", isError: true);
            return;
        }
        await SendCompanionAsync(new DeferCommitmentReviewCommand(commitment.Id, 30));
    }

    private async void SkipCommitmentReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var commitment = ResolveSelectedReviewCommitment();
        if (commitment is null)
        {
            SetOperationStatus("请先选择一条待回顾承诺。", isError: true);
            return;
        }
        await SendCompanionAsync(new SkipCommitmentReviewCommand(commitment.Id));
    }

    private async void ConfigureDailyReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TimeOnly.TryParseExact(
                DailyReviewTimeBox.Text.Trim(), ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            SetOperationStatus("每日复盘时间请填写 HH:mm。", isError: true);
            return;
        }

        await SendCompanionAsync(new ConfigureDailyReviewCommand(time));
    }

    private async void StartDailyReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new StartDailyReviewCommand());

    private async void SnoozeDailyReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new SnoozeDailyReviewCommand(30));

    private async void SnoozeDailyReviewSixtyButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new SnoozeDailyReviewCommand(60));

    private async void SkipDailyReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new SkipDailyReviewCommand());

    private async void AnswerDailyReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var review = _companionSnapshot?.DailyReview;
        if (review?.SessionId is null)
        {
            SetOperationStatus("当前没有进行中的每日复盘。", isError: true);
            return;
        }

        await SendCompanionAsync(new RespondDailyReviewCommand(
            review.SessionId.Value, DailyReviewAnswerBox.Text));
        DailyReviewAnswerBox.Clear();
    }

    private async void ConfigureCycleReviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(CycleIntervalBox.Text, out var days) ||
            !TimeOnly.TryParseExact(
                CycleReviewTimeBox.Text.Trim(), ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            SetOperationStatus("周期天数和时间格式无效。", isError: true);
            return;
        }

        await SendCompanionAsync(new ConfigureCycleReviewCommand(
            DateOnly.FromDateTime(DateTime.Today), days, time));
    }

    private async void StartCycleReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await SendCompanionAsync(new StartCycleReviewCommand());

    private async void ConfirmCycleFocusesButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var focuses = CycleFocusesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await SendCompanionAsync(new ConfirmCycleFocusesCommand(focuses));
    }

    private async void GenerateDailyAiReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await GenerateAiReviewDraftAsync(AiReviewKind.Daily);

    private async void GenerateCycleAiReviewButton_Click(object sender, RoutedEventArgs eventArgs) =>
        await GenerateAiReviewDraftAsync(AiReviewKind.Cycle);

    private async Task GenerateAiReviewDraftAsync(AiReviewKind kind)
    {
        if (_aiReviewBusy) return;
        SetAiReviewBusy(true);
        try
        {
            var command = new GenerateAiReviewDraftCommand(kind);
            var outcome = await SendCompanionAsync(command);
            if (outcome?.ErrorCode == "ai_cost_confirmation_required")
            {
                var confirmation = MessageBox.Show(
                    $"{outcome.Message}\n\n仍要继续生成复盘草稿吗？",
                    "确认本次 AI 费用",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirmation == MessageBoxResult.Yes)
                    await SendCompanionAsync(command with { ApprovedEstimatedCostOverOneCny = true });
            }
        }
        finally
        {
            SetAiReviewBusy(false);
        }
    }

    private async void ConfirmAiReviewDraftButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_activeAiReviewDraft is not { State: AiReviewDraftState.Pending } draft)
        {
            SetOperationStatus("当前没有可确认的 AI 复盘草稿。", isError: true);
            return;
        }
        if (!TryReadAiReviewEvaluation(out var evaluation)) return;
        await SendCompanionAsync(new ConfirmAiReviewDraftCommand(
            draft.DraftId,
            AiReviewDraftBox.Text,
            evaluation.QualityRating,
            evaluation.StructureReliable,
            evaluation.AmbiguityHandled,
            evaluation.NoOverreach,
            evaluation.PrivacyScopeConfirmed,
            evaluation.Note));
    }

    private async void DiscardAiReviewDraftButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_activeAiReviewDraft is not { State: AiReviewDraftState.Pending } draft)
        {
            SetOperationStatus("当前没有可放弃的 AI 复盘草稿。", isError: true);
            return;
        }
        await SendCompanionAsync(new DiscardAiReviewDraftCommand(draft.DraftId));
    }

    private async void RecordManualAiComparisonButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_activeAiReviewDraft is not { State: AiReviewDraftState.Confirmed } draft)
        {
            SetOperationStatus("请先确认一份 AI 复盘草稿，再记录手动对照。", isError: true);
            return;
        }
        if (!TryReadAiReviewEvaluation(out var evaluation)) return;
        await SendCompanionAsync(new RecordManualAiComparisonCommand(
            draft.DraftId,
            "qwen3.7-flash",
            ManualComparisonOutputBox.Text,
            evaluation.QualityRating,
            evaluation.StructureReliable,
            evaluation.AmbiguityHandled,
            evaluation.NoOverreach,
            evaluation.PrivacyScopeConfirmed,
            evaluation.Note));
    }

    private bool TryReadAiReviewEvaluation(out AiReviewEvaluationView evaluation)
    {
        if (!int.TryParse(AiReviewQualityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) ||
            rating is < 1 or > 5)
        {
            SetOperationStatus("AI 复盘质量评分请填写 1–5。", isError: true);
            evaluation = null!;
            return false;
        }
        evaluation = new AiReviewEvaluationView(
            rating,
            AiReviewStructureBox.IsChecked == true,
            AiReviewAmbiguityBox.IsChecked == true,
            AiReviewNoOverreachBox.IsChecked == true,
            AiReviewPrivacyBox.IsChecked == true,
            AiReviewEvaluationNoteBox.Text.Trim());
        return true;
    }

    private async Task<CompanionOutcome?> SendCompanionAsync(CompanionCommand command)
    {
        var response = await _coreClient.SendAsync(new CoreRequest(
            CoreOperations.DispatchCompanion,
            Companion: command));
        var outcome = response.CompanionOutcome;
        if (!response.Success || outcome is null)
        {
            SetOperationStatus(response.Message ?? "Core 未返回助手操作结果。", isError: true);
            return outcome;
        }

        SetOperationStatus(outcome.Message ?? (outcome.Success ? "已处理。" : "操作失败。"), !outcome.Success);
        if (outcome.Snapshot is not null)
            ApplyCompanionSnapshot(outcome.Snapshot);
        if (outcome.AssistantText is not null)
            AiChatResultText.Text = outcome.AssistantText;
        if (outcome.Candidate is not null)
        {
            _naturalLanguageCandidate = outcome.Candidate;
            NaturalLanguageCandidateText.Text = outcome.Candidate.Summary;
        }
        if (outcome.Success)
            await RefreshSnapshotAsync();
        return outcome;
    }

    private void ApplyCompanionSnapshot(CompanionSnapshot? snapshot)
    {
        if (snapshot is null) return;
        _companionSnapshot = snapshot;
        RefreshCommitmentGridRows();
        _naturalLanguageCandidate = snapshot.PendingCandidate;

        var ai = snapshot.Ai;
        AiModelPreferenceBox.SelectedItem = ai.ModelPreference;
        AiStatusText.Text = ai.Enabled
            ? $"{ai.Provider} · {ai.Model} · Key …{ai.CredentialLastFour} · 本月 ¥{ai.MonthSpendCny:F4}/¥{ai.MonthlyHardCapCny:F0}"
            : $"{ai.Provider} · {ai.Model} · 未配置（表单、模板和监督仍可用）";
        AiHardCapBox.Text = ai.MonthlyHardCapCny.ToString("0.##", CultureInfo.CurrentCulture);
        if (ai.IsRequestInProgress) AiStatusText.Text += " · 云端请求处理中";
        if (ai.Alert24Reached) AiStatusText.Text += " · 已达到 ¥24 预警";
        else if (ai.Alert15Reached) AiStatusText.Text += " · 已达到 ¥15 预警";
        if (!string.IsNullOrWhiteSpace(ai.LastError)) AiStatusText.Text += $" · 最近错误：{ai.LastError}";
        if (!_naturalLanguageBusy)
            NaturalLanguageCandidateText.Text = snapshot.PendingCandidate?.Summary ?? "当前没有候选操作。";
        ConfirmNaturalLanguageCandidateButton.IsEnabled = !_naturalLanguageBusy && _naturalLanguageCandidate is not null;
        DiscardNaturalLanguageCandidateButton.IsEnabled = !_naturalLanguageBusy && _naturalLanguageCandidate is not null;
        AiChatResultText.Text = snapshot.RecentChat.LastOrDefault(item => item.Role == "assistant")?.Text ?? "";

        var channel = snapshot.WorktimeChannel;
        WorktimeEnabledBox.IsChecked = channel.Enabled;
        DetailedPreviewBox.IsChecked = channel.PreviewMode == NotificationPreviewMode.Detailed;
        if (!string.IsNullOrWhiteSpace(channel.Profile)) LarkProfileBox.Text = channel.Profile;
        WorktimeStatusText.Text = !channel.Enabled
            ? "飞书通道未启用"
            : $"监听：{(channel.ListenerReady ? "就绪" : "未就绪")} · 用户：{(channel.UserBound ? $"已绑定 …{channel.BoundUserSuffix}" : "未绑定")}" +
              (string.IsNullOrWhiteSpace(channel.LastError) ? "" : $" · {channel.LastError}") +
              "\n手机提醒仅在连续偏离达到承诺中设置的首次手机阈值后发送；不会在监督开始或结束时自动发送。";
        MobileCardGrid.ItemsSource = snapshot.MobileCards.OrderByDescending(item => item.SentAt).ToArray();

        var previouslySelectedId = (CommitmentReviewList.SelectedItem as CommitmentReviewChoice)?.Review.CommitmentId;
        var reviewChoices = snapshot.CommitmentReviews
            .OrderByDescending(item => item.RequestedAt)
            .Select(item => new CommitmentReviewChoice(
                item,
                $"{item.CommitmentId.ToString()[..8]} · v{item.CommitmentVersion} · {item.State}" +
                (item.Assessment is null ? "" : $" · {item.Assessment}")))
            .ToArray();
        CommitmentReviewList.ItemsSource = reviewChoices;
        var selectedChoice = reviewChoices.SingleOrDefault(item => item.Review.CommitmentId == previouslySelectedId);
        var actionableChoices = reviewChoices.Where(item => item.Review.State is not
            (CommitmentReviewState.Completed or CommitmentReviewState.Skipped)).ToArray();
        CommitmentReviewList.SelectedItem = selectedChoice ??
            (actionableChoices.Length == 1 ? actionableChoices[0] : null);
        DailyReviewTimeBox.Text = snapshot.DailyReview.ScheduledLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        DailyReviewQuestionText.Text = snapshot.DailyReview.FactsSummary + "\n\n" + DailyQuestion(snapshot.DailyReview);
        DailyReviewRecordText.Text = DailyReviewRecordPresentation.Format(snapshot.DailyReview);

        var cycle = snapshot.CycleReview;
        CycleIntervalBox.Text = cycle.IntervalDays.ToString(CultureInfo.InvariantCulture);
        CycleReviewSummaryText.Text = cycle.Trends is null
            ? $"周期复盘：{cycle.State}"
            : $"{cycle.PeriodStart:yyyy-MM-dd} 至 {cycle.PeriodEnd:yyyy-MM-dd} · " +
              $"计划 {cycle.Trends.PlannedCommitments} 项/{cycle.Trends.PlannedMinutes:F0} 分钟 · " +
              $"实际可观察 {cycle.Trends.ObservedMinutes:F0} 分钟 · " +
              $"相关 {cycle.Trends.RelatedMinutes:F0} · 分心 {cycle.Trends.DistractingMinutes:F0} · " +
              $"休息 {cycle.Trends.RestMinutes:F0} 分钟 · 推迟 {cycle.Trends.DeferredReviews} · 未回应 {cycle.Trends.NoResponseCount}";
        if (cycle.Trends is not null)
        {
            var commitmentLines = cycle.Trends.Commitments.Select(item =>
                $"承诺 {item.CommitmentId.ToString()[..8]} · {item.LocalDate:MM-dd} · " +
                $"投入目标：{item.InputGoal ?? "—"}（计划 {item.PlannedMinutes:F0} 分钟 / 相关记录 {item.RelatedMinutes:F0} 分钟）· " +
                $"成果目标：{item.OutcomeGoal ?? "—"}（{item.Assessment?.ToString() ?? "未评估"}；{item.ReviewText ?? "无回顾原文"}）· " +
                $"偏离 {item.DistractingMinutes:F0} / 休息 {item.RestMinutes:F0} 分钟");
            var dailyLines = cycle.Trends.DailyReviews.Select(item =>
                $"每日复盘 {item.SessionId.ToString()[..8]} · {item.ReviewDate:MM-dd} · {item.State} · {item.AnswerCount} 条原始回答");
            var details = commitmentLines.Concat(dailyLines).ToArray();
            if (details.Length > 0)
                CycleReviewSummaryText.Text += "\n\n可追溯明细：\n" + string.Join("\n", details);
        }
        if (cycle.ConfirmedFocuses.Count > 0)
            CycleFocusesBox.Text = string.Join(Environment.NewLine, cycle.ConfirmedFocuses);

        _activeAiReviewDraft = snapshot.PendingAiReviewDraft ?? snapshot.ConfirmedAiReviewDrafts.FirstOrDefault();
        if (_activeAiReviewDraft is null)
        {
            _displayedAiReviewDraftId = null;
            AiReviewDraftStatusText.Text = "当前没有待确认的 AI 复盘草稿。";
            AiReviewDraftBox.Clear();
            ManualComparisonPromptBox.Clear();
        }
        else
        {
            if (_displayedAiReviewDraftId != _activeAiReviewDraft.DraftId)
            {
                AiReviewDraftBox.Text = _activeAiReviewDraft.ConfirmedText ?? _activeAiReviewDraft.DraftText;
                _displayedAiReviewDraftId = _activeAiReviewDraft.DraftId;
            }
            AiReviewDraftStatusText.Text =
                $"{_activeAiReviewDraft.Kind} · {_activeAiReviewDraft.State} · " +
                $"{_activeAiReviewDraft.Provider}/{_activeAiReviewDraft.Model}\n" +
                _activeAiReviewDraft.FactsScope;
            ManualComparisonPromptBox.Text = _activeAiReviewDraft.AnonymizedComparisonPrompt ?? "当前记录没有手动对照提示。";
        }
        ConfirmAiReviewDraftButton.IsEnabled = !_aiReviewBusy &&
                                               _activeAiReviewDraft?.State == AiReviewDraftState.Pending;
        DiscardAiReviewDraftButton.IsEnabled = ConfirmAiReviewDraftButton.IsEnabled;
        var trial = snapshot.AiTrialEvidence;
        AiTrialEvidenceText.Text = trial.TrialStartedAt is null
            ? "尚无复盘 AI 请求。"
            : $"试运行 {trial.TrialStartedAt:yyyy-MM-dd} 至 {trial.TrialEndsAt:yyyy-MM-dd} · " +
              $"请求 {trial.TotalRequests}（成功 {trial.SuccessfulRequests}/失败 {trial.FailedRequests}）· " +
              $"每日 {trial.DailyRequests}/周期 {trial.CycleRequests} · 确认 {trial.ConfirmedDrafts} · " +
              $"修改 {trial.ModifiedDrafts} · 手动对照 {trial.ManualComparisonCount} · " +
              $"平均延迟 {trial.AverageLatencyMilliseconds:F0}ms · 费用 ¥{trial.TotalCostCny:F4} · " +
              $"平均质量 {(trial.AverageQualityRating?.ToString("F1", CultureInfo.InvariantCulture) ?? "—")}\n" +
              $"结构可靠 {Rate(trial.StructureReliableRate)} · 歧义处理 {Rate(trial.AmbiguityHandledRate)} · " +
              $"无越权 {Rate(trial.NoOverreachRate)} · 最小事实范围 {Rate(trial.PrivacyScopeConfirmedRate)} · " +
              $"模型 {string.Join(", ", trial.UsedModels)} · " +
              (trial.TrialWindowComplete ? "两周窗口已完成" : "两周窗口进行中") +
              (trial.ManualComparisonCount == 0
                  ? ""
                  : $"\n手动 Qwen：平均质量 {trial.ManualAverageQualityRating?.ToString("F1", CultureInfo.InvariantCulture) ?? "—"} · " +
                    $"结构可靠 {Rate(trial.ManualStructureReliableRate)} · 歧义处理 {Rate(trial.ManualAmbiguityHandledRate)} · " +
                    $"无越权 {Rate(trial.ManualNoOverreachRate)}");
        AiReviewHistoryText.Text = snapshot.ConfirmedAiReviewDrafts.Count == 0
            ? "尚无已确认 AI 复盘记录。"
            : "已确认记录：\n" + string.Join("\n", snapshot.ConfirmedAiReviewDrafts.Select(item =>
                $"{item.PeriodStart:yyyy-MM-dd}–{item.PeriodEnd:yyyy-MM-dd} · {item.Kind} · " +
                $"{item.Model} · 质量 {item.Evaluation?.QualityRating.ToString(CultureInfo.InvariantCulture) ?? "—"} · " +
                (item.ConfirmedText ?? item.DraftText)));
    }

    private static string Rate(double? value) => value is null ? "—" : $"{value.Value:P0}";

    private void ClearCompanionProjection()
    {
        _companionSnapshot = null;
        _naturalLanguageCandidate = null;
        _activeAiReviewDraft = null;
        _displayedAiReviewDraftId = null;
        WorktimeStatusText.Text = "Core 未连接";
        MobileCardGrid.ItemsSource = null;
        CommitmentReviewList.ItemsSource = null;
        NaturalLanguageCandidateText.Text = "Core 未连接；没有可确认候选。";
        AiStatusText.Text = "Core 未连接";
        DailyReviewQuestionText.Text = "Core 未连接";
        CycleReviewSummaryText.Text = "Core 未连接";
        AiReviewDraftStatusText.Text = "Core 未连接";
        AiReviewDraftBox.Clear();
        AiTrialEvidenceText.Text = "Core 未连接";
        AiReviewHistoryText.Text = "Core 未连接";
    }

    private void SetAiReviewBusy(bool busy)
    {
        _aiReviewBusy = busy;
        AiReviewBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateDailyAiReviewButton.IsEnabled = !busy;
        GenerateCycleAiReviewButton.IsEnabled = !busy;
        ConfirmAiReviewDraftButton.IsEnabled = !busy &&
                                               _activeAiReviewDraft?.State == AiReviewDraftState.Pending;
        DiscardAiReviewDraftButton.IsEnabled = ConfirmAiReviewDraftButton.IsEnabled;
        if (busy) AiReviewDraftStatusText.Text = "正在生成 AI 复盘草稿，请稍后……";
    }

    private void SetNaturalLanguageBusy(bool busy)
    {
        _naturalLanguageBusy = busy;
        NaturalLanguageBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateNaturalLanguageCandidateButton.IsEnabled = !busy;
        ConfirmNaturalLanguageCandidateButton.IsEnabled = !busy && _naturalLanguageCandidate is not null;
        DiscardNaturalLanguageCandidateButton.IsEnabled = !busy && _naturalLanguageCandidate is not null;
        if (busy) NaturalLanguageCandidateText.Text = NaturalLanguageCandidatePresentation.BusyText;
    }

    private static string DailyQuestion(DailyReviewView review)
    {
        if (review.State != ReviewSessionState.InProgress || review.CurrentQuestion is null)
            return $"每日复盘：{review.State}";
        return review.CurrentQuestion switch
        {
            ReviewQuestionKind.Facts => "今天实际完成了什么？",
            ReviewQuestionKind.PendingCommitments => "还有哪些承诺待回顾或未收口？",
            ReviewQuestionKind.WhatWentWell => "今天哪些做法有效？",
            ReviewQuestionKind.WhatWentPoorly => "今天哪里不理想？",
            ReviewQuestionKind.Reasons => "你认为主要原因是什么？",
            ReviewQuestionKind.TomorrowAdjustments => "明天准备确认哪 1–3 个调整？",
            _ => "请继续回答。"
        };
    }

    private void PresentationMode_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (_snapshot is not null)
        {
            ApplyActiveSupervision(_snapshot, ActiveCommitment());
        }
    }

    private async Task SendActiveOperationAsync(CoreRequest request)
    {
        if (request.CommitmentId is null)
        {
            SetOperationStatus("当前没有正在自动监督的电脑型承诺。", isError: true);
            return;
        }

        var response = await _coreClient.SendAsync(request);
        SetOperationStatus(response.Message ?? (response.Success ? "已处理。" : "操作失败。"), !response.Success);
        if (response.Success)
        {
            ApplySnapshot(response.Snapshot);
        }
    }

    private void ApplyActiveSupervision(SupervisionSnapshot snapshot, CommitmentView? commitment)
    {
        var state = snapshot.ActiveSupervision;
        if (state is null || commitment is null)
        {
            ClearSupervisionProjection();
            return;
        }

        SupervisionPanel.Visibility = Visibility.Visible;
        var classification = state.Classification switch
        {
            ActivityClassification.Related => "相关",
            ActivityClassification.Distracting => "分心",
            ActivityClassification.Unknown => "未确定",
            _ => "无法观察"
        };
        var deviation = state.DeviationStartedAt is null
            ? "当前没有连续偏离"
            : $"连续偏离 {FormatDuration(state.CountedDeviation)}（起点 {state.DeviationStartedAt.Value.ToLocalTime():HH:mm:ss}）";
        var rest = state.ActiveRest is null
            ? ""
            : $" · 限时休息至 {state.ActiveRest.EndAt.ToLocalTime():HH:mm}";
        ActiveSupervisionText.Text =
            $"{DisplayTitle(commitment)} · 活动：{classification}{(state.IsIdle ? " / 空闲" : "")} · {deviation}{rest}";
        var actionableLabel = state.ActionableTarget is null
            ? null
            : FormatActionableTarget(state.ActionableTarget);
        CurrentRelatedButton.ToolTip = state.ActionableTarget?.Value;
        CurrentDistractingButton.ToolTip = state.ActionableTarget?.Value;
        CurrentRelatedButton.Content = actionableLabel is null
            ? "没有可确认的外部活动"
            : $"将 {actionableLabel} 标为相关";
        CurrentDistractingButton.Content = actionableLabel is null
            ? "没有可确认的外部活动"
            : $"将 {actionableLabel} 标为分心";
        CurrentRelatedButton.IsEnabled = actionableLabel is not null;
        CurrentDistractingButton.IsEnabled = actionableLabel is not null;
        AcceptRestButton.Visibility = state.PendingPrompt == SupervisionPromptKind.ConfirmRest
            ? Visibility.Visible
            : Visibility.Collapsed;
        DenyRestButton.Visibility = AcceptRestButton.Visibility;
        RuleScopeBox.IsEnabled = state.Classification == ActivityClassification.Unknown ||
                                 state.ReminderMarkerActive;

        var reminder = snapshot.LatestReminder;
        var isFullscreen = ForegroundPresentationDetector.IsFullscreen();
        FullscreenStateText.Text = $"全屏自动识别：{(isFullscreen ? "是" : "否")}";
        var presentation = LocalReminderPresentation.Evaluate(
            reminder,
            state,
            commitment.Id,
            snapshot.Now,
            commitment.ReminderSettings,
            QuietModeBox.IsChecked == true,
            isFullscreen,
            MuteSoundBox.IsChecked == true);
        ReminderMarkerText.Visibility = presentation.ShowMarker
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReminderBubble.Visibility = presentation.ShowBubble ? Visibility.Visible : Visibility.Collapsed;
        if (presentation.ShowBubble)
        {
            ReminderBubbleText.Text = reminder!.Message;
        }

        if (presentation.ShowOverlay)
        {
            _reminderOverlay.Present(
                presentation.ShowBubble
                    ? reminder!.Message
                    : $"{DisplayTitle(commitment)}：有待处理提醒");
        }
        else
        {
            _reminderOverlay.Hide();
        }

        if (_soundGate.Consume(
                reminder,
                commitment.Id,
                state.CommitmentVersion,
                snapshot.Now,
                presentation.SuppressSound))
        {
            GentleReminderSound.Play();
        }
    }

    private void ClearSupervisionProjection()
    {
        SupervisionPanel.Visibility = Visibility.Collapsed;
        CurrentRelatedButton.Content = "本次活动相关";
        CurrentDistractingButton.Content = "本次活动分心";
        CurrentRelatedButton.IsEnabled = false;
        CurrentDistractingButton.IsEnabled = false;
        ReminderBubble.Visibility = Visibility.Collapsed;
        ReminderMarkerText.Visibility = Visibility.Collapsed;
        _reminderOverlay.Hide();
    }

    private CommitmentView? ActiveCommitment() => _snapshot?.Commitments.SingleOrDefault(
        commitment => commitment.Id == _snapshot.ActiveComputerCommitmentId);

    private CommitmentView? ResolveSelectedReviewCommitment()
    {
        var reviewId = (CommitmentReviewList.SelectedItem as CommitmentReviewChoice)?.Review.CommitmentId;
        if (reviewId is not null)
        {
            return (_snapshot?.Commitments ?? [])
                .SingleOrDefault(item => item.Id == reviewId.Value);
        }

        return SelectedCommitment();
    }

    private CommitmentView? SelectedCommitment() =>
        (CommitmentGrid.SelectedItem as CommitmentGridRow)?.Commitment ??
        CommitmentGrid.SelectedItem as CommitmentView;

    private void RefreshCommitmentGridRows()
    {
        var selectedId = SelectedCommitment()?.Id;
        if (_snapshot is null)
        {
            CommitmentGrid.ItemsSource = null;
            return;
        }

        var reviews = (_companionSnapshot?.CommitmentReviews ?? [])
            .GroupBy(item => item.CommitmentId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.RequestedAt).First());
        var rows = _snapshot.Commitments.Select(commitment =>
        {
            reviews.TryGetValue(commitment.Id, out var review);
            var reviewStatus = review is not null
                ? ReviewStatus(review)
                : commitment.Phase == CommitmentPhase.AwaitingReview ? "待回顾" : "—";
            var supervisionStatus = commitment.Phase == CommitmentPhase.AwaitingReview &&
                                    review?.State == CommitmentReviewState.Completed
                ? "监督已结束"
                : PhaseText(commitment.Phase);
            return new CommitmentGridRow(commitment, supervisionStatus, reviewStatus);
        }).ToArray();
        CommitmentGrid.ItemsSource = rows;
        CommitmentGrid.SelectedItem = rows.SingleOrDefault(item => item.Commitment.Id == selectedId);
    }

    private static string ReviewStatus(CommitmentReviewView review) => review.State switch
    {
        CommitmentReviewState.Completed => review.Assessment switch
        {
            CompletionAssessment.Completed => "已回顾 · 已完成",
            CompletionAssessment.Partial => "已回顾 · 部分完成",
            CompletionAssessment.NotCompleted => "已回顾 · 未完成",
            _ => "已回顾"
        },
        CommitmentReviewState.Deferred => "稍后回顾",
        CommitmentReviewState.Skipped => "已跳过回顾",
        _ => "待回顾"
    };

    private void CommitmentGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        ContentScrollViewer.ScrollToVerticalOffset(ContentScrollViewer.VerticalOffset - eventArgs.Delta);
    }

    public void RestoreConfigurationWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void StopForApplicationExit()
    {
        _refreshTimer.Stop();
        _reminderOverlay.Close();
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}小时 {value.Minutes}分"
            : $"{Math.Max(0, (int)value.TotalMinutes)}分 {Math.Max(0, value.Seconds)}秒";

    private static readonly RuleScopeChoice[] RuleScopeChoices =
    [
        new("仅这次承诺", ActivityRuleScope.Commitment),
        new("同一模板", ActivityRuleScope.Template),
        new("全局默认", ActivityRuleScope.Global)
    ];

    private sealed record RuleScopeChoice(string Label, ActivityRuleScope Scope);

    private sealed record PlanChoice(RecurrencePlanView Plan, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CommitmentReviewChoice(CommitmentReviewView Review, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CommitmentGridRow(
        CommitmentView Commitment,
        string SupervisionStatus,
        string ReviewStatus);
}

public static class DailyReviewRecordPresentation
{
    public static string Format(DailyReviewView review)
    {
        if (review.SessionId is null || review.ReviewDate is null)
        {
            return "尚无每日复盘记录。";
        }

        var lines = new List<string>
        {
            $"{review.ReviewDate:yyyy-MM-dd} · {StateText(review.State)}"
        };
        if (review.AnswerDetails.Count == 0)
        {
            lines.Add("尚无原始回答。");
        }
        else
        {
            lines.AddRange(review.AnswerDetails.Select(item =>
                $"• {QuestionText(item.Question)}：{item.RawText}（{item.AnsweredAt.ToLocalTime():HH:mm}）"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string StateText(ReviewSessionState state) => state switch
    {
        ReviewSessionState.Completed => "已完成",
        ReviewSessionState.Skipped => "已跳过",
        ReviewSessionState.NoResponse => "未回应",
        ReviewSessionState.InProgress => "进行中",
        ReviewSessionState.Snoozed => "已稍后",
        _ => state.ToString()
    };

    private static string QuestionText(ReviewQuestionKind question) => question switch
    {
        ReviewQuestionKind.Facts => "今天实际完成",
        ReviewQuestionKind.PendingCommitments => "待回顾承诺",
        ReviewQuestionKind.WhatWentWell => "做得好的地方",
        ReviewQuestionKind.WhatWentPoorly => "不理想之处",
        ReviewQuestionKind.Reasons => "原因",
        ReviewQuestionKind.TomorrowAdjustments => "明日调整",
        _ => question.ToString()
    };
}

public static class CandidateCardSummary
{
    public static string Format(CommitmentCard card, bool includeTime = true)
    {
        var targets = FormatTargets(card.RelatedAppsOrSites);
        var activityRules = FormatRules(card.ActivityRules);
        var rest =
            $"闲置 {card.RestSettings.IdlePromptMinutes} 分钟询问；默认总休息 {card.RestSettings.DefaultTotalRestMinutes} 分钟";
        var time = includeTime
            ? $"{Environment.NewLine}时间：{card.StartAt.LocalDateTime:yyyy-MM-dd HH:mm} 至 {card.EndAt.LocalDateTime:yyyy-MM-dd HH:mm}"
            : "";

        return $"""
            类型：{(card.Kind == CommitmentKind.Computer ? "电脑型" : "线下")}{time}
            投入目标：{card.InputGoal ?? "未设置"}
            成果目标：{card.OutcomeGoal ?? "未设置"}
            相关项目：{targets}
            监督模式：{(card.Kind == CommitmentKind.Offline ? "不适用（线下不自动监督）" : card.SupervisionMode == SupervisionMode.Interactive ? "交互型" : "被动型")}
            活动分类规则：{activityRules}
            提醒：开始时提醒 {(card.ReminderSettings.StartReminderEnabled ? "开" : "关")}；本机偏离 {card.ReminderSettings.LocalDeviationMinutes} 分钟；手机 {card.ReminderSettings.FirstMobileDeviationMinutes} 分钟起、每 {card.ReminderSettings.MobileRepeatMinutes} 分钟、最多 {card.ReminderSettings.MaxMobileReminders} 条；声音 {(card.ReminderSettings.SoundEnabled ? "开" : "关")}；安静呈现 {(card.ReminderSettings.QuietPresentation ? "开" : "关")}
            休息：{rest}
            确认后，以上内容与三态规则均冻结为该次单次承诺；之后修改模板不会追溯改变它。

            {card.ConfirmationNotice}
            """;
    }

    private static string FormatTargets(IReadOnlyList<CommitmentTarget> targets) =>
        targets.Count == 0
            ? "无（线下承诺或未设置）"
            : string.Join("、", targets.Select(FormatTarget));

    private static string FormatRules(IReadOnlyList<ActivityRule> rules)
    {
        var groups = Enum.GetValues<ActivityClassification>()
            .Select(classification =>
            {
                var targets = rules.Where(rule => rule.Classification == classification)
                    .Select(rule => FormatTarget(rule.Target))
                    .ToArray();
                return $"{classification}：{(targets.Length == 0 ? "未设置" : string.Join("、", targets))}";
            });
        return string.Join("；", groups);
    }

    private static string FormatTarget(CommitmentTarget target) =>
        $"{(target.Kind == CommitmentTargetKind.Application ? "软件" : "网站")}：{target.Value}";
}

public static class TemplatePreviewDraft
{
    public static TemplateCommitmentDraft CreateInherited(Guid templateId, DateTimeOffset startAt) =>
        new(templateId, startAt);

    public static TemplateCommitmentDraft CreateOverridden(
        Guid templateId,
        DateTimeOffset startAt,
        int durationMinutes,
        string? inputGoal,
        string? outcomeGoal,
        IReadOnlyList<CommitmentTarget> targets,
        SupervisionMode? mode,
        ReminderSettings reminders,
        IReadOnlyList<ActivityRule> rules,
        RestSettings rest) => new(
            templateId,
            startAt,
            DurationMinutes: durationMinutes,
            InputGoal: inputGoal,
            OutcomeGoal: outcomeGoal,
            RelatedAppsOrSites: targets,
            SupervisionMode: mode,
            ReminderSettings: reminders,
            ActivityRules: rules,
            RestSettings: rest);
}

public static class CommitmentRevisionSummary
{
    public static string Format(CommitmentRevisionCard card)
    {
        var changes = ChangedLines(card.Before, card.After).ToArray();
        var changeText = changes.Length == 0
            ? "没有可见变化（请返回修改表单）。"
            : string.Join(Environment.NewLine, changes.Select(line => $"• {line}"));
        return $"""
            修改原因：{card.Reason}
            版本：v{card.FromVersion} → v{card.ToVersion}

            变更：
            {changeText}

            确认后立即向后生效；旧版本及此前的活动、偏离、提醒、回应和纠正都会保留。

            {card.ConfirmationNotice}
            """;
    }

    private static IEnumerable<string> ChangedLines(CommitmentCard before, CommitmentCard after)
    {
        if (before.StartAt != after.StartAt || before.EndAt != after.EndAt)
        {
            yield return $"时间：{before.StartAt.LocalDateTime:yyyy-MM-dd HH:mm}–{before.EndAt.LocalDateTime:HH:mm} → {after.StartAt.LocalDateTime:yyyy-MM-dd HH:mm}–{after.EndAt.LocalDateTime:HH:mm}";
        }

        if (before.InputGoal != after.InputGoal)
        {
            yield return $"投入目标：{before.InputGoal ?? "未设置"} → {after.InputGoal ?? "未设置"}";
        }

        if (before.OutcomeGoal != after.OutcomeGoal)
        {
            yield return $"成果目标：{before.OutcomeGoal ?? "未设置"} → {after.OutcomeGoal ?? "未设置"}";
        }

        if (!before.RelatedAppsOrSites.SequenceEqual(after.RelatedAppsOrSites))
        {
            yield return $"相关项目：{Targets(before.RelatedAppsOrSites)} → {Targets(after.RelatedAppsOrSites)}";
        }

        if (before.SupervisionMode != after.SupervisionMode)
        {
            yield return $"监督模式：{before.SupervisionMode} → {after.SupervisionMode}";
        }

        if (before.ReminderSettings != after.ReminderSettings)
        {
            yield return "提醒设置已修改";
        }

        if (!before.ActivityRules.SequenceEqual(after.ActivityRules))
        {
            yield return "活动分类规则已修改";
        }

        if (before.RestSettings != after.RestSettings)
        {
            yield return "休息设置已修改";
        }
    }

    private static string Targets(IReadOnlyList<CommitmentTarget> targets) => targets.Count == 0
        ? "无"
        : string.Join("、", targets.Select(target => target.Value));
}

public static class CommitmentHistorySummary
{
    public static string Format(CommitmentHistoryView history)
    {
        var lines = new List<string>
        {
            $"当前版本：v{history.CurrentVersion}",
            "",
            "版本"
        };
        foreach (var version in history.Versions.OrderByDescending(version => version.Version))
        {
            AppendVersion(lines, version);
        }
        AppendEvents(lines, "活动区段", history.ActivitySegments.Count,
            history.ActivitySegments.OrderByDescending(item => item.StartAt).Take(12).Select(item =>
                $"• {item.StartAt.ToLocalTime():MM-dd HH:mm:ss}–{item.EndAt.ToLocalTime():HH:mm:ss} · v{item.CommitmentVersion} · {item.EffectiveClassification?.ToString() ?? item.Availability.ToString()}{(item.Target is null ? "" : $" · {item.Target.Value}")}"));
        AppendEvents(lines, "提醒", history.Reminders.Count,
            history.Reminders.OrderByDescending(item => item.CreatedAt).Take(12).Select(item =>
                $"• {item.CreatedAt.ToLocalTime():MM-dd HH:mm:ss} · v{item.CommitmentVersion} · {item.Kind} · {item.Message}"));
        AppendEvents(lines, "分类纠正", history.Corrections.Count,
            history.Corrections.OrderByDescending(item => item.CorrectedAt).Take(12).Select(item =>
                $"• {item.CorrectedAt.ToLocalTime():MM-dd HH:mm:ss} · v{item.CommitmentVersion} · {item.Scope} · {item.Target.Value} · {item.OriginalClassification} → {item.CorrectedClassification}{(string.IsNullOrWhiteSpace(item.Note) ? "" : $" · {item.Note}")}"));
        AppendEvents(lines, "回应", history.Responses.Count,
            history.Responses.OrderByDescending(item => item.RecordedAt).Take(12).Select(item =>
                $"• {item.RecordedAt.ToLocalTime():MM-dd HH:mm:ss} · v{item.CommitmentVersion} · {item.Kind}{(string.IsNullOrWhiteSpace(item.Note) ? "" : $" · {item.Note}")}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendVersion(List<string> lines, CommitmentRevisionVersionView version)
    {
        var snapshot = version.Snapshot;
        var targets = snapshot.RelatedAppsOrSites.Count == 0
            ? "无"
            : string.Join("、", snapshot.RelatedAppsOrSites.Select(FormatTarget));
        var rules = snapshot.ActivityRules.Count == 0
            ? "无"
            : string.Join("、", snapshot.ActivityRules.Select(rule =>
                $"{FormatTarget(rule.Target)}={rule.Classification}"));
        var reminders = snapshot.ReminderSettings;
        var rest = snapshot.RestSettings;

        lines.Add($"• v{version.Version} · {version.EffectiveFrom.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} 起 · {version.Reason}");
        lines.Add($"  时间：{snapshot.StartAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} → {snapshot.EndAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        lines.Add($"  目标：投入={ValueOrNone(snapshot.InputGoal)}；成果={ValueOrNone(snapshot.OutcomeGoal)}");
        lines.Add($"  类型/模式：{snapshot.Kind}/{snapshot.SupervisionMode}；关联目标：{targets}");
        lines.Add($"  提醒：开始={OnOff(reminders.StartReminderEnabled)}；本地偏离={reminders.LocalDeviationMinutes}分；首次移动={reminders.FirstMobileDeviationMinutes}分；移动重复={reminders.MobileRepeatMinutes}分；最多={reminders.MaxMobileReminders}；声音={OnOff(reminders.SoundEnabled)}；安静={OnOff(reminders.QuietPresentation)}");
        lines.Add($"  活动规则：{rules}");
        lines.Add($"  休息：空闲询问={rest.IdlePromptMinutes}分；默认总时长={rest.DefaultTotalRestMinutes}分");
    }

    private static string FormatTarget(CommitmentTarget target) => $"{target.Kind}:{target.Value}";

    private static string ValueOrNone(string? value) => string.IsNullOrWhiteSpace(value) ? "无" : value;

    private static string OnOff(bool value) => value ? "开" : "关";

    private static void AppendEvents(
        List<string> lines,
        string title,
        int count,
        IEnumerable<string> values)
    {
        lines.Add("");
        lines.Add($"{title}（{count}）");
        var entries = values.ToArray();
        lines.AddRange(entries.Length == 0 ? ["• 暂无"] : entries);
        if (count > entries.Length)
        {
            lines.Add($"• 另有 {count - entries.Length} 条；正式记录仍完整保留。 ");
        }
    }
}

public static class RecurrenceChangeSummary
{
    public static string Format(RecurrenceChangeCard card)
    {
        var preview = string.Join(Environment.NewLine, card.AffectedOccurrences.Take(12).Select(item =>
            card.Kind == RecurrenceChangeKind.Skip
                ? $"• {item.Date:yyyy-MM-dd}：{item.BeforeStatus} → 已跳过"
                : $"• {item.Date:yyyy-MM-dd} · v{item.BeforeVersion} → v{item.AfterVersion}：{item.BeforeStartAt.LocalDateTime:MM-dd HH:mm}–{item.BeforeEndAt.LocalDateTime:HH:mm} → {item.AfterStartAt.LocalDateTime:MM-dd HH:mm}–{item.AfterEndAt.LocalDateTime:HH:mm}"));
        var omitted = card.AffectedOccurrences.Count > 12
            ? $"{Environment.NewLine}• 另有 {card.AffectedOccurrences.Count - 12} 个发生项"
            : "";
        var reason = card.Kind == RecurrenceChangeKind.Adjust
            ? $"{Environment.NewLine}调整原因：{card.Reason}"
            : "";
        return $"作用范围：{card.Scope}{reason}{Environment.NewLine}{preview}{omitted}{Environment.NewLine}{Environment.NewLine}{card.ConfirmationNotice}";
    }
}

internal static class GentleReminderSound
{
    public static void Play() => _ = Task.Run(PlaySynchronously);

    private static void PlaySynchronously()
    {
        const int sampleRate = 16_000;
        const int durationMilliseconds = 800;
        const double frequency = 440;
        const short amplitude = 1_800;
        var sampleCount = sampleRate * durationMilliseconds / 1000;
        using var stream = new MemoryStream(44 + sampleCount * sizeof(short));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + sampleCount * sizeof(short));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(sampleCount * sizeof(short));
            for (var index = 0; index < sampleCount; index++)
            {
                var envelope = Math.Min(1d, index / 800d) *
                               Math.Min(1d, (sampleCount - index) / 1600d);
                writer.Write((short)(amplitude * envelope *
                    Math.Sin(2 * Math.PI * frequency * index / sampleRate)));
            }
        }

        stream.Position = 0;
        using var player = new System.Media.SoundPlayer(stream);
        player.PlaySync();
    }
}
