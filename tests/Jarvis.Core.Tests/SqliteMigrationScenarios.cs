using Microsoft.Data.Sqlite;
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
        Assert.Equal(3L, (long)(await version.ExecuteScalarAsync())!);
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
}
