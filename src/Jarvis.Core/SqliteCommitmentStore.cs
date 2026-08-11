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
    DateTimeOffset? OfflineManuallyConfirmedAt);

internal sealed class SqliteCommitmentStore
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

        if (version > 1)
        {
            throw new InvalidOperationException($"数据库版本 {version} 高于当前程序支持的版本 1。");
        }

        if (version == 1)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmAsync(
        CommitmentCard card,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (card.Kind == CommitmentKind.Computer)
        {
            await using var conflictCommand = connection.CreateCommand();
            conflictCommand.Transaction = transaction;
            conflictCommand.CommandText = """
                SELECT COALESCE(input_goal, outcome_goal), start_at_utc, end_at_utc
                FROM commitments
                WHERE kind = $kind
                  AND start_at_utc < $end
                  AND end_at_utc > $start
                LIMIT 1;
                """;
            conflictCommand.Parameters.AddWithValue("$kind", (int)CommitmentKind.Computer);
            conflictCommand.Parameters.AddWithValue("$start", Format(card.StartAt));
            conflictCommand.Parameters.AddWithValue("$end", Format(card.EndAt));

            await using var conflictReader = await conflictCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await conflictReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingGoal = conflictReader.GetString(0);
                var existingStart = Parse(conflictReader.GetString(1)).ToLocalTime();
                var existingEnd = Parse(conflictReader.GetString(2)).ToLocalTime();
                return SupervisionResult<StoredCommitment>.Fail(
                    "computer_commitment_conflict",
                    $"与电脑型承诺“{existingGoal}”冲突（{existingStart:g}–{existingEnd:t}），请调整时间后重新预览。");
            }
        }

        var id = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO commitments (
                    id, kind, start_at_utc, end_at_utc, input_goal, outcome_goal,
                    supervision_mode, start_reminder_enabled,
                    local_deviation_minutes, first_mobile_deviation_minutes,
                    mobile_repeat_minutes, max_mobile_reminders,
                    confirmed_at_utc)
                VALUES (
                    $id, $kind, $start, $end, $inputGoal, $outcomeGoal,
                    $mode, $startReminderEnabled,
                    $localMinutes, $firstMobileMinutes,
                    $repeatMinutes, $maxMobileReminders,
                    $confirmedAt);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$kind", (int)card.Kind);
            insert.Parameters.AddWithValue("$start", Format(card.StartAt));
            insert.Parameters.AddWithValue("$end", Format(card.EndAt));
            insert.Parameters.AddWithValue("$inputGoal", (object?)card.InputGoal ?? DBNull.Value);
            insert.Parameters.AddWithValue("$outcomeGoal", (object?)card.OutcomeGoal ?? DBNull.Value);
            insert.Parameters.AddWithValue("$mode", (int)card.SupervisionMode);
            insert.Parameters.AddWithValue("$startReminderEnabled", card.ReminderSettings.StartReminderEnabled ? 1 : 0);
            insert.Parameters.AddWithValue("$localMinutes", card.ReminderSettings.LocalDeviationMinutes);
            insert.Parameters.AddWithValue("$firstMobileMinutes", card.ReminderSettings.FirstMobileDeviationMinutes);
            insert.Parameters.AddWithValue("$repeatMinutes", card.ReminderSettings.MobileRepeatMinutes);
            insert.Parameters.AddWithValue("$maxMobileReminders", card.ReminderSettings.MaxMobileReminders);
            insert.Parameters.AddWithValue("$confirmedAt", Format(confirmedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < card.RelatedAppsOrSites.Count; index++)
        {
            await using var targetInsert = connection.CreateCommand();
            targetInsert.Transaction = transaction;
            targetInsert.CommandText = """
                INSERT INTO commitment_targets (commitment_id, ordinal, kind, value)
                VALUES ($commitmentId, $ordinal, $kind, $value);
                """;
            targetInsert.Parameters.AddWithValue("$commitmentId", id.ToString("D"));
            targetInsert.Parameters.AddWithValue("$ordinal", index);
            targetInsert.Parameters.AddWithValue("$kind", (int)card.RelatedAppsOrSites[index].Kind);
            targetInsert.Parameters.AddWithValue("$value", card.RelatedAppsOrSites[index].Value);
            await targetInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var stored = new StoredCommitment(
            id,
            card.Kind,
            card.StartAt,
            card.EndAt,
            card.InputGoal,
            card.OutcomeGoal,
            card.RelatedAppsOrSites,
            card.SupervisionMode,
            card.ReminderSettings,
            confirmedAt,
            null,
            null);
        return SupervisionResult<StoredCommitment>.Ok(stored);
    }

    public async Task<IReadOnlyList<StoredCommitment>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var commitments = new List<StoredCommitment>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.id, c.kind, c.start_at_utc, c.end_at_utc, c.input_goal, c.outcome_goal,
                c.supervision_mode, c.start_reminder_enabled,
                c.local_deviation_minutes, c.first_mobile_deviation_minutes,
                c.mobile_repeat_minutes, c.max_mobile_reminders,
                c.confirmed_at_utc, c.start_reminder_sent_at_utc,
                c.offline_manually_confirmed_at_utc,
                t.kind, t.value
            FROM commitments c
            LEFT JOIN commitment_targets t ON t.commitment_id = c.id
            ORDER BY c.start_at_utc, c.id, t.ordinal;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        StoredCommitment? current = null;
        List<CommitmentTarget>? targets = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (current is null || current.Id != id)
            {
                if (current is not null)
                {
                    commitments.Add(current with { RelatedAppsOrSites = targets!.ToArray() });
                }

                targets = [];
                current = ReadStoredCommitment(reader, targets);
            }

            if (!reader.IsDBNull(15))
            {
                targets!.Add(new CommitmentTarget(
                    (CommitmentTargetKind)reader.GetInt32(15),
                    reader.GetString(16)));
            }
        }

        if (current is not null)
        {
            commitments.Add(current with { RelatedAppsOrSites = targets!.ToArray() });
        }

        return commitments;
    }

    public async Task<SupervisionResult<StoredCommitment>> ConfirmOfflineStartedAsync(
        Guid commitmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var commitment = all.SingleOrDefault(candidate => candidate.Id == commitmentId);
        if (commitment is null)
        {
            return SupervisionResult<StoredCommitment>.Fail("commitment_not_found", "没有找到这条工作承诺。");
        }

        if (commitment.Kind != CommitmentKind.Offline)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "manual_confirmation_not_allowed",
                "电脑型工作承诺由活动证据监督，不能使用线下开始确认。");
        }

        if (now < commitment.StartAt || now >= commitment.EndAt)
        {
            return SupervisionResult<StoredCommitment>.Fail(
                "offline_commitment_not_active",
                "只能在线下工作承诺的计划时段内确认开始。");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE commitments
            SET offline_manually_confirmed_at_utc = COALESCE(offline_manually_confirmed_at_utc, $confirmedAt)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$confirmedAt", Format(now));
        command.Parameters.AddWithValue("$id", commitmentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var updated = commitment with
        {
            OfflineManuallyConfirmedAt = commitment.OfflineManuallyConfirmedAt ?? now
        };
        return SupervisionResult<StoredCommitment>.Ok(updated);
    }

    public async Task MarkStartReminderSentAsync(
        Guid commitmentId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE commitments
            SET start_reminder_sent_at_utc = COALESCE(start_reminder_sent_at_utc, $sentAt)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$sentAt", Format(sentAt));
        command.Parameters.AddWithValue("$id", commitmentId.ToString("D"));
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

    private static StoredCommitment ReadStoredCommitment(
        SqliteDataReader reader,
        IReadOnlyList<CommitmentTarget> targets) => new(
        Guid.Parse(reader.GetString(0)),
        (CommitmentKind)reader.GetInt32(1),
        Parse(reader.GetString(2)),
        Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        targets,
        (SupervisionMode)reader.GetInt32(6),
        new ReminderSettings(
            reader.GetInt32(7) != 0,
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11)),
        Parse(reader.GetString(12)),
        reader.IsDBNull(13) ? null : Parse(reader.GetString(13)),
        reader.IsDBNull(14) ? null : Parse(reader.GetString(14)));

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
