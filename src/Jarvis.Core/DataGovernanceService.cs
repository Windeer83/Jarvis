using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed class DataGovernanceService
{
    private const int MaximumTimelineRows = 5000;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private DataDeletionCard? _deletionCandidate;

    public DataGovernanceService(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task<DataGovernanceStatusView> ReadStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT retention_days,last_retention_at_utc FROM data_governance_settings WHERE singleton=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return DataGovernanceStatusView.Default;
        return new(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)));
    }

    public async Task SetRetentionDaysAsync(int days, CancellationToken cancellationToken)
    {
        if (days is < 7 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days), "明细保留天数必须在 7–3650 天之间。");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE data_governance_settings SET retention_days=$days WHERE singleton=1;";
        command.Parameters.AddWithValue("$days", days);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyRetentionIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.LastRetentionAppliedAt is { } last && now - last < TimeSpan.FromHours(20))
            return;

        var cutoff = now.AddDays(-status.DetailedTimelineRetentionDays);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ArchiveExpiredSegmentsAsync(
            connection, transaction, cutoff, now, cancellationToken).ConfigureAwait(false);
        await AccumulateCountAsync(
            connection, transaction, "reminder_notices", "created_at_utc", "reminder_count", cutoff, now,
            cancellationToken).ConfigureAwait(false);
        await AccumulateCountAsync(
            connection, transaction, "supervision_responses", "recorded_at_utc", "response_count", cutoff, now,
            cancellationToken).ConfigureAwait(false);
        foreach (var statement in new[]
                 {
                     "DELETE FROM activity_corrections WHERE corrected_at_utc < $cutoff;",
                     "DELETE FROM reminder_notices WHERE created_at_utc < $cutoff;",
                     "DELETE FROM supervision_responses WHERE recorded_at_utc < $cutoff;",
                     "DELETE FROM activity_segments WHERE end_at_utc < $cutoff;"
                 })
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken, ("$cutoff", Format(cutoff)))
                .ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction,
            "UPDATE data_governance_settings SET last_retention_at_utc=$now WHERE singleton=1;",
            cancellationToken, ("$now", Format(now))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataRangeView> QueryRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        ValidateRange(startDate, endDate);
        var (start, endExclusive) = Range(startDate, endDate);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var timeline = new List<DataTimelineEntryView>();
        await ReadSegmentsAsync(connection, start, endExclusive, timeline, cancellationToken).ConfigureAwait(false);
        await ReadTimelineFactsAsync(connection, start, endExclusive, timeline, cancellationToken).ConfigureAwait(false);
        var ordered = timeline.OrderBy(item => item.At).Take(MaximumTimelineRows).ToArray();
        var summaries = await ReadSummariesAsync(connection, startDate, endDate, cancellationToken)
            .ConfigureAwait(false);
        var commitments = await ReadCommitmentsAsync(connection, start, endExclusive, cancellationToken)
            .ConfigureAwait(false);
        return new(startDate, endDate, ordered, summaries, commitments, timeline.Count > MaximumTimelineRows);
    }

    public async Task ExportRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        string destinationPath,
        string password,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (password.Length < 12)
            throw new ArgumentException("导出密码至少需要 12 个字符。", nameof(password));
        var fullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出路径不能覆盖正在使用的 Jarvis 数据库。", nameof(destinationPath));
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("请选择明确的导出目录。", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var data = await QueryRangeAsync(startDate, endDate, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Format = "Jarvis supervision export",
            Version = 1,
            ExportedAt = now,
            Scope = "Only commitments, reviews, supervision timeline and daily summaries. No credentials, AI keys, chat history, screenshots or growth context.",
            Data = data
        }, CoreProtocol.Json);
        var encrypted = EncryptedDataExport.Encrypt(payload, password);
        var temporary = fullPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, fullPath, overwrite: true);
    }

    public async Task<DataDeletionCard> PrepareDeletionAsync(
        DateOnly startDate,
        DateOnly endDate,
        DataDeletionScope scope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateRange(startDate, endDate);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == DataDeletionScope.AllSupervisionRecords && endDate >= DateOnly.FromDateTime(now.LocalDateTime))
            throw new InvalidOperationException("删除全部监督记录只能选择已经结束的日期，不能包含今天或未来安排。");
        var count = await CountDeletionAsync(startDate, endDate, scope, cancellationToken).ConfigureAwait(false);
        var phrase = $"永久删除 {startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}";
        _deletionCandidate = new(
            Guid.NewGuid(), startDate, endDate, scope, count, phrase, now.AddMinutes(10), ScopeDescription(scope));
        return _deletionCandidate;
    }

    public async Task<int> ConfirmDeletionAsync(
        Guid candidateId,
        string confirmationPhrase,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = _deletionCandidate;
        if (candidate is null || candidate.CandidateId != candidateId || now >= candidate.ExpiresAt)
            throw new InvalidOperationException("永久删除候选已经过期，请重新预览删除范围。");
        if (!string.Equals(candidate.ConfirmationPhrase, confirmationPhrase.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"请输入完整确认短语：{candidate.ConfirmationPhrase}");

        var (start, endExclusive) = Range(candidate.StartDate, candidate.EndDate);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var deleted = 0;
        deleted += await DeleteTimelineAsync(connection, transaction, start, endExclusive, cancellationToken)
            .ConfigureAwait(false);
        if (candidate.Scope is DataDeletionScope.TimelineAndDailySummaries or DataDeletionScope.AllSupervisionRecords)
        {
            deleted += await ExecuteAsync(connection, transaction,
                "DELETE FROM daily_activity_summaries WHERE local_date >= $startDate AND local_date <= $endDate;",
                cancellationToken,
                ("$startDate", candidate.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$endDate", candidate.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
        }
        if (candidate.Scope == DataDeletionScope.AllSupervisionRecords)
        {
            deleted += await DeleteAllRecordsAsync(connection, transaction, start, endExclusive, candidate, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _deletionCandidate = null;
        return deleted;
    }

    private async Task<int> CountDeletionAsync(
        DateOnly startDate,
        DateOnly endDate,
        DataDeletionScope scope,
        CancellationToken cancellationToken)
    {
        var (start, endExclusive) = Range(startDate, endDate);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var total = 0;
        foreach (var (table, column) in TimelineTables)
            total += await CountAsync(connection, table, column, start, endExclusive, cancellationToken).ConfigureAwait(false);
        if (scope is DataDeletionScope.TimelineAndDailySummaries or DataDeletionScope.AllSupervisionRecords)
            total += await CountDateAsync(connection, "daily_activity_summaries", "local_date", startDate, endDate, cancellationToken)
                .ConfigureAwait(false);
        if (scope == DataDeletionScope.AllSupervisionRecords)
        {
            total += await CountAsync(connection, "commitments", "start_at_utc", start, endExclusive, cancellationToken)
                .ConfigureAwait(false);
            total += await CountDateAsync(connection, "daily_review_sessions", "review_date", startDate, endDate, cancellationToken)
                .ConfigureAwait(false);
            total += await CountDateAsync(connection, "cycle_review_sessions", "period_end", startDate, endDate, cancellationToken)
                .ConfigureAwait(false);
            total += await CountDateAsync(connection, "ai_review_drafts", "period_end", startDate, endDate, cancellationToken)
                .ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<int> DeleteTimelineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var (table, column) in TimelineTables)
        {
            total += await ExecuteAsync(connection, transaction,
                $"DELETE FROM {table} WHERE {column} >= $start AND {column} < $end;",
                cancellationToken, ("$start", Format(start)), ("$end", Format(end))).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<int> DeleteAllRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset start,
        DateTimeOffset end,
        DataDeletionCard candidate,
        CancellationToken cancellationToken)
    {
        var total = 0;
        await ExecuteAsync(connection, transaction, """
            DELETE FROM recurrence_occurrences
             WHERE commitment_id IN (
                   SELECT id FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end);
            """, cancellationToken, ("$start", Format(start)), ("$end", Format(end))).ConfigureAwait(false);
        total += await ExecuteAsync(connection, transaction,
            "DELETE FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end;",
            cancellationToken, ("$start", Format(start)), ("$end", Format(end))).ConfigureAwait(false);
        total += await ExecuteAsync(connection, transaction,
            "DELETE FROM daily_review_sessions WHERE review_date >= $startDate AND review_date <= $endDate;",
            cancellationToken,
            ("$startDate", candidate.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$endDate", candidate.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        total += await ExecuteAsync(connection, transaction,
            "DELETE FROM cycle_review_sessions WHERE period_end >= $startDate AND period_end <= $endDate;",
            cancellationToken,
            ("$startDate", candidate.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$endDate", candidate.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        total += await ExecuteAsync(connection, transaction,
            "DELETE FROM ai_review_drafts WHERE period_end >= $startDate AND period_end <= $endDate;",
            cancellationToken,
            ("$startDate", candidate.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$endDate", candidate.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return total;
    }

    private static async Task ReadSegmentsAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        List<DataTimelineEntryView> destination,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT commitment_id,commitment_version,start_at_utc,end_at_utc,availability,
                   target_kind,target_value,effective_classification,is_idle
              FROM activity_segments
             WHERE start_at_utc < $end AND end_at_utc >= $start
             ORDER BY start_at_utc LIMIT 5001;
            """;
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var target = reader.IsDBNull(5) ? null : $"{(CommitmentTargetKind)reader.GetInt32(5)}:{reader.GetString(6)}";
            var classification = reader.IsDBNull(7) ? "未分类" : ((ActivityClassification)reader.GetInt32(7)).ToString();
            destination.Add(new(
                ParseTimestamp(reader.GetString(2)), ParseTimestamp(reader.GetString(3)), "活动区段",
                $"{target ?? "无法观察"} · {classification}{(reader.GetBoolean(8) ? " · 空闲" : "")}",
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1)));
        }
    }

    private static async Task ReadTimelineFactsAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        List<DataTimelineEntryView> destination,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT created_at_utc,'提醒',message,commitment_id,commitment_version FROM reminder_notices
             WHERE created_at_utc >= $start AND created_at_utc < $end
            UNION ALL
            SELECT recorded_at_utc,'回应',kind || CASE WHEN note IS NULL THEN '' ELSE ' · ' || note END,
                   commitment_id,commitment_version FROM supervision_responses
             WHERE recorded_at_utc >= $start AND recorded_at_utc < $end
            UNION ALL
            SELECT corrected_at_utc,'分类纠正',target_value || ' · ' || original_classification || '→' || corrected_classification,
                   commitment_id,commitment_version FROM activity_corrections
             WHERE corrected_at_utc >= $start AND corrected_at_utc < $end
            ORDER BY 1 LIMIT 5001;
            """;
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            destination.Add(new(
                ParseTimestamp(reader.GetString(0)), null, reader.GetString(1), reader.GetString(2),
                Guid.Parse(reader.GetString(3)), reader.GetInt32(4)));
    }

    private static async Task<IReadOnlyList<DailyActivitySummaryView>> ReadSummariesAsync(
        SqliteConnection connection,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var result = new List<DailyActivitySummaryView>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_date,observed_seconds,related_seconds,distracting_seconds,unknown_seconds,
                   unobservable_seconds,idle_seconds,reminder_count,response_count
              FROM daily_activity_summaries
             WHERE local_date >= $start AND local_date <= $end ORDER BY local_date;
            """;
        command.Parameters.AddWithValue("$start", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetInt32(7), reader.GetInt32(8)));
        return result;
    }

    private static async Task<IReadOnlyList<DataCommitmentRecordView>> ReadCommitmentsAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var result = new List<DataCommitmentRecordView>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.current_version,c.kind,c.start_at_utc,c.end_at_utc,c.input_goal,c.outcome_goal,
                   c.is_skipped,r.raw_text,r.assessment
              FROM commitments c LEFT JOIN commitment_reviews r ON r.commitment_id=c.id
             WHERE c.start_at_utc < $end AND c.end_at_utc >= $start ORDER BY c.start_at_utc;
            """;
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1), (CommitmentKind)reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : (CompletionAssessment?)reader.GetInt32(9)));
        return result;
    }

    private static async Task AccumulateCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string timestampColumn,
        string summaryColumn,
        DateTimeOffset cutoff,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO daily_activity_summaries (
                local_date,observed_seconds,related_seconds,distracting_seconds,unknown_seconds,
                unobservable_seconds,idle_seconds,reminder_count,response_count,updated_at_utc)
            SELECT date({timestampColumn},'localtime'),0,0,0,0,0,0,
                   {(summaryColumn == "reminder_count" ? "COUNT(*)" : "0")},
                   {(summaryColumn == "response_count" ? "COUNT(*)" : "0")},$now
              FROM {table} WHERE {timestampColumn} < $cutoff
             GROUP BY date({timestampColumn},'localtime')
            ON CONFLICT(local_date) DO UPDATE SET
                {summaryColumn}={summaryColumn}+excluded.{summaryColumn},
                updated_at_utc=excluded.updated_at_utc;
            """, cancellationToken, ("$now", Format(now)), ("$cutoff", Format(cutoff))).ConfigureAwait(false);

    private static async Task ArchiveExpiredSegmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset cutoff,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var segments = new List<ArchivedSegment>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT start_at_utc,end_at_utc,availability,effective_classification,is_idle
                  FROM activity_segments WHERE end_at_utc < $cutoff ORDER BY start_at_utc;
                """;
            command.Parameters.AddWithValue("$cutoff", Format(cutoff));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                segments.Add(new(
                    ParseTimestamp(reader.GetString(0)),
                    ParseTimestamp(reader.GetString(1)),
                    (ActivityAvailability)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : (ActivityClassification?)reader.GetInt32(3),
                    reader.GetBoolean(4)));
        }

        foreach (var segment in segments)
        {
            var cursor = segment.StartAt;
            while (cursor < segment.EndAt)
            {
                var localCursor = cursor.ToLocalTime();
                var nextLocalDate = DateOnly.FromDateTime(localCursor.DateTime).AddDays(1);
                var nextLocalMidnight = new DateTimeOffset(
                    DateTime.SpecifyKind(nextLocalDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
                var sliceEnd = nextLocalMidnight < segment.EndAt ? nextLocalMidnight : segment.EndAt;
                var seconds = Math.Max(0, (sliceEnd - cursor).TotalSeconds);
                await UpsertDailySegmentAsync(
                    connection, transaction, DateOnly.FromDateTime(localCursor.DateTime), seconds,
                    segment, now, cancellationToken).ConfigureAwait(false);
                cursor = sliceEnd;
            }
        }
    }

    private static Task<int> UpsertDailySegmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly date,
        double seconds,
        ArchivedSegment segment,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO daily_activity_summaries (
                local_date,observed_seconds,related_seconds,distracting_seconds,unknown_seconds,
                unobservable_seconds,idle_seconds,reminder_count,response_count,updated_at_utc)
            VALUES($date,$observed,$related,$distracting,$unknown,$unobservable,$idle,0,0,$now)
            ON CONFLICT(local_date) DO UPDATE SET
                observed_seconds=observed_seconds+excluded.observed_seconds,
                related_seconds=related_seconds+excluded.related_seconds,
                distracting_seconds=distracting_seconds+excluded.distracting_seconds,
                unknown_seconds=unknown_seconds+excluded.unknown_seconds,
                unobservable_seconds=unobservable_seconds+excluded.unobservable_seconds,
                idle_seconds=idle_seconds+excluded.idle_seconds,
                updated_at_utc=excluded.updated_at_utc;
            """, cancellationToken,
            ("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$observed", seconds),
            ("$related", segment.Classification == ActivityClassification.Related ? seconds : 0d),
            ("$distracting", segment.Classification == ActivityClassification.Distracting ? seconds : 0d),
            ("$unknown", segment.Classification == ActivityClassification.Unknown ? seconds : 0d),
            ("$unobservable", segment.Availability == ActivityAvailability.Unobservable ? seconds : 0d),
            ("$idle", segment.IsIdle ? seconds : 0d),
            ("$now", Format(now)));

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string table,
        string column,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} >= $start AND {column} < $end;";
        command.Parameters.AddWithValue("$start", Format(start));
        command.Parameters.AddWithValue("$end", Format(end));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountDateAsync(
        SqliteConnection connection,
        string table,
        string column,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} >= $start AND {column} <= $end;";
        command.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void ValidateRange(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate) throw new ArgumentException("结束日期不能早于开始日期。");
        if (endDate.DayNumber - startDate.DayNumber > 366)
            throw new ArgumentException("一次最多查看、导出或删除 367 个自然日。");
    }

    private static (DateTimeOffset Start, DateTimeOffset EndExclusive) Range(DateOnly startDate, DateOnly endDate)
    {
        var start = new DateTimeOffset(DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var end = new DateTimeOffset(DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        return (start, end);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ScopeDescription(DataDeletionScope scope) => scope switch
    {
        DataDeletionScope.DetailedTimelineOnly => "只删除活动区段、提醒、纠正与监督回应；保留每日汇总、承诺与复盘。",
        DataDeletionScope.TimelineAndDailySummaries => "删除详细监督时间线和对应每日汇总；保留承诺、修订、结果与复盘。",
        _ => "删除所选日期内的监督时间线、每日汇总、承诺与复盘；不删除凭据、聊天或范围外数据。"
    };

    private static readonly (string Table, string Column)[] TimelineTables =
    [
        ("activity_segments", "start_at_utc"),
        ("reminder_notices", "created_at_utc"),
        ("supervision_responses", "recorded_at_utc"),
        ("activity_corrections", "corrected_at_utc")
    ];

    private sealed record ArchivedSegment(
        DateTimeOffset StartAt,
        DateTimeOffset EndAt,
        ActivityAvailability Availability,
        ActivityClassification? Classification,
        bool IsIdle);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal static class EncryptedDataExport
{
    private const int Iterations = 210_000;

    public static byte[] Encrypt(byte[] payload, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, payload, ciphertext, tag);
        CryptographicOperations.ZeroMemory(key);
        return JsonSerializer.SerializeToUtf8Bytes(new ExportEnvelope(
            "JARVIS-EXPORT", 1, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext)));
    }

    internal static byte[] Decrypt(byte[] envelopeBytes, string password)
    {
        var envelope = JsonSerializer.Deserialize<ExportEnvelope>(envelopeBytes) ??
                       throw new InvalidDataException("导出文件格式无效。");
        if (envelope.Magic != "JARVIS-EXPORT" || envelope.Version != 1)
            throw new InvalidDataException("导出文件版本不受支持。");
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, envelope.Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, tag.Length)) aes.Decrypt(nonce, ciphertext, tag, plaintext);
        CryptographicOperations.ZeroMemory(key);
        return plaintext;
    }

    private sealed record ExportEnvelope(
        string Magic,
        int Version,
        int Iterations,
        string Salt,
        string Nonce,
        string Tag,
        string Ciphertext);
}
