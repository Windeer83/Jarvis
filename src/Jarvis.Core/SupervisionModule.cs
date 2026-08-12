using Jarvis.Contracts;

namespace Jarvis.Core;

public sealed record SupervisionResult<T>(
    bool Success,
    T? Value = default,
    string? ErrorCode = null,
    string? Message = null)
{
    public static SupervisionResult<T> Ok(T value) => new(true, value);

    public static SupervisionResult<T> Fail(string errorCode, string message) =>
        new(false, default, errorCode, message);
}

public sealed class SupervisionModule : IAsyncDisposable
{
    private static readonly ReminderSettings DefaultReminders = new(true, 5, 20, 20, 3);
    private static readonly RestSettings DefaultRestSettings = new(10, 15);
    private static readonly TimeSpan RelatedRecovery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BubbleDuration = TimeSpan.FromSeconds(10);

    private readonly SqliteCommitmentStore _store;
    private readonly IClock _clock;
    private readonly IActivitySource _activitySource;
    private readonly IReminderSink _reminderSink;
    private readonly object _candidateLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CommitmentCard? _candidate;
    private ActivityObservation? _latestActivity;
    private bool _disposed;

    private SupervisionModule(
        SqliteCommitmentStore store,
        IClock clock,
        IActivitySource activitySource,
        IReminderSink reminderSink)
    {
        _store = store;
        _clock = clock;
        _activitySource = activitySource;
        _reminderSink = reminderSink;
    }

    public static async Task<SupervisionModule> OpenAsync(
        string databasePath,
        IClock clock,
        IActivitySource activitySource,
        IReminderSink reminderSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(activitySource);
        ArgumentNullException.ThrowIfNull(reminderSink);
        var store = new SqliteCommitmentStore(databasePath);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await store.MarkObservationInterruptedAsync(cancellationToken).ConfigureAwait(false);
        return new SupervisionModule(store, clock, activitySource, reminderSink);
    }

    public Task<SupervisionResult<CommitmentCard>> PrepareAsync(
        CommitmentDraft draft,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(draft);
        if (!normalized.Success || normalized.Value is null)
        {
            return Task.FromResult(SupervisionResult<CommitmentCard>.Fail(
                normalized.ErrorCode!, normalized.Message!));
        }

        lock (_candidateLock)
        {
            _candidate = normalized.Value;
        }

        return Task.FromResult(normalized);
    }

    public async Task<SupervisionResult<CommitmentView>> ConfirmAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommitmentCard? card;
            lock (_candidateLock)
            {
                card = _candidate?.CandidateId == candidateId ? _candidate : null;
            }

            if (card is null)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "candidate_not_found", "候选承诺已失效，请重新预览后确认。");
            }

            var frozenActivityRules = card.ActivityRules ?? [];
            var confirmation = await _store.ConfirmAsync(
                    card, _clock.Now, frozenActivityRules, cancellationToken)
                .ConfigureAwait(false);
            if (!confirmation.Success || confirmation.Value is null)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    confirmation.ErrorCode!, confirmation.Message!);
            }

            lock (_candidateLock)
            {
                if (_candidate?.CandidateId == candidateId)
                {
                    _candidate = null;
                }
            }

            return SupervisionResult<CommitmentView>.Ok(
                ToView(confirmation.Value, _clock.Now, frozenActivityRules));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentView>> ConfirmOfflineStartedAsync(
        Guid commitmentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _store.ConfirmOfflineStartedAsync(
                commitmentId, _clock.Now, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.Value is null)
            {
                return SupervisionResult<CommitmentView>.Fail(result.ErrorCode!, result.Message!);
            }

            var rules = await _store.ReadActivityRulesAsync(
                ActivityRuleScope.Commitment, result.Value.Id, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<CommitmentView>.Ok(ToView(result.Value, _clock.Now, rules));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<ActivityRuleBinding>> SaveActivityRuleAsync(
        ActivityRuleBinding binding,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validation = ValidateRule(binding);
        if (validation is not null)
        {
            return SupervisionResult<ActivityRuleBinding>.Fail(validation.Value.Code, validation.Value.Message);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (binding.Scope == ActivityRuleScope.Commitment &&
                !(await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    .Any(commitment => commitment.Id == binding.ScopeId))
            {
                return SupervisionResult<ActivityRuleBinding>.Fail(
                    "commitment_not_found", "没有找到这条工作承诺。");
            }

            await _store.SaveActivityRuleAsync(binding, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<ActivityRuleBinding>.Ok(binding);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<ActiveSupervisionView>> RecordReturnIntentAsync(
        Guid commitmentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookup = await FindActiveComputerAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (!lookup.Success || lookup.Value is null)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(lookup.ErrorCode!, lookup.Message!);
            }

            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            state = state with { ReturnIntentAt = _clock.Now };
            await _store.WriteRuntimeAsync(state, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<ActiveSupervisionView>.Ok(
                await ToActiveViewAsync(state, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<ActiveSupervisionView>> ClassifyCurrentActivityAsync(
        Guid commitmentId,
        ActivityClassification classification,
        ActivityRuleScope scope,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (classification is not (ActivityClassification.Related or ActivityClassification.Distracting))
        {
            return SupervisionResult<ActiveSupervisionView>.Fail(
                "classification_invalid", "请确认当前活动是相关或分心。");
        }

        if (!Enum.IsDefined(scope))
        {
            return SupervisionResult<ActiveSupervisionView>.Fail(
                "activity_rule_scope_invalid", "活动分类规则的作用范围无效。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookup = await FindActiveComputerAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (!lookup.Success || lookup.Value is null)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(lookup.ErrorCode!, lookup.Message!);
            }

            var commitment = lookup.Value;
            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (state.CurrentTarget is null || state.ActivityStateStartedAt is null)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "activity_not_available", "当前没有可确认的活动。");
            }

            var scopeId = scope switch
            {
                ActivityRuleScope.Global => null,
                ActivityRuleScope.Template => commitment.TemplateId,
                ActivityRuleScope.Commitment => commitment.Id,
                _ => null
            };
            if (scope == ActivityRuleScope.Template && scopeId is null)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "template_scope_unavailable", "这条承诺不来自模板，不能保存为模板规则。");
            }

            var original = state.Classification ?? ActivityClassification.Unknown;
            var binding = new ActivityRuleBinding(
                scope, scopeId, new ActivityRule(state.CurrentTarget, classification));
            var correction = new ActivityCorrectionView(
                state.CurrentTarget, original, classification, state.ActivityStateStartedAt.Value,
                _clock.Now, scope, NormalizeOptional(note));
            ReminderNotice? reminder = null;
            if (classification == ActivityClassification.Related)
            {
                state = ResetDeviation(state) with
                {
                    Classification = ActivityClassification.Related,
                    RelatedStableSince = _clock.Now
                };
            }
            else
            {
                var effectiveFrom = state.ActivityStateStartedAt.Value;
                state = state with
                {
                    Classification = ActivityClassification.Distracting,
                    DeviationStartedAt = effectiveFrom,
                    CountedDeviation = _clock.Now > effectiveFrom
                        ? _clock.Now - effectiveFrom
                        : TimeSpan.Zero,
                    DeviationCountingSince = _clock.Now,
                    DeviationReason = DeviationReason.DistractingActivity,
                    RelatedStableSince = null,
                    PendingPrompt = null
                };
                (state, reminder) = PrepareLocalDeviationReminder(commitment, state);
            }

            await _store.PersistClassificationAsync(binding, correction, state, reminder, cancellationToken)
                .ConfigureAwait(false);
            if (reminder is not null)
            {
                await DeliverBestEffortAsync(reminder, cancellationToken).ConfigureAwait(false);
            }
            return SupervisionResult<ActiveSupervisionView>.Ok(
                await ToActiveViewAsync(state, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<TimedRestView>> RespondToRestPromptAsync(
        Guid commitmentId,
        bool isResting,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookup = await FindActiveComputerAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (!lookup.Success || lookup.Value is null)
            {
                return SupervisionResult<TimedRestView>.Fail(lookup.ErrorCode!, lookup.Message!);
            }

            var commitment = lookup.Value;
            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (state.PendingPrompt != SupervisionPromptKind.ConfirmRest || state.IdleStartedAt is null)
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "rest_prompt_not_active", "当前没有等待确认的休息询问。");
            }

            if (!isResting)
            {
                state = state with { PendingPrompt = null };
                await _store.WriteRuntimeAsync(state, cancellationToken).ConfigureAwait(false);
                return SupervisionResult<TimedRestView>.Fail(
                    "rest_denied", "已记录不是休息，空闲继续计入偏离。");
            }

            var rest = new TimedRestView(
                state.IdleStartedAt.Value,
                state.IdleStartedAt.Value.AddMinutes(commitment.RestSettings.DefaultTotalRestMinutes),
                TimedRestSource.IdleConfirmation);
            if (rest.EndAt <= _clock.Now)
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "rest_already_elapsed", "默认休息时间已经结束，请主动设置新的明确结束时间。");
            }

            state = ResetDeviation(state) with
            {
                ActiveRest = rest,
                PendingPrompt = null,
                IsIdle = false,
                IdleStartedAt = null
            };
            await _store.WriteRuntimeAsync(state, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<TimedRestView>.Ok(rest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<TimedRestView>> StartTimedRestAsync(
        Guid commitmentId,
        DateTimeOffset? endAt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (endAt is null)
        {
            return SupervisionResult<TimedRestView>.Fail(
                "rest_end_required", "限时休息必须有明确结束时间。");
        }

        if (endAt <= _clock.Now)
        {
            return SupervisionResult<TimedRestView>.Fail(
                "rest_end_invalid", "休息结束时间必须晚于现在。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookup = await FindActiveComputerAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (!lookup.Success)
            {
                return SupervisionResult<TimedRestView>.Fail(lookup.ErrorCode!, lookup.Message!);
            }

            var rest = new TimedRestView(_clock.Now, endAt.Value, TimedRestSource.Proactive);
            var state = ResetDeviation(
                await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false)) with
            {
                ActiveRest = rest,
                PendingPrompt = null,
                IsIdle = false,
                IdleStartedAt = null
            };
            await _store.WriteRuntimeAsync(state, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<TimedRestView>.Ok(rest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.Now;
            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            await PublishFreshStartRemindersAsync(commitments, now, cancellationToken).ConfigureAwait(false);
            var active = commitments.SingleOrDefault(commitment =>
                commitment.Kind == CommitmentKind.Computer &&
                commitment.StartAt <= now && now < commitment.EndAt);
            if (active is null)
            {
                _latestActivity = null;
                return;
            }

            var observation = await _activitySource.ObserveAsync(cancellationToken).ConfigureAwait(false);
            _latestActivity = observation;
            var state = await _store.ReadRuntimeAsync(active.Id, cancellationToken).ConfigureAwait(false);
            state = await AdvanceAsync(active, state, observation, now, cancellationToken)
                .ConfigureAwait(false);
            await _store.WriteRuntimeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.Now;
            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var views = new List<CommitmentView>(commitments.Count);
            foreach (var commitment in commitments)
            {
                var rules = await _store.ReadActivityRulesAsync(
                    ActivityRuleScope.Commitment, commitment.Id, cancellationToken).ConfigureAwait(false);
                views.Add(ToView(commitment, now, rules));
            }
            var active = commitments.SingleOrDefault(commitment =>
                commitment.Kind == CommitmentKind.Computer &&
                commitment.StartAt <= now && now < commitment.EndAt);
            var activeView = active is null
                ? null
                : await ToActiveViewAsync(
                    await _store.ReadRuntimeAsync(active.Id, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            return new SupervisionSnapshot(
                now, active?.Id, views, active is null ? null : _latestActivity,
                await _store.ReadLatestReminderAsync(cancellationToken).ConfigureAwait(false),
                activeView);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task<StoredSupervisionRuntime> AdvanceAsync(
        StoredCommitment commitment,
        StoredSupervisionRuntime state,
        ActivityObservation observation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state.ActiveRest is { } rest)
        {
            if (now < rest.EndAt)
            {
                return state with { LastObservedAt = now };
            }

            if (state.LastRestEndedAt != rest.EndAt)
            {
                var notice = new ReminderNotice(
                    commitment.Id,
                    "限时休息已结束，该回到工作承诺了。",
                    now,
                    ReminderKind.RestEnded,
                    Guid.NewGuid(),
                    now.Add(BubbleDuration));
                state = state with
                {
                    ActiveRest = null,
                    LastRestEndedAt = rest.EndAt,
                    ActivityStateStartedAt = rest.EndAt,
                    IdleStartedAt = null,
                    LastObservedAt = rest.EndAt
                };
                await PersistAndDeliverAsync(notice, state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                state = state with
                {
                    ActiveRest = null,
                    LastRestEndedAt = rest.EndAt,
                    ActivityStateStartedAt = rest.EndAt,
                    IdleStartedAt = null,
                    LastObservedAt = rest.EndAt
                };
            }
        }

        state = MaterializeDeviation(state, now);

        if (observation.Availability == ActivityAvailability.Unobservable)
        {
            return PauseForUnobservable(state, now);
        }

        if (state.LastUnobservableStartedAt is not null && state.LastUnobservableEndedAt is null)
        {
            state = state with
            {
                LastUnobservableEndedAt = now,
                DeviationCountingSince = state.DeviationStartedAt is null ? null : now,
                RelatedStableSince = null,
                ActivityStateStartedAt = now
            };
        }

        var target = ObservationTarget(observation);
        var classification = target is null
            ? ActivityClassification.Unknown
            : await ClassifyAsync(commitment, target, cancellationToken).ConfigureAwait(false);
        var idle = commitment.SupervisionMode == SupervisionMode.Interactive && !observation.IsUserActive;
        var idleStart = idle
            ? Max(commitment.StartAt, now - (observation.IdleDuration ?? TimeSpan.Zero))
            : (DateTimeOffset?)null;
        var stateChanged = state.Classification != classification ||
                           !TargetsEqual(state.CurrentTarget, target) ||
                           state.IsIdle != idle;
        if (stateChanged)
        {
            state = state with
            {
                Classification = classification,
                CurrentTarget = target,
                ActivityStateStartedAt = idle && idleStart < now ? idleStart : now,
                IsIdle = idle,
                IdleStartedAt = idleStart,
                UnknownPromptedForStart = classification == ActivityClassification.Unknown
                    ? null
                    : state.UnknownPromptedForStart,
                RestPromptedForIdleStart = idle ? state.RestPromptedForIdleStart : null
            };
        }
        else if (idle)
        {
            state = state with { IdleStartedAt = Min(state.IdleStartedAt, idleStart) };
        }

        var countsAsDeviation = classification != ActivityClassification.Related || idle;
        if (countsAsDeviation)
        {
            var effectiveStart = idle
                ? state.IdleStartedAt ?? now
                : state.ActivityStateStartedAt ?? now;
            state = state with
            {
                DeviationStartedAt = state.DeviationStartedAt ?? effectiveStart,
                DeviationCountingSince = state.DeviationCountingSince ?? effectiveStart,
                DeviationReason = idle
                    ? DeviationReason.InteractiveIdle
                    : classification == ActivityClassification.Distracting
                        ? DeviationReason.DistractingActivity
                        : DeviationReason.UnknownActivity,
                RelatedStableSince = null
            };
            state = MaterializeDeviation(state, now);
        }
        else if (state.DeviationStartedAt is { })
        {
            var relatedSince = state.RelatedStableSince ?? now;
            state = state with { RelatedStableSince = relatedSince };
            if (now - relatedSince >= RelatedRecovery)
            {
                state = ResetDeviation(state) with
                {
                    Classification = classification,
                    CurrentTarget = target,
                    ActivityStateStartedAt = state.ActivityStateStartedAt,
                    LastObservedAt = now
                };
            }
        }

        if (classification == ActivityClassification.Unknown &&
            state.ActivityStateStartedAt is { } unknownSince &&
            now - unknownSince >= TimeSpan.FromMinutes(commitment.ReminderSettings.LocalDeviationMinutes) &&
            state.UnknownPromptedForStart != unknownSince)
        {
            var notice = new ReminderNotice(
                commitment.Id,
                "当前活动还未分类，它与这项工作承诺相关吗？",
                now,
                ReminderKind.UnknownClassificationQuestion,
                Guid.NewGuid(),
                now.Add(BubbleDuration));
            state = state with
            {
                PendingPrompt = SupervisionPromptKind.UnknownClassification,
                UnknownPromptedForStart = unknownSince
            };
            await PersistAndDeliverAsync(notice, state, cancellationToken).ConfigureAwait(false);
        }

        if (idle && state.IdleStartedAt is { } since &&
            now - since >= TimeSpan.FromMinutes(commitment.RestSettings.IdlePromptMinutes) &&
            state.RestPromptedForIdleStart != since)
        {
            var notice = new ReminderNotice(
                commitment.Id,
                "检测到持续空闲，你是在休息吗？确认后按本次空闲起点计算总休息时间。",
                now,
                ReminderKind.RestQuestion,
                Guid.NewGuid(),
                now.Add(BubbleDuration));
            state = state with
            {
                PendingPrompt = SupervisionPromptKind.ConfirmRest,
                RestPromptedForIdleStart = since
            };
            await PersistAndDeliverAsync(notice, state, cancellationToken).ConfigureAwait(false);
        }

        if (state.DeviationReason != DeviationReason.UnknownActivity)
        {
            state = await MaybePublishLocalDeviationAsync(commitment, state, cancellationToken)
                .ConfigureAwait(false);
        }

        return state with { LastObservedAt = now };
    }

    private async Task<StoredSupervisionRuntime> MaybePublishLocalDeviationAsync(
        StoredCommitment commitment,
        StoredSupervisionRuntime state,
        CancellationToken cancellationToken)
    {
        var (updatedState, notice) = PrepareLocalDeviationReminder(commitment, state);
        if (notice is null)
        {
            return updatedState;
        }

        await PersistAndDeliverAsync(notice, updatedState, cancellationToken).ConfigureAwait(false);
        return updatedState;
    }

    private (StoredSupervisionRuntime State, ReminderNotice? Notice) PrepareLocalDeviationReminder(
        StoredCommitment commitment,
        StoredSupervisionRuntime state)
    {
        var now = _clock.Now;
        if (state.LocalReminderSentAt is not null || state.DeviationStartedAt is null ||
            state.CountedDeviation < TimeSpan.FromMinutes(commitment.ReminderSettings.LocalDeviationMinutes) ||
            now < commitment.StartAt.AddMinutes(5))
        {
            return (state, null);
        }

        var notice = new ReminderNotice(
            commitment.Id,
            "已经持续偏离工作一会儿了。可以现在回到承诺，或明确开始限时休息。",
            now,
            ReminderKind.LocalDeviation,
            Guid.NewGuid(),
            now.Add(BubbleDuration),
            PlaySound: true,
            PersistentMarker: true);
        return (state with { LocalReminderSentAt = now, ReminderMarkerActive = true }, notice);
    }

    private async Task PublishFreshStartRemindersAsync(
        IReadOnlyList<StoredCommitment> commitments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var commitment in commitments.Where(item =>
                     item.ReminderSettings.StartReminderEnabled && item.StartReminderSentAt is null &&
                     item.StartAt <= now && now < item.EndAt))
        {
            if (now <= commitment.StartAt.AddMinutes(5))
            {
                var title = commitment.InputGoal ?? commitment.OutcomeGoal!;
                var notice = new ReminderNotice(
                    commitment.Id,
                    commitment.Kind == CommitmentKind.Offline
                        ? $"线下工作“{title}”已到开始时间，请在开始后手动确认。"
                        : $"工作承诺“{title}”已自动生效，前五分钟为准备缓冲。",
                    now,
                    ReminderKind.CommitmentStarted,
                    Guid.NewGuid(),
                    now.Add(BubbleDuration));
                await _store.PersistStartReminderAsync(notice, now, cancellationToken)
                    .ConfigureAwait(false);
                await DeliverBestEffortAsync(notice, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _store.MarkStartReminderSentAsync(commitment.Id, now, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PersistAndDeliverAsync(
        ReminderNotice notice,
        StoredSupervisionRuntime state,
        CancellationToken cancellationToken)
    {
        await _store.PersistReminderAndRuntimeAsync(notice, state, cancellationToken).ConfigureAwait(false);
        await DeliverBestEffortAsync(notice, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverBestEffortAsync(ReminderNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            await _reminderSink.PublishAsync(notice, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // SQLite is the formal reminder projection. A volatile local sink must not roll back
            // or cause a later tick to create another reminder/sound for the same state transition.
        }
    }

    private async Task<ActivityClassification> ClassifyAsync(
        StoredCommitment commitment,
        CommitmentTarget target,
        CancellationToken cancellationToken)
    {
        var rule = await _store.FindActivityRuleAsync(
            ActivityRuleScope.Commitment, commitment.Id, target, cancellationToken)
            .ConfigureAwait(false);
        if (rule is not null)
        {
            return rule.Value;
        }

        if (commitment.RelatedAppsOrSites.Any(item => TargetsEqual(item, target)))
        {
            return ActivityClassification.Related;
        }

        if (commitment.TemplateId is { } templateId)
        {
            rule = await _store.FindActivityRuleAsync(
                ActivityRuleScope.Template, templateId, target, cancellationToken).ConfigureAwait(false);
            if (rule is not null)
            {
                return rule.Value;
            }
        }

        return await _store.FindActivityRuleAsync(
                   ActivityRuleScope.Global, null, target, cancellationToken).ConfigureAwait(false)
               ?? ActivityClassification.Unknown;
    }

    private async Task<SupervisionResult<StoredCommitment>> FindActiveComputerAsync(
        Guid commitmentId,
        CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        var commitment = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        if (commitment is null)
        {
            return SupervisionResult<StoredCommitment>.Fail("commitment_not_found", "没有找到这条工作承诺。");
        }

        if (commitment.Kind != CommitmentKind.Computer || now < commitment.StartAt || now >= commitment.EndAt)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "computer_supervision_not_active", "这条电脑型工作承诺当前不在监督时段内。");
        }

        return SupervisionResult<StoredCommitment>.Ok(commitment);
    }

    private async Task<ActiveSupervisionView> ToActiveViewAsync(
        StoredSupervisionRuntime state,
        CancellationToken cancellationToken)
    {
        var counted = state.CountedDeviation;
        if (state.DeviationCountingSince is { } since && _clock.Now > since)
        {
            counted += _clock.Now - since;
        }

        return new ActiveSupervisionView(
            state.CommitmentId, state.Classification, state.IsIdle, state.DeviationReason,
            state.DeviationStartedAt, counted, state.RelatedStableSince,
            state.ReminderMarkerActive, state.ReturnIntentAt, state.PendingPrompt, state.ActiveRest,
            state.LastUnobservableStartedAt, state.LastUnobservableEndedAt,
            await _store.ReadCorrectionsAsync(state.CommitmentId, cancellationToken).ConfigureAwait(false));
    }

    private static StoredSupervisionRuntime PauseForUnobservable(
        StoredSupervisionRuntime state,
        DateTimeOffset now)
    {
        var counted = state.CountedDeviation;
        if (state.DeviationCountingSince is { } since && state.LastObservedAt is { } observed && observed > since)
        {
            counted += observed - since;
        }

        return state with
        {
            Classification = null,
            CountedDeviation = counted,
            DeviationCountingSince = null,
            RelatedStableSince = null,
            LastUnobservableStartedAt = state.LastUnobservableStartedAt ?? state.LastObservedAt ?? now,
            LastUnobservableEndedAt = null,
            LastObservedAt = now
        };
    }

    private static StoredSupervisionRuntime MaterializeDeviation(
        StoredSupervisionRuntime state,
        DateTimeOffset now)
    {
        if (state.DeviationCountingSince is not { } since || now <= since)
        {
            return state;
        }

        return state with
        {
            CountedDeviation = state.CountedDeviation + (now - since),
            DeviationCountingSince = now
        };
    }

    private static StoredSupervisionRuntime ResetDeviation(StoredSupervisionRuntime state) => state with
    {
        DeviationStartedAt = null,
        CountedDeviation = TimeSpan.Zero,
        DeviationCountingSince = null,
        DeviationReason = null,
        RelatedStableSince = null,
        LocalReminderSentAt = null,
        ReminderMarkerActive = false,
        ReturnIntentAt = null,
        PendingPrompt = null,
        UnknownPromptedForStart = null,
        RestPromptedForIdleStart = null
    };

    private static SupervisionResult<CommitmentCard> Normalize(CommitmentDraft draft)
    {
        if (!Enum.IsDefined(draft.Kind))
        {
            return SupervisionResult<CommitmentCard>.Fail("commitment_kind_invalid", "工作承诺类型无效。");
        }

        if (draft.SupervisionMode is { } mode && !Enum.IsDefined(mode))
        {
            return SupervisionResult<CommitmentCard>.Fail("supervision_mode_invalid", "监督模式无效。");
        }

        var input = NormalizeOptional(draft.InputGoal);
        var outcome = NormalizeOptional(draft.OutcomeGoal);
        if (input is null && outcome is null)
        {
            return SupervisionResult<CommitmentCard>.Fail("goal_required", "请填写一个投入目标或成果目标。");
        }

        if (draft.DurationMinutes is <= 0)
        {
            return SupervisionResult<CommitmentCard>.Fail("duration_invalid", "持续时长必须大于 0 分钟。");
        }

        DateTimeOffset durationEnd;
        try
        {
            durationEnd = draft.StartAt.AddMinutes(draft.DurationMinutes ?? 60);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SupervisionResult<CommitmentCard>.Fail("time_invalid", "持续时长超出了可表示的日期范围。");
        }

        var end = draft.EndAt ?? durationEnd;
        if (draft.EndAt is not null && draft.DurationMinutes is not null && end != durationEnd)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "time_conflict", "结束时间与持续时长不一致，请只保留一种或改为一致值。");
        }

        if (end <= draft.StartAt)
        {
            return SupervisionResult<CommitmentCard>.Fail("time_invalid", "结束时间必须晚于开始时间。");
        }

        var targets = NormalizeTargets(draft.RelatedAppsOrSites ?? []);
        if (targets.Any(target => !Enum.IsDefined(target.Kind)))
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "related_activity_invalid", "相关项目必须明确标记为软件或网站。");
        }

        if (draft.Kind == CommitmentKind.Computer && targets.Length == 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "related_activity_required", "电脑型工作承诺至少需要一个相关软件或网站。");
        }

        var reminders = draft.ReminderSettings ?? DefaultReminders;
        if (reminders.LocalDeviationMinutes <= 0 ||
            reminders.FirstMobileDeviationMinutes < reminders.LocalDeviationMinutes ||
            reminders.MobileRepeatMinutes <= 0 || reminders.MaxMobileReminders <= 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "reminder_invalid", "提醒阈值必须为正数，且手机提醒不得早于本机提醒。");
        }

        var rest = draft.RestSettings ?? DefaultRestSettings;
        if (rest.IdlePromptMinutes <= 0 || rest.DefaultTotalRestMinutes <= 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "rest_settings_invalid", "空闲询问与默认总休息时间必须大于 0 分钟。");
        }

        var rules = (draft.ActivityRules ?? []).ToArray();
        if (rules.Any(rule => !Enum.IsDefined(rule.Target.Kind) ||
                              !Enum.IsDefined(rule.Classification) ||
                              string.IsNullOrWhiteSpace(rule.Target.Value)))
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "activity_rule_invalid", "活动分类规则必须包含有效目标与三态分类。");
        }

        return SupervisionResult<CommitmentCard>.Ok(new CommitmentCard(
            Guid.NewGuid(), draft.Kind, draft.StartAt, end, input, outcome, targets,
            draft.SupervisionMode ?? SupervisionMode.Interactive, reminders,
            draft.Kind == CommitmentKind.Computer
                ? "尚未正式成立；确认后到点自动监督，前五分钟为准备缓冲。"
                : "尚未正式成立；确认后到点提醒，活动证据不会用于判断线下履约。",
            rules, rest, draft.TemplateId));
    }

    private static (string Code, string Message)? ValidateRule(ActivityRuleBinding binding)
    {
        if (!Enum.IsDefined(binding.Scope) || !Enum.IsDefined(binding.Rule.Target.Kind) ||
            !Enum.IsDefined(binding.Rule.Classification) || string.IsNullOrWhiteSpace(binding.Rule.Target.Value))
        {
            return ("activity_rule_invalid", "活动分类规则无效。");
        }

        if ((binding.Scope == ActivityRuleScope.Global) != (binding.ScopeId is null))
        {
            return ("activity_rule_scope_invalid", "全局规则不能带范围编号，模板或单次规则必须带范围编号。");
        }

        return null;
    }

    private static CommitmentView ToView(
        StoredCommitment commitment,
        DateTimeOffset now,
        IReadOnlyList<ActivityRule> activityRules) => new(
        commitment.Id, commitment.Kind, commitment.StartAt, commitment.EndAt,
        commitment.InputGoal, commitment.OutcomeGoal, commitment.RelatedAppsOrSites,
        commitment.SupervisionMode, commitment.ReminderSettings, DerivePhase(commitment, now),
        commitment.ConfirmedAt, commitment.OfflineManuallyConfirmedAt,
        activityRules, commitment.RestSettings, commitment.TemplateId);

    private static CommitmentPhase DerivePhase(StoredCommitment commitment, DateTimeOffset now)
    {
        if (now < commitment.StartAt) return CommitmentPhase.Scheduled;
        if (now >= commitment.EndAt) return CommitmentPhase.AwaitingReview;
        if (commitment.Kind == CommitmentKind.Offline) return CommitmentPhase.ActiveUnsupervised;
        return now < commitment.StartAt.AddMinutes(5)
            ? CommitmentPhase.PreparationBuffer
            : CommitmentPhase.Supervising;
    }

    private static CommitmentTarget? ObservationTarget(ActivityObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.ForegroundWebsiteDomain))
        {
            return new CommitmentTarget(
                CommitmentTargetKind.Website, observation.ForegroundWebsiteDomain.Trim());
        }

        return string.IsNullOrWhiteSpace(observation.ForegroundProcess)
            ? null
            : new CommitmentTarget(CommitmentTargetKind.Application, observation.ForegroundProcess.Trim());
    }

    private static CommitmentTarget[] NormalizeTargets(IEnumerable<CommitmentTarget> targets) => targets
        .Select(target => target with { Value = target.Value.Trim() })
        .Where(target => target.Value.Length > 0)
        .GroupBy(target => $"{(int)target.Kind}:{TargetKey(target)}", StringComparer.Ordinal)
        .Select(group => group.First())
        .ToArray();

    private static bool TargetsEqual(CommitmentTarget? left, CommitmentTarget? right) =>
        left is not null && right is not null && left.Kind == right.Kind &&
        string.Equals(TargetKey(left), TargetKey(right), StringComparison.Ordinal);

    private static string TargetKey(CommitmentTarget target)
    {
        var value = target.Value.Trim();
        if (target.Kind == CommitmentTargetKind.Application && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.ToUpperInvariant();
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;

    private static DateTimeOffset? Min(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first <= second ? first : second;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
