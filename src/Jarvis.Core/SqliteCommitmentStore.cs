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
    bool IsSkipped,
    int Version,
    DateTimeOffset? EndedEarlyAt = null);

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

internal sealed record PendingActivitySegment(
    ActivityAvailability Availability,
    CommitmentTarget Target,
    ActivityClassification OriginalClassification,
    bool IsIdle,
    DeviationReason? DeviationReason,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt);

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
            Cache = SqliteCacheMode.Private,
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
        if (version > 7)
        {
            throw new InvalidOperationException($"数据库版本 {version} 高于当前程序支持的版本 7。");
        }

        if (version == 7)
        {
            return;
        }

        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version < 4)
        {
            await MigrateToVersionThreeAsync(connection, migration, version, cancellationToken)
                .ConfigureAwait(false);
            await MigrateToVersionFourAsync(connection, migration, cancellationToken)
                .ConfigureAwait(false);
        }

        if (version < 5)
        {
            await MigrateToVersionFiveAsync(connection, migration, cancellationToken)
                .ConfigureAwait(false);
        }
        if (version < 6)
        {
            await MigrateToVersionSixAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        }
        await MigrateToVersionSevenAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        await migration.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateToVersionSevenAsync(
        SqliteConnection connection,
        SqliteTransaction migration,
        CancellationToken cancellationToken)
    {
        await ExecuteSchemaAsync(connection, migration, """
            ALTER TABLE ai_usage ADD COLUMN latency_milliseconds INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE ai_review_drafts (
                draft_id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                source_id TEXT NOT NULL,
                request_id TEXT NOT NULL UNIQUE,
                period_start TEXT NOT NULL,
                period_end TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                state INTEGER NOT NULL,
                facts_scope TEXT NOT NULL,
                fact_item_count INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                anonymized_comparison_prompt TEXT NULL,
                confirmed_text TEXT NULL,
                confirmed_at_utc TEXT NULL,
                user_modified INTEGER NOT NULL DEFAULT 0,
                quality_rating INTEGER NULL,
                structure_reliable INTEGER NULL,
                ambiguity_handled INTEGER NULL,
                no_overreach INTEGER NULL,
                privacy_scope_confirmed INTEGER NULL,
                evaluation_note TEXT NULL,
                FOREIGN KEY (request_id) REFERENCES ai_usage(request_id)
            );
            CREATE INDEX ix_ai_review_drafts_created
                ON ai_review_drafts(created_at_utc DESC);
            CREATE TABLE manual_ai_comparisons (
                comparison_id TEXT PRIMARY KEY,
                draft_id TEXT NOT NULL,
                model TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                output_text TEXT NOT NULL,
                quality_rating INTEGER NOT NULL,
                structure_reliable INTEGER NOT NULL,
                ambiguity_handled INTEGER NOT NULL,
                no_overreach INTEGER NOT NULL,
                privacy_scope_confirmed INTEGER NOT NULL,
                evaluation_note TEXT NULL,
                FOREIGN KEY (draft_id) REFERENCES ai_review_drafts(draft_id) ON DELETE CASCADE
            );
            PRAGMA user_version = 7;
            """, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateToVersionSixAsync(
        SqliteConnection connection,
        SqliteTransaction migration,
        CancellationToken cancellationToken)
    {
        await ExecuteSchemaAsync(connection, migration, """
            ALTER TABLE mobile_escalation_cards
                ADD COLUMN counted_deviation_seconds REAL NOT NULL DEFAULT 0;
            UPDATE mobile_escalation_cards
               SET counted_deviation_seconds = ROUND(MAX(
                   0,
                   (julianday(sent_at_utc) - julianday(deviation_started_at_utc)) * 86400.0), 0);
            PRAGMA user_version = 6;
            """, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateToVersionFiveAsync(
        SqliteConnection connection,
        SqliteTransaction migration,
        CancellationToken cancellationToken)
    {
        await ExecuteSchemaAsync(connection, migration, """
            ALTER TABLE commitments ADD COLUMN ended_early_at_utc TEXT NULL;
            CREATE TABLE companion_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE mobile_escalation_cards (
                card_id TEXT PRIMARY KEY,
                commitment_id TEXT NOT NULL,
                commitment_version INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                sent_at_utc TEXT NOT NULL,
                planned_start_at_utc TEXT NOT NULL,
                planned_end_at_utc TEXT NOT NULL,
                deviation_started_at_utc TEXT NOT NULL,
                classification INTEGER NOT NULL,
                commitment_summary TEXT NOT NULL,
                privacy_preview TEXT NOT NULL,
                state INTEGER NOT NULL,
                platform_message_id TEXT NULL,
                default_rest_minutes INTEGER NOT NULL,
                invalidation_result_text TEXT NULL,
                UNIQUE (commitment_id, commitment_version, deviation_started_at_utc, sequence),
                FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
            );
            CREATE TABLE processed_worktime_events (
                event_id TEXT PRIMARY KEY,
                processed_at_utc TEXT NOT NULL,
                state TEXT NOT NULL,
                outcome_json TEXT NULL,
                candidate_id TEXT NULL,
                candidate_action TEXT NULL
            );
            CREATE TABLE processed_supervision_events (
                event_id TEXT PRIMARY KEY,
                processed_at_utc TEXT NOT NULL,
                outcome_text TEXT NOT NULL
            );
            CREATE TABLE worktime_reply_outbox (
                event_id TEXT PRIMARY KEY,
                recipient_open_id TEXT NOT NULL,
                reply_text TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                state TEXT NOT NULL,
                platform_message_id TEXT NULL
            );
            CREATE TABLE commitment_reviews (
                commitment_id TEXT PRIMARY KEY,
                commitment_version INTEGER NOT NULL,
                state INTEGER NOT NULL,
                requested_at_utc TEXT NOT NULL,
                deferred_until_utc TEXT NULL,
                raw_text TEXT NULL,
                assessment INTEGER NULL,
                answered_at_utc TEXT NULL,
                FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
            );
            CREATE TABLE daily_review_sessions (
                session_id TEXT PRIMARY KEY,
                review_date TEXT NOT NULL UNIQUE,
                state INTEGER NOT NULL,
                current_question INTEGER NULL,
                follow_up_used INTEGER NOT NULL DEFAULT 0,
                mobile_invite_sent INTEGER NOT NULL DEFAULT 0,
                snoozed_until_utc TEXT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE daily_review_answers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                question INTEGER NOT NULL,
                raw_text TEXT NOT NULL,
                answered_at_utc TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES daily_review_sessions(session_id) ON DELETE CASCADE
            );
            CREATE TABLE cycle_review_sessions (
                session_id TEXT PRIMARY KEY,
                period_start TEXT NOT NULL,
                period_end TEXT NOT NULL,
                state INTEGER NOT NULL,
                summary TEXT NOT NULL,
                trends_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE cycle_review_focuses (
                session_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY (session_id, ordinal),
                FOREIGN KEY (session_id) REFERENCES cycle_review_sessions(session_id) ON DELETE CASCADE
            );
            CREATE TABLE ai_usage (
                request_id TEXT PRIMARY KEY,
                requested_at_utc TEXT NOT NULL,
                purpose INTEGER NOT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                cache_hit_input_tokens INTEGER NOT NULL,
                price_version TEXT NOT NULL,
                cost_cny TEXT NOT NULL,
                success INTEGER NOT NULL,
                error_code TEXT NULL,
                state TEXT NOT NULL,
                result_json TEXT NULL
            );
            CREATE TABLE companion_chat_messages (
                message_id TEXT PRIMARY KEY,
                at_utc TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL
            );
            CREATE TABLE natural_language_candidates (
                candidate_id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                source INTEGER NOT NULL,
                original_text TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                summary TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                state TEXT NOT NULL
            );
            PRAGMA user_version = 5;
            """, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateToVersionFourAsync(
        SqliteConnection connection,
        SqliteTransaction migration,
        CancellationToken cancellationToken)
    {
        await ExecuteSchemaAsync(connection, migration, """
            ALTER TABLE commitments ADD COLUMN current_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE reminder_notices ADD COLUMN commitment_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE activity_corrections ADD COLUMN commitment_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE activity_corrections ADD COLUMN activity_segment_id INTEGER NULL;

            CREATE TABLE commitment_versions (
                commitment_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                effective_from_utc TEXT NOT NULL,
                confirmed_at_utc TEXT NOT NULL,
                reason TEXT NOT NULL,
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
                sound_enabled INTEGER NOT NULL,
                quiet_presentation INTEGER NOT NULL,
                idle_prompt_minutes INTEGER NOT NULL,
                default_total_rest_minutes INTEGER NOT NULL,
                template_id TEXT NULL,
                PRIMARY KEY (commitment_id, version),
                FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
            );
            CREATE TABLE commitment_version_targets (
                commitment_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (commitment_id, version, ordinal),
                FOREIGN KEY (commitment_id, version)
                    REFERENCES commitment_versions(commitment_id, version) ON DELETE CASCADE
            );
            CREATE TABLE commitment_version_rules (
                commitment_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                target_kind INTEGER NOT NULL,
                target_value TEXT NOT NULL,
                classification INTEGER NOT NULL,
                PRIMARY KEY (commitment_id, version, ordinal),
                FOREIGN KEY (commitment_id, version)
                    REFERENCES commitment_versions(commitment_id, version) ON DELETE CASCADE
            );
            CREATE TABLE activity_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                commitment_id TEXT NOT NULL,
                commitment_version INTEGER NOT NULL,
                start_at_utc TEXT NOT NULL,
                end_at_utc TEXT NOT NULL,
                availability INTEGER NOT NULL,
                target_kind INTEGER NULL,
                target_value TEXT NULL,
                original_classification INTEGER NULL,
                effective_classification INTEGER NULL,
                is_idle INTEGER NOT NULL,
                deviation_reason INTEGER NULL,
                corrected_at_utc TEXT NULL,
                correction_note TEXT NULL,
                FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
            );
            CREATE INDEX ix_activity_segments_commitment_time
                ON activity_segments(commitment_id, start_at_utc, id);
            CREATE TABLE supervision_responses (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                commitment_id TEXT NOT NULL,
                commitment_version INTEGER NOT NULL,
                kind TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                note TEXT NULL,
                FOREIGN KEY (commitment_id) REFERENCES commitments(id) ON DELETE CASCADE
            );
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteSchemaAsync(connection, migration, """
            INSERT INTO commitment_versions (
                commitment_id, version, effective_from_utc, confirmed_at_utc, reason,
                kind, start_at_utc, end_at_utc, input_goal, outcome_goal, supervision_mode,
                start_reminder_enabled, local_deviation_minutes, first_mobile_deviation_minutes,
                mobile_repeat_minutes, max_mobile_reminders, sound_enabled, quiet_presentation,
                idle_prompt_minutes, default_total_rest_minutes, template_id)
            SELECT id, 1, confirmed_at_utc, confirmed_at_utc, '建立工作承诺', kind,
                start_at_utc, end_at_utc, input_goal, outcome_goal, supervision_mode,
                start_reminder_enabled, local_deviation_minutes, first_mobile_deviation_minutes,
                mobile_repeat_minutes, max_mobile_reminders, sound_enabled, quiet_presentation,
                idle_prompt_minutes, default_total_rest_minutes, template_id
            FROM commitments;
            INSERT INTO commitment_version_targets (commitment_id, version, ordinal, kind, value)
            SELECT commitment_id, 1, ordinal, kind, value FROM commitment_targets;
            INSERT INTO commitment_version_rules (
                commitment_id, version, ordinal, target_kind, target_value, classification)
            SELECT scope_id, 1,
                ROW_NUMBER() OVER (PARTITION BY scope_id ORDER BY target_kind, target_key) - 1,
                target_kind, target_value, classification
            FROM activity_rules WHERE scope=2;
            PRAGMA user_version = 4;
            """, cancellationToken).ConfigureAwait(false);
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
                SELECT COALESCE(input_goal, outcome_goal), start_at_utc,
                       COALESCE(ended_early_at_utc,end_at_utc)
                FROM commitments
                WHERE kind = $kind AND is_skipped = 0
                  AND start_at_utc < $end
                  AND COALESCE(ended_early_at_utc,end_at_utc) > $start
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
            null, null, card.TemplateId, card.RestSettings, false, 1));
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

        await InsertCommitmentVersionAsync(
            connection, transaction, id, 1, confirmedAt, confirmedAt, "建立工作承诺", card,
            frozenActivityRules, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task InsertCommitmentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int version,
        DateTimeOffset effectiveFrom,
        DateTimeOffset confirmedAt,
        string reason,
        CommitmentCard card,
        IReadOnlyList<ActivityRule> rules,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO commitment_versions (
                    commitment_id, version, effective_from_utc, confirmed_at_utc, reason,
                    kind, start_at_utc, end_at_utc, input_goal, outcome_goal, supervision_mode,
                    start_reminder_enabled, local_deviation_minutes, first_mobile_deviation_minutes,
                    mobile_repeat_minutes, max_mobile_reminders, sound_enabled, quiet_presentation,
                    idle_prompt_minutes, default_total_rest_minutes, template_id)
                VALUES ($id,$version,$effective,$confirmed,$reason,$kind,$start,$end,$input,$outcome,
                    $mode,$startReminder,$local,$firstMobile,$repeat,$maxMobile,$sound,$quiet,
                    $idle,$totalRest,$templateId);
                """;
            Add(command, "$id", commitmentId.ToString("D"));
            Add(command, "$version", version);
            Add(command, "$effective", Format(effectiveFrom));
            Add(command, "$confirmed", Format(confirmedAt));
            Add(command, "$reason", reason);
            Add(command, "$kind", (int)card.Kind);
            Add(command, "$start", Format(card.StartAt));
            Add(command, "$end", Format(card.EndAt));
            Add(command, "$input", card.InputGoal);
            Add(command, "$outcome", card.OutcomeGoal);
            Add(command, "$mode", (int)card.SupervisionMode);
            Add(command, "$startReminder", card.ReminderSettings.StartReminderEnabled ? 1 : 0);
            Add(command, "$local", card.ReminderSettings.LocalDeviationMinutes);
            Add(command, "$firstMobile", card.ReminderSettings.FirstMobileDeviationMinutes);
            Add(command, "$repeat", card.ReminderSettings.MobileRepeatMinutes);
            Add(command, "$maxMobile", card.ReminderSettings.MaxMobileReminders);
            Add(command, "$sound", card.ReminderSettings.SoundEnabled ? 1 : 0);
            Add(command, "$quiet", card.ReminderSettings.QuietPresentation ? 1 : 0);
            Add(command, "$idle", card.RestSettings.IdlePromptMinutes);
            Add(command, "$totalRest", card.RestSettings.DefaultTotalRestMinutes);
            Add(command, "$templateId", card.TemplateId?.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < card.RelatedAppsOrSites.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO commitment_version_targets
                    (commitment_id,version,ordinal,kind,value)
                VALUES ($id,$version,$ordinal,$kind,$value);
                """;
            Add(command, "$id", commitmentId.ToString("D"));
            Add(command, "$version", version);
            Add(command, "$ordinal", index);
            Add(command, "$kind", (int)card.RelatedAppsOrSites[index].Kind);
            Add(command, "$value", card.RelatedAppsOrSites[index].Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < rules.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO commitment_version_rules
                    (commitment_id,version,ordinal,target_kind,target_value,classification)
                VALUES ($id,$version,$ordinal,$kind,$value,$classification);
                """;
            Add(command, "$id", commitmentId.ToString("D"));
            Add(command, "$version", version);
            Add(command, "$ordinal", index);
            Add(command, "$kind", (int)rules[index].Target.Kind);
            Add(command, "$value", rules[index].Target.Value);
            Add(command, "$classification", (int)rules[index].Classification);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                   c.sound_enabled, c.quiet_presentation, c.is_skipped, c.current_version,
                   t.kind, t.value, c.ended_early_at_utc
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

            if (!reader.IsDBNull(22))
            {
                targets!.Add(new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(22), reader.GetString(23)));
            }
        }

        if (current is not null)
        {
            results.Add(current with { RelatedAppsOrSites = targets!.ToArray() });
        }

        return results;
    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmRevisionAsync(
        CommitmentRevisionCard revision,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = """
            SELECT current_version,is_skipped,confirmed_at_utc,start_reminder_sent_at_utc,
                   offline_manually_confirmed_at_utc,ended_early_at_utc
            FROM commitments WHERE id=$id;
            """;
        Add(current, "$id", revision.CommitmentId.ToString("D"));
        await using var currentReader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await currentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return SupervisionResult<StoredCommitment>.Fail("commitment_not_found", "没有找到这条工作承诺。");
        }

        var currentVersion = currentReader.GetInt32(0);
        var isSkipped = currentReader.GetInt32(1) != 0;
        var confirmedAt = Parse(currentReader.GetString(2));
        var startReminderSentAt = NullableTime(currentReader, 3);
        var offlineManuallyConfirmedAt = NullableTime(currentReader, 4);
        var endedEarlyAt = NullableTime(currentReader, 5);
        if (currentVersion != revision.FromVersion || isSkipped || endedEarlyAt is not null)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
        }

        await currentReader.DisposeAsync().ConfigureAwait(false);
        var currentRules = await ReadActivityRulesAsync(
            connection, transaction, ActivityRuleScope.Commitment, revision.CommitmentId,
            cancellationToken).ConfigureAwait(false);
        if (!RulesEqual(currentRules, revision.Before.ActivityRules))
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
        }
        if (revision.After.Kind == CommitmentKind.Computer)
        {
            await using var conflict = connection.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = """
                SELECT 1 FROM commitments
                WHERE id <> $id AND kind=$kind AND is_skipped=0
                  AND start_at_utc < $end
                  AND COALESCE(ended_early_at_utc,end_at_utc) > $start LIMIT 1;
                """;
            Add(conflict, "$id", revision.CommitmentId.ToString("D"));
            Add(conflict, "$kind", (int)CommitmentKind.Computer);
            Add(conflict, "$start", Format(revision.After.StartAt));
            Add(conflict, "$end", Format(revision.After.EndAt));
            if (await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return SupervisionResult<StoredCommitment>.Fail(
                    "computer_commitment_conflict", "修订后的电脑监督时段与既有承诺冲突。");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE commitments SET start_at_utc=$start,end_at_utc=$end,input_goal=$input,
                    outcome_goal=$outcome,supervision_mode=$mode,
                    start_reminder_enabled=$startReminder,local_deviation_minutes=$local,
                    first_mobile_deviation_minutes=$firstMobile,mobile_repeat_minutes=$repeat,
                    max_mobile_reminders=$maxMobile,sound_enabled=$sound,quiet_presentation=$quiet,
                    idle_prompt_minutes=$idle,default_total_rest_minutes=$totalRest,
                    current_version=$version
                WHERE id=$id AND current_version=$expected AND is_skipped=0
                  AND ended_early_at_utc IS NULL
                  AND start_at_utc=$beforeStart AND end_at_utc=$beforeEnd;
                """;
            Add(update, "$id", revision.CommitmentId.ToString("D"));
            Add(update, "$start", Format(revision.After.StartAt));
            Add(update, "$end", Format(revision.After.EndAt));
            Add(update, "$input", revision.After.InputGoal);
            Add(update, "$outcome", revision.After.OutcomeGoal);
            Add(update, "$mode", (int)revision.After.SupervisionMode);
            Add(update, "$startReminder", revision.After.ReminderSettings.StartReminderEnabled ? 1 : 0);
            Add(update, "$local", revision.After.ReminderSettings.LocalDeviationMinutes);
            Add(update, "$firstMobile", revision.After.ReminderSettings.FirstMobileDeviationMinutes);
            Add(update, "$repeat", revision.After.ReminderSettings.MobileRepeatMinutes);
            Add(update, "$maxMobile", revision.After.ReminderSettings.MaxMobileReminders);
            Add(update, "$sound", revision.After.ReminderSettings.SoundEnabled ? 1 : 0);
            Add(update, "$quiet", revision.After.ReminderSettings.QuietPresentation ? 1 : 0);
            Add(update, "$idle", revision.After.RestSettings.IdlePromptMinutes);
            Add(update, "$totalRest", revision.After.RestSettings.DefaultTotalRestMinutes);
            Add(update, "$version", revision.ToVersion);
            Add(update, "$expected", revision.FromVersion);
            Add(update, "$beforeStart", Format(revision.Before.StartAt));
            Add(update, "$beforeEnd", Format(revision.Before.EndAt));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return SupervisionResult<StoredCommitment>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
        }

        await using (var deleteTargets = connection.CreateCommand())
        {
            deleteTargets.Transaction = transaction;
            deleteTargets.CommandText = "DELETE FROM commitment_targets WHERE commitment_id=$id;";
            Add(deleteTargets, "$id", revision.CommitmentId.ToString("D"));
            await deleteTargets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        for (var index = 0; index < revision.After.RelatedAppsOrSites.Count; index++)
        {
            await using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText = "INSERT INTO commitment_targets (commitment_id,ordinal,kind,value) VALUES ($id,$ordinal,$kind,$value);";
            Add(target, "$id", revision.CommitmentId.ToString("D"));
            Add(target, "$ordinal", index);
            Add(target, "$kind", (int)revision.After.RelatedAppsOrSites[index].Kind);
            Add(target, "$value", revision.After.RelatedAppsOrSites[index].Value);
            await target.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText = "DELETE FROM activity_rules WHERE scope=$scope AND scope_id=$id;";
            Add(deleteRules, "$scope", (int)ActivityRuleScope.Commitment);
            Add(deleteRules, "$id", revision.CommitmentId.ToString("D"));
            await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var rule in revision.After.ActivityRules)
        {
            await UpsertActivityRuleAsync(connection, transaction,
                new ActivityRuleBinding(ActivityRuleScope.Commitment, revision.CommitmentId, rule),
                cancellationToken).ConfigureAwait(false);
        }

        await using (var invalidateTransientState = connection.CreateCommand())
        {
            invalidateTransientState.Transaction = transaction;
            invalidateTransientState.CommandText = """
                UPDATE supervision_runtime SET
                    local_reminder_sent_at_utc=NULL,
                    reminder_marker_active=0,
                    pending_prompt=NULL,
                    unknown_prompted_for_start_utc=NULL,
                    rest_prompted_for_idle_start_utc=NULL
                WHERE commitment_id=$id;
                """;
            Add(invalidateTransientState, "$id", revision.CommitmentId.ToString("D"));
            await invalidateTransientState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCommitmentVersionAsync(connection, transaction, revision.CommitmentId,
            revision.ToVersion, revision.EffectiveFrom, revision.EffectiveFrom, revision.Reason,
            revision.After, revision.After.ActivityRules, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var updated = new StoredCommitment(
            revision.CommitmentId,
            revision.After.Kind,
            revision.After.StartAt,
            revision.After.EndAt,
            revision.After.InputGoal,
            revision.After.OutcomeGoal,
            revision.After.RelatedAppsOrSites,
            revision.After.SupervisionMode,
            revision.After.ReminderSettings,
            confirmedAt,
            startReminderSentAt,
            offlineManuallyConfirmedAt,
            revision.After.TemplateId,
            revision.After.RestSettings,
            IsSkipped: false,
            revision.ToVersion);
        return SupervisionResult<StoredCommitment>.Ok(updated);
    }

    public async Task<bool> EndEarlyAsync(
        Guid commitmentId,
        int expectedVersion,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE commitments
            SET ended_early_at_utc = COALESCE(ended_early_at_utc, $ended)
            WHERE id=$id AND current_version=$version AND is_skipped=0
              AND start_at_utc <= $ended AND end_at_utc > $ended;
            """;
        Add(command, "$ended", Format(endedAt));
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", expectedVersion);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task<bool> CancelCommitmentAsync(
        Guid commitmentId,
        int expectedVersion,
        DateTimeOffset cancelledAt,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE commitments SET is_skipped=1
                 WHERE id=$id AND current_version=$version AND is_skipped=0
                   AND ended_early_at_utc IS NULL AND end_at_utc>$now;
                """;
            Add(command, "$id", commitmentId.ToString("D"));
            Add(command, "$version", expectedVersion);
            Add(command, "$now", Format(cancelledAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                return false;
        }
        await using (var response = connection.CreateCommand())
        {
            response.Transaction = transaction;
            ConfigureResponseInsert(
                response, commitmentId, expectedVersion, "commitment_cancelled", cancelledAt, reason);
            await response.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SupervisionResult<StoredCommitment>> DeferCommitmentAsync(
        StoredCommitment current,
        CommitmentCard deferred,
        IReadOnlyList<ActivityRule> frozenRules,
        DateTimeOffset deferredAt,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (deferred.Kind == CommitmentKind.Computer)
        {
            await using var conflict = connection.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = """
                SELECT 1 FROM commitments
                 WHERE id<>$id AND kind=$kind AND is_skipped=0
                   AND start_at_utc<$end AND COALESCE(ended_early_at_utc,end_at_utc)>$start
                 LIMIT 1;
                """;
            Add(conflict, "$id", current.Id.ToString("D"));
            Add(conflict, "$kind", (int)CommitmentKind.Computer);
            Add(conflict, "$start", Format(deferred.StartAt));
            Add(conflict, "$end", Format(deferred.EndAt));
            if (await conflict.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                return SupervisionResult<StoredCommitment>.Fail(
                    "computer_commitment_conflict", "推迟后的时段与另一条电脑型承诺冲突。");
        }
        await using (var endCurrent = connection.CreateCommand())
        {
            endCurrent.Transaction = transaction;
            endCurrent.CommandText = """
                UPDATE commitments SET ended_early_at_utc=$now
                 WHERE id=$id AND current_version=$version AND is_skipped=0
                   AND ended_early_at_utc IS NULL AND start_at_utc<=$now AND end_at_utc>$now;
                """;
            Add(endCurrent, "$now", Format(deferredAt));
            Add(endCurrent, "$id", current.Id.ToString("D"));
            Add(endCurrent, "$version", current.Version);
            if (await endCurrent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                return SupervisionResult<StoredCommitment>.Fail(
                    "commitment_version_stale", "要推迟的承诺状态已经变化。");
        }
        var newId = Guid.NewGuid();
        await InsertCommitmentAsync(
            connection, transaction, newId, deferred, deferredAt, frozenRules, cancellationToken)
            .ConfigureAwait(false);
        await using (var response = connection.CreateCommand())
        {
            response.Transaction = transaction;
            ConfigureResponseInsert(
                response, current.Id, current.Version, "commitment_deferred", deferredAt,
                $"{reason}\n新承诺：{newId:D}");
            await response.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SupervisionResult<StoredCommitment>.Ok(new StoredCommitment(
            newId, deferred.Kind, deferred.StartAt, deferred.EndAt, deferred.InputGoal,
            deferred.OutcomeGoal, deferred.RelatedAppsOrSites, deferred.SupervisionMode,
            deferred.ReminderSettings, deferredAt, null, null, deferred.TemplateId,
            deferred.RestSettings, false, 1));
    }

    public Task<CommitmentHistoryView?> ReadHistoryAsync(
        Guid commitmentId,
        CancellationToken cancellationToken) =>
        ReadHistoryAsync(commitmentId, afterSnapshotStarted: null, cancellationToken);

    internal Task<CommitmentHistoryView?> ReadHistoryForTestAsync(
        Guid commitmentId,
        Func<CancellationToken, Task> afterSnapshotStarted,
        CancellationToken cancellationToken) =>
        ReadHistoryAsync(commitmentId, afterSnapshotStarted, cancellationToken);

    private async Task<CommitmentHistoryView?> ReadHistoryAsync(
        Guid commitmentId,
        Func<CancellationToken, Task>? afterSnapshotStarted,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        int? currentVersion;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT current_version FROM commitments WHERE id=$id;";
            Add(current, "$id", commitmentId.ToString("D"));
            var value = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            currentVersion = value is null or DBNull
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        if (currentVersion is null)
        {
            return null;
        }
        if (afterSnapshotStarted is not null)
        {
            await afterSnapshotStarted(cancellationToken).ConfigureAwait(false);
        }

        var versions = new List<CommitmentRevisionVersionView>();
        var rows = new List<(int Version, DateTimeOffset Effective, DateTimeOffset Confirmed,
            string Reason, CommitmentKind Kind, DateTimeOffset Start, DateTimeOffset End,
            string? Input, string? Outcome, SupervisionMode Mode, ReminderSettings Reminders,
            RestSettings RestSettings, Guid? TemplateId)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT version,effective_from_utc,confirmed_at_utc,reason,kind,start_at_utc,end_at_utc,
                    input_goal,outcome_goal,supervision_mode,start_reminder_enabled,
                    local_deviation_minutes,first_mobile_deviation_minutes,mobile_repeat_minutes,
                    max_mobile_reminders,sound_enabled,quiet_presentation,idle_prompt_minutes,
                    default_total_rest_minutes,template_id
                FROM commitment_versions WHERE commitment_id=$id ORDER BY version;
                """;
            Add(command, "$id", commitmentId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt32(0), Parse(reader.GetString(1)), Parse(reader.GetString(2)),
                    reader.GetString(3), (CommitmentKind)reader.GetInt32(4),
                    Parse(reader.GetString(5)), Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    (SupervisionMode)reader.GetInt32(9),
                    new ReminderSettings(
                        reader.GetInt32(10) != 0, reader.GetInt32(11), reader.GetInt32(12),
                        reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15) != 0,
                        reader.GetInt32(16) != 0),
                    new RestSettings(reader.GetInt32(17), reader.GetInt32(18)),
                    reader.IsDBNull(19) ? null : Guid.Parse(reader.GetString(19))));
            }
        }

        foreach (var row in rows)
        {
            var targets = await ReadVersionTargetsAsync(
                connection, transaction, commitmentId, row.Version, cancellationToken).ConfigureAwait(false);
            var rules = await ReadVersionRulesAsync(
                connection, transaction, commitmentId, row.Version, cancellationToken).ConfigureAwait(false);
            var card = new CommitmentCard(
                Guid.Empty, row.Kind, row.Start, row.End, row.Input, row.Outcome, targets,
                row.Mode, row.Reminders, "", rules, row.RestSettings, row.TemplateId);
            versions.Add(new CommitmentRevisionVersionView(
                commitmentId, row.Version, row.Effective, row.Confirmed, row.Reason, card));
        }

        var history = new CommitmentHistoryView(
            commitmentId,
            currentVersion.Value,
            versions,
            await ReadActivitySegmentsAsync(
                connection, transaction, commitmentId, cancellationToken).ConfigureAwait(false),
            await ReadRemindersAsync(
                connection, transaction, commitmentId, cancellationToken).ConfigureAwait(false),
            await ReadCorrectionsAsync(
                connection, transaction, commitmentId, recentOnly: false, cancellationToken)
                .ConfigureAwait(false),
            await ReadResponsesAsync(
                connection, transaction, commitmentId, cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return history;
    }

    public async Task<int> ReadVersionAtAsync(
        Guid commitmentId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadVersionAtAsync(
            connection, transaction: null, commitmentId, at, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadVersionAtAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version FROM commitment_versions
            WHERE commitment_id=$id AND effective_from_utc <= $at
            ORDER BY version DESC LIMIT 1;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$at", Format(at));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 1 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset> ReadVersionEffectiveFromAsync(
        Guid commitmentId,
        int version,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT effective_from_utc FROM commitment_versions
            WHERE commitment_id=$id AND version=$version;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", version);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException("The commitment version boundary is missing.");
        }
        return Parse((string)result);
    }

    private static async Task<IReadOnlyList<CommitmentTarget>> ReadVersionTargetsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        int version,
        CancellationToken cancellationToken)
    {
        var result = new List<CommitmentTarget>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind,value FROM commitment_version_targets
            WHERE commitment_id=$id AND version=$version ORDER BY ordinal;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ActivityRule>> ReadVersionRulesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        int version,
        CancellationToken cancellationToken)
    {
        var result = new List<ActivityRule>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_kind,target_value,classification FROM commitment_version_rules
            WHERE commitment_id=$id AND version=$version ORDER BY ordinal;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ActivityRule(
                new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)),
                (ActivityClassification)reader.GetInt32(2)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ActivitySegmentView>> ReadActivitySegmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        CancellationToken cancellationToken)
    {
        var result = new List<ActivitySegmentView>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,commitment_version,start_at_utc,end_at_utc,availability,target_kind,
                target_value,original_classification,effective_classification,is_idle,
                deviation_reason,corrected_at_utc,correction_note
            FROM activity_segments WHERE commitment_id=$id ORDER BY start_at_utc,id;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var target = reader.IsDBNull(5) ? null : new CommitmentTarget(
                (CommitmentTargetKind)reader.GetInt32(5), reader.GetString(6));
            result.Add(new ActivitySegmentView(
                reader.GetInt64(0), commitmentId, reader.GetInt32(1), Parse(reader.GetString(2)),
                Parse(reader.GetString(3)), (ActivityAvailability)reader.GetInt32(4), target,
                NullableEnum<ActivityClassification>(reader, 7),
                NullableEnum<ActivityClassification>(reader, 8), reader.GetInt32(9) != 0,
                NullableEnum<DeviationReason>(reader, 10), NullableTime(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ReminderNotice>> ReadRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        CancellationToken cancellationToken)
    {
        var result = new List<ReminderNotice>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT notice_id,kind,message,created_at_utc,bubble_expires_at_utc,play_sound,
                persistent_marker,commitment_version FROM reminder_notices
            WHERE commitment_id=$id ORDER BY created_at_utc,rowid;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ReminderNotice(
                commitmentId, reader.GetString(2), Parse(reader.GetString(3)),
                (ReminderKind)reader.GetInt32(1), Guid.Parse(reader.GetString(0)),
                NullableTime(reader, 4), reader.GetInt32(5) != 0, reader.GetInt32(6) != 0,
                reader.GetInt32(7)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SupervisionResponseView>> ReadResponsesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        CancellationToken cancellationToken)
    {
        var result = new List<SupervisionResponseView>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,commitment_version,kind,recorded_at_utc,note FROM supervision_responses
            WHERE commitment_id=$id ORDER BY recorded_at_utc,id;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SupervisionResponseView(
                reader.GetInt64(0), commitmentId, reader.GetInt32(1), reader.GetString(2),
                Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmOfflineStartedAsync(
        Guid commitmentId,
        int expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE commitments
                SET offline_manually_confirmed_at_utc = COALESCE(offline_manually_confirmed_at_utc, $at)
                WHERE id = $id AND current_version=$version AND is_skipped=0;
                """;
            Add(update, "$at", Format(now));
            Add(update, "$id", commitmentId.ToString("D"));
            Add(update, "$version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return SupervisionResult<StoredCommitment>.Fail(
                    "commitment_version_stale", "工作承诺已经变化，请按当前版本重新操作。");
            }
        }
        await using (var response = connection.CreateCommand())
        {
            response.Transaction = transaction;
            ConfigureResponseInsert(
                response, commitmentId, expectedVersion, "offline_started", now, null);
            await response.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SupervisionResult<StoredCommitment>.Ok(commitment with
        {
            OfflineManuallyConfirmedAt = commitment.OfflineManuallyConfirmedAt ?? now
        });
    }

    public async Task<bool> MarkStartReminderSentAsync(
        Guid id,
        int expectedVersion,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await AssertCurrentVersionAsync(
                connection, transaction, id, expectedVersion, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE commitments SET start_reminder_sent_at_utc = COALESCE(start_reminder_sent_at_utc, $at)
            WHERE id = $id;
            """;
        Add(update, "$at", Format(at));
        Add(update, "$id", id.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PersistStartReminderAsync(
        ReminderNotice notice,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await AssertCurrentVersionAsync(
                connection, transaction, notice.CommitmentId, notice.CommitmentVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }
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
        return true;
    }

    public async Task<bool> SaveActivityRuleAsync(
        ActivityRuleBinding binding,
        int? expectedCommitmentVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (binding.Scope == ActivityRuleScope.Commitment &&
            (expectedCommitmentVersion is null ||
             !await AssertCurrentVersionAsync(
                 connection, transaction, binding.ScopeId!.Value,
                 expectedCommitmentVersion.Value, cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }
        await UpsertActivityRuleAsync(connection, transaction, binding, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
        return await ReadActivityRulesAsync(
            connection, transaction: null, scope, scopeId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ActivityRule>> ReadActivityRulesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActivityRuleScope scope,
        Guid? scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static bool RulesEqual(
        IReadOnlyList<ActivityRule> left,
        IReadOnlyList<ActivityRule> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Classification == pair.Second.Classification &&
            pair.First.Target.Kind == pair.Second.Target.Kind &&
            string.Equals(
                Key(pair.First.Target.Kind, pair.First.Target.Value),
                Key(pair.Second.Target.Kind, pair.Second.Target.Value),
                StringComparison.Ordinal));

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

    public async Task AppendActivitySegmentAsync(
        Guid commitmentId,
        int commitmentVersion,
        ActivityObservation observation,
        CommitmentTarget? target,
        ActivityClassification? classification,
        bool isIdle,
        DeviationReason? deviationReason,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        if (endAt <= startAt)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await AppendActivitySegmentAsync(
            connection, transaction, commitmentId, commitmentVersion, observation, target,
            classification, isIdle, deviationReason, startAt, endAt, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PersistActivitySegmentAndRuntimeAsync(
        Guid commitmentId,
        int expectedVersion,
        int commitmentVersion,
        ActivityObservation observation,
        CommitmentTarget? target,
        ActivityClassification? classification,
        bool isIdle,
        DeviationReason? deviationReason,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        StoredSupervisionRuntime state,
        IReadOnlyList<ReminderNotice> notices,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await AssertCurrentVersionAsync(
                connection, transaction, commitmentId, expectedVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }
        await AppendActivitySegmentAsync(
            connection, transaction, commitmentId, commitmentVersion, observation, target,
            classification, isIdle, deviationReason, startAt, endAt, cancellationToken)
            .ConfigureAwait(false);
        await using var runtime = connection.CreateCommand();
        runtime.Transaction = transaction;
        ConfigureRuntimeWrite(runtime, state);
        await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var notice in notices)
        {
            await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PersistRuntimeAndRemindersAsync(
        StoredSupervisionRuntime state,
        int expectedVersion,
        IReadOnlyList<ReminderNotice> notices,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await AssertCurrentVersionAsync(
                connection, transaction, state.CommitmentId, expectedVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }
        await using (var runtime = connection.CreateCommand())
        {
            runtime.Transaction = transaction;
            ConfigureRuntimeWrite(runtime, state);
            await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var notice in notices)
        {
            await InsertReminderAsync(connection, transaction, notice, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task AppendActivitySegmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int commitmentVersion,
        ActivityObservation observation,
        CommitmentTarget? target,
        ActivityClassification? classification,
        bool isIdle,
        DeviationReason? deviationReason,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {

        var pieces = new List<(int Version, DateTimeOffset StartAt, DateTimeOffset EndAt)>();
        var currentVersion = commitmentVersion;
        var currentStart = startAt;
        await using (var boundaries = connection.CreateCommand())
        {
            boundaries.Transaction = transaction;
            boundaries.CommandText = """
                SELECT version,effective_from_utc FROM commitment_versions
                WHERE commitment_id=$id AND effective_from_utc > $start AND effective_from_utc < $end
                ORDER BY effective_from_utc,version;
                """;
            Add(boundaries, "$id", commitmentId.ToString("D"));
            Add(boundaries, "$start", Format(startAt));
            Add(boundaries, "$end", Format(endAt));
            await using var reader = await boundaries.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var boundary = Parse(reader.GetString(1));
                pieces.Add((currentVersion, currentStart, boundary));
                currentVersion = reader.GetInt32(0);
                currentStart = boundary;
            }
        }
        pieces.Add((currentVersion, currentStart, endAt));

        foreach (var piece in pieces)
        {
            var pieceClassification = piece.StartAt == startAt
                ? classification
                : await ResolveVersionClassificationAsync(
                    connection, transaction, commitmentId, piece.Version, target,
                    cancellationToken).ConfigureAwait(false);
            var pieceDeviationReason = isIdle
                ? deviationReason
                : pieceClassification switch
                {
                    ActivityClassification.Related => null,
                    ActivityClassification.Distracting => DeviationReason.DistractingActivity,
                    ActivityClassification.Unknown => DeviationReason.UnknownActivity,
                    _ => deviationReason
                };
            await MergeOrInsertActivitySegmentAsync(
                connection, transaction, commitmentId, piece.Version, observation, target,
                pieceClassification, isIdle, pieceDeviationReason, piece.StartAt, piece.EndAt,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ActivityClassification?> ResolveVersionClassificationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int commitmentVersion,
        CommitmentTarget? target,
        CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return ActivityClassification.Unknown;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_value,classification,priority FROM (
                SELECT target_value,classification,1 AS priority FROM commitment_version_rules
                WHERE commitment_id=$id AND version=$version AND target_kind=$kind
                UNION ALL
                SELECT value,$related,2 FROM commitment_version_targets
                WHERE commitment_id=$id AND version=$version AND kind=$kind
                UNION ALL
                SELECT ar.target_value,ar.classification,3
                FROM commitment_versions cv
                JOIN activity_rules ar ON ar.scope=$templateScope AND ar.scope_id=cv.template_id
                WHERE cv.commitment_id=$id AND cv.version=$version AND ar.target_kind=$kind
                UNION ALL
                SELECT target_value,classification,4 FROM activity_rules
                WHERE scope=$globalScope AND scope_id IS NULL AND target_kind=$kind
            ) ORDER BY priority;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", commitmentVersion);
        Add(command, "$kind", (int)target.Kind);
        Add(command, "$related", (int)ActivityClassification.Related);
        Add(command, "$templateScope", (int)ActivityRuleScope.Template);
        Add(command, "$globalScope", (int)ActivityRuleScope.Global);
        var targetKey = Key(target.Kind, target.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(
                    Key(target.Kind, reader.GetString(0)), targetKey, StringComparison.Ordinal))
            {
                return (ActivityClassification)reader.GetInt32(1);
            }
        }
        return ActivityClassification.Unknown;
    }

    private static async Task MergeOrInsertActivitySegmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int commitmentVersion,
        ActivityObservation observation,
        CommitmentTarget? target,
        ActivityClassification? classification,
        bool isIdle,
        DeviationReason? deviationReason,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken)
    {
        await using var merge = connection.CreateCommand();
        merge.Transaction = transaction;
        merge.CommandText = """
            UPDATE activity_segments SET end_at_utc=$end
            WHERE id=(
                SELECT id FROM activity_segments
                WHERE commitment_id=$id ORDER BY id DESC LIMIT 1
            )
              AND commitment_version=$version
              AND end_at_utc=$start
              AND availability=$availability
              AND target_kind IS $kind
              AND ((target_value IS NULL AND $value IS NULL) OR upper(target_value)=upper($value))
              AND original_classification IS $classification
              AND effective_classification IS $classification
              AND is_idle=$idle
              AND deviation_reason IS $reason
              AND corrected_at_utc IS NULL;
            """;
        Add(merge, "$id", commitmentId.ToString("D"));
        Add(merge, "$version", commitmentVersion);
        Add(merge, "$start", Format(startAt));
        Add(merge, "$end", Format(endAt));
        Add(merge, "$availability", (int)observation.Availability);
        Add(merge, "$kind", target is null ? null : (int)target.Kind);
        Add(merge, "$value", target?.Value);
        Add(merge, "$classification", classification is null ? null : (int)classification);
        Add(merge, "$idle", isIdle ? 1 : 0);
        Add(merge, "$reason", deviationReason is null ? null : (int)deviationReason);
        if (await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
            INSERT INTO activity_segments (
                commitment_id,commitment_version,start_at_utc,end_at_utc,availability,
                target_kind,target_value,original_classification,effective_classification,
                is_idle,deviation_reason)
            VALUES ($id,$version,$start,$end,$availability,$kind,$value,$classification,
                $classification,$idle,$reason);
            """;
            Add(insert, "$id", commitmentId.ToString("D"));
            Add(insert, "$version", commitmentVersion);
            Add(insert, "$start", Format(startAt));
            Add(insert, "$end", Format(endAt));
            Add(insert, "$availability", (int)observation.Availability);
            Add(insert, "$kind", target is null ? null : (int)target.Kind);
            Add(insert, "$value", target?.Value);
            Add(insert, "$classification", classification is null ? null : (int)classification);
            Add(insert, "$idle", isIdle ? 1 : 0);
            Add(insert, "$reason", deviationReason is null ? null : (int)deviationReason);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long?> CorrectActivitySegmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int commitmentVersion,
        CommitmentTarget target,
        DateTimeOffset effectiveFrom,
        ActivityClassification correctedClassification,
        DateTimeOffset correctedAt,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT id FROM activity_segments
            WHERE commitment_id=$id AND commitment_version=$version
              AND target_kind=$kind AND upper(target_value)=upper($value)
              AND start_at_utc <= $effective AND end_at_utc > $effective
            ORDER BY id DESC LIMIT 1;
            """;
        Add(find, "$id", commitmentId.ToString("D"));
        Add(find, "$version", commitmentVersion);
        Add(find, "$kind", (int)target.Kind);
        Add(find, "$value", target.Value);
        Add(find, "$effective", Format(effectiveFrom));
        var scalar = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is null or DBNull)
        {
            return null;
        }

        var segmentId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE activity_segments SET effective_classification=$classification,
                corrected_at_utc=$at,correction_note=$note WHERE id=$segment;
            """;
        Add(update, "$classification", (int)correctedClassification);
        Add(update, "$at", Format(correctedAt));
        Add(update, "$note", note);
        Add(update, "$segment", segmentId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return segmentId;
    }

    public async Task AppendResponseAsync(
        Guid commitmentId,
        int commitmentVersion,
        string kind,
        DateTimeOffset recordedAt,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        ConfigureResponseInsert(command, commitmentId, commitmentVersion, kind, recordedAt, note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PersistRuntimeAndResponseAsync(
        StoredSupervisionRuntime state,
        int commitmentVersion,
        string kind,
        DateTimeOffset recordedAt,
        string? note,
        CancellationToken cancellationToken,
        string? sourceEventId = null,
        string? sourceEventOutcome = null)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await TryClaimSupervisionEventAsync(
                connection, transaction, sourceEventId, sourceEventOutcome, recordedAt, cancellationToken)
            .ConfigureAwait(false))
            return true;
        if (!await AssertCurrentVersionAsync(
                connection, transaction, state.CommitmentId, commitmentVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }
        await using (var runtime = connection.CreateCommand())
        {
            runtime.Transaction = transaction;
            ConfigureRuntimeWrite(runtime, state);
            await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var response = connection.CreateCommand())
        {
            response.Transaction = transaction;
            ConfigureResponseInsert(
                response, state.CommitmentId, commitmentVersion, kind, recordedAt, note);
            await response.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void ConfigureResponseInsert(
        SqliteCommand command,
        Guid commitmentId,
        int commitmentVersion,
        string kind,
        DateTimeOffset recordedAt,
        string? note)
    {
        command.CommandText = """
            INSERT INTO supervision_responses
                (commitment_id,commitment_version,kind,recorded_at_utc,note)
            VALUES ($id,$version,$kind,$at,$note);
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", commitmentVersion);
        Add(command, "$kind", kind);
        Add(command, "$at", Format(recordedAt));
        Add(command, "$note", note);
    }

    public async Task<bool> PersistClassificationAsync(
        IReadOnlyList<ActivityRuleBinding> bindings,
        ActivityCorrectionView correction,
        int expectedVersion,
        PendingActivitySegment? pendingSegment,
        StoredSupervisionRuntime state,
        ReminderNotice? notice,
        CancellationToken cancellationToken,
        string? sourceEventId = null,
        string? sourceEventOutcome = null)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await TryClaimSupervisionEventAsync(
                connection, transaction, sourceEventId, sourceEventOutcome, correction.CorrectedAt,
                cancellationToken)
            .ConfigureAwait(false))
            return true;
        if (!await AssertCurrentVersionAsync(
                connection, transaction, state.CommitmentId, expectedVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }
        foreach (var binding in bindings)
        {
            await UpsertActivityRuleAsync(connection, transaction, binding, cancellationToken)
                .ConfigureAwait(false);
        }
        if (pendingSegment is not null && pendingSegment.EndAt > pendingSegment.StartAt)
        {
            var pendingVersion = await ReadVersionAtAsync(
                connection, transaction, state.CommitmentId, pendingSegment.StartAt,
                cancellationToken).ConfigureAwait(false);
            await AppendActivitySegmentAsync(
                connection, transaction, state.CommitmentId, pendingVersion,
                ObservationFrom(pendingSegment), pendingSegment.Target,
                pendingSegment.OriginalClassification, pendingSegment.IsIdle,
                pendingSegment.DeviationReason, pendingSegment.StartAt, pendingSegment.EndAt,
                cancellationToken).ConfigureAwait(false);
        }
        var segmentId = await CorrectActivitySegmentAsync(
            connection, transaction, state.CommitmentId, correction.CommitmentVersion,
            correction.Target, correction.EffectiveFrom, correction.CorrectedClassification,
            correction.CorrectedAt, correction.Note, cancellationToken).ConfigureAwait(false);
        correction = correction with { ActivitySegmentId = segmentId };
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
        return true;
    }

    private static ActivityObservation ObservationFrom(PendingActivitySegment segment) => new(
        segment.Availability,
        !segment.IsIdle,
        segment.Target.Kind == CommitmentTargetKind.Application ? segment.Target.Value : null,
        segment.EndAt,
        segment.Target.Kind == CommitmentTargetKind.Website ? segment.Target.Value : null);

    internal async Task PersistClassificationForTestAsync(
        IReadOnlyList<ActivityRuleBinding> bindings,
        ActivityCorrectionView correction,
        PendingActivitySegment? pendingSegment,
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
        if (pendingSegment is not null && pendingSegment.EndAt > pendingSegment.StartAt)
        {
            var pendingVersion = await ReadVersionAtAsync(
                connection, transaction, state.CommitmentId, pendingSegment.StartAt,
                cancellationToken).ConfigureAwait(false);
            await AppendActivitySegmentAsync(
                connection, transaction, state.CommitmentId, pendingVersion,
                ObservationFrom(pendingSegment), pendingSegment.Target,
                pendingSegment.OriginalClassification, pendingSegment.IsIdle,
                pendingSegment.DeviationReason, pendingSegment.StartAt, pendingSegment.EndAt,
                cancellationToken).ConfigureAwait(false);
        }
        var segmentId = await CorrectActivitySegmentAsync(
            connection, transaction, state.CommitmentId, correction.CommitmentVersion,
            correction.Target, correction.EffectiveFrom, correction.CorrectedClassification,
            correction.CorrectedAt, correction.Note, cancellationToken).ConfigureAwait(false);
        correction = correction with { ActivitySegmentId = segmentId };
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
                corrected_classification, effective_from_utc, corrected_at_utc, scope, note,
                commitment_version, activity_segment_id)
            VALUES ($id, $kind, $value, $original, $corrected, $effective, $at, $scope, $note,
                $version, $segment);
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
        Add(command, "$version", correction.CommitmentVersion);
        Add(command, "$segment", correction.ActivitySegmentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ActivityCorrectionView>> ReadCorrectionsAsync(
        Guid commitmentId,
        CancellationToken cancellationToken) =>
        ReadCorrectionsAsync(commitmentId, recentOnly: false, cancellationToken);

    public Task<IReadOnlyList<ActivityCorrectionView>> ReadRecentCorrectionsAsync(
        Guid commitmentId,
        CancellationToken cancellationToken) =>
        ReadCorrectionsAsync(commitmentId, recentOnly: true, cancellationToken);

    private async Task<IReadOnlyList<ActivityCorrectionView>> ReadCorrectionsAsync(
        Guid commitmentId,
        bool recentOnly,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCorrectionsAsync(
            connection, transaction: null, commitmentId, recentOnly, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ActivityCorrectionView>> ReadCorrectionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commitmentId,
        bool recentOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = recentOnly ? """
            SELECT target_kind, target_value, original_classification, corrected_classification,
                   effective_from_utc, corrected_at_utc, scope, note, commitment_version,
                   activity_segment_id
            FROM activity_corrections WHERE commitment_id = $id ORDER BY id DESC LIMIT 20;
            """ : """
            SELECT target_kind, target_value, original_classification, corrected_classification,
                   effective_from_utc, corrected_at_utc, scope, note, commitment_version,
                   activity_segment_id
            FROM activity_corrections WHERE commitment_id = $id ORDER BY id;
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
                (ActivityRuleScope)reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt64(9)));
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
                bubble_expires_at_utc, play_sound, persistent_marker, commitment_version)
            VALUES ($notice, $commitment, $kind, $message, $created, $expires, $sound, $marker,
                $version);
            """;
        Add(command, "$notice", notice.NoticeId.ToString("D"));
        Add(command, "$commitment", notice.CommitmentId.ToString("D"));
        Add(command, "$kind", (int)notice.Kind);
        Add(command, "$message", notice.Message);
        Add(command, "$created", Format(notice.CreatedAt));
        Add(command, "$expires", FormatNullable(notice.BubbleExpiresAt));
        Add(command, "$sound", notice.PlaySound ? 1 : 0);
        Add(command, "$marker", notice.PersistentMarker ? 1 : 0);
        Add(command, "$version", notice.CommitmentVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReminderNotice?> ReadLatestReminderAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT notice_id, commitment_id, kind, message, created_at_utc,
                   bubble_expires_at_utc, play_sound, persistent_marker, commitment_version
            FROM reminder_notices ORDER BY created_at_utc DESC, rowid DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReminderNotice(
                Guid.Parse(reader.GetString(1)), reader.GetString(3), Parse(reader.GetString(4)),
                (ReminderKind)reader.GetInt32(2), Guid.Parse(reader.GetString(0)),
                NullableTime(reader, 5), reader.GetInt32(6) != 0, reader.GetInt32(7) != 0,
                reader.GetInt32(8))
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

    private static async Task<bool> AssertCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commitmentId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM commitments
            WHERE id=$id AND current_version=$version AND is_skipped=0
              AND ended_early_at_utc IS NULL;
            """;
        Add(command, "$id", commitmentId.ToString("D"));
        Add(command, "$version", expectedVersion);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> TryClaimSupervisionEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? sourceEventId,
        string? sourceEventOutcome,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId)) return true;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processed_supervision_events(event_id,processed_at_utc,outcome_text)
            VALUES($id,$at,$outcome) ON CONFLICT(event_id) DO NOTHING;
            """;
        Add(command, "$id", sourceEventId);
        Add(command, "$at", Format(processedAt));
        Add(command, "$outcome", sourceEventOutcome ?? "操作已处理");
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
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
            reader.GetInt32(20) != 0,
            reader.GetInt32(21),
            NullableTime(reader, 24));

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
