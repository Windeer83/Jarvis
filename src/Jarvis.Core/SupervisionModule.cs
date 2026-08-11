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
    private static readonly ReminderSettings DefaultReminders = new(
        StartReminderEnabled: true,
        LocalDeviationMinutes: 5,
        FirstMobileDeviationMinutes: 20,
        MobileRepeatMinutes: 20,
        MaxMobileReminders: 3);

    private readonly SqliteCommitmentStore _store;
    private readonly IClock _clock;
    private readonly IActivitySource _activitySource;
    private readonly IReminderSink _reminderSink;
    private readonly object _candidateLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CommitmentCard? _candidate;
    private ActivityObservation? _latestActivity;
    private ReminderNotice? _latestReminder;
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

        var card = normalized.Value;
        lock (_candidateLock)
        {
            _candidate = card;
        }

        return Task.FromResult(SupervisionResult<CommitmentCard>.Ok(card));
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
                    "candidate_not_found",
                    "候选承诺已失效，请重新预览后确认。");
            }

            var confirmation = await _store.ConfirmAsync(card, _clock.Now, cancellationToken)
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

            return SupervisionResult<CommitmentView>.Ok(ToView(confirmation.Value, _clock.Now));
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
            var now = _clock.Now;
            var result = await _store.ConfirmOfflineStartedAsync(commitmentId, now, cancellationToken)
                .ConfigureAwait(false);
            return !result.Success || result.Value is null
                ? SupervisionResult<CommitmentView>.Fail(result.ErrorCode!, result.Message!)
                : SupervisionResult<CommitmentView>.Ok(ToView(result.Value, now));
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
            var activeComputer = commitments.SingleOrDefault(commitment =>
                commitment.Kind == CommitmentKind.Computer &&
                commitment.StartAt <= now &&
                now < commitment.EndAt);

            if (activeComputer is not null)
            {
                _latestActivity = await _activitySource.ObserveAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _latestActivity = null;
            }

            foreach (var commitment in commitments.Where(commitment =>
                         commitment.ReminderSettings.StartReminderEnabled &&
                         commitment.StartReminderSentAt is null &&
                         commitment.StartAt <= now &&
                         now < commitment.EndAt))
            {
                var title = commitment.InputGoal ?? commitment.OutcomeGoal!;
                var notice = new ReminderNotice(
                    commitment.Id,
                    commitment.Kind == CommitmentKind.Offline
                        ? $"线下工作“{title}”已到开始时间，请在开始后手动确认。"
                        : $"工作承诺“{title}”已自动生效，前五分钟为准备缓冲。",
                    now);

                await _reminderSink.PublishAsync(notice, cancellationToken).ConfigureAwait(false);
                await _store.MarkStartReminderSentAsync(commitment.Id, now, cancellationToken)
                    .ConfigureAwait(false);
                _latestReminder = notice;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.Now;
            var commitments = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var views = commitments.Select(commitment => ToView(commitment, now)).ToArray();
            var activeComputer = views.SingleOrDefault(commitment =>
                commitment.Kind == CommitmentKind.Computer &&
                commitment.StartAt <= now &&
                now < commitment.EndAt);

            return new SupervisionSnapshot(
                now,
                activeComputer?.Id,
                views,
                _latestActivity,
                _latestReminder);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private static SupervisionResult<CommitmentCard> Normalize(CommitmentDraft draft)
    {
        if (!Enum.IsDefined(draft.Kind))
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "commitment_kind_invalid",
                "工作承诺类型无效。");
        }

        if (draft.SupervisionMode is { } requestedMode && !Enum.IsDefined(requestedMode))
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "supervision_mode_invalid",
                "监督模式无效。");
        }

        var inputGoal = NormalizeOptional(draft.InputGoal);
        var outcomeGoal = NormalizeOptional(draft.OutcomeGoal);
        if (inputGoal is null && outcomeGoal is null)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "goal_required",
                "请填写一个投入目标或成果目标。");
        }

        if (draft.DurationMinutes is <= 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "duration_invalid",
                "持续时长必须大于 0 分钟。");
        }

        DateTimeOffset durationEnd;
        try
        {
            durationEnd = draft.StartAt.AddMinutes(draft.DurationMinutes ?? 60);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "time_invalid",
                "持续时长超出了可表示的日期范围。");
        }

        var endAt = draft.EndAt ?? durationEnd;
        if (draft.EndAt is not null && draft.DurationMinutes is not null &&
            endAt != durationEnd)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "time_conflict",
                "结束时间与持续时长不一致，请只保留一种或改为一致值。");
        }

        if (endAt <= draft.StartAt)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "time_invalid",
                "结束时间必须晚于开始时间。");
        }

        var targets = (draft.RelatedAppsOrSites ?? [])
            .Select(target => target with { Value = target.Value.Trim() })
            .Where(target => target.Value.Length > 0)
            .GroupBy(
                target => $"{(int)target.Kind}:{target.Value.ToUpperInvariant()}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (targets.Any(target => !Enum.IsDefined(target.Kind)))
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "related_activity_invalid",
                "相关项目必须明确标记为软件或网站。");
        }

        if (draft.Kind == CommitmentKind.Computer && targets.Length == 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "related_activity_required",
                "电脑型工作承诺至少需要一个相关软件或网站。");
        }

        var reminders = draft.ReminderSettings ?? DefaultReminders;
        if (reminders.LocalDeviationMinutes <= 0 ||
            reminders.FirstMobileDeviationMinutes < reminders.LocalDeviationMinutes ||
            reminders.MobileRepeatMinutes <= 0 ||
            reminders.MaxMobileReminders <= 0)
        {
            return SupervisionResult<CommitmentCard>.Fail(
                "reminder_invalid",
                "提醒阈值必须为正数，且手机提醒不得早于本机提醒。");
        }

        var card = new CommitmentCard(
            Guid.NewGuid(),
            draft.Kind,
            draft.StartAt,
            endAt,
            inputGoal,
            outcomeGoal,
            targets,
            draft.SupervisionMode ?? SupervisionMode.Interactive,
            reminders,
            draft.Kind == CommitmentKind.Computer
                ? "尚未正式成立；确认后到点自动监督，前五分钟为准备缓冲。"
                : "尚未正式成立；确认后到点提醒，活动证据不会用于判断线下履约。");

        return SupervisionResult<CommitmentCard>.Ok(card);
    }

    private static CommitmentView ToView(StoredCommitment commitment, DateTimeOffset now) => new(
        commitment.Id,
        commitment.Kind,
        commitment.StartAt,
        commitment.EndAt,
        commitment.InputGoal,
        commitment.OutcomeGoal,
        commitment.RelatedAppsOrSites,
        commitment.SupervisionMode,
        commitment.ReminderSettings,
        DerivePhase(commitment, now),
        commitment.ConfirmedAt,
        commitment.OfflineManuallyConfirmedAt);

    private static CommitmentPhase DerivePhase(StoredCommitment commitment, DateTimeOffset now)
    {
        if (now < commitment.StartAt)
        {
            return CommitmentPhase.Scheduled;
        }

        if (now >= commitment.EndAt)
        {
            return CommitmentPhase.AwaitingReview;
        }

        if (commitment.Kind == CommitmentKind.Offline)
        {
            return CommitmentPhase.ActiveUnsupervised;
        }

        return now < commitment.StartAt.AddMinutes(5)
            ? CommitmentPhase.PreparationBuffer
            : CommitmentPhase.Supervising;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
