using System.Globalization;
using System.Text.Json;
using Jarvis.Contracts;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core;

internal sealed class SqliteMobileSyncStore
    : IMobileSyncStore
{
    private readonly string _connectionString;

    public SqliteMobileSyncStore(string databasePath)
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mobile_sync_state(
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mobile_execution_events(
                event_id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                kind TEXT NOT NULL,
                value_json TEXT NOT NULL,
                received_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<StoredMobilePairingOffer?> ReadOfferAsync(CancellationToken cancellationToken) =>
        ReadAsync<StoredMobilePairingOffer>("offer", cancellationToken);

    public Task SaveOfferAsync(StoredMobilePairingOffer offer, CancellationToken cancellationToken) =>
        SaveAsync("offer", offer, cancellationToken);

    public Task ClearOfferAsync(CancellationToken cancellationToken) =>
        DeleteAsync("offer", cancellationToken);

    public Task<StoredMobilePairing?> ReadPairingAsync(CancellationToken cancellationToken) =>
        ReadAsync<StoredMobilePairing>("pairing", cancellationToken);

    public Task SavePairingAsync(StoredMobilePairing pairing, CancellationToken cancellationToken) =>
        SaveAsync("pairing", pairing, cancellationToken);

    public Task SaveHealthAsync(
        MobileHealthReport health,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken) =>
        SaveAsync("health", new StoredHealth(health, receivedAt), cancellationToken);

    public async Task<(MobileHealthReport Health, DateTimeOffset ReceivedAt)?> ReadHealthAsync(
        CancellationToken cancellationToken)
    {
        var value = await ReadAsync<StoredHealth>("health", cancellationToken).ConfigureAwait(false);
        return value is null ? null : (value.Health, value.ReceivedAt);
    }

    public async Task<bool> TryAppendEventAsync(
        MobileExecutionEvent value,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO mobile_execution_events(
                event_id, occurred_at, kind, value_json, received_at)
            VALUES($eventId, $occurredAt, $kind, $json, $receivedAt);
            """;
        command.Parameters.AddWithValue("$eventId", value.EventId.ToString("D"));
        command.Parameters.AddWithValue("$occurredAt", value.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$kind", value.Kind.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, MobileProtocol.Json));
        command.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM mobile_sync_state WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? default : JsonSerializer.Deserialize<T>(value, MobileProtocol.Json);
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mobile_sync_state(key, value_json) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, MobileProtocol.Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mobile_sync_state WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed record StoredHealth(MobileHealthReport Health, DateTimeOffset ReceivedAt);
}
