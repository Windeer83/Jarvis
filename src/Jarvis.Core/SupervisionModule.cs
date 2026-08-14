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
    private RecurrenceCard? _recurrenceCandidate;
    private (RecurrenceChangeCard Card, RecurrenceChangeRequest Request)? _recurrenceChangeCandidate;
    private CommitmentRevisionCard? _revisionCandidate;
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
            _recurrenceCandidate = null;
            _recurrenceChangeCandidate = null;
            _revisionCandidate = null;
        }

        return Task.FromResult(normalized);
    }

    public async Task<SupervisionResult<CommitmentTemplateView>> CreateTemplateAsync(
        CommitmentTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTemplate(draft, _clock.Now);
        if (!normalized.Success || normalized.Value is null)
        {
            return SupervisionResult<CommitmentTemplateView>.Fail(
                normalized.ErrorCode!, normalized.Message!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await _store.SaveTemplateAsync(null, normalized.Value, cancellationToken)
                .ConfigureAwait(false);
            return SupervisionResult<CommitmentTemplateView>.Ok(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentTemplateView>> UpdateTemplateAsync(
        Guid templateId,
        CommitmentTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        var existing = await _store.ReadTemplateAsync(templateId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null || existing.IsArchived)
        {
            return SupervisionResult<CommitmentTemplateView>.Fail("template_not_found", "没有找到可修改的模板。");
        }

        var normalized = NormalizeTemplate(draft, _clock.Now, existing);
        if (!normalized.Success || normalized.Value is null)
        {
            return SupervisionResult<CommitmentTemplateView>.Fail(
                normalized.ErrorCode!, normalized.Message!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await _store.SaveTemplateAsync(templateId, normalized.Value, cancellationToken)
                .ConfigureAwait(false);
            return SupervisionResult<CommitmentTemplateView>.Ok(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentTemplateView>> ArchiveTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var archived = await _store.ArchiveTemplateAsync(templateId, _clock.Now, cancellationToken)
                .ConfigureAwait(false);
            return archived is null
                ? SupervisionResult<CommitmentTemplateView>.Fail("template_not_found", "没有找到该模板。")
                : SupervisionResult<CommitmentTemplateView>.Ok(archived);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentCard>> PrepareFromTemplateAsync(
        TemplateCommitmentDraft draft,
        CancellationToken cancellationToken = default)
    {
        var template = await _store.ReadTemplateAsync(draft.TemplateId, cancellationToken)
            .ConfigureAwait(false);
        if (template is null || template.IsArchived)
        {
            return SupervisionResult<CommitmentCard>.Fail("template_not_found", "没有找到可使用的模板。");
        }

        var duration = draft.DurationMinutes ?? template.DurationMinutes;
        var commitment = new CommitmentDraft(
            template.Kind,
            draft.StartAt,
            draft.EndAt,
            duration,
            draft.InputGoal ?? template.InputGoal,
            draft.OutcomeGoal ?? template.OutcomeGoal,
            draft.RelatedAppsOrSites ?? template.RelatedAppsOrSites,
            draft.SupervisionMode ?? template.SupervisionMode,
            draft.ReminderSettings ?? template.ReminderSettings,
            draft.ActivityRules ?? template.ActivityRules,
            draft.RestSettings ?? template.RestSettings,
            template.Id);
        return await PrepareAsync(commitment, cancellationToken).ConfigureAwait(false);
    }

    public Task<SupervisionResult<RecurrenceCard>> PrepareRecurrenceAsync(
        RecurrenceDraft draft,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dates = ExpandDates(draft.Pattern);
        if (!dates.Success || dates.Value is null)
        {
            return Task.FromResult(SupervisionResult<RecurrenceCard>.Fail(
                dates.ErrorCode!, dates.Message!));
        }

        var cards = new List<CommitmentCard>();
        foreach (var date in dates.Value)
        {
            DateTimeOffset start;
            try
            {
                start = CombineLocal(date, TimeOnly.FromDateTime(draft.Commitment.StartAt.DateTime));
            }
            catch (ArgumentException)
            {
                return Task.FromResult(SupervisionResult<RecurrenceCard>.Fail(
                    "recurrence_time_invalid", $"{date:yyyy-MM-dd} 的本地开始时间不存在。"));
            }

            var occurrenceDraft = draft.Commitment with
            {
                StartAt = start,
                EndAt = draft.Commitment.EndAt is null
                    ? null
                    : start + (draft.Commitment.EndAt.Value - draft.Commitment.StartAt)
            };
            var normalized = Normalize(occurrenceDraft);
            if (!normalized.Success || normalized.Value is null)
            {
                return Task.FromResult(SupervisionResult<RecurrenceCard>.Fail(
                    normalized.ErrorCode!, $"{date:yyyy-MM-dd}: {normalized.Message}"));
            }

            cards.Add(normalized.Value);
        }

        var card = new RecurrenceCard(
            Guid.NewGuid(), draft.Pattern, cards,
            $"尚未正式成立；确认后将一次创建 {cards.Count} 条相互独立的工作承诺。");
        lock (_candidateLock)
        {
            _candidate = null;
            _recurrenceCandidate = card;
            _recurrenceChangeCandidate = null;
            _revisionCandidate = null;
        }

        return Task.FromResult(SupervisionResult<RecurrenceCard>.Ok(card));
    }

    public async Task<SupervisionResult<RecurrencePlanView>> ConfirmRecurrenceAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecurrenceCard? candidate;
            lock (_candidateLock)
            {
                candidate = _recurrenceCandidate?.CandidateId == candidateId
                    ? _recurrenceCandidate
                    : null;
            }

            if (candidate is null)
            {
                return SupervisionResult<RecurrencePlanView>.Fail(
                    "candidate_not_found", "重复安排候选已失效，请重新预览。");
            }

            var confirmed = await _store.ConfirmRecurrenceAsync(
                candidate, _clock.Now, cancellationToken).ConfigureAwait(false);
            if (confirmed.Success)
            {
                lock (_candidateLock)
                {
                    if (_recurrenceCandidate?.CandidateId == candidateId)
                    {
                        _recurrenceCandidate = null;
                    }
                }
            }

            return confirmed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<RecurrenceChangeCard>> PrepareRecurrenceChangeAsync(
        RecurrenceChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var result = await _store.PrepareRecurrenceChangeAsync(
                request, commitments, _clock.Now, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Value is not null)
            {
                lock (_candidateLock)
                {
                    _candidate = null;
                    _recurrenceCandidate = null;
                    _recurrenceChangeCandidate = (result.Value, request);
                    _revisionCandidate = null;
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<RecurrencePlanView>> ConfirmRecurrenceChangeAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (RecurrenceChangeCard Card, RecurrenceChangeRequest Request)? candidate;
            lock (_candidateLock)
            {
                candidate = _recurrenceChangeCandidate is { } current &&
                            current.Card.CandidateId == candidateId
                    ? current
                    : null;
            }

            if (candidate is null)
            {
                return SupervisionResult<RecurrencePlanView>.Fail(
                    "candidate_not_found", "重复安排修改候选已失效，请重新预览。");
            }

            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var result = await _store.ChangeRecurrenceAsync(
                candidate.Value.Request, candidate.Value.Card, commitments, _clock.Now, cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                lock (_candidateLock)
                {
                    if (_recurrenceChangeCandidate?.Card.CandidateId == candidateId)
                    {
                        _recurrenceChangeCandidate = null;
                    }
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
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

            var frozenActivityRules = card.ActivityRules;
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

    public async Task<SupervisionResult<CommitmentRevisionCard>> PrepareCommitmentRevisionAsync(
        CommitmentRevisionDraft draft,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reason = NormalizeOptional(draft.Reason);
        if (reason is null)
        {
            return SupervisionResult<CommitmentRevisionCard>.Fail(
                "revision_reason_required", "承诺修订必须保存自然语言原因。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == draft.CommitmentId);
            if (current is null)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "commitment_not_found", "没有找到这条工作承诺。");
            }

            if (current.Version != draft.ExpectedVersion)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }

            var now = _clock.Now;
            if (current.IsSkipped || now >= current.EndAt)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "revision_history_immutable", "已结束或跳过的承诺属于历史记录，不能修订。");
            }

            var normalized = Normalize(draft.Proposed with { TemplateId = current.TemplateId });
            if (!normalized.Success || normalized.Value is null)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    normalized.ErrorCode!, normalized.Message!);
            }

            var after = normalized.Value;
            if (after.Kind != current.Kind || after.TemplateId != current.TemplateId)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "revision_identity_immutable", "承诺类型和模板来源不能通过修订改变。");
            }

            if (now >= current.StartAt && after.StartAt != current.StartAt)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "revision_history_immutable", "已经开始的承诺不能倒改开始时间。");
            }

            if (now < current.StartAt && after.StartAt <= now || after.EndAt <= now)
            {
                return SupervisionResult<CommitmentRevisionCard>.Fail(
                    "revision_history_immutable", "修订后的监督时段不能覆盖已经发生的时间。");
            }

            var rules = await _store.ReadActivityRulesAsync(
                ActivityRuleScope.Commitment, current.Id, cancellationToken).ConfigureAwait(false);
            var before = ToCard(current, rules);
            var candidate = new CommitmentRevisionCard(
                Guid.NewGuid(), current.Id, current.Version, current.Version + 1, now,
                before, after, reason,
                "尚未写入；确认后从确认时刻起使用新版本，旧版本和既有监督记录继续保留。");
            lock (_candidateLock)
            {
                _candidate = null;
                _recurrenceCandidate = null;
                _recurrenceChangeCandidate = null;
                _revisionCandidate = candidate;
            }
            return SupervisionResult<CommitmentRevisionCard>.Ok(candidate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentView>> ConfirmCommitmentRevisionAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommitmentRevisionCard? candidate;
            lock (_candidateLock)
            {
                candidate = _revisionCandidate?.CandidateId == candidateId
                    ? _revisionCandidate
                    : null;
            }
            if (candidate is null)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "candidate_not_found", "修订候选已失效，请重新预览。");
            }

            var now = _clock.Now;
            var confirmedCandidate = candidate with { EffectiveFrom = now };
            if (confirmedCandidate.After.EndAt <= now ||
                confirmedCandidate.Before.StartAt <= now &&
                confirmedCandidate.After.StartAt != confirmedCandidate.Before.StartAt)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "revision_history_immutable", "当前时刻已变化，请重新预览修订。");
            }

            var result = await _store.ConfirmRevisionAsync(confirmedCandidate, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success || result.Value is null)
            {
                return SupervisionResult<CommitmentView>.Fail(result.ErrorCode!, result.Message!);
            }

            lock (_candidateLock)
            {
                if (_revisionCandidate?.CandidateId == candidateId)
                {
                    _revisionCandidate = null;
                }
            }
            return SupervisionResult<CommitmentView>.Ok(
                ToView(result.Value, now, confirmedCandidate.After.ActivityRules));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentHistoryView>> GetCommitmentHistoryAsync(
        Guid commitmentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = await _store.ReadHistoryAsync(commitmentId, cancellationToken)
                .ConfigureAwait(false);
            return history is null
                ? SupervisionResult<CommitmentHistoryView>.Fail(
                    "commitment_not_found", "没有找到这条工作承诺。")
                : SupervisionResult<CommitmentHistoryView>.Ok(history);
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
        var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        return current is null
            ? SupervisionResult<CommitmentView>.Fail("commitment_not_found", "没有找到这条工作承诺。")
            : await ConfirmOfflineStartedAsync(commitmentId, current.Version, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<SupervisionResult<CommitmentView>> ConfirmOfflineStartedAsync(
        Guid commitmentId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == commitmentId);
            if (stored?.IsSkipped == true)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "offline_commitment_skipped", "已跳过的发生项不能确认开始。");
            }
            if (stored is not null && stored.Version != expectedVersion)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }

            var result = await _store.ConfirmOfflineStartedAsync(
                commitmentId, expectedVersion, _clock.Now, cancellationToken).ConfigureAwait(false);
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
        int? expectedCommitmentVersion = null,
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
            if (binding.Scope == ActivityRuleScope.Commitment && expectedCommitmentVersion is null)
            {
                return SupervisionResult<ActivityRuleBinding>.Fail(
                    "commitment_version_required", "保存单次承诺规则必须绑定当前承诺版本。");
            }

            if (binding.Scope == ActivityRuleScope.Commitment &&
                !(await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    .Any(commitment => commitment.Id == binding.ScopeId))
            {
                return SupervisionResult<ActivityRuleBinding>.Fail(
                    "commitment_not_found", "没有找到这条工作承诺。");
            }

            if (!await _store.SaveActivityRuleAsync(
                    binding, expectedCommitmentVersion, cancellationToken).ConfigureAwait(false))
            {
                return SupervisionResult<ActivityRuleBinding>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
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
        var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        return current is null
            ? SupervisionResult<ActiveSupervisionView>.Fail(
                "commitment_not_found", "没有找到这条工作承诺。")
            : await RecordReturnIntentAsync(commitmentId, current.Version, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<SupervisionResult<ActiveSupervisionView>> RecordReturnIntentAsync(
        Guid commitmentId,
        int expectedVersion,
        CancellationToken cancellationToken = default,
        string? sourceEventId = null,
        string? sourceEventOutcome = null)
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

            if (lookup.Value.Version != expectedVersion)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }

            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            state = state with { ReturnIntentAt = _clock.Now };
            if (!await _store.PersistRuntimeAndResponseAsync(
                    state, expectedVersion, "return_intent", _clock.Now, null, cancellationToken,
                    sourceEventId, sourceEventOutcome)
                .ConfigureAwait(false))
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
            return SupervisionResult<ActiveSupervisionView>.Ok(
                await ToActiveViewAsync(state, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<SupervisionResult<ActiveSupervisionView>> ClassifyCurrentActivityAsync(
        Guid commitmentId,
        ActivityClassification classification,
        ActivityRuleScope scope,
        string? note = null,
        CancellationToken cancellationToken = default) => ClassifyActivityWithinGateAsync(
            commitmentId, null, null, null, classification, scope, note, cancellationToken, null, null);

    private async Task<SupervisionResult<ActiveSupervisionView>> ClassifyActivityWithinGateAsync(
        Guid commitmentId,
        int? expectedVersion,
        CommitmentTarget? expectedTarget,
        DateTimeOffset? expectedActivityStateStartedAt,
        ActivityClassification classification,
        ActivityRuleScope scope,
        string? note,
        CancellationToken cancellationToken,
        string? sourceEventId,
        string? sourceEventOutcome)
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
            if (expectedVersion is { } version && commitment.Version != version)
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }

            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (expectedTarget is not null &&
                (!TargetsEqual(state.CurrentTarget, expectedTarget) ||
                 state.ActivityStateStartedAt != expectedActivityStateStartedAt))
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "activity_changed", "当前活动已经变化，请按新的活动状态重新操作。");
            }
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
            var correctedAt = _clock.Now;
            var versionEffectiveFrom = await _store.ReadVersionEffectiveFromAsync(
                commitment.Id, commitment.Version, cancellationToken).ConfigureAwait(false);
            var correctionEffectiveFrom = Max(
                state.ActivityStateStartedAt.Value, versionEffectiveFrom);
            var binding = new ActivityRuleBinding(
                scope, scopeId, new ActivityRule(state.CurrentTarget, classification));
            IReadOnlyList<ActivityRuleBinding> bindings = scope == ActivityRuleScope.Commitment
                ? [binding]
                :
                [
                    binding,
                    new ActivityRuleBinding(
                        ActivityRuleScope.Commitment,
                        commitment.Id,
                        binding.Rule)
                ];
            var correction = new ActivityCorrectionView(
                state.CurrentTarget, original, classification, correctionEffectiveFrom,
                correctedAt, scope, NormalizeOptional(note), commitment.Version);
            var pendingSegment = state.LastObservedAt is { } pendingStart && correctedAt > pendingStart
                ? new PendingActivitySegment(
                    ActivityAvailability.Available,
                    state.CurrentTarget,
                    original,
                    state.IsIdle,
                    state.DeviationReason,
                    pendingStart,
                    correctedAt)
                : null;
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
                state = state with
                {
                    Classification = ActivityClassification.Distracting,
                    DeviationStartedAt = correctionEffectiveFrom,
                    CountedDeviation = _clock.Now > correctionEffectiveFrom
                        ? _clock.Now - correctionEffectiveFrom
                        : TimeSpan.Zero,
                    DeviationCountingSince = _clock.Now,
                    DeviationReason = DeviationReason.DistractingActivity,
                    RelatedStableSince = null,
                    PendingPrompt = null
                };
                (state, reminder) = PrepareLocalDeviationReminder(commitment, state);
            }

            if (!await _store.PersistClassificationAsync(
                    bindings, correction, commitment.Version, pendingSegment, state, reminder,
                    cancellationToken, sourceEventId, sourceEventOutcome).ConfigureAwait(false))
            {
                return SupervisionResult<ActiveSupervisionView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
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

    public async Task<SupervisionResult<ActiveSupervisionView>> ClassifyActivityAsync(
        Guid commitmentId,
        CommitmentTarget target,
        DateTimeOffset activityStateStartedAt,
        ActivityClassification classification,
        ActivityRuleScope scope,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var commitment = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        return commitment is null
            ? SupervisionResult<ActiveSupervisionView>.Fail(
                "commitment_not_found", "没有找到这条工作承诺。")
            : await ClassifyActivityAsync(
                commitmentId, commitment.Version, target, activityStateStartedAt,
                classification, scope, note, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisionResult<ActiveSupervisionView>> ClassifyActivityAsync(
        Guid commitmentId,
        int expectedVersion,
        CommitmentTarget target,
        DateTimeOffset activityStateStartedAt,
        ActivityClassification classification,
        ActivityRuleScope scope,
        string? note = null,
        CancellationToken cancellationToken = default,
        string? sourceEventId = null,
        string? sourceEventOutcome = null)
    {
        return await ClassifyActivityWithinGateAsync(
            commitmentId, expectedVersion, target, activityStateStartedAt,
            classification, scope, note, cancellationToken, sourceEventId, sourceEventOutcome)
            .ConfigureAwait(false);
    }

    public async Task<SupervisionResult<TimedRestView>> RespondToRestPromptAsync(
        Guid commitmentId,
        bool isResting,
        CancellationToken cancellationToken = default)
    {
        var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        return current is null
            ? SupervisionResult<TimedRestView>.Fail("commitment_not_found", "没有找到这条工作承诺。")
            : await RespondToRestPromptAsync(
                commitmentId, current.Version, isResting, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisionResult<TimedRestView>> RespondToRestPromptAsync(
        Guid commitmentId,
        int expectedVersion,
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
            if (commitment.Version != expectedVersion)
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
            var state = await _store.ReadRuntimeAsync(commitmentId, cancellationToken).ConfigureAwait(false);
            if (state.PendingPrompt != SupervisionPromptKind.ConfirmRest || state.IdleStartedAt is null)
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "rest_prompt_not_active", "当前没有等待确认的休息询问。");
            }

            if (!isResting)
            {
                state = state with { PendingPrompt = null };
                if (!await _store.PersistRuntimeAndResponseAsync(
                        state, expectedVersion, "rest_denied", _clock.Now, null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return SupervisionResult<TimedRestView>.Fail(
                        "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
                }
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
            if (!await _store.PersistRuntimeAndResponseAsync(
                    state, expectedVersion, "rest_confirmed", _clock.Now,
                    rest.EndAt.ToUniversalTime().ToString("O"), cancellationToken)
                .ConfigureAwait(false))
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
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
        var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        return current is null
            ? SupervisionResult<TimedRestView>.Fail("commitment_not_found", "没有找到这条工作承诺。")
            : await StartTimedRestAsync(
                commitmentId, current.Version, endAt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisionResult<TimedRestView>> StartTimedRestAsync(
        Guid commitmentId,
        int expectedVersion,
        DateTimeOffset? endAt,
        CancellationToken cancellationToken = default,
        string? sourceEventId = null,
        string? sourceEventOutcome = null)
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
            if (lookup.Value!.Version != expectedVersion)
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
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
            if (!await _store.PersistRuntimeAndResponseAsync(
                    state, expectedVersion, "timed_rest_started", _clock.Now,
                    rest.EndAt.ToUniversalTime().ToString("O"), cancellationToken, sourceEventId,
                    sourceEventOutcome)
                .ConfigureAwait(false))
            {
                return SupervisionResult<TimedRestView>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
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
                !commitment.IsSkipped &&
                commitment.EndedEarlyAt is null &&
                commitment.StartAt <= now && now < commitment.EndAt);
            if (active is null)
            {
                _latestActivity = null;
                return;
            }

            var observation = await _activitySource.ObserveAsync(cancellationToken).ConfigureAwait(false);
            _latestActivity = observation;
            var state = await _store.ReadRuntimeAsync(active.Id, cancellationToken).ConfigureAwait(false);
            var previousState = state;
            var advance = await AdvanceAsync(active, state, observation, now, cancellationToken)
                .ConfigureAwait(false);
            state = advance.State;
            bool persisted;
            if (previousState.LastObservedAt is { } segmentStart && now > segmentStart)
            {
                persisted = await _store.PersistActivitySegmentAndRuntimeAsync(
                    active.Id,
                    active.Version,
                    await _store.ReadVersionAtAsync(active.Id, segmentStart, cancellationToken)
                        .ConfigureAwait(false),
                    new ActivityObservation(
                        previousState.LastUnobservableStartedAt is not null &&
                        previousState.LastUnobservableEndedAt is null
                            ? ActivityAvailability.Unobservable
                            : ActivityAvailability.Available,
                        !previousState.IsIdle,
                        previousState.CurrentTarget?.Kind == CommitmentTargetKind.Application
                            ? previousState.CurrentTarget.Value
                            : null,
                        segmentStart,
                        previousState.CurrentTarget?.Kind == CommitmentTargetKind.Website
                            ? previousState.CurrentTarget.Value
                            : null),
                    previousState.CurrentTarget,
                    previousState.Classification,
                    previousState.IsIdle,
                    previousState.DeviationReason,
                    segmentStart,
                    now,
                    state,
                    advance.Notices,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                persisted = await _store.PersistRuntimeAndRemindersAsync(
                        state, active.Version, advance.Notices, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!persisted)
            {
                return;
            }
            foreach (var notice in advance.Notices)
            {
                await DeliverBestEffortAsync(notice, cancellationToken).ConfigureAwait(false);
            }
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
                !commitment.IsSkipped &&
                commitment.EndedEarlyAt is null &&
                commitment.StartAt <= now && now < commitment.EndAt);
            var activeView = active is null
                ? null
                : await ToActiveViewAsync(
                    await _store.ReadRuntimeAsync(active.Id, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            return new SupervisionSnapshot(
                now, active?.Id, views, active is null ? null : _latestActivity,
                await _store.ReadLatestReminderAsync(cancellationToken).ConfigureAwait(false),
                activeView,
                await _store.ReadTemplatesAsync(includeArchived: false, cancellationToken)
                    .ConfigureAwait(false),
                await _store.ReadPlansAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentView>> EndCommitmentEarlyAsync(
        Guid commitmentId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = await _store.EndEarlyAsync(
                commitmentId, expectedVersion, _clock.Now, cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                return SupervisionResult<CommitmentView>.Fail(
                    "commitment_version_stale", "承诺状态或版本已经变化，请刷新后再操作。");
            }

            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var current = commitments.Single(item => item.Id == commitmentId);
            var rules = await _store.ReadActivityRulesAsync(
                ActivityRuleScope.Commitment, current.Id, cancellationToken).ConfigureAwait(false);
            return SupervisionResult<CommitmentView>.Ok(ToView(current, _clock.Now, rules));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentView>> CancelCommitmentAsync(
        Guid commitmentId,
        int expectedVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
            return SupervisionResult<CommitmentView>.Fail("cancellation_reason_required", "取消原因不能为空。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _store.CancelCommitmentAsync(
                    commitmentId, expectedVersion, _clock.Now, normalizedReason, cancellationToken)
                .ConfigureAwait(false))
                return SupervisionResult<CommitmentView>.Fail(
                    "commitment_version_stale", "承诺状态或版本已经变化，请刷新后再操作。");
            var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == commitmentId);
            var rules = await _store.ReadActivityRulesAsync(
                ActivityRuleScope.Commitment, commitmentId, cancellationToken).ConfigureAwait(false);
            if (_latestActivity is not null) _latestActivity = null;
            return SupervisionResult<CommitmentView>.Ok(ToView(current, _clock.Now, rules));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionResult<CommitmentView>> DeferActiveCommitmentAsync(
        Guid commitmentId,
        int expectedVersion,
        DateTimeOffset newStartAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
            return SupervisionResult<CommitmentView>.Fail("defer_reason_required", "推迟原因不能为空。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == commitmentId);
            if (current is null || current.Version != expectedVersion || current.IsSkipped ||
                current.EndedEarlyAt is not null || current.StartAt > _clock.Now || current.EndAt <= _clock.Now)
                return SupervisionResult<CommitmentView>.Fail(
                    "commitment_version_stale", "只能推迟当前仍在进行的同版本承诺。");
            if (newStartAt <= _clock.Now)
                return SupervisionResult<CommitmentView>.Fail("deferred_start_invalid", "新的开始时间必须晚于现在。");
            var remaining = current.EndAt - _clock.Now;
            var rules = await _store.ReadActivityRulesAsync(
                ActivityRuleScope.Commitment, commitmentId, cancellationToken).ConfigureAwait(false);
            var deferred = ToCard(current, rules) with
            {
                CandidateId = Guid.NewGuid(),
                StartAt = newStartAt,
                EndAt = newStartAt.Add(remaining),
                ConfirmationNotice = "确认后当前监督立即停止，并以剩余时长建立一条新的未来承诺。"
            };
            var result = await _store.DeferCommitmentAsync(
                current, deferred, rules, _clock.Now, normalizedReason, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
                return SupervisionResult<CommitmentView>.Fail(result.ErrorCode!, result.Message!);
            _latestActivity = null;
            return SupervisionResult<CommitmentView>.Ok(ToView(result.Value!, _clock.Now, rules));
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

    private async Task<AdvanceResult> AdvanceAsync(
        StoredCommitment commitment,
        StoredSupervisionRuntime state,
        ActivityObservation observation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var notices = new List<ReminderNotice>();
        if (state.ActiveRest is { } rest)
        {
            if (now < rest.EndAt)
            {
                return new AdvanceResult(state with { LastObservedAt = now }, notices);
            }

            if (state.LastRestEndedAt != rest.EndAt)
            {
                var notice = new ReminderNotice(
                    commitment.Id,
                    "限时休息已结束，该回到工作承诺了。",
                    now,
                    ReminderKind.RestEnded,
                    Guid.NewGuid(),
                    now.Add(BubbleDuration),
                    CommitmentVersion: commitment.Version);
                state = state with
                {
                    ActiveRest = null,
                    LastRestEndedAt = rest.EndAt,
                    ActivityStateStartedAt = rest.EndAt,
                    IdleStartedAt = null,
                    LastObservedAt = rest.EndAt
                };
                notices.Add(notice);
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
            return new AdvanceResult(PauseForUnobservable(state, now), notices);
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
        if (IsJarvisDesktop(target))
        {
            return new AdvanceResult(state with { LastObservedAt = now }, notices);
        }
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
                now.Add(BubbleDuration),
                CommitmentVersion: commitment.Version);
            state = state with
            {
                PendingPrompt = SupervisionPromptKind.UnknownClassification,
                UnknownPromptedForStart = unknownSince
            };
            notices.Add(notice);
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
                now.Add(BubbleDuration),
                CommitmentVersion: commitment.Version);
            state = state with
            {
                PendingPrompt = SupervisionPromptKind.ConfirmRest,
                RestPromptedForIdleStart = since
            };
            notices.Add(notice);
        }

        if (state.DeviationReason != DeviationReason.UnknownActivity)
        {
            var local = PrepareLocalDeviationReminder(commitment, state);
            state = local.State;
            if (local.Notice is not null)
            {
                notices.Add(local.Notice);
            }
        }

        return new AdvanceResult(state with { LastObservedAt = now }, notices);
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
            PersistentMarker: true,
            CommitmentVersion: commitment.Version);
        return (state with { LocalReminderSentAt = now, ReminderMarkerActive = true }, notice);
    }

    private async Task PublishFreshStartRemindersAsync(
        IReadOnlyList<StoredCommitment> commitments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var commitment in commitments.Where(item =>
                     !item.IsSkipped && item.EndedEarlyAt is null &&
                     item.ReminderSettings.StartReminderEnabled &&
                     item.StartReminderSentAt is null &&
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
                    now.Add(BubbleDuration),
                    CommitmentVersion: commitment.Version);
                if (await _store.PersistStartReminderAsync(notice, now, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await DeliverBestEffortAsync(notice, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            await _store.MarkStartReminderSentAsync(
                    commitment.Id, commitment.Version, now, cancellationToken)
                .ConfigureAwait(false);
        }
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

    private sealed record AdvanceResult(
        StoredSupervisionRuntime State,
        IReadOnlyList<ReminderNotice> Notices);

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

        if (commitment.IsSkipped || commitment.Kind != CommitmentKind.Computer ||
            now < commitment.StartAt || now >= commitment.EndAt)
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
            await _store.ReadRecentCorrectionsAsync(state.CommitmentId, cancellationToken).ConfigureAwait(false),
            (await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == state.CommitmentId).Version,
            state.CurrentTarget,
            state.ActivityStateStartedAt);
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
        activityRules, commitment.RestSettings, commitment.TemplateId, commitment.Version);

    private static CommitmentCard ToCard(
        StoredCommitment commitment,
        IReadOnlyList<ActivityRule> activityRules) => new(
        Guid.Empty, commitment.Kind, commitment.StartAt, commitment.EndAt,
        commitment.InputGoal, commitment.OutcomeGoal, commitment.RelatedAppsOrSites,
        commitment.SupervisionMode, commitment.ReminderSettings, "", activityRules,
        commitment.RestSettings, commitment.TemplateId);

    private static CommitmentPhase DerivePhase(StoredCommitment commitment, DateTimeOffset now)
    {
        if (commitment.IsSkipped) return CommitmentPhase.Skipped;
        if (now < commitment.StartAt) return CommitmentPhase.Scheduled;
        if (commitment.EndedEarlyAt is not null || now >= commitment.EndAt)
            return CommitmentPhase.AwaitingReview;
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

    private static bool IsJarvisDesktop(CommitmentTarget? target) =>
        target is { Kind: CommitmentTargetKind.Application } &&
        string.Equals(TargetKey(target), "JARVIS.DESKTOP", StringComparison.Ordinal);

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

    internal static SupervisionResult<CommitmentTemplateView> NormalizeTemplate(
        CommitmentTemplateDraft draft,
        DateTimeOffset now,
        CommitmentTemplateView? existing = null)
    {
        var name = NormalizeOptional(draft.Name);
        if (name is null)
        {
            return SupervisionResult<CommitmentTemplateView>.Fail(
                "template_name_required", "模板名称不能为空。");
        }

        var normalized = Normalize(new CommitmentDraft(
            draft.Kind, now.AddDays(1), null, draft.DurationMinutes,
            draft.InputGoal, draft.OutcomeGoal, draft.RelatedAppsOrSites,
            draft.SupervisionMode, draft.ReminderSettings, draft.ActivityRules,
            draft.RestSettings));
        if (!normalized.Success || normalized.Value is null)
        {
            return SupervisionResult<CommitmentTemplateView>.Fail(
                normalized.ErrorCode!, normalized.Message!);
        }

        var card = normalized.Value;
        return SupervisionResult<CommitmentTemplateView>.Ok(new CommitmentTemplateView(
            existing?.Id ?? Guid.NewGuid(),
            name,
            card.Kind,
            (int)(card.EndAt - card.StartAt).TotalMinutes,
            card.InputGoal,
            card.OutcomeGoal,
            card.RelatedAppsOrSites,
            card.SupervisionMode,
            card.ReminderSettings,
            card.ActivityRules,
            card.RestSettings,
            existing?.CreatedAt ?? now,
            now,
            existing?.IsArchived ?? false));
    }

    private static SupervisionResult<IReadOnlyList<DateOnly>> ExpandDates(RecurrencePattern pattern)
    {
        if (!Enum.IsDefined(pattern.Kind))
        {
            return SupervisionResult<IReadOnlyList<DateOnly>>.Fail(
                "recurrence_kind_invalid", "重复规则类型无效。");
        }

        if (pattern.Kind == RecurrenceKind.SelectedDates)
        {
            var selected = (pattern.SelectedDates ?? []).Distinct().Order().ToArray();
            return selected.Length == 0
                ? SupervisionResult<IReadOnlyList<DateOnly>>.Fail(
                    "recurrence_dates_required", "请至少选择一个日期。")
                : SupervisionResult<IReadOnlyList<DateOnly>>.Ok(selected);
        }

        if (pattern.StartDate is null || pattern.EndDate is null || pattern.EndDate < pattern.StartDate)
        {
            return SupervisionResult<IReadOnlyList<DateOnly>>.Fail(
                "recurrence_range_invalid", "每天或每周重复需要有限且有效的起止日期。");
        }

        var weekdays = (pattern.Weekdays ?? []).Distinct().ToHashSet();
        if (pattern.Kind == RecurrenceKind.Weekly && weekdays.Count == 0)
        {
            return SupervisionResult<IReadOnlyList<DateOnly>>.Fail(
                "recurrence_weekdays_required", "每周重复请至少选择一个星期。");
        }

        var dates = new List<DateOnly>();
        for (var date = pattern.StartDate.Value; date <= pattern.EndDate.Value; date = date.AddDays(1))
        {
            if (pattern.Kind == RecurrenceKind.Daily || weekdays.Contains(date.DayOfWeek))
            {
                dates.Add(date);
            }

            if (date == DateOnly.MaxValue)
            {
                break;
            }
        }

        return dates.Count == 0
            ? SupervisionResult<IReadOnlyList<DateOnly>>.Fail(
                "recurrence_has_no_occurrences", "所选范围内没有符合规则的日期。")
            : SupervisionResult<IReadOnlyList<DateOnly>>.Ok(dates);
    }

    private static DateTimeOffset CombineLocal(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
        {
            throw new ArgumentException("Invalid local time.");
        }

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }


    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
