using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;

namespace Jarvis.Core;

internal sealed class CompanionModule : IAsyncDisposable
{
    private const string ProviderName = "DeepSeek";
    private const string ModelName = "deepseek-v4-flash";
    private const string PriceVersion = "2026-04-24";
    private readonly SqliteCompanionStore _store;
    private readonly SupervisionModule _supervision;
    private readonly IClock _clock;
    private readonly IWorktimeChannel _worktimeChannel;
    private readonly ICloudAiProvider _aiProvider;
    private readonly IAiCredentialStore _credentialStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _listenerReady;
    private string? _worktimeError;
    private string? _aiError;
    private volatile bool _aiRequestInProgress;
    private DateOnly? _dailyReviewDeferredDate;
    private bool _disposed;

    private CompanionModule(
        SqliteCompanionStore store,
        SupervisionModule supervision,
        IClock clock,
        IWorktimeChannel worktimeChannel,
        ICloudAiProvider aiProvider,
        IAiCredentialStore credentialStore)
    {
        _store = store;
        _supervision = supervision;
        _clock = clock;
        _worktimeChannel = worktimeChannel;
        _aiProvider = aiProvider;
        _credentialStore = credentialStore;
    }

    public static async Task<CompanionModule> OpenAsync(
        string databasePath,
        SupervisionModule supervision,
        IClock clock,
        IWorktimeChannel worktimeChannel,
        ICloudAiProvider aiProvider,
        IAiCredentialStore credentialStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(supervision);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(worktimeChannel);
        ArgumentNullException.ThrowIfNull(aiProvider);
        ArgumentNullException.ThrowIfNull(credentialStore);
        var module = new CompanionModule(
            new SqliteCompanionStore(databasePath), supervision, clock, worktimeChannel,
            aiProvider, credentialStore);
        var dailyConfiguration = await module._store.ReadDailyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (dailyConfiguration.ConfiguredAt is null)
            await module._store.SaveDailyConfigurationAsync(
                dailyConfiguration.LocalTime, clock.Now, cancellationToken).ConfigureAwait(false);
        await module.ConfigureChannelFromStoreAsync(cancellationToken).ConfigureAwait(false);
        return module;
    }

    public async Task<CompanionOutcome> DispatchAsync(
        CompanionCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var worktimeEventId = command switch
        {
            BindWorktimeUserCommand value => value.EventId,
            HandleWorktimeActionCommand value => value.EventId,
            HandleWorktimeTextCommand value => value.EventId,
            _ => null
        };
        try
        {
            if (worktimeEventId is not null &&
                !await _store.TryBeginWorktimeEventAsync(worktimeEventId, _clock.Now, cancellationToken)
                    .ConfigureAwait(false))
            {
                var recorded = await _store.ReadWorktimeEventOutcomeAsync(worktimeEventId, cancellationToken)
                    .ConfigureAwait(false);
                if (recorded is not null && command is HandleWorktimeTextCommand repeatedText)
                {
                    await _store.CompleteWorktimeTextEventAsync(
                        worktimeEventId, _clock.Now, recorded, repeatedText.SenderId,
                        FormatWorktimeReply(recorded), StableGuid(worktimeEventId), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                return recorded ?? Ok("重复飞书事件已忽略。", await SnapshotAsync(cancellationToken));
            }
            var outcome = command switch
            {
                ConfigureWorktimeChannelCommand value => await ConfigureWorktimeAsync(value, cancellationToken),
                BindWorktimeUserCommand value => await BindWorktimeUserAsync(value, cancellationToken),
                HandleWorktimeActionCommand value => await HandleWorktimeActionAsync(value, cancellationToken),
                HandleWorktimeTextCommand value => await HandleWorktimeTextAsync(value, cancellationToken),
                EndCommitmentEarlyCommand value => await EndCommitmentEarlyAsync(value, cancellationToken),
                CancelCommitmentCommand value => await CancelCommitmentAsync(value, cancellationToken),
                DeferActiveCommitmentCommand value => await DeferActiveCommitmentAsync(value, cancellationToken),
                SubmitCommitmentReviewCommand value => await SubmitCommitmentReviewAsync(value, cancellationToken),
                DeferCommitmentReviewCommand value => await DeferCommitmentReviewAsync(value, cancellationToken),
                SkipCommitmentReviewCommand value => await SkipCommitmentReviewAsync(value, cancellationToken),
                ConfigureDailyReviewCommand value => await ConfigureDailyReviewAsync(value, cancellationToken),
                StartDailyReviewCommand => await StartDailyReviewAsync(cancellationToken),
                RespondDailyReviewCommand value => await RespondDailyReviewAsync(value, cancellationToken),
                SnoozeDailyReviewCommand value => await SnoozeDailyReviewAsync(value, cancellationToken),
                SkipDailyReviewCommand => await SkipDailyReviewAsync(cancellationToken),
                ConfigureCycleReviewCommand value => await ConfigureCycleReviewAsync(value, cancellationToken),
                StartCycleReviewCommand => await StartCycleReviewAsync(cancellationToken),
                ConfirmCycleFocusesCommand value => await ConfirmCycleFocusesAsync(value, cancellationToken),
                SaveAiCredentialCommand value => await SaveAiCredentialAsync(value, cancellationToken),
                DeleteAiCredentialCommand => await DeleteAiCredentialAsync(cancellationToken),
                SetAiMonthlyHardCapCommand value => await SetAiMonthlyHardCapAsync(value, cancellationToken),
                RequestAiChatCommand value => await RequestAiChatAsync(value, cancellationToken),
                InterpretNaturalLanguageCommand value => await InterpretNaturalLanguageAsync(value, cancellationToken),
                ConfirmNaturalLanguageCandidateCommand value =>
                    await ConfirmNaturalLanguageCandidateAsync(value, cancellationToken),
                DiscardNaturalLanguageCandidateCommand value =>
                    await DiscardNaturalLanguageCandidateAsync(value, cancellationToken),
                _ => Fail("companion_command_unknown", "Core 无法识别这项助手操作。")
            };
            if (worktimeEventId is not null)
            {
                if (command is HandleWorktimeTextCommand textCommand)
                {
                    await _store.CompleteWorktimeTextEventAsync(
                        worktimeEventId, _clock.Now, outcome, textCommand.SenderId,
                        FormatWorktimeReply(outcome), StableGuid(worktimeEventId), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _store.CompleteWorktimeEventAsync(
                        worktimeEventId, _clock.Now, outcome, CancellationToken.None).ConfigureAwait(false);
                }
            }
            return outcome;
        }
        catch
        {
            if (worktimeEventId is not null)
                await _store.FailWorktimeEventAsync(
                    worktimeEventId, _clock.Now, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AdvanceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var worktimeSettings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (worktimeSettings.Enabled && _worktimeChannel.NeedsRestart)
                await ConfigureChannelAsync(worktimeSettings, cancellationToken).ConfigureAwait(false);
            if (worktimeSettings.Enabled)
                await RetryPendingWorktimeRepliesAsync(cancellationToken).ConfigureAwait(false);
            var supervision = await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await _store.ResumeDueCommitmentReviewsAsync(_clock.Now, cancellationToken).ConfigureAwait(false);
            await EnsureCommitmentReviewsAsync(supervision, cancellationToken).ConfigureAwait(false);
            await AdvanceMobileEscalationAsync(supervision, cancellationToken).ConfigureAwait(false);
            await AdvanceDailyReviewAsync(supervision, cancellationToken).ConfigureAwait(false);
            await AdvanceCycleReviewAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CompanionSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        var currentCredential = await _credentialStore.ReadAsync("deepseek", cancellationToken)
            .ConfigureAwait(false);
        var lastFour = string.IsNullOrEmpty(currentCredential) ? null : currentCredential[^Math.Min(4, currentCredential.Length)..];
        var spend = await _store.ReadMonthSpendAsync(_clock.Now, cancellationToken).ConfigureAwait(false);
        var hardCap = await _store.ReadAiMonthlyHardCapAsync(cancellationToken).ConfigureAwait(false);
        var dailyReview = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        var factsDate = dailyReview.ReviewDate ?? DateOnly.FromDateTime(_clock.Now.ToLocalTime().DateTime);
        dailyReview = dailyReview with
        {
            FactsSummary = await _store.CalculateDailyFactsAsync(factsDate, cancellationToken)
                .ConfigureAwait(false)
        };
        return new CompanionSnapshot(
            new WorktimeChannelView(
                settings.Enabled, _listenerReady && _worktimeChannel.IsHealthy,
                !string.IsNullOrWhiteSpace(settings.BoundUserId), settings.Profile,
                Suffix(settings.BoundUserId), _worktimeError ?? _worktimeChannel.LastError,
                settings.PreviewMode),
            await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false),
            await _store.ReadCommitmentReviewsAsync(cancellationToken).ConfigureAwait(false),
            dailyReview,
            await _store.ReadCycleReviewAsync(cancellationToken).ConfigureAwait(false),
            new AiStatusView(
                lastFour is not null, ProviderName, ModelName, lastFour, spend, hardCap,
                spend >= 15m, spend >= 24m, _aiError, _aiRequestInProgress),
            await _store.ReadRecentAiUsageAsync(cancellationToken).ConfigureAwait(false),
            await _store.ReadRecentChatAsync(cancellationToken).ConfigureAwait(false),
            await _store.ReadPendingCandidateAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _worktimeChannel.DisposeAsync().ConfigureAwait(false);
        switch (_aiProvider)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
        _gate.Dispose();
    }

    private async Task ConfigureChannelFromStoreAsync(CancellationToken cancellationToken)
    {
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled) return;
        await ConfigureChannelAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompanionOutcome> ConfigureWorktimeAsync(
        ConfigureWorktimeChannelCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CliPath) || string.IsNullOrWhiteSpace(command.Profile))
            return Fail("worktime_configuration_invalid", "飞书 CLI 路径和 profile 不能为空。");
        var previous = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        var settings = new StoredWorktimeSettings(
            command.Enabled, command.CliPath.Trim(), command.Profile.Trim(),
            previous.BoundUserId, previous.BoundChatId, command.PreviewMode);
        await _store.SaveWorktimeSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        await ConfigureChannelAsync(settings, cancellationToken).ConfigureAwait(false);
        return Ok(command.Enabled ? "飞书工作时段通道已启用。" : "飞书工作时段通道已停用。",
            await SnapshotAsync(cancellationToken));
    }

    private async Task ConfigureChannelAsync(
        StoredWorktimeSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _worktimeChannel.ConfigureAsync(
                new WorktimeChannelConfiguration(
                    settings.Enabled, settings.CliPath, settings.Profile,
                    settings.BoundUserId, settings.BoundChatId),
                HandleInboundAsync,
                cancellationToken).ConfigureAwait(false);
            _listenerReady = settings.Enabled;
            _worktimeError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _listenerReady = false;
            _worktimeError = exception.Message;
        }
    }

    private async Task HandleInboundAsync(WorktimeInboundEvent inbound, CancellationToken cancellationToken)
    {
        CompanionCommand command = inbound switch
        {
            WorktimeTextInboundEvent text => new HandleWorktimeTextCommand(
                text.EventId, text.SenderId, text.ChatId, text.MessageId, text.Text, text.ReceivedAt),
            WorktimeActionInboundEvent action => new HandleWorktimeActionCommand(
                action.EventId, action.SenderId, action.CardId, action.CommitmentId,
                action.CommitmentVersion, action.Action, action.RestEndAt),
            _ => throw new InvalidOperationException("未知飞书工作时段事件。")
        };
        var outcome = await DispatchAsync(command, cancellationToken).ConfigureAwait(false);
        if (inbound is WorktimeTextInboundEvent)
        {
            await RetryPendingWorktimeRepliesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RetryPendingWorktimeRepliesAsync(CancellationToken cancellationToken)
    {
        foreach (var reply in await _store.ReadPendingWorktimeRepliesAsync(cancellationToken).ConfigureAwait(false))
        {
            var delivered = await _worktimeChannel.SendTextAsync(
                reply.RecipientOpenId, reply.Text, reply.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (!delivered.Success || delivered.PlatformMessageId is null)
            {
                _worktimeError = delivered.Message ?? delivered.ErrorCode ?? "飞书回复发送失败。";
                continue;
            }
            await _store.CompleteWorktimeReplyAsync(
                reply.EventId, delivered.PlatformMessageId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatWorktimeReply(CompanionOutcome outcome)
    {
        if (!outcome.Success) return $"未执行：{outcome.Message ?? outcome.ErrorCode ?? "未知错误"}";
        if (outcome.Candidate is not null)
            return "候选操作（确认前不会修改正式状态）\n" + outcome.Candidate.Summary +
                   "\n\n回复“确认候选”即可正式执行；回复“放弃候选”可取消。" +
                   "如需修改，请直接重新描述完整要求，Jarvis 会替换为新候选。";
        return outcome.AssistantText ?? outcome.Message ?? "操作已完成。";
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task<CompanionOutcome> BindWorktimeUserAsync(
        BindWorktimeUserCommand command,
        CancellationToken cancellationToken)
    {
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings = settings with { BoundUserId = command.SenderId, BoundChatId = command.ChatId };
        await _store.SaveWorktimeSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        if (settings.Enabled) await ConfigureChannelAsync(settings, cancellationToken).ConfigureAwait(false);
        return Ok("已绑定当前飞书私聊用户。", await SnapshotAsync(cancellationToken));
    }

    private async Task AdvanceMobileEscalationAsync(
        SupervisionSnapshot supervision,
        CancellationToken cancellationToken)
    {
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        var allCards = await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false);
        await RetryPendingCardInvalidationsAsync(allCards, cancellationToken).ConfigureAwait(false);
        allCards = await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false);
        var active = supervision.ActiveSupervision;
        var commitment = active is null
            ? null
            : supervision.Commitments.SingleOrDefault(item => item.Id == active.CommitmentId);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.BoundUserId) || active is null ||
            commitment is null || active.DeviationStartedAt is null || active.ActiveRest is not null ||
            active.Classification == ActivityClassification.Related)
        {
            await CancelCardsAsync(
                allCards.Where(card => card.State is MobileCardState.Active or MobileCardState.PendingDelivery),
                cancellationToken);
            return;
        }

        await CancelCardsAsync(
            allCards.Where(card => card.State is MobileCardState.Active or MobileCardState.PendingDelivery &&
                                   (card.CommitmentId != commitment.Id ||
                                    card.CommitmentVersion != commitment.Version ||
                                    card.DeviationStartedAt != active.DeviationStartedAt.Value)),
            cancellationToken);

        var elapsed = _clock.Now - active.DeviationStartedAt.Value;
        var first = commitment.ReminderSettings.FirstMobileDeviationMinutes;
        var repeat = commitment.ReminderSettings.MobileRepeatMinutes;
        var max = commitment.ReminderSettings.MaxMobileReminders;
        var cards = allCards
            .Where(card => card.CommitmentId == commitment.Id &&
                           card.CommitmentVersion == commitment.Version &&
                           card.DeviationStartedAt == active.DeviationStartedAt.Value)
            .ToArray();
        var pending = cards.LastOrDefault(item => item.State == MobileCardState.PendingDelivery);
        var nextSequence = pending?.Sequence ?? cards.Length + 1;
        if (nextSequence > max) return;
        var due = TimeSpan.FromMinutes(first + (nextSequence - 1) * repeat);
        if (elapsed < due) return;
        if (elapsed >= due.Add(TimeSpan.FromMinutes(2)))
        {
            if (pending is not null)
                await _store.SetMobileCardStateAsync(
                    pending.CardId, MobileCardState.Cancelled, cancellationToken).ConfigureAwait(false);
            return;
        }

        var card = pending ?? new MobileEscalationCard(
            Guid.NewGuid(), commitment.Id, commitment.Version, nextSequence, _clock.Now,
            commitment.StartAt, commitment.EndAt, active.DeviationStartedAt.Value,
            active.Classification ?? ActivityClassification.Unknown,
            commitment.OutcomeGoal ?? commitment.InputGoal ?? "当前工作承诺",
            BuildNotificationPreview(settings.PreviewMode, commitment, active, elapsed),
            MobileCardState.PendingDelivery,
            DefaultRestMinutes: commitment.RestSettings.DefaultTotalRestMinutes);
        if (pending is null)
            await _store.InsertMobileCardAsync(card, cancellationToken).ConfigureAwait(false);
        var delivery = await _worktimeChannel.SendAsync(card, cancellationToken).ConfigureAwait(false);
        if (!delivery.Success || string.IsNullOrWhiteSpace(delivery.PlatformMessageId))
        {
            _worktimeError = delivery.Message ?? delivery.ErrorCode ?? "飞书卡片发送失败。";
            return;
        }

        await _store.ActivateMobileCardAsync(
            card.CardId, delivery.PlatformMessageId, cancellationToken).ConfigureAwait(false);
        var previous = cards.LastOrDefault(item => item.State == MobileCardState.Active);
        if (previous?.PlatformMessageId is not null)
        {
            await _store.BeginMobileInvalidationAsync(
                previous.CardId, MobileCardState.SupersedePending,
                "已由下一次提醒替代", cancellationToken).ConfigureAwait(false);
            await RetryPendingCardInvalidationAsync(
                previous with
                {
                    State = MobileCardState.SupersedePending,
                    InvalidationResultText = "已由下一次提醒替代"
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CancelActiveCardsAsync(Guid commitmentId, CancellationToken cancellationToken)
    {
        var active = (await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false))
            .Where(card => card.CommitmentId == commitmentId &&
                           card.State is MobileCardState.Active or MobileCardState.PendingDelivery)
            .ToArray();
        await CancelCardsAsync(active, cancellationToken);
    }

    private static string BuildNotificationPreview(
        NotificationPreviewMode mode,
        CommitmentView commitment,
        ActiveSupervisionView active,
        TimeSpan elapsed) => mode == NotificationPreviewMode.Privacy
        ? "Jarvis 工作提醒：请解锁飞书查看详情。"
        : $"Jarvis · {commitment.OutcomeGoal ?? commitment.InputGoal ?? "当前工作承诺"} · " +
          $"{commitment.StartAt.ToLocalTime():HH:mm}–{commitment.EndAt.ToLocalTime():HH:mm} · " +
          $"偏离 {Math.Max(0, Math.Floor(elapsed.TotalMinutes)):0} 分钟 · " +
          $"{active.Classification ?? ActivityClassification.Unknown}";

    private async Task CancelCardsAsync(
        IEnumerable<MobileEscalationCard> cards,
        CancellationToken cancellationToken)
    {
        foreach (var card in cards)
        {
            const string result = "当前偏离已结束或承诺状态已变化";
            await _store.BeginMobileInvalidationAsync(
                card.CardId, MobileCardState.CancellationPending, result, cancellationToken)
                .ConfigureAwait(false);
            await RetryPendingCardInvalidationAsync(
                card with { State = MobileCardState.CancellationPending, InvalidationResultText = result },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RetryPendingCardInvalidationsAsync(
        IEnumerable<MobileEscalationCard> cards,
        CancellationToken cancellationToken)
    {
        foreach (var card in cards.Where(item => item.State is
                     MobileCardState.SupersedePending or
                     MobileCardState.CancellationPending or
                     MobileCardState.ResponsePending))
            await RetryPendingCardInvalidationAsync(card, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetryPendingCardInvalidationAsync(
        MobileEscalationCard card,
        CancellationToken cancellationToken)
    {
        var finalState = card.State switch
        {
            MobileCardState.SupersedePending => MobileCardState.Superseded,
            MobileCardState.CancellationPending => MobileCardState.Cancelled,
            MobileCardState.ResponsePending => MobileCardState.Responded,
            _ => throw new InvalidOperationException("卡片不在待失效状态。")
        };
        if (card.PlatformMessageId is null || await _worktimeChannel.InvalidateAsync(
                card.CardId, card.PlatformMessageId,
                card.InvalidationResultText ?? "状态已更新", cancellationToken).ConfigureAwait(false))
            await _store.CompleteMobileInvalidationAsync(card.CardId, finalState, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<CompanionOutcome> HandleWorktimeActionAsync(
        HandleWorktimeActionCommand command,
        CancellationToken cancellationToken)
    {
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(settings.BoundUserId, command.SenderId, StringComparison.Ordinal))
            return Fail("worktime_sender_unauthorized", "这项操作不属于已绑定用户。");
        var card = (await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.CardId == command.CardId);
        var previousResult = await _store.ReadSupervisionEventOutcomeAsync(
            command.EventId, cancellationToken).ConfigureAwait(false);
        if (previousResult is not null)
        {
            if (card is { State: MobileCardState.Active })
            {
                await _store.StoreWorktimeActionOutcomeAsync(
                    command.EventId, _clock.Now, new CompanionOutcome(true, Message: previousResult),
                    card.CardId, previousResult, CancellationToken.None).ConfigureAwait(false);
                await RetryPendingCardInvalidationAsync(
                    card with
                    {
                        State = MobileCardState.ResponsePending,
                        InvalidationResultText = previousResult
                    }, cancellationToken).ConfigureAwait(false);
            }
            return new CompanionOutcome(
                true, Message: previousResult,
                Snapshot: await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false));
        }
        if (card is null || card.State != MobileCardState.Active ||
            card.CommitmentId != command.CommitmentId || card.CommitmentVersion != command.ExpectedVersion)
            return Fail("mobile_card_stale", "这张卡已经失效，请查看最新卡片。");
        var supervision = await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var current = supervision.Commitments.SingleOrDefault(item => item.Id == command.CommitmentId);
        if (current is null || current.Version != command.ExpectedVersion)
            return Fail("commitment_version_stale", "承诺版本已经变化，请查看当前状态。");

        SupervisionResult<ActiveSupervisionView>? result = null;
        var handledResult = "操作已处理";
        switch (command.Action)
        {
            case WorktimeActionKind.ReturnNow:
                handledResult = "已记录：马上回去";
                result = await _supervision.RecordReturnIntentAsync(
                    command.CommitmentId, command.ExpectedVersion, cancellationToken, command.EventId,
                    handledResult)
                    .ConfigureAwait(false);
                break;
            case WorktimeActionKind.StartRest:
                if (command.RestEndAt is null)
                    return Fail("rest_end_required", "请补充休息结束时间后再确认。");
                handledResult = $"已开始休息，至 {command.RestEndAt.Value.ToLocalTime():HH:mm}";
                var rest = await _supervision.StartTimedRestAsync(
                    command.CommitmentId, command.ExpectedVersion, command.RestEndAt, cancellationToken,
                    command.EventId, handledResult)
                    .ConfigureAwait(false);
                if (!rest.Success) return Fail(rest.ErrorCode!, rest.Message!);
                break;
            case WorktimeActionKind.Misclassification:
                var active = supervision.ActiveSupervision;
                if (active?.ActionableTarget is null || active.ActivityStateStartedAt is null)
                    return Fail("activity_changed", "当前活动已经变化，请刷新后再纠正。");
                handledResult = "已纠正：当前活动相关";
                result = await _supervision.ClassifyActivityAsync(
                    command.CommitmentId, command.ExpectedVersion, active.ActionableTarget,
                    active.ActivityStateStartedAt.Value, ActivityClassification.Related,
                    ActivityRuleScope.Commitment, "飞书卡片：误判", cancellationToken, command.EventId,
                    handledResult)
                    .ConfigureAwait(false);
                break;
            case WorktimeActionKind.AdjustCommitment:
                return Fail("revision_confirmation_required", "请用文字说明如何调整，Jarvis 会生成修订候选卡供确认。");
            default:
                return Fail("worktime_action_invalid", "未知卡片操作。");
        }

        if (result is not null && !result.Success) return Fail(result.ErrorCode!, result.Message!);
        await _store.StoreWorktimeActionOutcomeAsync(
            command.EventId, _clock.Now,
            new CompanionOutcome(true, Message: handledResult), card.CardId, handledResult,
            CancellationToken.None)
            .ConfigureAwait(false);
        await RetryPendingCardInvalidationAsync(
            card with { State = MobileCardState.ResponsePending, InvalidationResultText = handledResult },
            cancellationToken).ConfigureAwait(false);
        return new CompanionOutcome(
            true, Message: "操作已由 Core 接受。",
            Snapshot: await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<CompanionOutcome> HandleWorktimeTextAsync(
        HandleWorktimeTextCommand command,
        CancellationToken cancellationToken)
    {
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.BoundUserId is null)
        {
            if (!string.Equals(command.Text.Trim(), "绑定 Jarvis", StringComparison.OrdinalIgnoreCase))
                return Fail("worktime_user_not_bound", "请先在飞书私聊中发送“绑定 Jarvis”。");
            settings = settings with { BoundUserId = command.SenderId, BoundChatId = command.ChatId };
            await _store.SaveWorktimeSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            if (settings.Enabled) await ConfigureChannelAsync(settings, cancellationToken).ConfigureAwait(false);
            return Ok("已绑定当前飞书私聊用户。", await SnapshotAsync(cancellationToken));
        }
        if (!string.Equals(settings.BoundUserId, command.SenderId, StringComparison.Ordinal))
            return Fail("worktime_sender_unauthorized", "这条消息不属于已绑定用户。");
        await _store.InsertChatAsync(
            new ChatMessageView(Guid.NewGuid(), command.ReceivedAt, "user", command.Text), cancellationToken)
            .ConfigureAwait(false);

        var repeatedShortcut = await _store.ReadSupervisionEventOutcomeAsync(
            command.EventId + ":shortcut", cancellationToken).ConfigureAwait(false);
        if (repeatedShortcut is not null)
            return new CompanionOutcome(true, Message: repeatedShortcut);

        var latest = (await _store.ReadMobileCardsAsync(cancellationToken).ConfigureAwait(false))
            .LastOrDefault(card => card.State == MobileCardState.Active);
        var normalized = command.Text.Trim();
        if (normalized is "确认候选" or "确认这个候选")
        {
            var binding = await _store.ReadWorktimeCandidateBindingAsync(command.EventId, cancellationToken)
                .ConfigureAwait(false);
            if (binding is not null)
                return binding.Action == "confirm"
                    ? await ConfirmNaturalLanguageCandidateAsync(
                        new ConfirmNaturalLanguageCandidateCommand(binding.CandidateId), cancellationToken)
                    : Fail("worktime_candidate_binding_conflict", "这条飞书事件已绑定另一项候选操作。");
            var candidate = await _store.ReadPendingCandidateAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                binding = await _store.BindWorktimeCandidateAsync(
                    command.EventId, candidate.CandidateId, "confirm", cancellationToken).ConfigureAwait(false);
                return await ConfirmNaturalLanguageCandidateAsync(
                        new ConfirmNaturalLanguageCandidateCommand(binding.CandidateId), cancellationToken)
                    .ConfigureAwait(false);
            }
            var latestCandidate = await _store.ReadLatestCandidateStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (latestCandidate is not null && latestCandidate.State == "committed")
            {
                await _store.CompleteCandidateConfirmationAsync(
                    latestCandidate.CandidateId, true, CancellationToken.None).ConfigureAwait(false);
                return Ok("候选操作已经正式确认。", await SnapshotAsync(cancellationToken));
            }
            if (latestCandidate is not null && latestCandidate.State == "confirmed")
                return Ok("候选操作已经正式确认。", await SnapshotAsync(cancellationToken));
            return latestCandidate is not null && latestCandidate.State == "confirming"
                ? Fail("candidate_result_uncertain", "候选操作的正式结果尚未完成记录，请稍后重试。")
                : Fail("candidate_stale", "当前没有可确认的候选操作。");
        }
        if (normalized is "放弃候选" or "取消候选")
        {
            var binding = await _store.ReadWorktimeCandidateBindingAsync(command.EventId, cancellationToken)
                .ConfigureAwait(false);
            if (binding is not null)
                return binding.Action == "discard"
                    ? await DiscardNaturalLanguageCandidateAsync(
                        new DiscardNaturalLanguageCandidateCommand(binding.CandidateId), cancellationToken)
                    : Fail("worktime_candidate_binding_conflict", "这条飞书事件已绑定另一项候选操作。");
            var candidate = await _store.ReadPendingCandidateAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                binding = await _store.BindWorktimeCandidateAsync(
                    command.EventId, candidate.CandidateId, "discard", cancellationToken).ConfigureAwait(false);
                return await DiscardNaturalLanguageCandidateAsync(
                        new DiscardNaturalLanguageCandidateCommand(binding.CandidateId), cancellationToken)
                    .ConfigureAwait(false);
            }
            return string.Equals(
                await _store.ReadLatestCandidateStateAsync(cancellationToken).ConfigureAwait(false),
                "discarded", StringComparison.Ordinal)
                ? Ok("候选操作已经放弃。", await SnapshotAsync(cancellationToken))
                : Fail("candidate_stale", "当前没有可放弃的候选操作。");
        }
        if (normalized is "现在复盘" or "开始复盘")
            return await StartDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (normalized is "30分钟后复盘" or "30 分钟后复盘")
            return await SnoozeDailyReviewAsync(new SnoozeDailyReviewCommand(30), cancellationToken)
                .ConfigureAwait(false);
        if (normalized is "60分钟后复盘" or "60 分钟后复盘")
            return await SnoozeDailyReviewAsync(new SnoozeDailyReviewCommand(60), cancellationToken)
                .ConfigureAwait(false);
        if (normalized is "跳过复盘" or "今天不复盘")
            return await SkipDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (latest is not null && normalized is "马上回去" or "回去" or "立即返回")
            return await HandleWorktimeActionAsync(new HandleWorktimeActionCommand(
                command.EventId + ":shortcut", command.SenderId, latest.CardId, latest.CommitmentId,
                latest.CommitmentVersion, WorktimeActionKind.ReturnNow, null), cancellationToken)
                .ConfigureAwait(false);
        if (latest is not null && normalized.Contains("误判", StringComparison.Ordinal))
            return await HandleWorktimeActionAsync(new HandleWorktimeActionCommand(
                command.EventId + ":shortcut", command.SenderId, latest.CardId, latest.CommitmentId,
                latest.CommitmentVersion, WorktimeActionKind.Misclassification, null), cancellationToken)
                .ConfigureAwait(false);
        if (latest is not null && TryParseRestEnd(normalized, _clock.Now, out var restEnd))
            return await HandleWorktimeActionAsync(new HandleWorktimeActionCommand(
                command.EventId + ":shortcut", command.SenderId, latest.CardId, latest.CommitmentId,
                latest.CommitmentVersion, WorktimeActionKind.StartRest, restEnd), cancellationToken)
                .ConfigureAwait(false);
        var interpreted = await InterpretNaturalLanguageAsync(
            new InterpretNaturalLanguageCommand(command.Text, CandidateSource.Feishu, command.EventId), cancellationToken)
            .ConfigureAwait(false);
        return interpreted;
    }

    private static bool TryParseRestEnd(
        string text,
        DateTimeOffset now,
        out DateTimeOffset restEnd)
    {
        restEnd = default;
        var marker = text.IndexOf("休息到", StringComparison.Ordinal);
        if (marker < 0) return false;
        var value = text[(marker + "休息到".Length)..].Trim();
        if (!TimeOnly.TryParseExact(
                value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return false;
        var localNow = now.ToLocalTime();
        restEnd = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day,
            time.Hour, time.Minute, 0, localNow.Offset);
        return restEnd > now;
    }

    private async Task EnsureCommitmentReviewsAsync(
        SupervisionSnapshot supervision,
        CancellationToken cancellationToken)
    {
        foreach (var commitment in supervision.Commitments.Where(item => item.Phase == CommitmentPhase.AwaitingReview))
            await _store.EnsureReviewPendingAsync(commitment, _clock.Now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompanionOutcome> EndCommitmentEarlyAsync(
        EndCommitmentEarlyCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _supervision.EndCommitmentEarlyAsync(
            command.CommitmentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return Fail(result.ErrorCode!, result.Message!);
        await _store.EnsureReviewPendingAsync(result.Value!, _clock.Now, cancellationToken).ConfigureAwait(false);
        await CancelActiveCardsAsync(command.CommitmentId, cancellationToken).ConfigureAwait(false);
        return Ok("监督已结束，承诺进入待回顾；不会自动标记完成。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> CancelCommitmentAsync(
        CancelCommitmentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _supervision.CancelCommitmentAsync(
            command.CommitmentId, command.ExpectedVersion, command.Reason, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success) return Fail(result.ErrorCode!, result.Message!);
        await CancelActiveCardsAsync(command.CommitmentId, cancellationToken).ConfigureAwait(false);
        return Ok("承诺已取消；历史和取消原因已保留，不会标记完成。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> DeferActiveCommitmentAsync(
        DeferActiveCommitmentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _supervision.DeferActiveCommitmentAsync(
            command.CommitmentId, command.ExpectedVersion, command.NewStartAt,
            command.Reason, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return Fail(result.ErrorCode!, result.Message!);
        await CancelActiveCardsAsync(command.CommitmentId, cancellationToken).ConfigureAwait(false);
        return Ok("当前监督已结束，并按剩余时长建立了未来承诺。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SubmitCommitmentReviewAsync(
        SubmitCommitmentReviewCommand command,
        CancellationToken cancellationToken)
    {
        var text = command.RawText.Trim();
        if (text.Length == 0) return Fail("review_text_required", "请保留你的原始回顾文字。");
        var review = (await _store.ReadCommitmentReviewsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.CommitmentId == command.CommitmentId);
        if (review is null || review.State is CommitmentReviewState.Completed or CommitmentReviewState.Skipped)
            return Fail("review_not_pending", "这项承诺当前没有待提交的回顾。");
        await _store.CompleteCommitmentReviewAsync(
            command.CommitmentId, text, command.Assessment, _clock.Now, cancellationToken)
            .ConfigureAwait(false);
        return Ok("原始回顾已保存；结构化完成状态仅作辅助。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> DeferCommitmentReviewAsync(
        DeferCommitmentReviewCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Minutes is not (30 or 60))
            return Fail("review_defer_invalid", "稍后回顾只支持 30 或 60 分钟。");
        await _store.DeferCommitmentReviewAsync(
            command.CommitmentId, _clock.Now.AddMinutes(command.Minutes), cancellationToken).ConfigureAwait(false);
        return Ok("回顾已进入队列，期间不会反复打扰。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SkipCommitmentReviewAsync(
        SkipCommitmentReviewCommand command,
        CancellationToken cancellationToken)
    {
        await _store.SkipCommitmentReviewAsync(command.CommitmentId, cancellationToken).ConfigureAwait(false);
        return Ok("已明确记录为不回顾；没有推断完成状态或原因。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> ConfigureDailyReviewAsync(
        ConfigureDailyReviewCommand command,
        CancellationToken cancellationToken)
    {
        await _store.SaveDailyConfigurationAsync(command.LocalTime, _clock.Now, cancellationToken)
            .ConfigureAwait(false);
        return Ok("每日复盘时间已更新，只影响未来触发。", await SnapshotAsync(cancellationToken));
    }

    private async Task AdvanceDailyReviewAsync(
        SupervisionSnapshot supervision,
        CancellationToken cancellationToken)
    {
        var current = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await _store.ReadDailyConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var localNow = _clock.Now.ToLocalTime();
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var todayDue = date.ToDateTime(current.ScheduledLocalTime);
        var hasActive = supervision.Commitments.Any(item => item.Phase is
            CommitmentPhase.PreparationBuffer or CommitmentPhase.Supervising or CommitmentPhase.ActiveUnsupervised);
        if (hasActive && localNow.DateTime >= todayDue && current.ReviewDate != date)
        {
            _dailyReviewDeferredDate = date;
            return;
        }
        if (current.State == ReviewSessionState.Snoozed && current.SessionId is not null &&
            current.SnoozedUntil <= _clock.Now &&
            !current.FollowUpUsed)
        {
            await SendDailyReviewFollowUpAsync(current, cancellationToken).ConfigureAwait(false);
            await _store.ResumeDailyReviewAsync(current.SessionId.Value, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (current.ReviewDate is not null && current.ReviewDate < date &&
            current.State is ReviewSessionState.Pending or ReviewSessionState.Snoozed or ReviewSessionState.InProgress)
        {
            if (!current.FollowUpUsed)
            {
                if (current.InvitedAt is null || current.InvitedAt.Value.AddMinutes(30) > _clock.Now)
                    return;
                await SendDailyReviewFollowUpAsync(current, cancellationToken).ConfigureAwait(false);
                if (current.State == ReviewSessionState.Snoozed && current.SessionId is not null)
                    await _store.ResumeDailyReviewAsync(current.SessionId.Value, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (localNow.DateTime < todayDue || current.SessionId is null) return;
            await _store.MarkDailyReviewNoResponseAsync(current.SessionId.Value, cancellationToken)
                .ConfigureAwait(false);
            current = current with { State = ReviewSessionState.NoResponse };
        }
        if (current.State == ReviewSessionState.Pending && !current.FollowUpUsed &&
            current.InvitedAt is not null && current.InvitedAt.Value.AddMinutes(30) <= _clock.Now)
        {
            await SendDailyReviewFollowUpAsync(current, cancellationToken).ConfigureAwait(false);
            return;
        }

        var targetDate = localNow.DateTime >= todayDue ? date : date.AddDays(-1);
        var targetDue = targetDate.ToDateTime(current.ScheduledLocalTime);
        if (current.ReviewDate != targetDate && !hasActive && configuration.ConfiguredAt is not null &&
            configuration.ConfiguredAt.Value.ToLocalTime().DateTime <= targetDue)
        {
            var sessionId = await _store.EnsureDailyReviewAsync(targetDate, _clock.Now, cancellationToken)
                .ConfigureAwait(false);
            current = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
            var wasDeferredByCommitment = _dailyReviewDeferredDate == targetDate;
            if (targetDate < date && !wasDeferredByCommitment)
            {
                // A review missed while Core was not running becomes visible locally first.
                // It must not create a surprise mobile notification merely because Core restarted.
                return;
            }
            if ((localNow.DateTime < todayDue.AddMinutes(2) || wasDeferredByCommitment) && settings.Enabled &&
                !string.IsNullOrWhiteSpace(settings.BoundUserId))
            {
                var invitation = await _worktimeChannel.SendDailyReviewInvitationAsync(
                    sessionId, targetDate, false, cancellationToken).ConfigureAwait(false);
                if (invitation.Success)
                    await _store.MarkDailyReviewInviteSentAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);
                else
                    _worktimeError = invitation.Message ?? invitation.ErrorCode;
            }
            if (wasDeferredByCommitment)
                _dailyReviewDeferredDate = null;
        }
    }

    private async Task SendDailyReviewFollowUpAsync(
        DailyReviewView review,
        CancellationToken cancellationToken)
    {
        if (review.SessionId is null || review.ReviewDate is null) return;
        var settings = await _store.ReadWorktimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Enabled && !string.IsNullOrWhiteSpace(settings.BoundUserId))
        {
            var delivery = await _worktimeChannel.SendDailyReviewInvitationAsync(
                review.SessionId.Value, review.ReviewDate.Value, true, cancellationToken).ConfigureAwait(false);
            if (!delivery.Success)
                _worktimeError = delivery.Message ?? delivery.ErrorCode ?? "飞书每日复盘追问发送失败。";
        }
        await _store.MarkDailyReviewFollowUpSentAsync(review.SessionId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CompanionOutcome> StartDailyReviewAsync(CancellationToken cancellationToken)
    {
        var review = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.State == ReviewSessionState.NotDue ||
            (review.State is ReviewSessionState.Completed or ReviewSessionState.Skipped or ReviewSessionState.NoResponse &&
             review.ReviewDate != DateOnly.FromDateTime(_clock.Now.ToLocalTime().DateTime)))
        {
            var id = await _store.EnsureDailyReviewAsync(
                DateOnly.FromDateTime(_clock.Now.ToLocalTime().DateTime), _clock.Now, cancellationToken)
                .ConfigureAwait(false);
            review = review with { SessionId = id, State = ReviewSessionState.Pending };
        }
        if (review.SessionId is null || review.State is not (ReviewSessionState.Pending or ReviewSessionState.Snoozed))
            return review.State == ReviewSessionState.InProgress
                ? Ok("每日复盘已经开始。", await SnapshotAsync(cancellationToken))
                : Fail("daily_review_not_pending", "当前每日复盘已经开始或完成。");
        var hasPendingCommitments = (await _store.ReadCommitmentReviewsAsync(cancellationToken)
                .ConfigureAwait(false))
            .Any(item => item.State is CommitmentReviewState.Pending or CommitmentReviewState.Deferred);
        await _store.StartDailyReviewAsync(
            review.SessionId.Value,
            hasPendingCommitments ? ReviewQuestionKind.PendingCommitments : ReviewQuestionKind.WhatWentWell,
            cancellationToken).ConfigureAwait(false);
        return Ok("每日复盘已开始；Jarvis 每次只问一个问题。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> RespondDailyReviewAsync(
        RespondDailyReviewCommand command,
        CancellationToken cancellationToken)
    {
        var review = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.SessionId != command.SessionId || review.State != ReviewSessionState.InProgress ||
            review.CurrentQuestion is null)
            return Fail("daily_review_stale", "复盘问题已经变化，请刷新。");
        var text = command.RawText.Trim();
        if (text.Length == 0) return Fail("daily_review_text_required", "回答不能为空。");
        if (review.CurrentQuestion == ReviewQuestionKind.TomorrowAdjustments)
        {
            var adjustments = text.Split(
                ['\r', '\n', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (adjustments.Length is < 1 or > 3)
                return Fail("daily_adjustment_count_invalid", "请明确确认 1–3 个明日调整，每行一个。");
        }
        var next = review.CurrentQuestion == ReviewQuestionKind.TomorrowAdjustments
            ? (ReviewQuestionKind?)null
            : (ReviewQuestionKind)((int)review.CurrentQuestion.Value + 1);
        await _store.AddDailyAnswerAsync(
            command.SessionId, review.CurrentQuestion.Value, next, text, _clock.Now, cancellationToken)
            .ConfigureAwait(false);
        return Ok(
            review.CurrentQuestion == ReviewQuestionKind.TomorrowAdjustments
                ? "原始回答和 1–3 个明日调整已确认保存。"
                : "原始回答已保存。",
            await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SnoozeDailyReviewAsync(
        SnoozeDailyReviewCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Minutes is not (30 or 60))
            return Fail("daily_review_snooze_invalid", "每日复盘稍后只支持 30 或 60 分钟。");
        var review = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.State == ReviewSessionState.Snoozed)
            return Ok("每日复盘已经处于稍后状态。", await SnapshotAsync(cancellationToken));
        if (review.SessionId is null || review.State is ReviewSessionState.Completed or ReviewSessionState.Skipped)
            return Fail("daily_review_not_pending", "当前没有可延后的每日复盘。");
        if (review.FollowUpUsed)
            return Fail("daily_follow_up_exhausted", "本次每日复盘已经补问过一次；请选择现在开始或明确跳过。");
        await _store.SnoozeDailyReviewAsync(
            review.SessionId.Value, _clock.Now.AddMinutes(command.Minutes), review.FollowUpUsed,
            cancellationToken).ConfigureAwait(false);
        return Ok("每日复盘已稍后；最多只再提醒一次。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SkipDailyReviewAsync(CancellationToken cancellationToken)
    {
        var review = await _store.ReadDailyReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.SessionId is null) return Fail("daily_review_not_pending", "当前没有每日复盘。");
        await _store.SkipDailyReviewAsync(review.SessionId.Value, cancellationToken).ConfigureAwait(false);
        return Ok("已明确跳过本次每日复盘。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> ConfigureCycleReviewAsync(
        ConfigureCycleReviewCommand command,
        CancellationToken cancellationToken)
    {
        if (command.IntervalDays is not (7 or 14 or 28) && command.IntervalDays < 2)
            return Fail("cycle_interval_invalid", "周期长度必须至少 2 天；常用值为 7、14、28 天。");
        await _store.SaveCycleConfigurationAsync(
            new StoredCycleConfiguration(command.AnchorDate, command.IntervalDays, command.LocalTime),
            cancellationToken).ConfigureAwait(false);
        return Ok("周期复盘边界已更新，只影响未来周期。", await SnapshotAsync(cancellationToken));
    }

    private async Task AdvanceCycleReviewAsync(CancellationToken cancellationToken)
    {
        var config = await _store.ReadCycleConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _store.ReadCycleReviewAsync(cancellationToken).ConfigureAwait(false);
        var localNow = _clock.Now.ToLocalTime();
        var today = DateOnly.FromDateTime(localNow.DateTime);
        if (today < config.AnchorDate.AddDays(config.IntervalDays)) return;
        var periods = (today.DayNumber - config.AnchorDate.DayNumber) / config.IntervalDays;
        var periodEnd = config.AnchorDate.AddDays(periods * config.IntervalDays);
        var due = periodEnd.ToDateTime(config.LocalTime);
        if (localNow.DateTime < due || existing.PeriodEnd == periodEnd) return;
        var periodStart = periodEnd.AddDays(-config.IntervalDays + 1);
        var trends = await _store.CalculateCycleTrendsAsync(periodStart, periodEnd, cancellationToken)
            .ConfigureAwait(false);
        await _store.EnsureCycleReviewAsync(periodStart, periodEnd, trends, _clock.Now, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CompanionOutcome> StartCycleReviewAsync(CancellationToken cancellationToken)
    {
        var review = await _store.ReadCycleReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.State != ReviewSessionState.Pending || review.PeriodStart is null || review.PeriodEnd is null)
        {
            var config = await _store.ReadCycleConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var end = DateOnly.FromDateTime(_clock.Now.ToLocalTime().DateTime);
            if (review.PeriodEnd == end && review.State is ReviewSessionState.Completed or ReviewSessionState.InProgress)
                return Fail("cycle_review_already_started", "当前周期复盘已经开始或完成。");
            var start = end.AddDays(-config.IntervalDays + 1);
            var trends = await _store.CalculateCycleTrendsAsync(start, end, cancellationToken)
                .ConfigureAwait(false);
            await _store.EnsureCycleReviewAsync(start, end, trends, _clock.Now, cancellationToken)
                .ConfigureAwait(false);
            review = await _store.ReadCycleReviewAsync(cancellationToken).ConfigureAwait(false);
        }
        if (review.PeriodStart is null || review.PeriodEnd is null)
            return Fail("cycle_review_not_pending", "周期复盘边界不可用，请重新配置。 ");
        var sessionId = await _store.FindCycleSessionIdAsync(
            review.PeriodStart.Value, review.PeriodEnd.Value, cancellationToken).ConfigureAwait(false);
        await _store.StartCycleReviewAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Ok("周期复盘已开始；所有趋势均来自监督和每日复盘事实。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> ConfirmCycleFocusesAsync(
        ConfirmCycleFocusesCommand command,
        CancellationToken cancellationToken)
    {
        var focuses = command.Focuses.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (focuses.Length is < 1 or > 3)
            return Fail("cycle_focus_count_invalid", "请确认 1–3 个下周期重点。");
        var review = await _store.ReadCycleReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.State != ReviewSessionState.InProgress || review.PeriodStart is null || review.PeriodEnd is null)
            return Fail("cycle_review_not_in_progress", "当前周期复盘不在确认阶段。");
        var sessionId = await _store.FindCycleSessionIdAsync(
            review.PeriodStart.Value, review.PeriodEnd.Value, cancellationToken).ConfigureAwait(false);
        await _store.SaveCycleFocusesAsync(sessionId, focuses, cancellationToken).ConfigureAwait(false);
        return Ok("下周期重点已确认。", await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SaveAiCredentialAsync(
        SaveAiCredentialCommand command,
        CancellationToken cancellationToken)
    {
        var credential = command.Credential.Trim();
        if (credential.Length < 8) return Fail("ai_credential_invalid", "API Key 太短或为空。");
        var previous = await _credentialStore.ReadAsync("deepseek", cancellationToken).ConfigureAwait(false);
        await _credentialStore.SaveAsync("deepseek", credential, cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.WriteSettingForModuleAsync(
                "ai-last4", credential[^4..], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previous is null)
                await _credentialStore.DeleteAsync("deepseek", CancellationToken.None).ConfigureAwait(false);
            else
                await _credentialStore.SaveAsync("deepseek", previous, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        _aiError = null;
        return Ok("DeepSeek 凭据已保存到 Windows 凭据管理器；SQLite 只保留末四位。",
            await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> DeleteAiCredentialAsync(CancellationToken cancellationToken)
    {
        var previous = await _credentialStore.ReadAsync("deepseek", cancellationToken).ConfigureAwait(false);
        await _credentialStore.DeleteAsync("deepseek", cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteSettingForModuleAsync("ai-last4", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previous is not null)
                await _credentialStore.SaveAsync("deepseek", previous, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return Ok("本机凭据已删除；如需彻底撤销，请同时到供应商后台撤销旧密钥。",
            await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> SetAiMonthlyHardCapAsync(
        SetAiMonthlyHardCapCommand command,
        CancellationToken cancellationToken)
    {
        if (command.HardCapCny is < 1m or > 10_000m)
            return Fail("ai_monthly_cap_invalid", "AI 月度硬上限必须在 1 元到 10000 元之间。");
        var hardCap = decimal.Round(command.HardCapCny, 2, MidpointRounding.AwayFromZero);
        await _store.SaveAiMonthlyHardCapAsync(hardCap, cancellationToken).ConfigureAwait(false);
        return Ok($"AI 月度硬上限已由用户明确设置为 {hardCap:F2} 元。",
            await SnapshotAsync(cancellationToken));
    }

    private async Task<CompanionOutcome> RequestAiChatAsync(
        RequestAiChatCommand command,
        CancellationToken cancellationToken)
    {
        var text = command.Text.Trim();
        if (text.Length == 0) return Fail("ai_text_required", "聊天内容不能为空。");
        var result = await CallAiAsync(
            AiRequestPurpose.BasicChat, text, command.MaxOutputTokens,
            command.ApprovedEstimatedCostOverOneCny, cancellationToken).ConfigureAwait(false);
        if (!result.Outcome.Success) return result.Outcome;
        await _store.InsertChatAsync(new ChatMessageView(Guid.NewGuid(), _clock.Now, "user", text), cancellationToken)
            .ConfigureAwait(false);
        await _store.InsertChatAsync(new ChatMessageView(
            Guid.NewGuid(), _clock.Now, "assistant", result.ProviderResult!.Text ?? ""), cancellationToken)
            .ConfigureAwait(false);
        return new CompanionOutcome(
            true, Message: "AI 已回复。", Snapshot: await SnapshotAsync(cancellationToken),
            AssistantText: result.ProviderResult.Text);
    }

    private async Task<CompanionOutcome> InterpretNaturalLanguageAsync(
        InterpretNaturalLanguageCommand command,
        CancellationToken cancellationToken)
    {
        var text = command.Text.Trim();
        if (text.Length == 0) return Fail("candidate_text_required", "自然语言内容不能为空。");
        var result = await CallAiAsync(
            AiRequestPurpose.NaturalLanguageOperation, text, 2048, false, cancellationToken,
            command.SourceEventId is null ? null : StableGuid($"ai:{command.SourceEventId}"))
            .ConfigureAwait(false);
        if (!result.Outcome.Success) return result.Outcome;
        var candidate = result.ProviderResult!.Candidate;
        if (candidate is null)
            return Fail("ai_candidate_invalid", "AI 没有返回可验证的候选操作；请改用表单或模板。");
        var normalized = await NormalizeNaturalLanguageCandidateAsync(candidate, cancellationToken)
            .ConfigureAwait(false);
        if (!normalized.Outcome.Success) return normalized.Outcome;
        candidate = normalized.Candidate! with { Source = command.Source, CreatedAt = _clock.Now };
        var outcome = new CompanionOutcome(
            true, Message: "已生成候选操作；确认前不会修改正式状态。",
            Candidate: candidate);
        await _store.SaveCandidateAsync(
            candidate, command.SourceEventId, outcome, cancellationToken).ConfigureAwait(false);
        return outcome with { Snapshot = await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false) };
    }

    private async Task<(CompanionOutcome Outcome, NaturalLanguageOperationCandidate? Candidate)>
        NormalizeNaturalLanguageCandidateAsync(
            NaturalLanguageOperationCandidate candidate,
            CancellationToken cancellationToken)
    {
        switch (candidate.Kind)
        {
            case CandidateOperationKind.CreateCommitment when candidate.Commitment is not null:
                {
                    var prepared = await _supervision.PrepareAsync(candidate.Commitment, cancellationToken)
                        .ConfigureAwait(false);
                    if (!prepared.Success) return (Fail(prepared.ErrorCode!, prepared.Message!), null);
                    var card = prepared.Value!;
                    var draft = new CommitmentDraft(
                        card.Kind, card.StartAt, card.EndAt, null, card.InputGoal, card.OutcomeGoal,
                        card.RelatedAppsOrSites, card.SupervisionMode, card.ReminderSettings,
                        card.ActivityRules, card.RestSettings, card.TemplateId);
                    return (new CompanionOutcome(true), candidate with
                    {
                        Commitment = draft,
                        Summary = FormatCommitmentCandidate("新建一次性承诺", card)
                    });
                }
            case CandidateOperationKind.ReviseCommitment when candidate.Revision is not null:
                {
                    var prepared = await _supervision.PrepareCommitmentRevisionAsync(
                        candidate.Revision, cancellationToken).ConfigureAwait(false);
                    if (!prepared.Success) return (Fail(prepared.ErrorCode!, prepared.Message!), null);
                    var card = prepared.Value!;
                    return (new CompanionOutcome(true), candidate with
                    {
                        Summary = $"修订承诺 {card.CommitmentId.ToString()[..8]} · " +
                                  $"v{card.FromVersion} → v{card.ToVersion} · {card.Reason}\n" +
                                  FormatCommitmentCandidate("修订后", card.After)
                    });
                }
            case CandidateOperationKind.CreateFromTemplate when candidate.FromTemplate is not null:
                {
                    var prepared = await _supervision.PrepareFromTemplateAsync(
                        candidate.FromTemplate, cancellationToken).ConfigureAwait(false);
                    if (!prepared.Success) return (Fail(prepared.ErrorCode!, prepared.Message!), null);
                    return (new CompanionOutcome(true), candidate with
                    {
                        Summary = FormatCommitmentCandidate("从模板创建", prepared.Value!)
                    });
                }
            case CandidateOperationKind.CreateRecurrence when candidate.Recurrence is not null:
                {
                    var prepared = await _supervision.PrepareRecurrenceAsync(candidate.Recurrence, cancellationToken)
                        .ConfigureAwait(false);
                    if (!prepared.Success) return (Fail(prepared.ErrorCode!, prepared.Message!), null);
                    var card = prepared.Value!;
                    return (new CompanionOutcome(true), candidate with
                    {
                        Summary = $"新建有限重复安排 · {card.Pattern.Kind} · 共 {card.Occurrences.Count} 次\n" +
                                  FormatCommitmentCandidate("首个发生项", card.Occurrences[0])
                    });
                }
            case CandidateOperationKind.SaveTemplate when candidate.Template is not null:
                {
                    var normalized = SupervisionModule.NormalizeTemplate(candidate.Template, _clock.Now);
                    if (!normalized.Success)
                        return (Fail(normalized.ErrorCode!, normalized.Message!), null);
                    var template = normalized.Value!;
                    var draft = new CommitmentTemplateDraft(
                        template.Name, template.Kind, template.DurationMinutes,
                        template.InputGoal, template.OutcomeGoal, template.RelatedAppsOrSites,
                        template.SupervisionMode, template.ReminderSettings,
                        template.ActivityRules, template.RestSettings);
                    return (new CompanionOutcome(true), candidate with
                    {
                        Template = draft,
                        Summary = $"保存承诺模板 · {template.Name}\n" +
                                  $"{template.Kind} · {template.DurationMinutes} 分钟 · " +
                                  $"{template.InputGoal ?? template.OutcomeGoal}\n" +
                                  $"目标：{FormatTargets(template.RelatedAppsOrSites ?? [])}"
                    });
                }
            case CandidateOperationKind.EndCommitmentEarly
                when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null:
                {
                    var snapshot = await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var commitment = snapshot.Commitments.SingleOrDefault(
                        item => item.Id == candidate.TargetCommitmentId.Value);
                    if (commitment is null || commitment.Version != candidate.ExpectedVersion.Value ||
                        commitment.Phase is not (CommitmentPhase.PreparationBuffer or
                            CommitmentPhase.Supervising or CommitmentPhase.ActiveUnsupervised))
                        return (Fail("commitment_version_stale", "要结束的承诺已经变化或不在进行中。"), null);
                    return (new CompanionOutcome(true), candidate with
                    {
                        Summary = $"提前结束承诺 · {commitment.Id.ToString()[..8]} · v{commitment.Version}\n" +
                                  $"{commitment.InputGoal ?? commitment.OutcomeGoal}\n" +
                                  "确认后只停止监督并进入待回顾，不会自动标记完成。"
                    });
                }
            case CandidateOperationKind.CancelCommitment
                when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null &&
                     !string.IsNullOrWhiteSpace(candidate.Reason):
                {
                    var snapshot = await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var commitment = snapshot.Commitments.SingleOrDefault(
                        item => item.Id == candidate.TargetCommitmentId.Value);
                    if (commitment is null || commitment.Version != candidate.ExpectedVersion.Value ||
                        commitment.Phase is CommitmentPhase.AwaitingReview or CommitmentPhase.Skipped)
                        return (Fail("commitment_version_stale", "要取消的承诺已经变化或不可取消。"), null);
                    return (new CompanionOutcome(true), candidate with
                    {
                        Reason = candidate.Reason.Trim(),
                        Summary = $"取消承诺 · {commitment.Id.ToString()[..8]} · v{commitment.Version}\n" +
                                  $"{commitment.InputGoal ?? commitment.OutcomeGoal}\n原因：{candidate.Reason.Trim()}\n" +
                                  "确认后停止监督并保留取消事实，不会标记完成。"
                    });
                }
            case CandidateOperationKind.DeferCommitment
                when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null &&
                     candidate.DeferredStartAt is not null && !string.IsNullOrWhiteSpace(candidate.Reason):
                {
                    var snapshot = await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var commitment = snapshot.Commitments.SingleOrDefault(
                        item => item.Id == candidate.TargetCommitmentId.Value);
                    if (commitment is null || commitment.Version != candidate.ExpectedVersion.Value ||
                        commitment.Phase is not (CommitmentPhase.PreparationBuffer or
                            CommitmentPhase.Supervising or CommitmentPhase.ActiveUnsupervised) ||
                        candidate.DeferredStartAt.Value <= _clock.Now)
                        return (Fail("commitment_version_stale", "只能把仍在进行的当前版本承诺推迟到未来。"), null);
                    var remaining = commitment.EndAt - _clock.Now;
                    return (new CompanionOutcome(true), candidate with
                    {
                        Reason = candidate.Reason.Trim(),
                        Summary = $"推迟进行中的承诺 · {commitment.Id.ToString()[..8]} · v{commitment.Version}\n" +
                                  $"新时间：{candidate.DeferredStartAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} – " +
                                  $"{candidate.DeferredStartAt.Value.Add(remaining).ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                                  $"原因：{candidate.Reason.Trim()}\n确认后当前监督停止，并以剩余时长建立新承诺。"
                    });
                }
            default:
                return (Fail("candidate_payload_invalid", "候选操作缺少必要内容。"), null);
        }
    }

    private static string FormatCommitmentCandidate(string title, CommitmentCard card) =>
        $"{title}\n" +
        $"时间：{card.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} – {card.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
        $"目标：{card.InputGoal ?? "—"} / {card.OutcomeGoal ?? "—"}\n" +
        $"类型：{card.Kind} · 模式：{card.SupervisionMode} · 相关项：{FormatTargets(card.RelatedAppsOrSites)}\n" +
        $"提醒：本机 {card.ReminderSettings.LocalDeviationMinutes} 分钟，手机 " +
        $"{card.ReminderSettings.FirstMobileDeviationMinutes}/" +
        $"{card.ReminderSettings.MobileRepeatMinutes} 分钟，最多 {card.ReminderSettings.MaxMobileReminders} 次；" +
        $"休息 {card.RestSettings.IdlePromptMinutes}/{card.RestSettings.DefaultTotalRestMinutes} 分钟";

    private static string FormatTargets(IReadOnlyList<CommitmentTarget> targets) =>
        targets.Count == 0
            ? "无"
            : string.Join("、", targets.Select(item => $"{item.Kind}:{item.Value}"));

    private async Task<CompanionOutcome> ConfirmNaturalLanguageCandidateAsync(
        ConfirmNaturalLanguageCandidateCommand command,
        CancellationToken cancellationToken)
    {
        var candidate = await _store.ReadPendingCandidateAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null || candidate.CandidateId != command.CandidateId)
        {
            var state = await _store.ReadCandidateStateAsync(command.CandidateId, cancellationToken)
                .ConfigureAwait(false);
            if (state == "committed")
            {
                await _store.CompleteCandidateConfirmationAsync(
                    command.CandidateId, true, CancellationToken.None).ConfigureAwait(false);
                return new CompanionOutcome(
                    true, Message: "候选操作已经由 Core 再校验并正式确认。",
                    Snapshot: await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false));
            }
            if (state == "confirmed")
                return new CompanionOutcome(
                    true, Message: "候选操作已经由 Core 再校验并正式确认。",
                    Snapshot: await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false));
            return state == "confirming"
                ? Fail("candidate_result_uncertain", "候选操作的正式结果尚未完成记录，请稍后重试。")
                : Fail("candidate_stale", "候选操作已失效，请重新生成。");
        }
        if (!await _store.TryBeginCandidateConfirmationAsync(command.CandidateId, cancellationToken)
                .ConfigureAwait(false))
            return Fail("candidate_stale", "候选操作正在确认或已经失效，请刷新。");
        var completed = false;
        var officialActionCommitted = false;
        try
        {
            SupervisionResult<CommitmentCard>? preparedCommitment = null;
            SupervisionResult<CommitmentRevisionCard>? preparedRevision = null;
            SupervisionResult<RecurrenceCard>? preparedRecurrence = null;
            switch (candidate.Kind)
            {
                case CandidateOperationKind.CreateCommitment when candidate.Commitment is not null:
                    preparedCommitment = await _supervision.PrepareAsync(candidate.Commitment, cancellationToken)
                        .ConfigureAwait(false);
                    if (!preparedCommitment.Success) return Fail(preparedCommitment.ErrorCode!, preparedCommitment.Message!);
                    var confirmed = await _supervision.ConfirmAsync(preparedCommitment.Value!.CandidateId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!confirmed.Success) return Fail(confirmed.ErrorCode!, confirmed.Message!);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.ReviseCommitment when candidate.Revision is not null:
                    preparedRevision = await _supervision.PrepareCommitmentRevisionAsync(
                        candidate.Revision, cancellationToken).ConfigureAwait(false);
                    if (!preparedRevision.Success) return Fail(preparedRevision.ErrorCode!, preparedRevision.Message!);
                    var revised = await _supervision.ConfirmCommitmentRevisionAsync(
                        preparedRevision.Value!.CandidateId, cancellationToken).ConfigureAwait(false);
                    if (!revised.Success) return Fail(revised.ErrorCode!, revised.Message!);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.CreateFromTemplate when candidate.FromTemplate is not null:
                    preparedCommitment = await _supervision.PrepareFromTemplateAsync(
                        candidate.FromTemplate, cancellationToken).ConfigureAwait(false);
                    if (!preparedCommitment.Success) return Fail(preparedCommitment.ErrorCode!, preparedCommitment.Message!);
                    var fromTemplate = await _supervision.ConfirmAsync(preparedCommitment.Value!.CandidateId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!fromTemplate.Success) return Fail(fromTemplate.ErrorCode!, fromTemplate.Message!);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.CreateRecurrence when candidate.Recurrence is not null:
                    preparedRecurrence = await _supervision.PrepareRecurrenceAsync(
                        candidate.Recurrence, cancellationToken).ConfigureAwait(false);
                    if (!preparedRecurrence.Success) return Fail(preparedRecurrence.ErrorCode!, preparedRecurrence.Message!);
                    var recurrence = await _supervision.ConfirmRecurrenceAsync(
                        preparedRecurrence.Value!.CandidateId, cancellationToken).ConfigureAwait(false);
                    if (!recurrence.Success) return Fail(recurrence.ErrorCode!, recurrence.Message!);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.SaveTemplate when candidate.Template is not null:
                    var template = await _supervision.CreateTemplateAsync(candidate.Template, cancellationToken)
                        .ConfigureAwait(false);
                    if (!template.Success) return Fail(template.ErrorCode!, template.Message!);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.EndCommitmentEarly
                    when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null:
                    var ended = await EndCommitmentEarlyAsync(
                        new EndCommitmentEarlyCommand(
                            candidate.TargetCommitmentId.Value, candidate.ExpectedVersion.Value),
                        cancellationToken).ConfigureAwait(false);
                    if (!ended.Success) return ended;
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.CancelCommitment
                    when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null &&
                         !string.IsNullOrWhiteSpace(candidate.Reason):
                    var cancelled = await _supervision.CancelCommitmentAsync(
                        candidate.TargetCommitmentId.Value, candidate.ExpectedVersion.Value,
                        candidate.Reason, cancellationToken).ConfigureAwait(false);
                    if (!cancelled.Success) return Fail(cancelled.ErrorCode!, cancelled.Message!);
                    await CancelActiveCardsAsync(candidate.TargetCommitmentId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    officialActionCommitted = true;
                    break;
                case CandidateOperationKind.DeferCommitment
                    when candidate.TargetCommitmentId is not null && candidate.ExpectedVersion is not null &&
                         candidate.DeferredStartAt is not null && !string.IsNullOrWhiteSpace(candidate.Reason):
                    var deferred = await _supervision.DeferActiveCommitmentAsync(
                        candidate.TargetCommitmentId.Value, candidate.ExpectedVersion.Value,
                        candidate.DeferredStartAt.Value, candidate.Reason, cancellationToken)
                        .ConfigureAwait(false);
                    if (!deferred.Success) return Fail(deferred.ErrorCode!, deferred.Message!);
                    await CancelActiveCardsAsync(candidate.TargetCommitmentId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    officialActionCommitted = true;
                    break;
                default:
                    return Fail("candidate_payload_invalid", "候选操作缺少必要内容。");
            }

            await _store.MarkCandidateOfficialActionCommittedAsync(
                command.CandidateId, CancellationToken.None).ConfigureAwait(false);
            await _store.CompleteCandidateConfirmationAsync(
                command.CandidateId, true, CancellationToken.None).ConfigureAwait(false);
            completed = true;
            return new CompanionOutcome(
                true, Message: "候选操作已经由 Core 再校验并正式确认。",
                Snapshot: await SafeSnapshotAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (!completed && !officialActionCommitted)
                await _store.CompleteCandidateConfirmationAsync(
                    command.CandidateId, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<CompanionOutcome> DiscardNaturalLanguageCandidateAsync(
        DiscardNaturalLanguageCandidateCommand command,
        CancellationToken cancellationToken)
    {
        var state = await _store.ReadCandidateStateAsync(command.CandidateId, cancellationToken)
            .ConfigureAwait(false);
        if (state == "discarded")
            return Ok("候选操作已放弃，正式状态没有变化。", await SnapshotAsync(cancellationToken));
        if (state != "pending")
            return Fail("candidate_stale", "候选操作已失效，请刷新。");
        await _store.SetCandidateStateAsync(command.CandidateId, "discarded", cancellationToken)
            .ConfigureAwait(false);
        return Ok("候选操作已放弃，正式状态没有变化。", await SnapshotAsync(cancellationToken));
    }

    private async Task<(CompanionOutcome Outcome, AiProviderResult? ProviderResult)> CallAiAsync(
        AiRequestPurpose purpose,
        string text,
        int maxOutputTokens,
        bool approvedOverOneCny,
        CancellationToken cancellationToken,
        Guid? stableRequestId = null)
    {
        var requestId = stableRequestId ?? Guid.NewGuid();
        if (stableRequestId is not null)
        {
            var previous = await _store.ReadAiCallAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (previous.IsSettled && previous.Result is not null)
                return ProviderOutcome(previous.Result);
            if (previous.Exists)
                return (Fail(
                    "ai_previous_result_uncertain",
                    "这条消息的 AI 调用可能已经计费，但结果没有安全落库；不会自动再次调用，请重新描述后再试。"), null);
        }
        var credential = await _credentialStore.ReadAsync("deepseek", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential))
            return (Fail("ai_credential_missing", "AI 未配置；表单、按钮和模板仍可正常使用。"), null);
        var spend = await _store.ReadMonthSpendAsync(_clock.Now, cancellationToken).ConfigureAwait(false);
        var hardCap = await _store.ReadAiMonthlyHardCapAsync(cancellationToken).ConfigureAwait(false);
        if (spend >= hardCap)
            return (Fail("ai_monthly_cap_reached", $"本月 AI 费用已到 {hardCap:F2} 元硬上限；确定性监督继续运行。"), null);
        var aiRequest = new AiProviderRequest(
            purpose, text, ModelName, maxOutputTokens, _clock.Now,
            await _supervision.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));
        var estimate = _aiProvider.EstimateCostCny(aiRequest);
        if (estimate > 1m && !approvedOverOneCny)
            return (Fail("ai_cost_confirmation_required", $"本次预计约 {estimate:F2} 元，需要明确确认后再调用。"), null);
        if (spend + estimate > hardCap)
            return (Fail("ai_monthly_cap_would_exceed", $"本次调用预计会超过 {hardCap:F2} 元月度硬上限。"), null);

        var reservation = new AiRequestRecordView(
            requestId, _clock.Now, purpose, ProviderName, ModelName,
            0, 0, 0, PriceVersion, estimate, false);
        if (!await _store.TryReserveAiRequestAsync(reservation, cancellationToken).ConfigureAwait(false))
        {
            var previous = await _store.ReadAiCallAsync(requestId, cancellationToken).ConfigureAwait(false);
            return previous.IsSettled && previous.Result is not null
                ? ProviderOutcome(previous.Result)
                : (Fail(
                    "ai_previous_result_uncertain",
                    "这次 AI 调用已有预算预留；为避免重复收费，Jarvis 不会自动重放。"), null);
        }

        AiProviderResult providerResult;
        _aiRequestInProgress = true;
        try
        {
            providerResult = await _aiProvider.CompleteAsync(
                aiRequest, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _aiError = exception.Message;
            return (Fail("ai_provider_unavailable", "云端 AI 暂时不可用；确定性监督和手工操作不受影响。"), null);
        }
        finally
        {
            _aiRequestInProgress = false;
        }

        var cost = CalculateCost(providerResult.Usage);
        var record = new AiRequestRecordView(
            requestId, _clock.Now, purpose, ProviderName, ModelName,
            providerResult.Usage.InputTokens, providerResult.Usage.OutputTokens,
            providerResult.Usage.CacheHitInputTokens, PriceVersion, cost, providerResult.Success);
        await _store.SettleAiRequestAsync(record, providerResult.ErrorCode, providerResult, cancellationToken)
            .ConfigureAwait(false);
        return ProviderOutcome(providerResult);
    }

    private (CompanionOutcome Outcome, AiProviderResult? ProviderResult) ProviderOutcome(
        AiProviderResult providerResult)
    {
        if (!providerResult.Success)
        {
            _aiError = providerResult.Message ?? providerResult.ErrorCode;
            return (Fail(
                providerResult.ErrorCode ?? "ai_provider_failed",
                providerResult.ErrorCode == "ai_clarification_required"
                    ? providerResult.Message ?? "请补充候选操作中的歧义。"
                    : "云端 AI 调用失败；确定性监督和手工操作不受影响。"), providerResult);
        }

        _aiError = null;
        return (new CompanionOutcome(true), providerResult);
    }

    private static decimal CalculateCost(AiTokenUsage usage)
    {
        var cacheHit = Math.Min(usage.InputTokens, usage.CacheHitInputTokens);
        var cacheMiss = Math.Max(0, usage.InputTokens - cacheHit);
        return Math.Round(
            cacheHit / 1_000_000m * 0.02m +
            cacheMiss / 1_000_000m * 1m +
            usage.OutputTokens / 1_000_000m * 2m,
            6,
            MidpointRounding.AwayFromZero);
    }

    private static CompanionOutcome Ok(string message, CompanionSnapshot snapshot) =>
        new(true, Message: message, Snapshot: snapshot);

    private async Task<CompanionSnapshot?> SafeSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _worktimeError ??= $"状态刷新稍后重试：{exception.GetType().Name}";
            return null;
        }
    }

    private static CompanionOutcome Fail(string code, string message) =>
        new(false, code, message);

    private static string? Suffix(string? value) =>
        string.IsNullOrEmpty(value) ? null : value[^Math.Min(4, value.Length)..];
}
