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
    public async Task Failed_version_two_migration_rolls_back_all_schema_changes_and_version()
    {
        using var database = new TemporaryDatabase();
        await CreateVersionOneDatabaseAsync(database.Path, createConflictingTable: true);
        await using var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var migration = (SqliteTransaction)await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<SqliteException>(() => SqliteCommitmentStore.MigrateToVersionTwoAsync(
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
}
