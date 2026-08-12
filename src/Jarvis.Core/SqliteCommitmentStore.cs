using System.Globalization;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed record StoredCommitment(
    Guid Id,
    CommitmentKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? InputGoal,
    string? OutcomeGoal,
    IReadOnlyList<CommitmentTarget> RelatedAppsOrSites,
    SupervisionMode SupervisionMode,
    ReminderSettings ReminderSettings,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset? StartReminderSentAt,
    DateTimeOffset? OfflineManuallyConfirmedAt,
    Guid? TemplateId,
    RestSettings RestSettings,
    bool IsSkipped);

internal sealed record StoredSupervisionRuntime(
    Guid CommitmentId,
    ActivityClassification? Classification = null,
    CommitmentTarget? CurrentTarget = null,
    DateTimeOffset? ActivityStateStartedAt = null,
    bool IsIdle = false,
    DateTimeOffset? IdleStartedAt = null,
    DateTimeOffset? DeviationStartedAt = null,
    TimeSpan CountedDeviation = default,
    DateTimeOffset? DeviationCountingSince = null,
    DeviationReason? DeviationReason = null,
    DateTimeOffset? RelatedStableSince = null,
    DateTimeOffset? LocalReminderSentAt = null,
    bool ReminderMarkerActive = false,
    DateTimeOffset? ReturnIntentAt = null,
    SupervisionPromptKind? PendingPrompt = null,
    TimedRestView? ActiveRest = null,
    DateTimeOffset? LastUnobservableStartedAt = null,
    DateTimeOffset? LastUnobservableEndedAt = null,
    DateTimeOffset? LastObservedAt = null,
    DateTimeOffset? UnknownPromptedForStart = null,
    DateTimeOffset? RestPromptedForIdleStart = null,
    DateTimeOffset? LastRestEndedAt = null);

internal sealed partial class SqliteCommitmentStore
{
    private readonly string _connectionString;

    public SqliteCommitmentStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (version > 3)
        {
            throw new InvalidOperationException($"数据库版本 {version} 高于当前程序支持的版本 3。");
        }

        if (version == 3)
        {
            return;
        }

        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await MigrateToVersionThreeAsync(connection, migration, version, cancellationToken)
            .ConfigureAwait(false);
        await migration.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateToVersionThreeAsync(
        SqliteConnection connection,
        SqliteTransaction migration,
        int version,
        CancellationToken cancellationToken)
    {
        if (version == 0)
        {
            await ExecuteSchemaAsync(connection, migration, """
                CREATE TABLE commitments (
                    id TEXT PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    start_at_utc TEXT NOT NULL,
                    end_at_utc TEXT NOT NULL,
                    input_goal TEXT NULL,
                    outcome_goal TEXT NULL,
                    supervision_mode INTEGER NOT NULL,
                    start_reminder_enabled INTEGER NOT NULL,
                    local_deviation_minutes INTEGER NOT NULL,
                    first_mobile_deviation_minutes INTEGER NOT NULL,
                    mobile_repeat_minutes INTEGER NOT NULL,
                    max_mobile_reminders INTEGER NOT NULL,
                    confirmed_at_utc TEXT NOT NULL,
                    start_reminder_sent_at_utc TEXT NULL,
                    offline_manually_confirmed_at_utc TEXT NULL,
                    template_id TEXT NULL,
                    idle_prompt_minutes INTEGER NOT NULL DEFAULT 10,
                    default_total_rest_minutes INTEGER NOT NULL DEFAULT 15,
                    sound_enabled INTEGER NOT NULL DEFAULT 1,
                    quiet_presentation INTEGER NOT NULL DEFAULT 0,
                    is_skipped INTEGER NOT NULL DEFAULT 0,
                    CHECK (end_at_utc > start_at_utc),
                    CHECK (
                        (input_goal IS NOT NULL AND length(trim(input_goal)) > 0)
                        OR (outcome_goal IS NOT NULL AND length(trim(outcome_goal)) > 0)
                    )
                );
                CREATE TABLE commitment_targets (
                    commitment_id TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    kind INTEGER NOT NULL,
                    value TEXT NOT NULL,
                    PRIMARY KEY (commitment_id, ordinal),
                    FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
                );
                CREATE INDEX ix_commitments_kind_time
                    ON commitments(kind, start_at_utc, end_at_utc);
                """, cancellationToken).ConfigureAwait(false);
        }
        else if (version == 1)
        {
            await ExecuteSchemaAsync(connection, migration, """
                ALTER TABLE commitments ADD COLUMN template_id TEXT NULL;
                ALTER TABLE commitments ADD COLUMN idle_prompt_minutes INTEGER NOT NULL DEFAULT 10;
                ALTER TABLE commitments ADD COLUMN default_total_rest_minutes INTEGER NOT NULL DEFAULT 15;
                """, cancellationToken).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await ExecuteSchemaAsync(connection, migration, """
                CREATE TABLE activity_rules (
                    scope INTEGER NOT NULL,
                    scope_id TEXT NOT NULL,
                    target_kind INTEGER NOT NULL,
                    target_value TEXT NOT NULL,
                    target_key TEXT NOT NULL,
                    classification INTEGER NOT NULL,
                    PRIMARY KEY (scope, scope_id, target_kind, target_key)
                );
                CREATE TABLE supervision_runtime (
                    commitment_id TEXT PRIMARY KEY,
                    classification INTEGER NULL,
                    current_target_kind INTEGER NULL,
                    current_target_value TEXT NULL,
                    activity_state_started_at_utc TEXT NULL,
                    is_idle INTEGER NOT NULL DEFAULT 0,
                    idle_started_at_utc TEXT NULL,
                    deviation_started_at_utc TEXT NULL,
                    counted_deviation_seconds REAL NOT NULL DEFAULT 0,
                    deviation_counting_since_utc TEXT NULL,
                    deviation_reason INTEGER NULL,
                    related_stable_since_utc TEXT NULL,
                    local_reminder_sent_at_utc TEXT NULL,
                    reminder_marker_active INTEGER NOT NULL DEFAULT 0,
                    return_intent_at_utc TEXT NULL,
                    pending_prompt INTEGER NULL,
                    active_rest_start_at_utc TEXT NULL,
                    active_rest_end_at_utc TEXT NULL,
                    active_rest_source INTEGER NULL,
                    last_unobservable_started_at_utc TEXT NULL,
                    last_unobservable_ended_at_utc TEXT NULL,
                    last_observed_at_utc TEXT NULL,
                    unknown_prompted_for_start_utc TEXT NULL,
                    rest_prompted_for_idle_start_utc TEXT NULL,
                    last_rest_ended_at_utc TEXT NULL,
                    FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
                );
                CREATE TABLE activity_corrections (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    commitment_id TEXT NOT NULL,
                    target_kind INTEGER NOT NULL,
                    target_value TEXT NOT NULL,
                    original_classification INTEGER NOT NULL,
                    corrected_classification INTEGER NOT NULL,
                    effective_from_utc TEXT NOT NULL,
                    corrected_at_utc TEXT NOT NULL,
                    scope INTEGER NOT NULL,
                    note TEXT NULL,
                    FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
                );
                CREATE TABLE reminder_notices (
                    notice_id TEXT PRIMARY KEY,
                    commitment_id TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    bubble_expires_at_utc TEXT NULL,
                    play_sound INTEGER NOT NULL,
                    persistent_marker INTEGER NOT NULL,
                    FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
                );
                CREATE INDEX ix_reminder_notices_created
                    ON reminder_notices(created_at_utc, notice_id);
                """, cancellationToken).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await ExecuteSchemaAsync(connection, migration, "PRAGMA user_version = 2;", cancellationToken)
                .ConfigureAwait(false);
        }

        if (version is 1 or 2)
        {
            await ExecuteSchemaAsync(connection, migration, """
                ALTER TABLE commitments ADD COLUMN sound_enabled INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE commitments ADD COLUMN quiet_presentation INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE commitments ADD COLUMN is_skipped INTEGER NOT NULL DEFAULT 0;
                """, cancellationToken).ConfigureAwait(false);
        }

        if (version < 3)
        {
            await ExecuteSchemaAsync(connection, migration, PlanningSchema, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteSchemaAsync(connection, migration, "PRAGMA user_version = 3;", cancellationToken)
                .ConfigureAwait(false);
        }

    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmAsync(
        CommitmentCard card,
        DateTimeOffset confirmedAt,
        IReadOnlyList<ActivityRule> frozenActivityRules,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (card.Kind == CommitmentKind.Computer)
        {
            await using var conflict = connection.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = """
                SELECT COALESCE(input_goal, outcome_goal), start_at_utc, end_at_utc
                FROM commitments
                WHERE kind = $kind AND is_skipped = 0
                  AND start_at_utc < $end AND end_at_utc > $start
                LIMIT 1;
                """;
            conflict.Parameters.AddWithValue("$kind", (int)CommitmentKind.Computer);
            conflict.Parameters.AddWithValue("$start", Format(card.StartAt));
            conflict.Parameters.AddWithValue("$end", Format(card.EndAt));
            await using var reader = await conflict.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return SupervisionResult<StoredCommitment>.Fail(
                    "computer_commitment_conflict",
                    $"与电脑型承诺“{reader.GetString(0)}”冲突（{Parse(reader.GetString(1)).ToLocalTime():g}–{Parse(reader.GetString(2)).ToLocalTime():t}），请调整时间后重新预览。");
            }
        }

        var id = Guid.NewGuid();
        await InsertCommitmentAsync(
            connection, transaction, id, card, confirmedAt, frozenActivityRules, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SupervisionResult<StoredCommitment>.Ok(new StoredCommitment(
            id, card.Kind, card.StartAt, card.EndAt, card.InputGoal, card.OutcomeGoal,
            card.RelatedAppsOrSites, card.SupervisionMode, card.ReminderSettings, confirmedAt,
            null, null, card.TemplateId, card.RestSettings, false));
    }

    private static async Task InsertCommitmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CommitmentCard card,
        DateTimeOffset confirmedAt,
        IReadOnlyList<ActivityRule> frozenActivityRules,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO commitments (
                    id, kind, start_at_utc, end_at_utc, input_goal, outcome_goal,
                    supervision_mode, start_reminder_enabled, local_deviation_minutes,
                    first_mobile_deviation_minutes, mobile_repeat_minutes, max_mobile_reminders,
                    confirmed_at_utc, template_id, idle_prompt_minutes, default_total_rest_minutes,
                    sound_enabled, quiet_presentation, is_skipped)
                VALUES ($id, $kind, $start, $end, $inputGoal, $outcomeGoal, $mode,
                    $startReminderEnabled, $localMinutes, $firstMobileMinutes, $repeatMinutes,
                    $maxMobileReminders, $confirmedAt, $templateId, $idlePrompt, $defaultRest,
                    $soundEnabled, $quietPresentation, 0);
                """;
            Add(insert, "$id", id.ToString("D"));
            Add(insert, "$kind", (int)card.Kind);
            Add(insert, "$start", Format(card.StartAt));
            Add(insert, "$end", Format(card.EndAt));
            Add(insert, "$inputGoal", card.InputGoal);
            Add(insert, "$outcomeGoal", card.OutcomeGoal);
            Add(insert, "$mode", (int)card.SupervisionMode);
            Add(insert, "$startReminderEnabled", card.ReminderSettings.StartReminderEnabled ? 1 : 0);
            Add(insert, "$localMinutes", card.ReminderSettings.LocalDeviationMinutes);
            Add(insert, "$firstMobileMinutes", card.ReminderSettings.FirstMobileDeviationMinutes);
            Add(insert, "$repeatMinutes", card.ReminderSettings.MobileRepeatMinutes);
            Add(insert, "$maxMobileReminders", card.ReminderSettings.MaxMobileReminders);
            Add(insert, "$confirmedAt", Format(confirmedAt));
            Add(insert, "$templateId", card.TemplateId?.ToString("D"));
            Add(insert, "$idlePrompt", card.RestSettings.IdlePromptMinutes);
            Add(insert, "$defaultRest", card.RestSettings.DefaultTotalRestMinutes);
            Add(insert, "$soundEnabled", card.ReminderSettings.SoundEnabled ? 1 : 0);
            Add(insert, "$quietPresentation", card.ReminderSettings.QuietPresentation ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < card.RelatedAppsOrSites.Count; index++)
        {
            await using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText = """
                INSERT INTO commitment_targets (commitment_id, ordinal, kind, value)
                VALUES ($id, $ordinal, $kind, $value);
                """;
            Add(target, "$id", id.ToString("D"));
            Add(target, "$ordinal", index);
            Add(target, "$kind", (int)card.RelatedAppsOrSites[index].Kind);
            Add(target, "$value", card.RelatedAppsOrSites[index].Value);
            await target.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in frozenActivityRules)
        {
            await UpsertActivityRuleAsync(
                    connection,
                    transaction,
                    new ActivityRuleBinding(ActivityRuleScope.Commitment, id, rule),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<StoredCommitment>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.kind, c.start_at_utc, c.end_at_utc, c.input_goal, c.outcome_goal,
                   c.supervision_mode, c.start_reminder_enabled, c.local_deviation_minutes,
                   c.first_mobile_deviation_minutes, c.mobile_repeat_minutes, c.max_mobile_reminders,
                   c.confirmed_at_utc, c.start_reminder_sent_at_utc,
                   c.offline_manually_confirmed_at_utc, c.template_id,
                   c.idle_prompt_minutes, c.default_total_rest_minutes,
                   c.sound_enabled, c.quiet_presentation, c.is_skipped, t.kind, t.value
            FROM commitments c
            LEFT JOIN commitment_targets t ON t.commitment_id = c.id
            ORDER BY c.start_at_utc, c.id, t.ordinal;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<StoredCommitment>();
        StoredCommitment? current = null;
        List<CommitmentTarget>? targets = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (current is null || current.Id != id)
            {
                if (current is not null)
                {
                    results.Add(current with { RelatedAppsOrSites = targets!.ToArray() });
                }

                targets = [];
                current = ReadCommitment(reader, targets);
            }

            if (!reader.IsDBNull(21))
            {
                targets!.Add(new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(21), reader.GetString(22)));
            }
        }

        if (current is not null)
        {
            results.Add(current with { RelatedAppsOrSites = targets!.ToArray() });
        }

        return results;
    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmOfflineStartedAsync(
        Guid commitmentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var commitment = (await ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == commitmentId);
        if (commitment is null)
        {
            return SupervisionResult<StoredCommitment>.Fail("commitment_not_found", "没有找到这条工作承诺。");
        }

        if (commitment.Kind != CommitmentKind.Offline)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "manual_confirmation_not_allowed", "电脑型工作承诺由活动证据监督，不能使用线下开始确认。");
        }

        if (now < commitment.StartAt || now >= commitment.EndAt)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "offline_commitment_not_active", "只能在线下工作承诺的计划时段内确认开始。");
        }

        await ExecuteAsync("""
            UPDATE commitments
            SET offline_manually_confirmed_at_utc = COALESCE(offline_manually_confirmed_at_utc, $at)
            WHERE id = $id;
            """, cancellationToken, ("$at", Format(now)), ("$id", commitmentId.ToString("D")))
            .ConfigureAwait(false);
        return SupervisionResult<StoredCommitment>.Ok(commitment with
        {
            OfflineManuallyConfirmedAt = commitment.OfflineManuallyConfirmedAt ?? now
        });
    }

    public Task MarkStartReminderSentAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            UPDATE commitments SET start_reminder_sent_at_utc = COALESCE(start_reminder_sent_at_utc, $at)
            WHERE id = $id;
            """, cancellationToken, ("$at", Format(at)), ("$id", id.ToString("D")));

    public async Task PersistStartReminderAsync(
        ReminderNotice notice,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE commitments SET start_reminder_sent_at_utc = COALESCE(start_reminder_sent_at_utc, $at)
            WHERE id = $id;
            """;
        Add(update, "$at", Format(sentAt));
        Add(update, "$id", notice.CommitmentId.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveActivityRuleAsync(ActivityRuleBinding binding, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await UpsertActivityRuleAsync(connection, transaction: null, binding, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task UpsertActivityRuleAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActivityRuleBinding binding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO activity_rules (
                scope, scope_id, target_kind, target_value, target_key, classification)
            VALUES ($scope, $scopeId, $kind, $value, $key, $classification)
            ON CONFLICT(scope, scope_id, target_kind, target_key) DO UPDATE SET
                target_value = excluded.target_value,
                classification = excluded.classification;
            """;
        Add(command, "$scope", (int)binding.Scope);
        Add(command, "$scopeId", binding.ScopeId?.ToString("D") ?? string.Empty);
        Add(command, "$kind", (int)binding.Rule.Target.Kind);
        Add(command, "$value", binding.Rule.Target.Value);
        Add(command, "$key", Key(binding.Rule.Target.Kind, binding.Rule.Target.Value));
        Add(command, "$classification", (int)binding.Rule.Classification);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActivityClassification?> FindActivityRuleAsync(
        ActivityRuleScope scope,
        Guid? scopeId,
        CommitmentTarget target,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT classification
            FROM activity_rules
            WHERE scope = $scope AND scope_id = $scopeId
              AND target_kind = $kind AND target_key = $key
            LIMIT 1;
            """;
        Add(command, "$scope", (int)scope);
        Add(command, "$scopeId", scopeId?.ToString("D") ?? string.Empty);
        Add(command, "$kind", (int)target.Kind);
        Add(command, "$key", Key(target.Kind, target.Value));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (ActivityClassification)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ActivityRule>> ReadActivityRulesAsync(
        ActivityRuleScope scope,
        Guid? scopeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_kind, target_value, classification
            FROM activity_rules
            WHERE scope = $scope AND scope_id = $scopeId
            ORDER BY target_kind, target_key;
            """;
        Add(command, "$scope", (int)scope);
        Add(command, "$scopeId", scopeId?.ToString("D") ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<ActivityRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new ActivityRule(
                new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)),
                (ActivityClassification)reader.GetInt32(2)));
        }

        return rules;
    }

    public async Task<StoredSupervisionRuntime> ReadRuntimeAsync(
        Guid commitmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM supervision_runtime WHERE commitment_id = $id;";
        Add(command, "$id", commitmentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StoredSupervisionRuntime(commitmentId);
        }

        CommitmentTarget? target = reader.IsDBNull(2)
            ? null
            : new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(2), reader.GetString(3));
        TimedRestView? rest = reader.IsDBNull(16)
            ? null
            : new TimedRestView(Parse(reader.GetString(16)), Parse(reader.GetString(17)),
                (TimedRestSource)reader.GetInt32(18));
        return new StoredSupervisionRuntime(
            commitmentId,
            NullableEnum<ActivityClassification>(reader, 1),
            target,
            NullableTime(reader, 4),
            reader.GetInt32(5) != 0,
            NullableTime(reader, 6),
            NullableTime(reader, 7),
            TimeSpan.FromSeconds(reader.GetDouble(8)),
            NullableTime(reader, 9),
            NullableEnum<DeviationReason>(reader, 10),
            NullableTime(reader, 11),
            NullableTime(reader, 12),
            reader.GetInt32(13) != 0,
            NullableTime(reader, 14),
            NullableEnum<SupervisionPromptKind>(reader, 15),
            rest,
            NullableTime(reader, 19),
            NullableTime(reader, 20),
            NullableTime(reader, 21),
            NullableTime(reader, 22),
            NullableTime(reader, 23),
            NullableTime(reader, 24));
    }

    public async Task WriteRuntimeAsync(StoredSupervisionRuntime state, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        ConfigureRuntimeWrite(command, state);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistReminderAndRuntimeAsync(
        ReminderNotice notice,
        StoredSupervisionRuntime state,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        await using var runtime = connection.CreateCommand();
        runtime.Transaction = transaction;
        ConfigureRuntimeWrite(runtime, state);
        await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistClassificationAsync(
        IReadOnlyList<ActivityRuleBinding> bindings,
        ActivityCorrectionView correction,
        StoredSupervisionRuntime state,
        ReminderNotice? notice,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var binding in bindings)
        {
            await UpsertActivityRuleAsync(connection, transaction, binding, cancellationToken)
                .ConfigureAwait(false);
        }
        await InsertCorrectionAsync(connection, transaction, state.CommitmentId, correction, cancellationToken)
            .ConfigureAwait(false);
        if (notice is not null)
        {
            await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
        await using var runtime = connection.CreateCommand();
        runtime.Transaction = transaction;
        ConfigureRuntimeWrite(runtime, state);
        await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task PersistClassificationForTestAsync(
        IReadOnlyList<ActivityRuleBinding> bindings,
        ActivityCorrectionView correction,
        StoredSupervisionRuntime state,
        ReminderNotice? notice,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> failBeforeCommit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var binding in bindings)
        {
            await UpsertActivityRuleAsync(connection, transaction, binding, cancellationToken)
                .ConfigureAwait(false);
        }
        await InsertCorrectionAsync(connection, transaction, state.CommitmentId, correction, cancellationToken)
            .ConfigureAwait(false);
        if (notice is not null)
        {
            await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }

        await using var runtime = connection.CreateCommand();
        runtime.Transaction = transaction;
        ConfigureRuntimeWrite(runtime, state);
        await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await failBeforeCommit(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ConfigureRuntimeWrite(SqliteCommand command, StoredSupervisionRuntime state)
    {
        command.CommandText = """
            INSERT INTO supervision_runtime (
                commitment_id, classification, current_target_kind, current_target_value,
                activity_state_started_at_utc, is_idle, idle_started_at_utc,
                deviation_started_at_utc, counted_deviation_seconds, deviation_counting_since_utc,
                deviation_reason, related_stable_since_utc, local_reminder_sent_at_utc,
                reminder_marker_active, return_intent_at_utc, pending_prompt,
                active_rest_start_at_utc, active_rest_end_at_utc, active_rest_source,
                last_unobservable_started_at_utc, last_unobservable_ended_at_utc,
                last_observed_at_utc, unknown_prompted_for_start_utc,
                rest_prompted_for_idle_start_utc, last_rest_ended_at_utc)
            VALUES ($id, $classification, $targetKind, $targetValue, $stateStart, $idle,
                $idleStart, $deviationStart, $counted, $countingSince, $reason, $relatedSince,
                $localSent, $marker, $returnAt, $prompt, $restStart, $restEnd, $restSource,
                $unobservableStart, $unobservableEnd, $lastObserved, $unknownPrompted,
                $restPrompted, $lastRestEnd)
            ON CONFLICT(commitment_id) DO UPDATE SET
                classification=excluded.classification,
                current_target_kind=excluded.current_target_kind,
                current_target_value=excluded.current_target_value,
                activity_state_started_at_utc=excluded.activity_state_started_at_utc,
                is_idle=excluded.is_idle, idle_started_at_utc=excluded.idle_started_at_utc,
                deviation_started_at_utc=excluded.deviation_started_at_utc,
                counted_deviation_seconds=excluded.counted_deviation_seconds,
                deviation_counting_since_utc=excluded.deviation_counting_since_utc,
                deviation_reason=excluded.deviation_reason,
                related_stable_since_utc=excluded.related_stable_since_utc,
                local_reminder_sent_at_utc=excluded.local_reminder_sent_at_utc,
                reminder_marker_active=excluded.reminder_marker_active,
                return_intent_at_utc=excluded.return_intent_at_utc,
                pending_prompt=excluded.pending_prompt,
                active_rest_start_at_utc=excluded.active_rest_start_at_utc,
                active_rest_end_at_utc=excluded.active_rest_end_at_utc,
                active_rest_source=excluded.active_rest_source,
                last_unobservable_started_at_utc=excluded.last_unobservable_started_at_utc,
                last_unobservable_ended_at_utc=excluded.last_unobservable_ended_at_utc,
                last_observed_at_utc=excluded.last_observed_at_utc,
                unknown_prompted_for_start_utc=excluded.unknown_prompted_for_start_utc,
                rest_prompted_for_idle_start_utc=excluded.rest_prompted_for_idle_start_utc,
                last_rest_ended_at_utc=excluded.last_rest_ended_at_utc;
            """;
        Add(command, "$id", state.CommitmentId.ToString("D"));
        Add(command, "$classification", state.Classification is null ? null : (int)state.Classification);
        Add(command, "$targetKind", state.CurrentTarget is null ? null : (int)state.CurrentTarget.Kind);
        Add(command, "$targetValue", state.CurrentTarget?.Value);
        Add(command, "$stateStart", FormatNullable(state.ActivityStateStartedAt));
        Add(command, "$idle", state.IsIdle ? 1 : 0);
        Add(command, "$idleStart", FormatNullable(state.IdleStartedAt));
        Add(command, "$deviationStart", FormatNullable(state.DeviationStartedAt));
        Add(command, "$counted", state.CountedDeviation.TotalSeconds);
        Add(command, "$countingSince", FormatNullable(state.DeviationCountingSince));
        Add(command, "$reason", state.DeviationReason is null ? null : (int)state.DeviationReason);
        Add(command, "$relatedSince", FormatNullable(state.RelatedStableSince));
        Add(command, "$localSent", FormatNullable(state.LocalReminderSentAt));
        Add(command, "$marker", state.ReminderMarkerActive ? 1 : 0);
        Add(command, "$returnAt", FormatNullable(state.ReturnIntentAt));
        Add(command, "$prompt", state.PendingPrompt is null ? null : (int)state.PendingPrompt);
        Add(command, "$restStart", FormatNullable(state.ActiveRest?.StartAt));
        Add(command, "$restEnd", FormatNullable(state.ActiveRest?.EndAt));
        Add(command, "$restSource", state.ActiveRest is null ? null : (int)state.ActiveRest.Source);
        Add(command, "$unobservableStart", FormatNullable(state.LastUnobservableStartedAt));
        Add(command, "$unobservableEnd", FormatNullable(state.LastUnobservableEndedAt));
        Add(command, "$lastObserved", FormatNullable(state.LastObservedAt));
        Add(command, "$unknownPrompted", FormatNullable(state.UnknownPromptedForStart));
        Add(command, "$restPrompted", FormatNullable(state.RestPromptedForIdleStart));
        Add(command, "$lastRestEnd", FormatNullable(state.LastRestEndedAt));
    }

    public async Task MarkObservationInterruptedAsync(CancellationToken cancellationToken)
    {
        var commitments = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var commitment in commitments.Where(item => item.Kind == CommitmentKind.Computer))
        {
            var state = await ReadRuntimeAsync(commitment.Id, cancellationToken).ConfigureAwait(false);
            if (state.LastObservedAt is null)
            {
                continue;
            }

            var counted = state.CountedDeviation;
            if (state.DeviationCountingSince is { } since && state.LastObservedAt > since)
            {
                counted += state.LastObservedAt.Value - since;
            }

            await WriteRuntimeAsync(state with
            {
                CountedDeviation = counted,
                DeviationCountingSince = null,
                LastUnobservableStartedAt = state.LastObservedAt
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertCorrectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        ActivityCorrectionView correction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO activity_corrections (
                commitment_id, target_kind, target_value, original_classification,
                corrected_classification, effective_from_utc, corrected_at_utc, scope, note)
            VALUES ($id, $kind, $value, $original, $corrected, $effective, $at, $scope, $note);
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$kind", (int)correction.Target.Kind);
        Add(command, "$value", correction.Target.Value);
        Add(command, "$original", (int)correction.OriginalClassification);
        Add(command, "$corrected", (int)correction.CorrectedClassification);
        Add(command, "$effective", Format(correction.EffectiveFrom));
        Add(command, "$at", Format(correction.CorrectedAt));
        Add(command, "$scope", (int)correction.Scope);
        Add(command, "$note", correction.Note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActivityCorrectionView>> ReadCorrectionsAsync(
        Guid commitmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_kind, target_value, original_classification, corrected_classification,
                   effective_from_utc, corrected_at_utc, scope, note
            FROM activity_corrections WHERE commitment_id = $id ORDER BY id DESC LIMIT 20;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var corrections = new List<ActivityCorrectionView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            corrections.Add(new ActivityCorrectionView(
                new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)),
                (ActivityClassification)reader.GetInt32(2),
                (ActivityClassification)reader.GetInt32(3),
                Parse(reader.GetString(4)), Parse(reader.GetString(5)),
                (ActivityRuleScope)reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return corrections;
    }

    private static async Task InsertReminderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReminderNotice notice,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO reminder_notices (
                notice_id, commitment_id, kind, message, created_at_utc,
                bubble_expires_at_utc, play_sound, persistent_marker)
            VALUES ($notice, $commitment, $kind, $message, $created, $expires, $sound, $marker);
            """;
        Add(command, "$notice", notice.NoticeId.ToString("D"));
        Add(command, "$commitment", notice.CommitmentId.ToString("D"));
        Add(command, "$kind", (int)notice.Kind);
        Add(command, "$message", notice.Message);
        Add(command, "$created", Format(notice.CreatedAt));
        Add(command, "$expires", FormatNullable(notice.BubbleExpiresAt));
        Add(command, "$sound", notice.PlaySound ? 1 : 0);
        Add(command, "$marker", notice.PersistentMarker ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReminderNotice?> ReadLatestReminderAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT notice_id, commitment_id, kind, message, created_at_utc,
                   bubble_expires_at_utc, play_sound, persistent_marker
            FROM reminder_notices ORDER BY created_at_utc DESC, rowid DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReminderNotice(
                Guid.Parse(reader.GetString(1)), reader.GetString(3), Parse(reader.GetString(4)),
                (ReminderKind)reader.GetInt32(2), Guid.Parse(reader.GetString(0)),
                NullableTime(reader, 5), reader.GetInt32(6) != 0, reader.GetInt32(7) != 0)
            : null;
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static StoredCommitment ReadCommitment(SqliteDataReader reader, IReadOnlyList<CommitmentTarget> targets) =>
        new(
            Guid.Parse(reader.GetString(0)), (CommitmentKind)reader.GetInt32(1),
            Parse(reader.GetString(2)), Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), targets,
            (SupervisionMode)reader.GetInt32(6),
            new ReminderSettings(reader.GetInt32(7) != 0, reader.GetInt32(8), reader.GetInt32(9),
                reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(18) != 0,
                reader.GetInt32(19) != 0),
            Parse(reader.GetString(12)), NullableTime(reader, 13), NullableTime(reader, 14),
            reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
            new RestSettings(reader.GetInt32(16), reader.GetInt32(17)),
            reader.GetInt32(20) != 0);

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Key(CommitmentTargetKind kind, string value)
    {
        var key = value.Trim();
        if (kind == CommitmentTargetKind.Application &&
            key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }

        return key.ToUpperInvariant();
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatNullable(DateTimeOffset? value) => value is null ? null : Format(value.Value);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? NullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

    private static T? NullableEnum<T>(SqliteDataReader reader, int ordinal) where T : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : (T)Enum.ToObject(typeof(T), reader.GetInt32(ordinal));
}
