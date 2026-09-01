using System.Globalization;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed partial class SqliteCommitmentStore
{
    private const string PlanningSchema = """
            CREATE TABLE IF NOT EXISTS commitment_templates (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                duration_minutes INTEGER NOT NULL,
                input_goal TEXT NULL,
                outcome_goal TEXT NULL,
                supervision_mode INTEGER NOT NULL,
                start_reminder_enabled INTEGER NOT NULL,
                local_deviation_minutes INTEGER NOT NULL,
                first_mobile_deviation_minutes INTEGER NOT NULL,
                mobile_repeat_minutes INTEGER NOT NULL,
                max_mobile_reminders INTEGER NOT NULL,
                sound_enabled INTEGER NOT NULL DEFAULT 1,
                quiet_presentation INTEGER NOT NULL DEFAULT 0,
                rest_idle_prompt_minutes INTEGER NOT NULL,
                rest_total_minutes INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                archived_at_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS template_targets (
                template_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (template_id, ordinal),
                FOREIGN KEY (template_id) REFERENCES commitment_templates(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS recurrence_plans (
                id TEXT PRIMARY KEY,
                template_id TEXT NULL,
                kind INTEGER NOT NULL,
                start_date TEXT NULL,
                end_date TEXT NULL,
                confirmed_at_utc TEXT NOT NULL,
                FOREIGN KEY (template_id) REFERENCES commitment_templates(id)
            );

            CREATE TABLE IF NOT EXISTS recurrence_weekdays (
                plan_id TEXT NOT NULL,
                weekday INTEGER NOT NULL,
                PRIMARY KEY (plan_id, weekday),
                FOREIGN KEY (plan_id) REFERENCES recurrence_plans(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS recurrence_selected_dates (
                plan_id TEXT NOT NULL,
                selected_date TEXT NOT NULL,
                PRIMARY KEY (plan_id, selected_date),
                FOREIGN KEY (plan_id) REFERENCES recurrence_plans(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS recurrence_occurrences (
                plan_id TEXT NOT NULL,
                commitment_id TEXT NOT NULL UNIQUE,
                occurrence_date TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY (plan_id, ordinal),
                FOREIGN KEY (plan_id) REFERENCES recurrence_plans(id) ON DELETE CASCADE,
                FOREIGN KEY (commitment_id) REFERENCES commitments(id)
            );

            CREATE INDEX IF NOT EXISTS ix_recurrence_occurrences_commitment
                ON recurrence_occurrences(commitment_id);
            """;

    public async Task<CommitmentTemplateView> SaveTemplateAsync(
        Guid? templateId,
        CommitmentTemplateView template,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var id = templateId ?? template.Id;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = templateId is null
                ? """
                    INSERT INTO commitment_templates (
                        id, name, kind, duration_minutes, input_goal, outcome_goal, supervision_mode,
                        start_reminder_enabled, local_deviation_minutes, first_mobile_deviation_minutes,
                        mobile_repeat_minutes, max_mobile_reminders, sound_enabled,
                        quiet_presentation, rest_idle_prompt_minutes,
                        rest_total_minutes, created_at_utc, updated_at_utc)
                    VALUES ($id, $name, $kind, $duration, $input, $outcome, $mode,
                        $startReminder, $local, $firstMobile, $repeat, $maxMobile, $sound,
                        $quiet, $idle,
                        $total, $created, $updated);
                    """
                : """
                    UPDATE commitment_templates SET
                        name=$name, kind=$kind, duration_minutes=$duration, input_goal=$input,
                        outcome_goal=$outcome, supervision_mode=$mode,
                        start_reminder_enabled=$startReminder, local_deviation_minutes=$local,
                        first_mobile_deviation_minutes=$firstMobile, mobile_repeat_minutes=$repeat,
                        max_mobile_reminders=$maxMobile, sound_enabled=$sound,
                        quiet_presentation=$quiet, rest_idle_prompt_minutes=$idle,
                        rest_total_minutes=$total, updated_at_utc=$updated
                    WHERE id=$id AND archived_at_utc IS NULL;
                    """;
            AddTemplateParameters(command, id, template);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                throw new KeyNotFoundException("Template not found or archived.");
            }
        }

        await ReplaceTemplateChildrenAsync(connection, transaction, id, template, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return template with { Id = id };
    }

    public async Task<CommitmentTemplateView?> ReadTemplateAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var templates = await ReadTemplatesAsync(includeArchived: true, cancellationToken)
            .ConfigureAwait(false);
        return templates.SingleOrDefault(template => template.Id == id);
    }

    public async Task<IReadOnlyList<CommitmentTemplateView>> ReadTemplatesAsync(
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(Guid Id, string Name, CommitmentKind Kind, int Duration,
            string? Input, string? Outcome, SupervisionMode Mode, ReminderSettings Reminders,
            RestSettings RestSettings, DateTimeOffset Created, DateTimeOffset Updated, bool Archived)>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, duration_minutes, input_goal, outcome_goal, supervision_mode,
                   start_reminder_enabled, local_deviation_minutes, first_mobile_deviation_minutes,
                   mobile_repeat_minutes, max_mobile_reminders, rest_idle_prompt_minutes,
                   rest_total_minutes, created_at_utc, updated_at_utc, archived_at_utc,
                   sound_enabled, quiet_presentation
            FROM commitment_templates
            WHERE $includeArchived = 1 OR archived_at_utc IS NULL
            ORDER BY archived_at_utc IS NOT NULL, name, id;
            """;
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                (CommitmentKind)reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                (SupervisionMode)reader.GetInt32(6),
                new ReminderSettings(reader.GetInt32(7) != 0, reader.GetInt32(8), reader.GetInt32(9),
                    reader.GetInt32(10), reader.GetInt32(11),
                    reader.GetInt32(17) != 0, reader.GetInt32(18) != 0),
                new RestSettings(reader.GetInt32(12), reader.GetInt32(13)),
                ParseInstant(reader.GetString(14)),
                ParseInstant(reader.GetString(15)),
                !reader.IsDBNull(16)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        var templates = new List<CommitmentTemplateView>();
        foreach (var row in rows)
        {
            templates.Add(new CommitmentTemplateView(
                row.Id, row.Name, row.Kind, row.Duration, row.Input, row.Outcome,
                await ReadTargetsAsync(connection, "template_targets", "template_id", row.Id, cancellationToken)
                    .ConfigureAwait(false),
                row.Mode, row.Reminders,
                await ReadScopedRulesAsync(
                        connection, ActivityRuleScope.Template, row.Id, cancellationToken)
                    .ConfigureAwait(false),
                row.RestSettings, row.Created, row.Updated, row.Archived));
        }

        return templates;
    }

    public async Task<CommitmentTemplateView?> ArchiveTemplateAsync(
        Guid id,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE commitment_templates
            SET archived_at_utc = COALESCE(archived_at_utc, $at), updated_at_utc=$at
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$at", FormatInstant(archivedAt));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 0 ? null : await ReadTemplateAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupervisionResult<RecurrencePlanView>> ConfirmRecurrenceAsync(
        RecurrenceCard card,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var conflict = await FindConflictDateAsync(connection, transaction, card.Occurrences, [], cancellationToken)
            .ConfigureAwait(false);
        if (conflict is not null)
        {
            return SupervisionResult<RecurrencePlanView>.Fail(
                "recurrence_computer_conflict",
                $"重复安排在 {conflict:yyyy-MM-dd} 存在电脑监督时间冲突，本批次未保存。");
        }

        var planId = Guid.NewGuid();
        var templateId = card.Occurrences.Select(item => item.TemplateId).Distinct().SingleOrDefault();
        await using (var planInsert = connection.CreateCommand())
        {
            planInsert.Transaction = transaction;
            planInsert.CommandText = """
                INSERT INTO recurrence_plans
                    (id, template_id, kind, start_date, end_date, confirmed_at_utc)
                VALUES ($id, $templateId, $kind, $start, $end, $confirmed);
                """;
            planInsert.Parameters.AddWithValue("$id", planId.ToString("D"));
            planInsert.Parameters.AddWithValue("$templateId", templateId?.ToString("D") ?? (object)DBNull.Value);
            planInsert.Parameters.AddWithValue("$kind", (int)card.Pattern.Kind);
            planInsert.Parameters.AddWithValue("$start", card.Pattern.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            planInsert.Parameters.AddWithValue("$end", card.Pattern.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            planInsert.Parameters.AddWithValue("$confirmed", FormatInstant(confirmedAt));
            await planInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var weekday in card.Pattern.Weekdays ?? [])
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO recurrence_weekdays (plan_id,weekday) VALUES ($id,$value);";
            insert.Parameters.AddWithValue("$id", planId.ToString("D"));
            insert.Parameters.AddWithValue("$value", (int)weekday);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var date in card.Pattern.SelectedDates ?? [])
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO recurrence_selected_dates (plan_id,selected_date) VALUES ($id,$value);";
            insert.Parameters.AddWithValue("$id", planId.ToString("D"));
            insert.Parameters.AddWithValue("$value", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var occurrences = new List<RecurrenceOccurrenceView>();
        for (var index = 0; index < card.Occurrences.Count; index++)
        {
            var occurrence = card.Occurrences[index];
            var commitmentId = Guid.NewGuid();
            await InsertCommitmentAsync(
                    connection, transaction, commitmentId, occurrence, confirmedAt,
                    occurrence.ActivityRules, cancellationToken)
                .ConfigureAwait(false);
            var date = DateOnly.FromDateTime(occurrence.StartAt.DateTime);
            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                INSERT INTO recurrence_occurrences
                    (plan_id,commitment_id,occurrence_date,ordinal)
                VALUES ($planId,$commitmentId,$date,$ordinal);
                """;
            link.Parameters.AddWithValue("$planId", planId.ToString("D"));
            link.Parameters.AddWithValue("$commitmentId", commitmentId.ToString("D"));
            link.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            link.Parameters.AddWithValue("$ordinal", index);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            occurrences.Add(new RecurrenceOccurrenceView(
                commitmentId, date, occurrence.StartAt, occurrence.EndAt, RecurrenceOccurrenceStatus.Active));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SupervisionResult<RecurrencePlanView>.Ok(new RecurrencePlanView(
            planId, templateId, card.Pattern, occurrences, confirmedAt));
    }

    public async Task<IReadOnlyList<RecurrencePlanView>> ReadPlansAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var planRows = new List<(Guid Id, Guid? TemplateId, RecurrenceKind Kind, DateOnly? Start,
            DateOnly? End, DateTimeOffset Confirmed)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,template_id,kind,start_date,end_date,confirmed_at_utc FROM recurrence_plans ORDER BY confirmed_at_utc,id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                planRows.Add((
                    Guid.Parse(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    (RecurrenceKind)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ParseInstant(reader.GetString(5))));
            }
        }

        var result = new List<RecurrencePlanView>();
        foreach (var row in planRows)
        {
            var weekdays = await ReadWeekdaysAsync(connection, row.Id, cancellationToken).ConfigureAwait(false);
            var selected = await ReadSelectedDatesAsync(connection, row.Id, cancellationToken).ConfigureAwait(false);
            var occurrences = await ReadOccurrencesAsync(connection, row.Id, cancellationToken).ConfigureAwait(false);
            result.Add(new RecurrencePlanView(
                row.Id,
                row.TemplateId,
                new RecurrencePattern(row.Kind, row.Start, row.End, weekdays, selected),
                occurrences,
                row.Confirmed));
        }

        return result;
    }

    public async Task<SupervisionResult<RecurrenceChangeCard>> PrepareRecurrenceChangeAsync(
        RecurrenceChangeRequest request,
        IReadOnlyList<StoredCommitment> commitments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var prepared = await BuildRecurrenceChangeAsync(request, commitments, now, cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.Success || prepared.Value is null)
        {
            return SupervisionResult<RecurrenceChangeCard>.Fail(
                prepared.ErrorCode!, prepared.Message!);
        }

        var previews = prepared.Value.Affected.Select((occurrence, index) =>
        {
            var revised = prepared.Value.Revised[index];
            return new RecurrenceChangeOccurrencePreview(
                occurrence.CommitmentId,
                occurrence.Date,
                occurrence.StartAt,
                occurrence.EndAt,
                occurrence.Status,
                revised.StartAt,
                revised.EndAt,
                request.Kind == RecurrenceChangeKind.Skip
                    ? RecurrenceOccurrenceStatus.Skipped
                    : occurrence.Status,
                commitments.Single(item => item.Id == occurrence.CommitmentId).Version,
                request.Kind == RecurrenceChangeKind.Adjust
                    ? commitments.Single(item => item.Id == occurrence.CommitmentId).Version + 1
                    : commitments.Single(item => item.Id == occurrence.CommitmentId).Version);
        }).ToArray();
        return SupervisionResult<RecurrenceChangeCard>.Ok(new RecurrenceChangeCard(
            Guid.NewGuid(),
            request.PlanId,
            request.Kind,
            request.Scope,
            previews,
            $"尚未写入；确认后将影响 {previews.Length} 个未来发生项。",
            request.Kind == RecurrenceChangeKind.Adjust ? request.Reason?.Trim() : null));
    }

    public async Task<SupervisionResult<RecurrencePlanView>> ChangeRecurrenceAsync(
        RecurrenceChangeRequest request,
        RecurrenceChangeCard expectedCard,
        IReadOnlyList<StoredCommitment> commitments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var prepared = await BuildRecurrenceChangeAsync(request, commitments, now, cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.Success || prepared.Value is null)
        {
            return SupervisionResult<RecurrencePlanView>.Fail(
                prepared.ErrorCode!, prepared.Message!);
        }

        var plan = prepared.Value.Plan;
        var affected = prepared.Value.Affected;
        var affectedIds = affected.Select(item => item.CommitmentId).ToHashSet();
        var revised = prepared.Value.Revised;

        if (expectedCard.PlanId != request.PlanId || expectedCard.Kind != request.Kind ||
            expectedCard.Scope != request.Scope || expectedCard.AffectedOccurrences.Count != affected.Length)
        {
            return SupervisionResult<RecurrencePlanView>.Fail(
                "commitment_version_stale", "重复发生项已经变化，请重新预览整个修改。");
        }

        var expectedById = expectedCard.AffectedOccurrences.ToDictionary(item => item.CommitmentId);
        for (var index = 0; index < affected.Length; index++)
        {
            var occurrence = affected[index];
            var existing = commitments.Single(item => item.Id == occurrence.CommitmentId);
            if (!expectedById.TryGetValue(occurrence.CommitmentId, out var expected) ||
                expected.BeforeVersion != existing.Version ||
                expected.BeforeStartAt != occurrence.StartAt ||
                expected.BeforeEndAt != occurrence.EndAt ||
                expected.BeforeStatus != occurrence.Status ||
                expected.AfterStartAt != revised[index].StartAt ||
                expected.AfterEndAt != revised[index].EndAt ||
                expected.AfterStatus != (request.Kind == RecurrenceChangeKind.Skip
                    ? RecurrenceOccurrenceStatus.Skipped
                    : occurrence.Status))
            {
                return SupervisionResult<RecurrencePlanView>.Fail(
                    "commitment_version_stale", "重复发生项已经变化，请重新预览整个修改。");
            }
        }

        var replacements = affected.Select((occurrence, index) =>
            new RecurrenceOccurrenceView(
                occurrence.CommitmentId,
                occurrence.Date,
                revised[index].StartAt,
                revised[index].EndAt,
                request.Kind == RecurrenceChangeKind.Skip
                    ? RecurrenceOccurrenceStatus.Skipped
                    : occurrence.Status))
            .ToDictionary(item => item.CommitmentId);
        var updated = plan with
        {
            Occurrences = plan.Occurrences
                .Select(item => replacements.GetValueOrDefault(item.CommitmentId, item))
                .ToArray()
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (request.Kind == RecurrenceChangeKind.Adjust)
        {
            var conflict = await FindConflictDateAsync(connection, transaction, revised, affectedIds, cancellationToken)
                .ConfigureAwait(false);
            if (conflict is not null)
            {
                return SupervisionResult<RecurrencePlanView>.Fail(
                    "recurrence_computer_conflict", $"调整后的 {conflict:yyyy-MM-dd} 存在电脑监督时间冲突，未保存任何修改。");
            }
        }

        for (var index = 0; index < affected.Length; index++)
        {
            var expected = expectedById[affected[index].CommitmentId];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (request.Kind == RecurrenceChangeKind.Skip)
            {
                command.CommandText = """
                    UPDATE commitments SET is_skipped=1
                    WHERE id=$id AND current_version=$version AND is_skipped=0
                      AND ended_early_at_utc IS NULL
                      AND start_at_utc=$beforeStart AND end_at_utc=$beforeEnd;
                    """;
            }
            else
            {
                command.CommandText = """
                    UPDATE commitments SET start_at_utc=$start,end_at_utc=$end,
                        current_version=current_version+1
                    WHERE id=$id AND current_version=$version AND is_skipped=0
                      AND ended_early_at_utc IS NULL
                      AND start_at_utc=$beforeStart AND end_at_utc=$beforeEnd;
                    """;
                command.Parameters.AddWithValue("$start", FormatInstant(revised[index].StartAt));
                command.Parameters.AddWithValue("$end", FormatInstant(revised[index].EndAt));
            }

            command.Parameters.AddWithValue("$id", affected[index].CommitmentId.ToString("D"));
            command.Parameters.AddWithValue("$version", expected.BeforeVersion);
            command.Parameters.AddWithValue("$beforeStart", FormatInstant(expected.BeforeStartAt));
            command.Parameters.AddWithValue("$beforeEnd", FormatInstant(expected.BeforeEndAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return SupervisionResult<RecurrencePlanView>.Fail(
                    "commitment_version_stale", "重复发生项已经变化，请重新预览整个修改。");
            }
            if (request.Kind == RecurrenceChangeKind.Adjust)
            {
                var existing = commitments.Single(item => item.Id == affected[index].CommitmentId);
                await SqliteCommitmentStore.InsertCommitmentVersionAsync(
                    connection, transaction, existing.Id, expected.BeforeVersion + 1, now, now,
                    request.Reason!.Trim(), revised[index],
                    await ReadScopedRulesAsync(
                        connection, ActivityRuleScope.Commitment, existing.Id, cancellationToken)
                        .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SupervisionResult<RecurrencePlanView>.Ok(updated);
    }

    private async Task<SupervisionResult<PreparedRecurrenceChange>> BuildRecurrenceChangeAsync(
        RecurrenceChangeRequest request,
        IReadOnlyList<StoredCommitment> commitments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var plan = (await ReadPlansAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Id == request.PlanId);
        if (plan is null)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail("recurrence_plan_not_found", "没有找到重复安排。");
        }

        var anchorIndex = plan.Occurrences.ToList().FindIndex(item => item.CommitmentId == request.AnchorCommitmentId);
        if (anchorIndex < 0)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail("recurrence_occurrence_not_found", "所选发生项不属于该重复安排。");
        }

        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Scope))
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail("recurrence_change_invalid", "重复安排修改类型或作用范围无效。");
        }

        var scoped = request.Scope switch
        {
            RecurrenceChangeScope.ThisOccurrence => plan.Occurrences.Skip(anchorIndex).Take(1).ToArray(),
            RecurrenceChangeScope.ThisAndFuture => plan.Occurrences.Skip(anchorIndex).ToArray(),
            RecurrenceChangeScope.EntirePlan => plan.Occurrences.ToArray(),
            _ => []
        };
        var anchorOccurrence = plan.Occurrences[anchorIndex];
        if (anchorOccurrence.StartAt <= now ||
            anchorOccurrence.Status == RecurrenceOccurrenceStatus.Skipped)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail(
                "recurrence_history_immutable",
                "已经开始、结束或跳过的发生项属于历史记录，不能再被覆盖修改。");
        }

        var affected = scoped.Where(item =>
            item.StartAt > now && item.Status == RecurrenceOccurrenceStatus.Active).ToArray();
        if (affected.Length == 0)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail(
                "recurrence_history_immutable",
                "该作用范围内没有可修改的未来发生项。");
        }

        if (request.Kind == RecurrenceChangeKind.Adjust && request.NewStartAt is null && request.NewDurationMinutes is null)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail("recurrence_adjustment_required", "调整需要新的开始时间或持续时长。");
        }

        if (request.Kind == RecurrenceChangeKind.Adjust && string.IsNullOrWhiteSpace(request.Reason))
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail(
                "revision_reason_required", "调整重复发生项必须保存自然语言原因。");
        }

        if (request.NewDurationMinutes is <= 0)
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail("duration_invalid", "持续时长必须大于 0 分钟。");
        }

        var anchor = plan.Occurrences[anchorIndex];
        var delta = request.NewStartAt is null ? TimeSpan.Zero : request.NewStartAt.Value - anchor.StartAt;
        var revised = affected.Select(item =>
        {
            var existing = commitments.Single(value => value.Id == item.CommitmentId);
            var start = item.StartAt + delta;
            var end = request.NewDurationMinutes is { } duration
                ? start.AddMinutes(duration)
                : item.EndAt + delta;
            return new CommitmentCard(
                Guid.Empty, existing.Kind, start, end, existing.InputGoal, existing.OutcomeGoal,
                existing.RelatedAppsOrSites, existing.SupervisionMode, existing.ReminderSettings, "",
                [], existing.RestSettings, existing.TemplateId);
        }).ToArray();
        if (revised.Any(card => card.StartAt <= now))
        {
            return SupervisionResult<PreparedRecurrenceChange>.Fail(
                "recurrence_history_immutable",
                "调整结果不能把任何发生项移动到已经开始的时间。");
        }

        return SupervisionResult<PreparedRecurrenceChange>.Ok(new PreparedRecurrenceChange(
            plan, affected, revised));
    }

    private sealed record PreparedRecurrenceChange(
        RecurrencePlanView Plan,
        RecurrenceOccurrenceView[] Affected,
        CommitmentCard[] Revised);

    private static async Task<IReadOnlyList<DayOfWeek>> ReadWeekdaysAsync(
        SqliteConnection connection, Guid planId, CancellationToken cancellationToken)
    {
        var values = new List<DayOfWeek>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT weekday FROM recurrence_weekdays WHERE plan_id=$id ORDER BY weekday;";
        command.Parameters.AddWithValue("$id", planId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add((DayOfWeek)reader.GetInt32(0));
        }

        return values;
    }

    private static async Task<IReadOnlyList<DateOnly>> ReadSelectedDatesAsync(
        SqliteConnection connection, Guid planId, CancellationToken cancellationToken)
    {
        var values = new List<DateOnly>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT selected_date FROM recurrence_selected_dates WHERE plan_id=$id ORDER BY selected_date;";
        command.Parameters.AddWithValue("$id", planId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return values;
    }

    private static async Task<IReadOnlyList<RecurrenceOccurrenceView>> ReadOccurrencesAsync(
        SqliteConnection connection, Guid planId, CancellationToken cancellationToken)
    {
        var values = new List<RecurrenceOccurrenceView>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ro.commitment_id,ro.occurrence_date,c.start_at_utc,c.end_at_utc,
                   c.is_skipped
            FROM recurrence_occurrences ro
            JOIN commitments c ON c.id=ro.commitment_id
            WHERE ro.plan_id=$id ORDER BY ro.ordinal;
            """;
        command.Parameters.AddWithValue("$id", planId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new RecurrenceOccurrenceView(
                Guid.Parse(reader.GetString(0)),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParseInstant(reader.GetString(2)),
                ParseInstant(reader.GetString(3)),
                reader.GetInt32(4) == 0 ? RecurrenceOccurrenceStatus.Active : RecurrenceOccurrenceStatus.Skipped));
        }

        return values;
    }

    private static async Task ReplaceTemplateChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CommitmentTemplateView template,
        CancellationToken cancellationToken)
    {
        await using (var deleteTargets = connection.CreateCommand())
        {
            deleteTargets.Transaction = transaction;
            deleteTargets.CommandText = "DELETE FROM template_targets WHERE template_id=$id;";
            deleteTargets.Parameters.AddWithValue("$id", id.ToString("D"));
            await deleteTargets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText = "DELETE FROM activity_rules WHERE scope=$scope AND scope_id=$id;";
            deleteRules.Parameters.AddWithValue("$scope", (int)ActivityRuleScope.Template);
            deleteRules.Parameters.AddWithValue("$id", id.ToString("D"));
            await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertTargetsAsync(connection, transaction, "template_targets", "template_id", id,
            template.RelatedAppsOrSites, cancellationToken).ConfigureAwait(false);
        foreach (var rule in template.ActivityRules)
        {
            await UpsertActivityRuleAsync(
                    connection,
                    transaction,
                    new ActivityRuleBinding(ActivityRuleScope.Template, id, rule),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void AddTemplateParameters(
        SqliteCommand command,
        Guid id,
        CommitmentTemplateView template)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", template.Name);
        command.Parameters.AddWithValue("$kind", (int)template.Kind);
        command.Parameters.AddWithValue("$duration", template.DurationMinutes);
        command.Parameters.AddWithValue("$input", (object?)template.InputGoal ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", (object?)template.OutcomeGoal ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)template.SupervisionMode);
        command.Parameters.AddWithValue("$startReminder", template.ReminderSettings.StartReminderEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$local", template.ReminderSettings.LocalDeviationMinutes);
        command.Parameters.AddWithValue("$firstMobile", template.ReminderSettings.FirstMobileDeviationMinutes);
        command.Parameters.AddWithValue("$repeat", template.ReminderSettings.MobileRepeatMinutes);
        command.Parameters.AddWithValue("$maxMobile", template.ReminderSettings.MaxMobileReminders);
        command.Parameters.AddWithValue("$sound", template.ReminderSettings.SoundEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$quiet", template.ReminderSettings.QuietPresentation ? 1 : 0);
        command.Parameters.AddWithValue("$idle", template.RestSettings.IdlePromptMinutes);
        command.Parameters.AddWithValue("$total", template.RestSettings.DefaultTotalRestMinutes);
        command.Parameters.AddWithValue("$created", FormatInstant(template.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatInstant(template.UpdatedAt));
    }

    private static async Task InsertTargetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string ownerColumn,
        Guid ownerId,
        IReadOnlyList<CommitmentTarget> targets,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < targets.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} ({ownerColumn}, ordinal, kind, value) VALUES ($id,$ordinal,$kind,$value);";
            command.Parameters.AddWithValue("$id", ownerId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$kind", (int)targets[index].Kind);
            command.Parameters.AddWithValue("$value", targets[index].Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<CommitmentTarget>> ReadTargetsAsync(
        SqliteConnection connection,
        string table,
        string ownerColumn,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var result = new List<CommitmentTarget>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT kind,value FROM {table} WHERE {ownerColumn}=$id ORDER BY ordinal;";
        command.Parameters.AddWithValue("$id", ownerId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ActivityRule>> ReadScopedRulesAsync(
        SqliteConnection connection,
        ActivityRuleScope scope,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var result = new List<ActivityRule>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_kind,target_value,classification
            FROM activity_rules
            WHERE scope=$scope AND scope_id=$id
            ORDER BY target_kind,target_key;
            """;
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$id", ownerId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ActivityRule(
                new CommitmentTarget((CommitmentTargetKind)reader.GetInt32(0), reader.GetString(1)),
                (ActivityClassification)reader.GetInt32(2)));
        }

        return result;
    }

    private static async Task<DateOnly?> FindConflictDateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CommitmentCard> cards,
        IReadOnlyCollection<Guid> excludedIds,
        CancellationToken cancellationToken)
    {
        var computers = cards.Where(card => card.Kind == CommitmentKind.Computer)
            .OrderBy(card => card.StartAt).ToArray();
        for (var index = 1; index < computers.Length; index++)
        {
            if (computers[index].StartAt < computers[index - 1].EndAt)
            {
                return DateOnly.FromDateTime(computers[index].StartAt.Date);
            }
        }

        foreach (var card in computers)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT c.id FROM commitments c
                WHERE c.kind=$kind AND c.is_skipped=0
                  AND c.start_at_utc < $end
                  AND COALESCE(c.ended_early_at_utc,c.end_at_utc) > $start;
                """;
            command.Parameters.AddWithValue("$kind", (int)CommitmentKind.Computer);
            command.Parameters.AddWithValue("$start", FormatInstant(card.StartAt));
            command.Parameters.AddWithValue("$end", FormatInstant(card.EndAt));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!excludedIds.Contains(Guid.Parse(reader.GetString(0))))
                {
                    return DateOnly.FromDateTime(card.StartAt.Date);
                }
            }
        }

        return null;
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
