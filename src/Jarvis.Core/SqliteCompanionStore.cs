using System.Globalization;
using System.Text.Json;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed record StoredWorktimeSettings(
    bool Enabled,
    string CliPath,
    string Profile,
    string? BoundUserId,
    string? BoundChatId,
    NotificationPreviewMode PreviewMode = NotificationPreviewMode.Privacy);

internal sealed record StoredDailyConfiguration(TimeOnly LocalTime, DateTimeOffset? ConfiguredAt = null);

internal sealed record StoredCycleConfiguration(DateOnly AnchorDate, int IntervalDays, TimeOnly LocalTime);

internal sealed record StoredWorktimeReply(
    string EventId,
    string RecipientOpenId,
    string Text,
    Guid IdempotencyKey);

internal sealed record StoredAiCall(bool Exists, bool IsSettled, AiProviderResult? Result);

internal sealed record StoredCandidateState(Guid CandidateId, string State);

internal sealed record StoredWorktimeCandidateBinding(Guid CandidateId, string Action);

internal sealed record StoredCompanionPersonaState(
    CompanionPersonaSettingsView Settings,
    ProactiveCompanionPromptView? CurrentPrompt,
    int TotalResponses,
    int TotalIgnores,
    int ConsecutiveIgnores,
    int TodayPromptCount,
    DateOnly LocalDate,
    DateTimeOffset? LastPromptAt);

internal sealed class SqliteCompanionStore
{
    private readonly string _connectionString;

    public SqliteCompanionStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task<StoredWorktimeSettings> ReadWorktimeSettingsAsync(CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync("worktime", cancellationToken).ConfigureAwait(false);
        return value is null
            ? new StoredWorktimeSettings(false, "lark-cli", "jarvis-t04", null, null)
            : JsonSerializer.Deserialize<StoredWorktimeSettings>(value, CoreProtocol.Json)!;
    }

    public Task SaveWorktimeSettingsAsync(
        StoredWorktimeSettings settings,
        CancellationToken cancellationToken) =>
        WriteSettingAsync("worktime", JsonSerializer.Serialize(settings, CoreProtocol.Json), cancellationToken);

    public Task<string?> ReadSettingForModuleAsync(string key, CancellationToken cancellationToken) =>
        ReadSettingAsync(key, cancellationToken);

    public Task WriteSettingForModuleAsync(
        string key,
        string value,
        CancellationToken cancellationToken) => WriteSettingAsync(key, value, cancellationToken);

    public Task DeleteSettingForModuleAsync(string key, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM companion_settings WHERE key=$key;", cancellationToken, ("$key", key));

    public async Task<decimal> ReadAiMonthlyHardCapAsync(CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync("ai-monthly-hard-cap", cancellationToken).ConfigureAwait(false);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var hardCap)
            ? hardCap
            : 30m;
    }

    public Task SaveAiMonthlyHardCapAsync(decimal hardCap, CancellationToken cancellationToken) =>
        WriteSettingAsync(
            "ai-monthly-hard-cap", hardCap.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public async Task<AiModelPreference> ReadAiModelPreferenceAsync(CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync("ai-model-preference", cancellationToken).ConfigureAwait(false);
        return Enum.TryParse<AiModelPreference>(value, ignoreCase: true, out var preference) &&
               Enum.IsDefined(preference)
            ? preference
            : AiModelPreference.Flash;
    }

    public Task SaveAiModelPreferenceAsync(
        AiModelPreference preference,
        CancellationToken cancellationToken) =>
        WriteSettingAsync("ai-model-preference", preference.ToString(), cancellationToken);

    public async Task<StoredCompanionPersonaState> ReadCompanionPersonaStateAsync(
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync("companion-persona", cancellationToken).ConfigureAwait(false);
        var state = value is null
            ? new StoredCompanionPersonaState(
                CompanionPersonaSettingsView.Default, null, 0, 0, 0, 0, localDate, null)
            : JsonSerializer.Deserialize<StoredCompanionPersonaState>(value, CoreProtocol.Json) ??
              new StoredCompanionPersonaState(
                  CompanionPersonaSettingsView.Default, null, 0, 0, 0, 0, localDate, null);
        return state.LocalDate == localDate
            ? state
            : state with { LocalDate = localDate, TodayPromptCount = 0 };
    }

    public Task SaveCompanionPersonaStateAsync(
        StoredCompanionPersonaState state,
        CancellationToken cancellationToken) =>
        WriteSettingAsync(
            "companion-persona",
            JsonSerializer.Serialize(state, CoreProtocol.Json),
            cancellationToken);

    public async Task CompleteProactiveResponseAsync(
        StoredCompanionPersonaState state,
        ChatMessageView prompt,
        ChatMessageView response,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var setting = connection.CreateCommand())
        {
            setting.Transaction = transaction;
            setting.CommandText = """
                INSERT INTO companion_settings(key,value) VALUES('companion-persona',$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            Add(setting, "$value", JsonSerializer.Serialize(state, CoreProtocol.Json));
            await setting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var message in new[] { prompt, response })
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO companion_chat_messages(message_id,at_utc,role,text)
                VALUES($id,$at,$role,$text);
                """;
            Add(insert, "$id", message.MessageId.ToString("D"));
            Add(insert, "$at", Format(message.At));
            Add(insert, "$role", message.Role);
            Add(insert, "$text", message.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredWorktimeCandidateBinding?> ReadWorktimeCandidateBindingAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id,candidate_action
              FROM processed_worktime_events
             WHERE event_id=$event AND candidate_id IS NOT NULL;
            """;
        Add(command, "$event", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredWorktimeCandidateBinding(Guid.Parse(reader.GetString(0)), reader.GetString(1))
            : null;
    }

    public async Task<StoredWorktimeCandidateBinding> BindWorktimeCandidateAsync(
        string eventId,
        Guid candidateId,
        string action,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processed_worktime_events
                   SET candidate_id=$candidate,candidate_action=$action
                 WHERE event_id=$event AND state='processing' AND candidate_id IS NULL;
                """;
            Add(update, "$candidate", candidateId.ToString("D"));
            Add(update, "$action", action);
            Add(update, "$event", eventId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT candidate_id,candidate_action
              FROM processed_worktime_events
             WHERE event_id=$event AND candidate_id IS NOT NULL;
            """;
        Add(read, "$event", eventId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("飞书事件无法绑定候选操作。");
        var binding = new StoredWorktimeCandidateBinding(
            Guid.Parse(reader.GetString(0)), reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return binding;
    }

    public async Task<bool> TryBeginWorktimeEventAsync(
        string eventId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processed_worktime_events(event_id,processed_at_utc,state)
            VALUES($id,$at,'processing')
            ON CONFLICT(event_id) DO UPDATE SET processed_at_utc=excluded.processed_at_utc,state='processing'
            WHERE processed_worktime_events.state IN ('processing','retryable');
            """;
        Add(command, "$id", eventId);
        Add(command, "$at", Format(processedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task CompleteWorktimeEventAsync(
        string eventId,
        DateTimeOffset processedAt,
        CompanionOutcome outcome,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE processed_worktime_events SET state='completed',processed_at_utc=$at,outcome_json=$outcome WHERE event_id=$id;",
            cancellationToken, ("$at", Format(processedAt)),
            ("$outcome", JsonSerializer.Serialize(outcome with { Snapshot = null }, CoreProtocol.Json)),
            ("$id", eventId));

    public async Task CompleteWorktimeTextEventAsync(
        string eventId,
        DateTimeOffset processedAt,
        CompanionOutcome outcome,
        string recipientOpenId,
        string replyText,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = """
                UPDATE processed_worktime_events
                   SET state='completed',processed_at_utc=$at,outcome_json=$outcome
                 WHERE event_id=$id;
                """;
            Add(inbox, "$at", Format(processedAt));
            Add(inbox, "$outcome", JsonSerializer.Serialize(outcome with { Snapshot = null }, CoreProtocol.Json));
            Add(inbox, "$id", eventId);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var reply = connection.CreateCommand())
        {
            reply.Transaction = transaction;
            reply.CommandText = """
                INSERT INTO worktime_reply_outbox(
                    event_id,recipient_open_id,reply_text,idempotency_key,state)
                VALUES($event,$recipient,$text,$key,'pending')
                ON CONFLICT(event_id) DO NOTHING;
                """;
            Add(reply, "$event", eventId);
            Add(reply, "$recipient", recipientOpenId);
            Add(reply, "$text", replyText);
            Add(reply, "$key", idempotencyKey.ToString("D"));
            await reply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadSupervisionEventOutcomeAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome_text FROM processed_supervision_events WHERE event_id=$id;";
        Add(command, "$id", eventId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task StoreWorktimeActionOutcomeAsync(
        string eventId,
        DateTimeOffset processedAt,
        CompanionOutcome outcome,
        Guid cardId,
        string resultText,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = """
                UPDATE processed_worktime_events
                   SET state='business_completed',processed_at_utc=$at,outcome_json=$outcome
                 WHERE event_id=$id AND state='processing';
                """;
            Add(inbox, "$at", Format(processedAt));
            Add(inbox, "$outcome", JsonSerializer.Serialize(
                outcome with { Snapshot = null }, CoreProtocol.Json));
            Add(inbox, "$id", eventId);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var card = connection.CreateCommand())
        {
            card.Transaction = transaction;
            card.CommandText = """
                UPDATE mobile_escalation_cards
                   SET state=$state,invalidation_result_text=$result
                 WHERE card_id=$card AND state=$active;
                """;
            Add(card, "$state", (int)MobileCardState.ResponsePending);
            Add(card, "$result", resultText);
            Add(card, "$card", cardId.ToString("D"));
            Add(card, "$active", (int)MobileCardState.Active);
            if (await card.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("手机提醒卡状态已经变化。");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompanionOutcome?> ReadWorktimeEventOutcomeAsync(
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome_json FROM processed_worktime_events WHERE event_id=$id;";
        Add(command, "$id", eventId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json
            ? JsonSerializer.Deserialize<CompanionOutcome>(json, CoreProtocol.Json)
            : null;
    }

    public Task FailWorktimeEventAsync(
        string eventId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE processed_worktime_events SET state='retryable',processed_at_utc=$at WHERE event_id=$id AND state='processing';",
            cancellationToken, ("$at", Format(processedAt)), ("$id", eventId));

    public async Task<IReadOnlyList<StoredWorktimeReply>> ReadPendingWorktimeRepliesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id,recipient_open_id,reply_text,idempotency_key
              FROM worktime_reply_outbox WHERE state='pending' ORDER BY rowid;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<StoredWorktimeReply>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new StoredWorktimeReply(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Guid.Parse(reader.GetString(3))));
        return result;
    }

    public Task CompleteWorktimeReplyAsync(
        string eventId,
        string platformMessageId,
        CancellationToken cancellationToken) => ExecuteAsync(
        """
        UPDATE worktime_reply_outbox SET state='sent',platform_message_id=$message
         WHERE event_id=$event AND state='pending';
        """,
        cancellationToken, ("$message", platformMessageId), ("$event", eventId));

    public async Task<IReadOnlyList<MobileEscalationCard>> ReadMobileCardsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT card_id,commitment_id,commitment_version,sequence,sent_at_utc,
                   planned_start_at_utc,planned_end_at_utc,deviation_started_at_utc,
                   classification,commitment_summary,privacy_preview,state,platform_message_id,
                   default_rest_minutes,invalidation_result_text,counted_deviation_seconds
            FROM mobile_escalation_cards ORDER BY sent_at_utc, sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MobileEscalationCard>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MobileEscalationCard(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                reader.GetInt32(3), Parse(reader.GetString(4)), Parse(reader.GetString(5)),
                Parse(reader.GetString(6)), Parse(reader.GetString(7)),
                (ActivityClassification)reader.GetInt32(8), reader.GetString(9), reader.GetString(10),
                (MobileCardState)reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                TimeSpan.FromSeconds(reader.GetDouble(15))));
        }

        return result;
    }

    public async Task InsertMobileCardAsync(
        MobileEscalationCard card,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mobile_escalation_cards(
                card_id,commitment_id,commitment_version,sequence,sent_at_utc,
                planned_start_at_utc,planned_end_at_utc,deviation_started_at_utc,
                classification,commitment_summary,privacy_preview,state,platform_message_id,
                default_rest_minutes,invalidation_result_text,counted_deviation_seconds)
            VALUES($card,$commitment,$version,$sequence,$sent,$start,$end,$deviation,
                $classification,$summary,$preview,$state,$message,$rest,$result,$counted);
            """;
        Add(command, "$card", card.CardId.ToString("D"));
        Add(command, "$commitment", card.CommitmentId.ToString("D"));
        Add(command, "$version", card.CommitmentVersion);
        Add(command, "$sequence", card.Sequence);
        Add(command, "$sent", Format(card.SentAt));
        Add(command, "$start", Format(card.PlannedStartAt));
        Add(command, "$end", Format(card.PlannedEndAt));
        Add(command, "$deviation", Format(card.DeviationStartedAt));
        Add(command, "$classification", (int)card.Classification);
        Add(command, "$summary", card.CommitmentSummary);
        Add(command, "$preview", card.PrivacyPreview);
        Add(command, "$state", (int)card.State);
        Add(command, "$message", card.PlatformMessageId);
        Add(command, "$rest", card.DefaultRestMinutes);
        Add(command, "$result", card.InvalidationResultText);
        Add(command, "$counted", (card.CountedDeviation ?? (card.SentAt - card.DeviationStartedAt)).TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ActivateMobileCardAsync(
        Guid cardId,
        string platformMessageId,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE mobile_escalation_cards SET platform_message_id=$message,state=$state WHERE card_id=$card AND state=$pending;",
            cancellationToken,
            ("$message", platformMessageId),
            ("$state", (int)MobileCardState.Active),
            ("$pending", (int)MobileCardState.PendingDelivery),
            ("$card", cardId.ToString("D")));

    public Task SetMobileCardStateAsync(
        Guid cardId,
        MobileCardState state,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE mobile_escalation_cards SET state=$state WHERE card_id=$card;",
            cancellationToken,
            ("$state", (int)state), ("$card", cardId.ToString("D")));

    public Task BeginMobileInvalidationAsync(
        Guid cardId,
        MobileCardState pendingState,
        string resultText,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE mobile_escalation_cards SET state=$state,invalidation_result_text=$result WHERE card_id=$card;",
            cancellationToken,
            ("$state", (int)pendingState), ("$result", resultText), ("$card", cardId.ToString("D")));

    public Task CompleteMobileInvalidationAsync(
        Guid cardId,
        MobileCardState finalState,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE mobile_escalation_cards SET state=$state WHERE card_id=$card;",
            cancellationToken,
            ("$state", (int)finalState), ("$card", cardId.ToString("D")));

    public Task CancelActiveMobileCardsAsync(
        Guid commitmentId,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE mobile_escalation_cards SET state=$cancelled WHERE commitment_id=$id AND state=$active;",
            cancellationToken,
            ("$cancelled", (int)MobileCardState.Cancelled),
            ("$active", (int)MobileCardState.Active),
            ("$id", commitmentId.ToString("D")));

    public async Task EnsureReviewPendingAsync(
        CommitmentView commitment,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO commitment_reviews(
                commitment_id,commitment_version,state,requested_at_utc)
            VALUES($id,$version,$state,$at);
            """;
        Add(command, "$id", commitment.Id.ToString("D"));
        Add(command, "$version", commitment.Version);
        Add(command, "$state", (int)CommitmentReviewState.Pending);
        Add(command, "$at", Format(requestedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommitmentReviewView>> ReadCommitmentReviewsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT commitment_id,commitment_version,state,requested_at_utc,deferred_until_utc,
                   raw_text,assessment,answered_at_utc
            FROM commitment_reviews ORDER BY requested_at_utc;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CommitmentReviewView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CommitmentReviewView(
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
                (CommitmentReviewState)reader.GetInt32(2), Parse(reader.GetString(3)),
                NullableTime(reader, 4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : (CompletionAssessment)reader.GetInt32(6),
                NullableTime(reader, 7)));
        }

        return result;
    }

    public Task CompleteCommitmentReviewAsync(
        Guid commitmentId,
        string rawText,
        CompletionAssessment? assessment,
        DateTimeOffset answeredAt,
        CancellationToken cancellationToken) => ExecuteAsync(
            """
            UPDATE commitment_reviews SET state=$state,raw_text=$text,assessment=$assessment,
                answered_at_utc=$at,deferred_until_utc=NULL
            WHERE commitment_id=$id AND state IN ($pending,$deferred);
            """,
            cancellationToken,
            ("$state", (int)CommitmentReviewState.Completed), ("$text", rawText),
            ("$assessment", assessment is null ? null : (int)assessment), ("$at", Format(answeredAt)),
            ("$id", commitmentId.ToString("D")), ("$pending", (int)CommitmentReviewState.Pending),
            ("$deferred", (int)CommitmentReviewState.Deferred));

    public Task DeferCommitmentReviewAsync(
        Guid commitmentId,
        DateTimeOffset until,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE commitment_reviews SET state=$state,deferred_until_utc=$until WHERE commitment_id=$id;",
            cancellationToken,
            ("$state", (int)CommitmentReviewState.Deferred), ("$until", Format(until)),
            ("$id", commitmentId.ToString("D")));

    public Task SkipCommitmentReviewAsync(Guid commitmentId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE commitment_reviews SET state=$state,raw_text=NULL,assessment=NULL,
                answered_at_utc=NULL,deferred_until_utc=NULL WHERE commitment_id=$id;
            """,
            cancellationToken,
            ("$state", (int)CommitmentReviewState.Skipped), ("$id", commitmentId.ToString("D")));

    public Task ResumeDueCommitmentReviewsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) => ExecuteAsync(
        """
        UPDATE commitment_reviews SET state=$pending,deferred_until_utc=NULL
        WHERE state=$deferred AND deferred_until_utc IS NOT NULL AND deferred_until_utc <= $now;
        """,
        cancellationToken,
        ("$pending", (int)CommitmentReviewState.Pending),
        ("$deferred", (int)CommitmentReviewState.Deferred),
        ("$now", Format(now)));

    public async Task<StoredDailyConfiguration> ReadDailyConfigurationAsync(
        CancellationToken cancellationToken)
    {
        var configuration = await ReadSettingAsync("daily-review-config", cancellationToken).ConfigureAwait(false);
        if (configuration is not null)
            return JsonSerializer.Deserialize<StoredDailyConfiguration>(configuration, CoreProtocol.Json)!;
        var value = await ReadSettingAsync("daily-review-time", cancellationToken).ConfigureAwait(false);
        return new StoredDailyConfiguration(value is null ? new TimeOnly(23, 0) : TimeOnly.Parse(value));
    }

    public Task SaveDailyConfigurationAsync(
        TimeOnly localTime,
        DateTimeOffset configuredAt,
        CancellationToken cancellationToken) =>
        WriteSettingAsync(
            "daily-review-config",
            JsonSerializer.Serialize(new StoredDailyConfiguration(localTime, configuredAt), CoreProtocol.Json),
            cancellationToken);

    public async Task<DailyReviewView> ReadDailyReviewAsync(CancellationToken cancellationToken)
    {
        var config = await ReadDailyConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,review_date,state,current_question,follow_up_used,mobile_invite_sent,
                   snoozed_until_utc,created_at_utc
            FROM daily_review_sessions ORDER BY review_date DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DailyReviewView(ReviewSessionState.NotDue, config.LocalTime);
        }

        var sessionId = Guid.Parse(reader.GetString(0));
        var answers = await ReadDailyAnswersAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new DailyReviewView(
            (ReviewSessionState)reader.GetInt32(2), config.LocalTime, sessionId,
            DateOnly.Parse(reader.GetString(1)),
            reader.IsDBNull(3) ? null : (ReviewQuestionKind)reader.GetInt32(3),
            reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, NullableTime(reader, 6),
            answers.Select(item => item.RawText).ToArray(), answers, Parse(reader.GetString(7)));
    }

    public async Task<Guid> EnsureDailyReviewAsync(
        DateOnly reviewDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO daily_review_sessions(
                session_id,review_date,state,current_question,created_at_utc)
            VALUES($id,$date,$state,NULL,$at);
            SELECT session_id FROM daily_review_sessions WHERE review_date=$date;
            """;
        Add(command, "$id", id.ToString("D"));
        Add(command, "$date", reviewDate.ToString("O"));
        Add(command, "$state", (int)ReviewSessionState.Pending);
        Add(command, "$at", Format(now));
        return Guid.Parse(Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture)!);
    }

    public Task StartDailyReviewAsync(
        Guid sessionId,
        ReviewQuestionKind firstQuestion,
        CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET state=$state,current_question=$question,snoozed_until_utc=NULL WHERE session_id=$id;",
        cancellationToken,
        ("$state", (int)ReviewSessionState.InProgress), ("$question", (int)firstQuestion),
        ("$id", sessionId.ToString("D")));

    public async Task AddDailyAnswerAsync(
        Guid sessionId,
        ReviewQuestionKind question,
        ReviewQuestionKind? nextQuestion,
        string rawText,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var next = nextQuestion;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var answer = connection.CreateCommand())
        {
            answer.Transaction = transaction;
            answer.CommandText = """
                INSERT INTO daily_review_answers(session_id,question,raw_text,answered_at_utc)
                VALUES($id,$question,$text,$at);
                """;
            Add(answer, "$id", sessionId.ToString("D"));
            Add(answer, "$question", (int)question);
            Add(answer, "$text", rawText);
            Add(answer, "$at", Format(now));
            await answer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE daily_review_sessions SET current_question=$next,
                    state=CASE WHEN $next IS NULL THEN $complete ELSE $progress END
                WHERE session_id=$id AND current_question=$current;
                """;
            Add(update, "$next", next is null ? null : (int)next);
            Add(update, "$complete", (int)ReviewSessionState.Completed);
            Add(update, "$progress", (int)ReviewSessionState.InProgress);
            Add(update, "$id", sessionId.ToString("D"));
            Add(update, "$current", (int)question);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("每日复盘问题已经变化，请刷新。");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SnoozeDailyReviewAsync(
        Guid sessionId,
        DateTimeOffset until,
        bool followUpUsed,
        CancellationToken cancellationToken) => ExecuteAsync(
            "UPDATE daily_review_sessions SET state=$state,snoozed_until_utc=$until,follow_up_used=$used WHERE session_id=$id;",
            cancellationToken,
            ("$state", (int)ReviewSessionState.Snoozed), ("$until", Format(until)),
            ("$used", followUpUsed ? 1 : 0), ("$id", sessionId.ToString("D")));

    public Task ResumeDailyReviewAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET state=$state,snoozed_until_utc=NULL WHERE session_id=$id;",
        cancellationToken,
        ("$state", (int)ReviewSessionState.Pending), ("$id", sessionId.ToString("D")));

    public Task MarkDailyReviewInviteSentAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET mobile_invite_sent=1 WHERE session_id=$id;",
        cancellationToken,
        ("$id", sessionId.ToString("D")));

    public Task MarkDailyReviewFollowUpSentAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET follow_up_used=1 WHERE session_id=$id AND follow_up_used=0;",
        cancellationToken,
        ("$id", sessionId.ToString("D")));

    public async Task<string> CalculateDailyFactsAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var localStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
        var localEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM((julianday(end_at_utc)-julianday(start_at_utc))*1440),0)
                 FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM(CASE WHEN effective_classification=$related
                    THEN (julianday(end_at_utc)-julianday(start_at_utc))*1440 ELSE 0 END),0)
                 FROM activity_segments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM(CASE WHEN effective_classification=$distracting
                    THEN (julianday(end_at_utc)-julianday(start_at_utc))*1440 ELSE 0 END),0)
                 FROM activity_segments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COUNT(*) FROM reminder_notices WHERE created_at_utc >= $start AND created_at_utc < $end),
              (SELECT COUNT(*) FROM commitment_reviews
                 WHERE requested_at_utc >= $start AND requested_at_utc < $end AND state IN ($pending,$deferred)),
              (SELECT COALESCE(SUM(MAX(0,(julianday(note)-julianday(recorded_at_utc))*1440)),0)
                 FROM supervision_responses
                WHERE kind IN ($restConfirmed,$restStarted) AND note IS NOT NULL
                  AND recorded_at_utc >= $start AND recorded_at_utc < $end),
              (SELECT COUNT(*) FROM commitment_reviews
                WHERE answered_at_utc >= $start AND answered_at_utc < $end AND state=$completed),
              (SELECT COUNT(*) FROM commitment_reviews
                WHERE answered_at_utc >= $start AND answered_at_utc < $end AND assessment=$assessmentCompleted),
              (SELECT COUNT(*) FROM commitment_reviews
                WHERE answered_at_utc >= $start AND answered_at_utc < $end AND assessment=$assessmentPartial),
              (SELECT COUNT(*) FROM commitment_reviews
                WHERE answered_at_utc >= $start AND answered_at_utc < $end AND assessment=$assessmentNotCompleted);
            """;
        Add(command, "$start", Format(localStart));
        Add(command, "$end", Format(localEnd));
        Add(command, "$related", (int)ActivityClassification.Related);
        Add(command, "$distracting", (int)ActivityClassification.Distracting);
        Add(command, "$pending", (int)CommitmentReviewState.Pending);
        Add(command, "$deferred", (int)CommitmentReviewState.Deferred);
        Add(command, "$completed", (int)CommitmentReviewState.Completed);
        Add(command, "$assessmentCompleted", (int)CompletionAssessment.Completed);
        Add(command, "$assessmentPartial", (int)CompletionAssessment.Partial);
        Add(command, "$assessmentNotCompleted", (int)CompletionAssessment.NotCompleted);
        Add(command, "$restConfirmed", "rest_confirmed");
        Add(command, "$restStarted", "timed_rest_started");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return $"Core 客观事实：正式承诺 {reader.GetInt32(0)} 项，计划 {reader.GetDouble(1):0.#} 分钟；" +
               $"记录到相关 {reader.GetDouble(2):0.#} 分钟、偏离 {reader.GetDouble(3):0.#} 分钟、" +
               $"休息 {reader.GetDouble(6):0.#} 分钟、本机提醒 {reader.GetInt32(4)} 次、待回顾 {reader.GetInt32(5)} 项；" +
               $"已确认完成结果 {reader.GetInt32(7)} 项（完成 {reader.GetInt32(8)} / 部分 {reader.GetInt32(9)} / 未完成 {reader.GetInt32(10)}）。" +
               "以下问题只记录你的解释，不由这些事实推断原因。";
    }

    public Task SkipDailyReviewAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET state=$state,current_question=NULL,snoozed_until_utc=NULL WHERE session_id=$id;",
        cancellationToken,
        ("$state", (int)ReviewSessionState.Skipped), ("$id", sessionId.ToString("D")));

    public Task MarkDailyReviewNoResponseAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE daily_review_sessions SET state=$state,current_question=NULL,snoozed_until_utc=NULL WHERE session_id=$id;",
        cancellationToken,
        ("$state", (int)ReviewSessionState.NoResponse), ("$id", sessionId.ToString("D")));

    public async Task<StoredCycleConfiguration> ReadCycleConfigurationAsync(
        CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync("cycle-review", cancellationToken).ConfigureAwait(false);
        return value is null
            ? new StoredCycleConfiguration(DateOnly.FromDateTime(DateTime.Today), 14, new TimeOnly(20, 0))
            : JsonSerializer.Deserialize<StoredCycleConfiguration>(value, CoreProtocol.Json)!;
    }

    public Task SaveCycleConfigurationAsync(
        StoredCycleConfiguration configuration,
        CancellationToken cancellationToken) =>
        WriteSettingAsync("cycle-review", JsonSerializer.Serialize(configuration, CoreProtocol.Json), cancellationToken);

    public async Task<CycleReviewView> ReadCycleReviewAsync(CancellationToken cancellationToken)
    {
        var config = await ReadCycleConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,period_start,period_end,state,summary,trends_json
            FROM cycle_review_sessions ORDER BY period_end DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new CycleReviewView(ReviewSessionState.NotDue, config.IntervalDays);
        var sessionId = Guid.Parse(reader.GetString(0));
        var focuses = await ReadCycleFocusesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new CycleReviewView(
            (ReviewSessionState)reader.GetInt32(3), config.IntervalDays,
            DateOnly.Parse(reader.GetString(1)), DateOnly.Parse(reader.GetString(2)),
            JsonSerializer.Deserialize<CycleTrendView>(reader.GetString(5), CoreProtocol.Json),
            reader.GetString(4), focuses);
    }

    public async Task<Guid> EnsureCycleReviewAsync(
        DateOnly periodStart,
        DateOnly periodEnd,
        CycleTrendView trends,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO cycle_review_sessions(
                session_id,period_start,period_end,state,summary,trends_json,created_at_utc)
            VALUES($id,$start,$end,$state,$summary,$trends,$at);
            SELECT session_id FROM cycle_review_sessions WHERE period_start=$start AND period_end=$end;
            """;
        Add(command, "$id", id.ToString("D"));
        Add(command, "$start", periodStart.ToString("O"));
        Add(command, "$end", periodEnd.ToString("O"));
        Add(command, "$state", (int)ReviewSessionState.Pending);
        Add(command, "$summary", "以下只汇总计划、实际活动、偏离、休息和回应事实，不进行人格或纪律评分。");
        Add(command, "$trends", JsonSerializer.Serialize(trends, CoreProtocol.Json));
        Add(command, "$at", Format(now));
        return Guid.Parse(Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture)!);
    }

    public Task StartCycleReviewAsync(Guid sessionId, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE cycle_review_sessions SET state=$state WHERE session_id=$id;",
        cancellationToken,
        ("$state", (int)ReviewSessionState.InProgress), ("$id", sessionId.ToString("D")));

    public async Task<Guid> FindCycleSessionIdAsync(
        DateOnly periodStart,
        DateOnly periodEnd,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM cycle_review_sessions WHERE period_start=$start AND period_end=$end;";
        Add(command, "$start", periodStart.ToString("O"));
        Add(command, "$end", periodEnd.ToString("O"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? throw new InvalidOperationException("没有找到周期复盘会话。") : Guid.Parse(value);
    }

    public async Task SaveCycleFocusesAsync(
        Guid sessionId,
        IReadOnlyList<string> focuses,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < focuses.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cycle_review_focuses(session_id,ordinal,text) VALUES($id,$ordinal,$text);
                """;
            Add(command, "$id", sessionId.ToString("D"));
            Add(command, "$ordinal", index);
            Add(command, "$text", focuses[index]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE cycle_review_sessions SET state=$state WHERE session_id=$id;";
            Add(update, "$state", (int)ReviewSessionState.Completed);
            Add(update, "$id", sessionId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CycleTrendView> CalculateCycleTrendsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COUNT(*) FROM commitment_reviews WHERE requested_at_utc >= $start AND requested_at_utc < $end AND state=$completed),
              (SELECT COALESCE(SUM((julianday(end_at_utc)-julianday(start_at_utc))*1440),0) FROM commitments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM(CASE WHEN effective_classification=$related THEN (julianday(end_at_utc)-julianday(start_at_utc))*1440 ELSE 0 END),0) FROM activity_segments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM(CASE WHEN effective_classification=$distracting THEN (julianday(end_at_utc)-julianday(start_at_utc))*1440 ELSE 0 END),0) FROM activity_segments WHERE start_at_utc >= $start AND start_at_utc < $end),
              (SELECT COALESCE(SUM(
                    MAX(0, (julianday(MIN(note,$end))-julianday(MAX(recorded_at_utc,$start)))*1440)
                 ),0)
               FROM supervision_responses
               WHERE kind IN ($restConfirmed,$restStarted)
                 AND note IS NOT NULL AND recorded_at_utc < $end AND note > $start),
              (SELECT COUNT(*) FROM commitment_reviews WHERE requested_at_utc >= $start AND requested_at_utc < $end AND state=$deferred),
              ((SELECT COUNT(*) FROM commitment_reviews WHERE requested_at_utc >= $start AND requested_at_utc < $end AND state=$pending)
               + (SELECT COUNT(*) FROM daily_review_sessions WHERE review_date >= $startDate AND review_date <= $endDate AND state=$dailyNoResponse)),
              (SELECT COALESCE(SUM((julianday(end_at_utc)-julianday(start_at_utc))*1440),0)
                 FROM activity_segments
                WHERE availability=$available AND start_at_utc >= $start AND start_at_utc < $end);
            """;
        var localStart = start.ToDateTime(TimeOnly.MinValue);
        var localEnd = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        Add(command, "$start", Format(new DateTimeOffset(localStart)));
        Add(command, "$end", Format(new DateTimeOffset(localEnd)));
        Add(command, "$completed", (int)CommitmentReviewState.Completed);
        Add(command, "$deferred", (int)CommitmentReviewState.Deferred);
        Add(command, "$pending", (int)CommitmentReviewState.Pending);
        Add(command, "$startDate", start.ToString("O"));
        Add(command, "$endDate", end.ToString("O"));
        Add(command, "$dailyNoResponse", (int)ReviewSessionState.NoResponse);
        Add(command, "$related", (int)ActivityClassification.Related);
        Add(command, "$distracting", (int)ActivityClassification.Distracting);
        Add(command, "$available", (int)ActivityAvailability.Available);
        Add(command, "$restConfirmed", "rest_confirmed");
        Add(command, "$restStarted", "timed_rest_started");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var aggregate = new CycleTrendView(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetDouble(3),
            reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetDouble(8));
        await reader.DisposeAsync().ConfigureAwait(false);
        var commitments = await ReadCycleCommitmentTracesAsync(start, end, cancellationToken)
            .ConfigureAwait(false);
        var dailyReviews = await ReadDailyReviewTracesAsync(start, end, cancellationToken)
            .ConfigureAwait(false);
        return aggregate with { CommitmentDetails = commitments, DailyReviewDetails = dailyReviews };
    }

    private async Task<IReadOnlyList<CycleCommitmentTraceView>> ReadCycleCommitmentTracesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var localStart = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue));
        var localEnd = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.start_at_utc,c.input_goal,c.outcome_goal,
                   (julianday(c.end_at_utc)-julianday(c.start_at_utc))*1440,
                   (SELECT COALESCE(SUM(CASE WHEN s.effective_classification=$related
                        THEN (julianday(s.end_at_utc)-julianday(s.start_at_utc))*1440 ELSE 0 END),0)
                      FROM activity_segments s WHERE s.commitment_id=c.id),
                   (SELECT COALESCE(SUM(CASE WHEN s.effective_classification=$distracting
                        THEN (julianday(s.end_at_utc)-julianday(s.start_at_utc))*1440 ELSE 0 END),0)
                      FROM activity_segments s WHERE s.commitment_id=c.id),
                   (SELECT COALESCE(SUM(MAX(0,(julianday(r.note)-julianday(r.recorded_at_utc))*1440)),0)
                      FROM supervision_responses r
                     WHERE r.commitment_id=c.id AND r.kind IN ($restConfirmed,$restStarted) AND r.note IS NOT NULL),
                   cr.state,cr.assessment,cr.raw_text
              FROM commitments c
              LEFT JOIN commitment_reviews cr ON cr.commitment_id=c.id
             WHERE c.start_at_utc >= $start AND c.start_at_utc < $end
             ORDER BY c.start_at_utc,c.id;
            """;
        Add(command, "$start", Format(localStart));
        Add(command, "$end", Format(localEnd));
        Add(command, "$related", (int)ActivityClassification.Related);
        Add(command, "$distracting", (int)ActivityClassification.Distracting);
        Add(command, "$restConfirmed", "rest_confirmed");
        Add(command, "$restStarted", "timed_rest_started");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CycleCommitmentTraceView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CycleCommitmentTraceView(
                Guid.Parse(reader.GetString(0)),
                DateOnly.FromDateTime(Parse(reader.GetString(1)).ToLocalTime().DateTime),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7),
                reader.IsDBNull(8) ? null : (CommitmentReviewState)reader.GetInt32(8),
                reader.IsDBNull(9) ? null : (CompletionAssessment)reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return result;
    }

    private async Task<IReadOnlyList<DailyReviewTraceView>> ReadDailyReviewTracesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.session_id,d.review_date,d.state,
                   (SELECT COUNT(*) FROM daily_review_answers a WHERE a.session_id=d.session_id)
              FROM daily_review_sessions d
             WHERE d.review_date >= $start AND d.review_date <= $end
             ORDER BY d.review_date,d.session_id;
            """;
        Add(command, "$start", start.ToString("O"));
        Add(command, "$end", end.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DailyReviewTraceView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DailyReviewTraceView(
                Guid.Parse(reader.GetString(0)), DateOnly.Parse(reader.GetString(1)),
                (ReviewSessionState)reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    public async Task<decimal> ReadMonthSpendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(CAST(cost_cny AS REAL)),0) FROM ai_usage WHERE requested_at_utc >= $first;";
        Add(command, "$first", Format(first));
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryReserveAiRequestAsync(
        AiRequestRecordView reservation,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_usage(request_id,requested_at_utc,purpose,provider,model,input_tokens,
                output_tokens,cache_hit_input_tokens,price_version,cost_cny,success,error_code,state,result_json)
            VALUES($id,$at,$purpose,$provider,$model,0,0,0,$price,$cost,0,'ai_request_reserved','reserved',NULL)
            ON CONFLICT(request_id) DO NOTHING;
            """;
        Add(command, "$id", reservation.RequestId.ToString("D"));
        Add(command, "$at", Format(reservation.RequestedAt));
        Add(command, "$purpose", (int)reservation.Purpose);
        Add(command, "$provider", reservation.Provider);
        Add(command, "$model", reservation.Model);
        Add(command, "$price", reservation.PriceVersion);
        Add(command, "$cost", reservation.CostCny.ToString(CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<StoredAiCall> ReadAiCallAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,result_json FROM ai_usage WHERE request_id=$id;";
        Add(command, "$id", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new StoredAiCall(false, false, null);
        var settled = string.Equals(reader.GetString(0), "settled", StringComparison.Ordinal);
        var result = reader.IsDBNull(1)
            ? null
            : JsonSerializer.Deserialize<AiProviderResult>(reader.GetString(1), CoreProtocol.Json);
        return new StoredAiCall(true, settled, result);
    }

    public Task SettleAiRequestAsync(
        AiRequestRecordView record,
        string? errorCode,
        AiProviderResult result,
        CancellationToken cancellationToken) => ExecuteAsync(
        """
        UPDATE ai_usage
           SET input_tokens=$input,output_tokens=$output,cache_hit_input_tokens=$cache,
               cost_cny=$cost,success=$success,error_code=$error,state='settled',result_json=$result,
               latency_milliseconds=$latency
         WHERE request_id=$id AND state='reserved';
        """,
        cancellationToken,
        ("$input", record.InputTokens), ("$output", record.OutputTokens),
        ("$cache", record.CacheHitInputTokens),
        ("$cost", record.CostCny.ToString(CultureInfo.InvariantCulture)),
        ("$success", record.Success ? 1 : 0), ("$error", errorCode),
        ("$latency", record.LatencyMilliseconds),
        ("$result", JsonSerializer.Serialize(result, CoreProtocol.Json)),
        ("$id", record.RequestId.ToString("D")));

    public async Task<IReadOnlyList<AiRequestRecordView>> ReadRecentAiUsageAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id,requested_at_utc,purpose,provider,model,input_tokens,output_tokens,
                   cache_hit_input_tokens,price_version,cost_cny,success,latency_milliseconds
            FROM ai_usage ORDER BY requested_at_utc DESC LIMIT 50;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<AiRequestRecordView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AiRequestRecordView(
                Guid.Parse(reader.GetString(0)), Parse(reader.GetString(1)),
                (AiRequestPurpose)reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8),
                decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture), reader.GetInt32(10) != 0,
                reader.GetInt32(11)));
        }

        return result;
    }

    public Task InsertChatAsync(ChatMessageView message, CancellationToken cancellationToken) => ExecuteAsync(
        "INSERT INTO companion_chat_messages(message_id,at_utc,role,text) VALUES($id,$at,$role,$text);",
        cancellationToken,
        ("$id", message.MessageId.ToString("D")), ("$at", Format(message.At)),
        ("$role", message.Role), ("$text", message.Text));

    public async Task<IReadOnlyList<ChatMessageView>> ReadRecentChatAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id,at_utc,role,text FROM companion_chat_messages ORDER BY at_utc DESC,rowid DESC LIMIT 30;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ChatMessageView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ChatMessageView(Guid.Parse(reader.GetString(0)), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        result.Reverse();
        return result;
    }

    public async Task SaveAiReviewDraftAsync(
        AiReviewDraftView draft,
        AiReviewDraftPayload payload,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var discard = connection.CreateCommand())
        {
            discard.Transaction = transaction;
            discard.CommandText = "UPDATE ai_review_drafts SET state=$discarded WHERE state=$pending;";
            Add(discard, "$discarded", (int)AiReviewDraftState.Discarded);
            Add(discard, "$pending", (int)AiReviewDraftState.Pending);
            await discard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ai_review_drafts(
                    draft_id,kind,source_id,request_id,period_start,period_end,created_at_utc,state,
                    facts_scope,fact_item_count,payload_json,anonymized_comparison_prompt)
                VALUES($id,$kind,$source,$request,$start,$end,$created,$state,$scope,$count,$payload,$prompt);
                """;
            Add(insert, "$id", draft.DraftId.ToString("D"));
            Add(insert, "$kind", (int)draft.Kind);
            Add(insert, "$source", draft.SourceId.ToString("D"));
            Add(insert, "$request", draft.RequestId.ToString("D"));
            Add(insert, "$start", draft.PeriodStart.ToString("O"));
            Add(insert, "$end", draft.PeriodEnd.ToString("O"));
            Add(insert, "$created", Format(draft.CreatedAt));
            Add(insert, "$state", (int)AiReviewDraftState.Pending);
            Add(insert, "$scope", draft.FactsScope);
            Add(insert, "$count", draft.FactItemCount);
            Add(insert, "$payload", JsonSerializer.Serialize(payload, CoreProtocol.Json));
            Add(insert, "$prompt", draft.AnonymizedComparisonPrompt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiReviewDraftView?> ReadPendingAiReviewDraftAsync(
        CancellationToken cancellationToken) =>
        (await ReadAiReviewDraftsAsync(
            "WHERE d.state=$state ORDER BY d.created_at_utc DESC LIMIT 1",
            cancellationToken, ("$state", (object)(int)AiReviewDraftState.Pending)).ConfigureAwait(false))
        .SingleOrDefault();

    public async Task<IReadOnlyList<AiReviewDraftView>> ReadConfirmedAiReviewDraftsAsync(
        CancellationToken cancellationToken) =>
        await ReadAiReviewDraftsAsync(
            "WHERE d.state=$state ORDER BY d.confirmed_at_utc DESC LIMIT 50",
            cancellationToken, ("$state", (object)(int)AiReviewDraftState.Confirmed)).ConfigureAwait(false);

    public async Task<AiReviewDraftView?> ReadAiReviewDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken) =>
        (await ReadAiReviewDraftsAsync(
            "WHERE d.draft_id=$id LIMIT 1",
            cancellationToken, ("$id", (object)draftId.ToString("D"))).ConfigureAwait(false))
        .SingleOrDefault();

    public async Task<bool> ConfirmAiReviewDraftAsync(
        Guid draftId,
        string confirmedText,
        AiReviewEvaluationView evaluation,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_review_drafts
               SET state=$confirmed,confirmed_text=$text,confirmed_at_utc=$at,
                   user_modified=CASE WHEN TRIM($text)<>TRIM(json_extract(payload_json,'$.draftText')) THEN 1 ELSE 0 END,
                   quality_rating=$quality,structure_reliable=$structure,
                   ambiguity_handled=$ambiguity,no_overreach=$overreach,
                   privacy_scope_confirmed=$privacy,evaluation_note=$note
             WHERE draft_id=$id AND state=$pending;
            """;
        Add(command, "$confirmed", (int)AiReviewDraftState.Confirmed);
        Add(command, "$text", confirmedText);
        Add(command, "$at", Format(confirmedAt));
        Add(command, "$quality", evaluation.QualityRating);
        Add(command, "$structure", evaluation.StructureReliable ? 1 : 0);
        Add(command, "$ambiguity", evaluation.AmbiguityHandled ? 1 : 0);
        Add(command, "$overreach", evaluation.NoOverreach ? 1 : 0);
        Add(command, "$privacy", evaluation.PrivacyScopeConfirmed ? 1 : 0);
        Add(command, "$note", evaluation.Note);
        Add(command, "$id", draftId.ToString("D"));
        Add(command, "$pending", (int)AiReviewDraftState.Pending);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DiscardAiReviewDraftAsync(Guid draftId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ai_review_drafts SET state=$discarded WHERE draft_id=$id AND state=$pending;";
        Add(command, "$discarded", (int)AiReviewDraftState.Discarded);
        Add(command, "$id", draftId.ToString("D"));
        Add(command, "$pending", (int)AiReviewDraftState.Pending);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> RecordManualAiComparisonAsync(
        RecordManualAiComparisonCommand comparison,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO manual_ai_comparisons(
                comparison_id,draft_id,model,recorded_at_utc,output_text,quality_rating,
                structure_reliable,ambiguity_handled,no_overreach,privacy_scope_confirmed,evaluation_note)
            SELECT $comparison,draft_id,$model,$at,$output,$quality,$structure,$ambiguity,$overreach,$privacy,$note
              FROM ai_review_drafts
             WHERE draft_id=$draft AND state=$confirmed AND anonymized_comparison_prompt IS NOT NULL;
            """;
        Add(command, "$comparison", Guid.NewGuid().ToString("D"));
        Add(command, "$draft", comparison.DraftId.ToString("D"));
        Add(command, "$confirmed", (int)AiReviewDraftState.Confirmed);
        Add(command, "$model", comparison.Model);
        Add(command, "$at", Format(recordedAt));
        Add(command, "$output", comparison.OutputText);
        Add(command, "$quality", comparison.QualityRating);
        Add(command, "$structure", comparison.StructureReliable ? 1 : 0);
        Add(command, "$ambiguity", comparison.AmbiguityHandled ? 1 : 0);
        Add(command, "$overreach", comparison.NoOverreach ? 1 : 0);
        Add(command, "$privacy", comparison.PrivacyScopeConfirmed ? 1 : 0);
        Add(command, "$note", comparison.Note);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<AiTrialEvidenceView> ReadAiTrialEvidenceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? startedAt;
        await using (var start = connection.CreateCommand())
        {
            start.CommandText = "SELECT MIN(requested_at_utc) FROM ai_usage WHERE purpose IN ($daily,$cycle);";
            Add(start, "$daily", (int)AiRequestPurpose.DailyReviewAssist);
            Add(start, "$cycle", (int)AiRequestPurpose.CycleReviewAssist);
            var value = await start.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            startedAt = value is null or DBNull ? null : Parse((string)value);
        }
        if (startedAt is null) return AiTrialEvidenceView.Empty;
        var endsAt = startedAt.Value.AddDays(14);
        int total;
        int succeeded;
        int daily;
        int cycle;
        double latency;
        decimal cost;
        await using (var usage = connection.CreateCommand())
        {
            usage.CommandText = """
                SELECT COUNT(*),SUM(CASE WHEN success=1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN purpose=$daily THEN 1 ELSE 0 END),
                       SUM(CASE WHEN purpose=$cycle THEN 1 ELSE 0 END),
                       COALESCE(AVG(CASE WHEN latency_milliseconds>0 THEN latency_milliseconds END),0),
                       COALESCE(SUM(CAST(cost_cny AS REAL)),0)
                  FROM ai_usage
                 WHERE purpose IN ($daily,$cycle)
                   AND requested_at_utc >= $start AND requested_at_utc < $end;
                """;
            Add(usage, "$daily", (int)AiRequestPurpose.DailyReviewAssist);
            Add(usage, "$cycle", (int)AiRequestPurpose.CycleReviewAssist);
            Add(usage, "$start", Format(startedAt.Value));
            Add(usage, "$end", Format(endsAt));
            await using var reader = await usage.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = reader.GetInt32(0);
            succeeded = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            daily = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            cycle = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            latency = reader.GetDouble(4);
            cost = Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture);
        }
        int confirmed;
        int modified;
        double? averageQuality;
        double? structureRate;
        double? ambiguityRate;
        double? overreachRate;
        double? privacyRate;
        await using (var drafts = connection.CreateCommand())
        {
            drafts.CommandText = """
                SELECT COUNT(*),SUM(user_modified),AVG(quality_rating),AVG(structure_reliable),
                       AVG(ambiguity_handled),AVG(no_overreach),AVG(privacy_scope_confirmed)
                  FROM ai_review_drafts d
                  JOIN ai_usage u ON u.request_id=d.request_id
                 WHERE d.state=$confirmed
                   AND u.requested_at_utc >= $start AND u.requested_at_utc < $end;
                """;
            Add(drafts, "$confirmed", (int)AiReviewDraftState.Confirmed);
            Add(drafts, "$start", Format(startedAt.Value));
            Add(drafts, "$end", Format(endsAt));
            await using var reader = await drafts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            confirmed = reader.GetInt32(0);
            modified = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            averageQuality = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            structureRate = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            ambiguityRate = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            overreachRate = reader.IsDBNull(5) ? null : reader.GetDouble(5);
            privacyRate = reader.IsDBNull(6) ? null : reader.GetDouble(6);
        }
        int comparisons;
        double? manualAverageQuality;
        double? manualStructureRate;
        double? manualAmbiguityRate;
        double? manualOverreachRate;
        await using (var comparison = connection.CreateCommand())
        {
            comparison.CommandText = """
                SELECT COUNT(*),AVG(c.quality_rating),AVG(c.structure_reliable),
                       AVG(c.ambiguity_handled),AVG(c.no_overreach)
                  FROM manual_ai_comparisons c
                  JOIN ai_review_drafts d ON d.draft_id=c.draft_id
                  JOIN ai_usage u ON u.request_id=d.request_id
                 WHERE u.requested_at_utc >= $start AND u.requested_at_utc < $end;
                """;
            Add(comparison, "$start", Format(startedAt.Value));
            Add(comparison, "$end", Format(endsAt));
            await using var reader = await comparison.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            comparisons = reader.GetInt32(0);
            manualAverageQuality = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            manualStructureRate = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            manualAmbiguityRate = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            manualOverreachRate = reader.IsDBNull(4) ? null : reader.GetDouble(4);
        }
        var models = new List<string>();
        await using (var model = connection.CreateCommand())
        {
            model.CommandText = """
                SELECT DISTINCT model FROM ai_usage
                 WHERE purpose IN ($daily,$cycle)
                   AND requested_at_utc >= $start AND requested_at_utc < $end
                UNION
                SELECT DISTINCT c.model FROM manual_ai_comparisons c
                JOIN ai_review_drafts d ON d.draft_id=c.draft_id
                JOIN ai_usage u ON u.request_id=d.request_id
                 WHERE u.requested_at_utc >= $start AND u.requested_at_utc < $end
                ORDER BY model;
                """;
            Add(model, "$daily", (int)AiRequestPurpose.DailyReviewAssist);
            Add(model, "$cycle", (int)AiRequestPurpose.CycleReviewAssist);
            Add(model, "$start", Format(startedAt.Value));
            Add(model, "$end", Format(endsAt));
            await using var reader = await model.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) models.Add(reader.GetString(0));
        }
        return new AiTrialEvidenceView(
            startedAt, endsAt, now >= endsAt,
            total, succeeded, total - succeeded, daily, cycle, confirmed, modified, comparisons,
            latency, cost, averageQuality, structureRate, ambiguityRate, overreachRate, privacyRate, models,
            manualAverageQuality, manualStructureRate, manualAmbiguityRate, manualOverreachRate);
    }

    private async Task<IReadOnlyList<AiReviewDraftView>> ReadAiReviewDraftsAsync(
        string whereClause,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.draft_id,d.kind,d.source_id,d.request_id,d.period_start,d.period_end,
                   d.created_at_utc,d.state,u.provider,u.model,d.facts_scope,d.fact_item_count,
                   d.payload_json,d.confirmed_text,d.confirmed_at_utc,d.user_modified,
                   d.quality_rating,d.structure_reliable,d.ambiguity_handled,d.no_overreach,
                   d.privacy_scope_confirmed,d.evaluation_note,d.anonymized_comparison_prompt
              FROM ai_review_drafts d JOIN ai_usage u ON u.request_id=d.request_id
              {whereClause};
            """;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<AiReviewDraftView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.Deserialize<AiReviewDraftPayload>(reader.GetString(12), CoreProtocol.Json)!;
            var evaluation = reader.IsDBNull(16)
                ? null
                : new AiReviewEvaluationView(
                    reader.GetInt32(16), reader.GetInt32(17) != 0, reader.GetInt32(18) != 0,
                    reader.GetInt32(19) != 0, reader.GetInt32(20) != 0,
                    reader.IsDBNull(21) ? null : reader.GetString(21));
            result.Add(new AiReviewDraftView(
                Guid.Parse(reader.GetString(0)), (AiReviewKind)reader.GetInt32(1),
                Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)),
                DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Parse(reader.GetString(6)), (AiReviewDraftState)reader.GetInt32(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt32(11),
                payload.DraftText, reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : Parse(reader.GetString(14)), reader.GetInt32(15) != 0,
                evaluation, reader.IsDBNull(22) ? null : reader.GetString(22)));
        }
        return result;
    }

    public async Task SaveCandidateAsync(
        NaturalLanguageOperationCandidate candidate,
        string? worktimeEventId,
        CompanionOutcome? worktimeOutcome,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE natural_language_candidates SET state='discarded' WHERE state='pending';
                INSERT INTO natural_language_candidates(candidate_id,kind,source,original_text,payload_json,
                    summary,created_at_utc,state)
                VALUES($id,$kind,$source,$text,$payload,$summary,$at,'pending');
                """;
            Add(command, "$id", candidate.CandidateId.ToString("D"));
            Add(command, "$kind", (int)candidate.Kind);
            Add(command, "$source", (int)candidate.Source);
            Add(command, "$text", candidate.OriginalText);
            Add(command, "$payload", JsonSerializer.Serialize(candidate, CoreProtocol.Json));
            Add(command, "$summary", candidate.Summary);
            Add(command, "$at", Format(candidate.CreatedAt ?? DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (worktimeEventId is not null && worktimeOutcome is not null)
        {
            await using var inbox = connection.CreateCommand();
            inbox.Transaction = transaction;
            inbox.CommandText = """
                UPDATE processed_worktime_events
                   SET state='business_completed',processed_at_utc=$at,outcome_json=$outcome
                 WHERE event_id=$id AND state='processing';
                """;
            Add(inbox, "$at", Format(candidate.CreatedAt ?? DateTimeOffset.UtcNow));
            Add(inbox, "$outcome", JsonSerializer.Serialize(
                worktimeOutcome with { Snapshot = null }, CoreProtocol.Json));
            Add(inbox, "$id", worktimeEventId);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NaturalLanguageOperationCandidate?> ReadPendingCandidateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM natural_language_candidates WHERE state='pending' ORDER BY created_at_utc DESC LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<NaturalLanguageOperationCandidate>(value, CoreProtocol.Json);
    }

    public async Task<string?> ReadLatestCandidateStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM natural_language_candidates ORDER BY created_at_utc DESC LIMIT 1;";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task<StoredCandidateState?> ReadLatestCandidateStatusAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id,state
              FROM natural_language_candidates
             ORDER BY created_at_utc DESC
             LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredCandidateState(Guid.Parse(reader.GetString(0)), reader.GetString(1))
            : null;
    }

    public async Task<string?> ReadCandidateStateAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM natural_language_candidates WHERE candidate_id=$id;";
        Add(command, "$id", candidateId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public Task SetCandidateStateAsync(Guid candidateId, string state, CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE natural_language_candidates SET state=$state WHERE candidate_id=$id AND state='pending';",
        cancellationToken,
        ("$state", state), ("$id", candidateId.ToString("D")));

    public async Task<bool> TryBeginCandidateConfirmationAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE natural_language_candidates SET state='confirming'
            WHERE candidate_id=$id AND state='pending';
            """;
        Add(command, "$id", candidateId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task MarkCandidateOfficialActionCommittedAsync(
        Guid candidateId,
        CancellationToken cancellationToken) => ExecuteAsync(
        "UPDATE natural_language_candidates SET state='committed' WHERE candidate_id=$id AND state='confirming';",
        cancellationToken,
        ("$id", candidateId.ToString("D")));

    public Task CompleteCandidateConfirmationAsync(
        Guid candidateId,
        bool succeeded,
        CancellationToken cancellationToken) => ExecuteAsync(
        succeeded
            ? "UPDATE natural_language_candidates SET state='confirmed' WHERE candidate_id=$id AND state IN ('confirming','committed');"
            : "UPDATE natural_language_candidates SET state='pending' WHERE candidate_id=$id AND state='confirming';",
        cancellationToken,
        ("$id", candidateId.ToString("D")));

    private async Task<IReadOnlyList<DailyReviewAnswerView>> ReadDailyAnswersAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT question,raw_text,answered_at_utc FROM daily_review_answers WHERE session_id=$id ORDER BY id;";
        Add(command, "$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DailyReviewAnswerView>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new DailyReviewAnswerView(
                (ReviewQuestionKind)reader.GetInt32(0), reader.GetString(1), Parse(reader.GetString(2))));
        return result;
    }

    private async Task<IReadOnlyList<string>> ReadCycleFocusesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM cycle_review_focuses WHERE session_id=$id ORDER BY ordinal;";
        Add(command, "$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM companion_settings WHERE key=$key;";
        Add(command, "$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private Task WriteSettingAsync(string key, string value, CancellationToken cancellationToken) => ExecuteAsync(
        "INSERT INTO companion_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
        cancellationToken, ("$key", key), ("$value", value));

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? NullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
}
