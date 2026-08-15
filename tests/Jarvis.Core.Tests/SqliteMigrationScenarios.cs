using Microsoft.Data.Sqlite;
using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class SqliteMigrationScenarios
{
    [Fact]
    public async Task Version_one_commitments_migrate_in_place_and_remain_readable()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionOneDatabaseAsync(database.Path);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.FromHours(8)));

        await using var module = await SupervisionModule.OpenAsync(
            database.Path, clock, new FakeActivitySource(), new FakeReminderSink());
        var snapshot = await module.GetSnapshotAsync();

        var commitment = Assert.Single(snapshot.Commitments);
        Assert.Equal("旧版承诺", commitment.InputGoal);
        Assert.Equal(new Jarvis.Contracts.RestSettings(10, 15), commitment.RestSettings);
        Assert.Null(commitment.TemplateId);
    }

    [Fact]
    public async Task Version_two_migration_survives_restart_with_version_three_planning_data()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionTwoDatabaseAsync(database.Path);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.FromHours(8)));

        Guid templateId;
        Guid planId;
        await using (var module = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            var template = await module.CreateTemplateAsync(new Jarvis.Contracts.CommitmentTemplateDraft(
                "v3 planning",
                Jarvis.Contracts.CommitmentKind.Offline,
                30,
                "review",
                null,
                null,
                null,
                null,
                null,
                new Jarvis.Contracts.RestSettings(10, 15)));
            Assert.True(template.Success, template.Message);
            templateId = template.Value!.Id;

            var recurrence = await module.PrepareRecurrenceAsync(new Jarvis.Contracts.RecurrenceDraft(
                new Jarvis.Contracts.CommitmentDraft(
                    Jarvis.Contracts.CommitmentKind.Offline,
                    clock.Now.AddDays(1),
                    EndAt: null,
                    DurationMinutes: 30,
                    InputGoal: "review",
                    OutcomeGoal: null,
                    RelatedAppsOrSites: null,
                    SupervisionMode: null,
                    ReminderSettings: null,
                    TemplateId: templateId),
                new Jarvis.Contracts.RecurrencePattern(
                    Jarvis.Contracts.RecurrenceKind.SelectedDates,
                    SelectedDates: [DateOnly.FromDateTime(clock.Now.AddDays(1).Date)])));
            Assert.True(recurrence.Success, recurrence.Message);
            var confirmed = await module.ConfirmRecurrenceAsync(recurrence.Value!.CandidateId);
            Assert.True(confirmed.Success, confirmed.Message);
            planId = confirmed.Value!.Id;
        }

        await using (var restarted = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            var snapshot = await restarted.GetSnapshotAsync();
            Assert.Contains(snapshot.Templates, item => item.Id == templateId);
            Assert.Contains(snapshot.RecurrencePlans, item => item.Id == planId);
        }

        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(9L, (long)(await version.ExecuteScalarAsync())!);
        await using var planningTables = connection.CreateCommand();
        planningTables.CommandText = """
            SELECT count(*)
            FROM sqlite_master
            WHERE type = 'table' AND name IN (
                'commitment_templates', 'template_targets', 'recurrence_plans',
                'recurrence_weekdays', 'recurrence_selected_dates', 'recurrence_occurrences');
            """;
        Assert.Equal(6L, (long)(await planningTables.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Failed_version_three_migration_rolls_back_all_schema_changes_and_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionOneDatabaseAsync(database.Path, createConflictingTable: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionThreeAsync(
            connection, migration, version: 1, CancellationToken.None));
        await migration.RollbackAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, (long)(await version.ExecuteScalarAsync())!);
        await using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT count(*) FROM pragma_table_info('commitments') WHERE name = 'template_id';";
        Assert.Equal(0L, (long)(await columns.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_three_database_backfills_complete_version_one_history_and_reopens_idempotently()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionThreeDatabaseAsync(database.Path);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.FromHours(8)));

        await using (var module = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            await AssertVersionThreeDataWasBackfilledAsync(module);
        }

        await AssertUserVersionAsync(database.Path, 9);

        await using (var reopened = await SupervisionModule.OpenAsync(
                         database.Path, clock, new FakeActivitySource(), new FakeReminderSink()))
        {
            await AssertVersionThreeDataWasBackfilledAsync(reopened);
        }

        await AssertUserVersionAsync(database.Path, 9);
    }

    [Fact]
    public async Task Version_five_mobile_cards_backfill_displayed_deviation_and_reopen_as_version_six()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionThreeDatabaseAsync(database.Path);
        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();
            await SqliteCommitmentStore.MigrateToVersionFourAsync(
                connection, migration, CancellationToken.None);
            await SqliteCommitmentStore.MigrateToVersionFiveAsync(
                connection, migration, CancellationToken.None);
            await migration.CommitAsync();
            await using var card = connection.CreateCommand();
            card.CommandText = """
                INSERT INTO mobile_escalation_cards(
                    card_id,commitment_id,commitment_version,sequence,sent_at_utc,
                    planned_start_at_utc,planned_end_at_utc,deviation_started_at_utc,
                    classification,commitment_summary,privacy_preview,state,platform_message_id,
                    default_rest_minutes,invalidation_result_text)
                VALUES(
                    '44444444-4444-4444-4444-444444444444',
                    '11111111-1111-1111-1111-111111111111',1,1,
                    '2026-08-12T01:20:00.0000000+00:00',
                    '2026-08-12T01:00:00.0000000+00:00',
                    '2026-08-12T02:00:00.0000000+00:00',
                    '2026-08-12T01:00:00.0000000+00:00',
                    1,'v5 card','privacy',1,NULL,15,NULL);
                """;
            await card.ExecuteNonQueryAsync();
        }

        var commitmentStore = new SqliteCommitmentStore(database.Path);
        await commitmentStore.InitializeAsync(CancellationToken.None);
        var companionStore = new SqliteCompanionStore(database.Path);
        var migrated = Assert.Single(await companionStore.ReadMobileCardsAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(20), migrated.CountedDeviation);
        await AssertUserVersionAsync(database.Path, 9);
    }

    [Fact]
    public async Task Version_six_ai_usage_migrates_to_review_evidence_and_reopens_idempotently()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionSixDatabaseAsync(database.Path);

        var store = new SqliteCommitmentStore(database.Path);
        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);

        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var preserved = connection.CreateCommand();
            preserved.CommandText = "SELECT model, latency_milliseconds FROM ai_usage WHERE request_id='77777777-7777-7777-7777-777777777777';";
            await using var reader = await preserved.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));

            await using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('ai_review_drafts','manual_ai_comparisons');";
            Assert.Equal(2L, (long)(await schema.ExecuteScalarAsync())!);
        }

        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);
    }

    [Fact]
    public async Task Failed_version_seven_migration_rolls_back_latency_column_review_tables_and_user_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionSixDatabaseAsync(database.Path, createReviewDraftConflict: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionSevenAsync(
            connection, migration, CancellationToken.None));
        await migration.RollbackAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
        await using var latency = connection.CreateCommand();
        latency.CommandText = "SELECT count(*) FROM pragma_table_info('ai_usage') WHERE name='latency_milliseconds';";
        Assert.Equal(0L, (long)(await latency.ExecuteScalarAsync())!);
        await using var comparison = connection.CreateCommand();
        comparison.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='manual_ai_comparisons';";
        Assert.Equal(0L, (long)(await comparison.ExecuteScalarAsync())!);
        await using var deliberateConflict = connection.CreateCommand();
        deliberateConflict.CommandText = "SELECT count(*) FROM pragma_table_info('ai_review_drafts') WHERE name='preexisting';";
        Assert.Equal(1L, (long)(await deliberateConflict.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_seven_database_migrates_data_governance_and_reopens_idempotently()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionSevenDatabaseAsync(database.Path);

        var store = new SqliteCommitmentStore(database.Path);
        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);

        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var settings = connection.CreateCommand();
            settings.CommandText = "SELECT retention_days,last_retention_at_utc FROM data_governance_settings WHERE singleton=1;";
            await using var reader = await settings.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(90, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));

            await using var summaries = connection.CreateCommand();
            summaries.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='daily_activity_summaries';";
            Assert.Equal(1L, (long)(await summaries.ExecuteScalarAsync())!);
        }

        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);
    }

    [Fact]
    public async Task Failed_version_eight_migration_rolls_back_settings_summaries_and_user_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionSevenDatabaseAsync(database.Path, createSummaryConflict: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionEightAsync(
            connection, migration, CancellationToken.None));
        await migration.RollbackAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, (long)(await version.ExecuteScalarAsync())!);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='data_governance_settings';";
        Assert.Equal(0L, (long)(await settings.ExecuteScalarAsync())!);
        await using var conflict = connection.CreateCommand();
        conflict.CommandText = "SELECT count(*) FROM pragma_table_info('daily_activity_summaries') WHERE name='preexisting';";
        Assert.Equal(1L, (long)(await conflict.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Version_eight_database_adds_backup_settings_and_reopens_idempotently()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionEightDatabaseAsync(database.Path);

        var store = new SqliteCommitmentStore(database.Path);
        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);

        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var settings = connection.CreateCommand();
            settings.CommandText = """
                SELECT directory_path,last_auto_attempt_at_utc,daily_retention,
                       monthly_retention,upgrade_retention
                  FROM backup_settings WHERE singleton=1;
                """;
            await using var reader = await settings.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(30, reader.GetInt32(2));
            Assert.Equal(12, reader.GetInt32(3));
            Assert.Equal(3, reader.GetInt32(4));
        }

        await store.InitializeAsync(CancellationToken.None);
        await AssertUserVersionAsync(database.Path, 9);
    }

    [Fact]
    public async Task Failed_version_nine_migration_rolls_back_backup_settings_and_user_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionEightDatabaseAsync(database.Path, createBackupSettingsConflict: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionNineAsync(
            connection, migration, CancellationToken.None));
        await migration.RollbackAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, (long)(await version.ExecuteScalarAsync())!);
        await using var conflict = connection.CreateCommand();
        conflict.CommandText = "SELECT count(*) FROM pragma_table_info('backup_settings') WHERE name='preexisting';";
        Assert.Equal(1L, (long)(await conflict.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Failed_version_four_migration_rolls_back_columns_tables_and_user_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionThreeDatabaseAsync(database.Path, createVersionFourConflict: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionFourAsync(
            connection, migration, CancellationToken.None));
        await migration.RollbackAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, (long)(await version.ExecuteScalarAsync())!);

        await using var addedColumns = connection.CreateCommand();
        addedColumns.CommandText = """
            SELECT
                (SELECT count(*) FROM pragma_table_info('commitments')
                    WHERE name = 'current_version')
              + (SELECT count(*) FROM pragma_table_info('reminder_notices')
                    WHERE name = 'commitment_version')
              + (SELECT count(*) FROM pragma_table_info('activity_corrections')
                    WHERE name IN ('commitment_version', 'activity_segment_id'));
            """;
        Assert.Equal(0L, (long)(await addedColumns.ExecuteScalarAsync())!);

        await using var newObjects = connection.CreateCommand();
        newObjects.CommandText = """
            SELECT count(*) FROM sqlite_master
            WHERE name IN (
                'commitment_versions', 'commitment_version_targets', 'commitment_version_rules',
                'activity_segments', 'ix_activity_segments_commitment_time');
            """;
        Assert.Equal(0L, (long)(await newObjects.ExecuteScalarAsync())!);

        await using var deliberateConflict = connection.CreateCommand();
        deliberateConflict.CommandText = """
            SELECT count(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'supervision_responses';
            """;
        Assert.Equal(1L, (long)(await deliberateConflict.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Failed_version_five_migration_rolls_back_all_companion_schema_and_user_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionThreeDatabaseAsync(database.Path);
        await using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();
            await SqliteCommitmentStore.MigrateToVersionFourAsync(
                connection, migration, CancellationToken.None);
            await migration.CommitAsync();
            await using var conflict = connection.CreateCommand();
            conflict.CommandText = "CREATE TABLE companion_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);";
            await conflict.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await reopened.OpenAsync();
        await using var failedMigration = (SqliteTransaction)await reopened.BeginTransactionAsync();
        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionFiveAsync(
            reopened, failedMigration, CancellationToken.None));
        await failedMigration.RollbackAsync();

        await using var version = reopened.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, (long)(await version.ExecuteScalarAsync())!);
        await using var columns = reopened.CreateCommand();
        columns.CommandText = "SELECT count(*) FROM pragma_table_info('commitments') WHERE name='ended_early_at_utc';";
        Assert.Equal(0L, (long)(await columns.ExecuteScalarAsync())!);
        await using var tables = reopened.CreateCommand();
        tables.CommandText = """
            SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN (
                'mobile_escalation_cards','commitment_reviews','daily_review_sessions',
                'cycle_review_sessions','ai_usage','natural_language_candidates');
            """;
        Assert.Equal(0L, (long)(await tables.ExecuteScalarAsync())!);
    }

    private static async Task CreateVersionOneDatabaseAsync(
        string path,
        bool createConflictingTable = false)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
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
                offline_manually_confirmed_at_utc TEXT NULL
            );
            CREATE TABLE commitment_targets (
                commitment_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (commitment_id, ordinal)
            );
            INSERT INTO commitments VALUES (
                '11111111-1111-1111-1111-111111111111', 0,
                '2026-08-12T01:00:00.0000000+00:00', '2026-08-12T02:00:00.0000000+00:00',
                '旧版承诺', NULL, 0, 1, 5, 20, 20, 3,
                '2026-08-12T00:00:00.0000000+00:00', NULL, NULL);
            INSERT INTO commitment_targets VALUES (
                '11111111-1111-1111-1111-111111111111', 0, 0, 'Excel.exe');
            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync();
        if (createConflictingTable)
        {
            command.CommandText = "CREATE TABLE activity_rules (conflicting_column TEXT);";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateVersionTwoDatabaseAsync(string path)
    {
        await CreateVersionOneDatabaseAsync(path);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE commitments ADD COLUMN template_id TEXT NULL;
            ALTER TABLE commitments ADD COLUMN idle_prompt_minutes INTEGER NOT NULL DEFAULT 10;
            ALTER TABLE commitments ADD COLUMN default_total_rest_minutes INTEGER NOT NULL DEFAULT 15;
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
            PRAGMA user_version = 2;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateVersionThreeDatabaseAsync(
        string path,
        bool createVersionFourConflict = false)
    {
        await CreateVersionTwoDatabaseAsync(path);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE commitments ADD COLUMN sound_enabled INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE commitments ADD COLUMN quiet_presentation INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE commitments ADD COLUMN is_skipped INTEGER NOT NULL DEFAULT 0;

            CREATE TABLE commitment_templates (
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
            CREATE TABLE template_targets (
                template_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (template_id, ordinal)
            );
            CREATE TABLE recurrence_plans (
                id TEXT PRIMARY KEY,
                template_id TEXT NULL,
                kind INTEGER NOT NULL,
                start_date TEXT NULL,
                end_date TEXT NULL,
                confirmed_at_utc TEXT NOT NULL
            );
            CREATE TABLE recurrence_weekdays (
                plan_id TEXT NOT NULL,
                weekday INTEGER NOT NULL,
                PRIMARY KEY (plan_id, weekday)
            );
            CREATE TABLE recurrence_selected_dates (
                plan_id TEXT NOT NULL,
                selected_date TEXT NOT NULL,
                PRIMARY KEY (plan_id, selected_date)
            );
            CREATE TABLE recurrence_occurrences (
                plan_id TEXT NOT NULL,
                commitment_id TEXT NOT NULL UNIQUE,
                occurrence_date TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY (plan_id, ordinal)
            );
            CREATE INDEX ix_recurrence_occurrences_commitment
                ON recurrence_occurrences(commitment_id);

            UPDATE commitments SET
                input_goal = 'v3 commitment',
                outcome_goal = 'migration remains complete',
                template_id = '22222222-2222-2222-2222-222222222222',
                idle_prompt_minutes = 12,
                default_total_rest_minutes = 18,
                sound_enabled = 0,
                quiet_presentation = 1;
            INSERT INTO activity_rules VALUES (
                2, '11111111-1111-1111-1111-111111111111', 0,
                'Excel.exe', 'excel.exe', 0);
            INSERT INTO commitment_templates VALUES (
                '22222222-2222-2222-2222-222222222222', 'v3 template', 0, 60,
                'template input', 'template outcome', 0, 1, 6, 21, 22, 4,
                0, 1, 13, 19,
                '2026-08-11T23:00:00.0000000+00:00',
                '2026-08-11T23:30:00.0000000+00:00', NULL);
            INSERT INTO template_targets VALUES (
                '22222222-2222-2222-2222-222222222222', 0, 0, 'Excel.exe');
            INSERT INTO activity_rules VALUES (
                1, '22222222-2222-2222-2222-222222222222', 0,
                'Excel.exe', 'excel.exe', 0);
            INSERT INTO recurrence_plans VALUES (
                '33333333-3333-3333-3333-333333333333',
                '22222222-2222-2222-2222-222222222222', 2,
                NULL, NULL, '2026-08-12T00:00:00.0000000+00:00');
            INSERT INTO recurrence_selected_dates VALUES (
                '33333333-3333-3333-3333-333333333333', '2026-08-12');
            INSERT INTO recurrence_occurrences VALUES (
                '33333333-3333-3333-3333-333333333333',
                '11111111-1111-1111-1111-111111111111', '2026-08-12', 0);
            PRAGMA user_version = 3;
            """;
        await command.ExecuteNonQueryAsync();

        if (createVersionFourConflict)
        {
            command.CommandText = "CREATE TABLE supervision_responses (preexisting TEXT);";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateVersionSixDatabaseAsync(
        string path,
        bool createReviewDraftConflict = false)
    {
        await CreateVersionThreeDatabaseAsync(path);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using (var migration = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await SqliteCommitmentStore.MigrateToVersionFourAsync(
                connection, migration, CancellationToken.None);
            await SqliteCommitmentStore.MigrateToVersionFiveAsync(
                connection, migration, CancellationToken.None);
            await SqliteCommitmentStore.MigrateToVersionSixAsync(
                connection, migration, CancellationToken.None);
            await migration.CommitAsync();
        }

        await using (var usage = connection.CreateCommand())
        {
            usage.CommandText = """
                INSERT INTO ai_usage(
                    request_id, requested_at_utc, purpose, provider, model,
                    input_tokens, output_tokens, cache_hit_input_tokens, price_version,
                    cost_cny, success, error_code, state, result_json)
                VALUES(
                    '77777777-7777-7777-7777-777777777777',
                    '2026-08-15T01:00:00.0000000+00:00',0,'siliconflow',
                    'deepseek-ai/DeepSeek-V4-Flash',10,20,0,'2026-08-01',
                    '0.001',1,NULL,'settled',NULL);
                """;
            await usage.ExecuteNonQueryAsync();
        }

        if (createReviewDraftConflict)
        {
            await using var conflict = connection.CreateCommand();
            conflict.CommandText = "CREATE TABLE ai_review_drafts(preexisting TEXT);";
            await conflict.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateVersionSevenDatabaseAsync(
        string path,
        bool createSummaryConflict = false)
    {
        await CreateVersionSixDatabaseAsync(path);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using (var migration = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await SqliteCommitmentStore.MigrateToVersionSevenAsync(
                connection, migration, CancellationToken.None);
            await migration.CommitAsync();
        }

        if (createSummaryConflict)
        {
            await using var conflict = connection.CreateCommand();
            conflict.CommandText = "CREATE TABLE daily_activity_summaries(preexisting TEXT);";
            await conflict.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateVersionEightDatabaseAsync(
        string path,
        bool createBackupSettingsConflict = false)
    {
        await CreateVersionSevenDatabaseAsync(path);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using (var migration = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await SqliteCommitmentStore.MigrateToVersionEightAsync(
                connection, migration, CancellationToken.None);
            await migration.CommitAsync();
        }

        if (createBackupSettingsConflict)
        {
            await using var conflict = connection.CreateCommand();
            conflict.CommandText = "CREATE TABLE backup_settings(preexisting TEXT);";
            await conflict.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertVersionThreeDataWasBackfilledAsync(SupervisionModule module)
    {
        var snapshot = await module.GetSnapshotAsync();
        var commitment = Assert.Single(snapshot.Commitments);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), commitment.Id);
        Assert.Equal(1, commitment.Version);
        Assert.Equal("v3 commitment", commitment.InputGoal);
        Assert.Equal("migration remains complete", commitment.OutcomeGoal);
        Assert.Equal(new ReminderSettings(true, 5, 20, 20, 3, false, true), commitment.ReminderSettings);
        Assert.Equal(new RestSettings(12, 18), commitment.RestSettings);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), commitment.TemplateId);
        var target = Assert.Single(commitment.RelatedAppsOrSites);
        Assert.Equal(new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"), target);
        var rule = Assert.Single(commitment.ActivityRules);
        Assert.Equal(new ActivityRule(target, ActivityClassification.Related), rule);

        var historyResult = await module.GetCommitmentHistoryAsync(commitment.Id);
        Assert.True(historyResult.Success, historyResult.Message);
        var history = historyResult.Value!;
        Assert.Equal(1, history.CurrentVersion);
        var version = Assert.Single(history.Versions);
        Assert.Equal(1, version.Version);
        Assert.Equal(commitment.ConfirmedAt, version.EffectiveFrom);
        Assert.Equal(commitment.ConfirmedAt, version.ConfirmedAt);
        Assert.False(string.IsNullOrWhiteSpace(version.Reason));
        Assert.Equal(commitment.Kind, version.Snapshot.Kind);
        Assert.Equal(commitment.StartAt, version.Snapshot.StartAt);
        Assert.Equal(commitment.EndAt, version.Snapshot.EndAt);
        Assert.Equal(commitment.InputGoal, version.Snapshot.InputGoal);
        Assert.Equal(commitment.OutcomeGoal, version.Snapshot.OutcomeGoal);
        Assert.Equal(commitment.SupervisionMode, version.Snapshot.SupervisionMode);
        Assert.Equal(commitment.ReminderSettings, version.Snapshot.ReminderSettings);
        Assert.Equal(commitment.RestSettings, version.Snapshot.RestSettings);
        Assert.Equal(commitment.TemplateId, version.Snapshot.TemplateId);
        Assert.Equal(commitment.RelatedAppsOrSites, version.Snapshot.RelatedAppsOrSites);
        Assert.Equal(commitment.ActivityRules, version.Snapshot.ActivityRules);

        var template = Assert.Single(snapshot.Templates);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), template.Id);
        Assert.Equal("v3 template", template.Name);
        Assert.Equal(new ReminderSettings(true, 6, 21, 22, 4, false, true), template.ReminderSettings);
        Assert.Equal(new RestSettings(13, 19), template.RestSettings);
        Assert.Equal(new ActivityRule(
            new CommitmentTarget(CommitmentTargetKind.Application, "Excel.exe"),
            ActivityClassification.Related), Assert.Single(template.ActivityRules));

        var plan = Assert.Single(snapshot.RecurrencePlans);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), plan.Id);
        Assert.Equal(template.Id, plan.TemplateId);
        Assert.Equal(RecurrenceKind.SelectedDates, plan.Pattern.Kind);
        Assert.Equal(new DateOnly(2026, 8, 12), Assert.Single(plan.Pattern.SelectedDates!));
        var occurrence = Assert.Single(plan.Occurrences);
        Assert.Equal(commitment.Id, occurrence.CommitmentId);
        Assert.Equal(new DateOnly(2026, 8, 12), occurrence.Date);
        Assert.Equal(RecurrenceOccurrenceStatus.Active, occurrence.Status);
    }

    private static async Task AssertUserVersionAsync(string path, long expected)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(expected, (long)(await command.ExecuteScalarAsync())!);
    }
}
