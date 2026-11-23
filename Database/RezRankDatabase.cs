using MySqlConnector;
using RezRank.Configuration;

namespace RezRank.Database;

public sealed class RezRankDatabase
{
    private readonly string _connectionString;

    public RezRankDatabase(DatabaseConfig config)
    {
        _connectionString = config.BuildConnectionString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            CREATE TABLE IF NOT EXISTS rezrank_players (
                steam_id VARCHAR(32) NOT NULL,
                last_known_name VARCHAR(128) NOT NULL DEFAULT '',
                xp BIGINT NOT NULL DEFAULT 0,
                kills INT NOT NULL DEFAULT 0,
                deaths INT NOT NULL DEFAULT 0,
                last_activity DATETIME(6) NOT NULL,
                PRIMARY KEY (steam_id),
                INDEX idx_last_activity (last_activity)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlayerRecord?> GetPlayerAsync(string steamId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT steam_id, last_known_name, xp, kills, deaths, last_activity
            FROM rezrank_players
            WHERE steam_id = @steamId
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@steamId", steamId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new PlayerRecord
        {
            SteamId = reader.GetString(reader.GetOrdinal("steam_id")),
            LastKnownName = reader.GetString(reader.GetOrdinal("last_known_name")),
            Xp = reader.GetInt64(reader.GetOrdinal("xp")),
            Kills = reader.GetInt32(reader.GetOrdinal("kills")),
            Deaths = reader.GetInt32(reader.GetOrdinal("deaths")),
            LastActivity = reader.GetDateTime(reader.GetOrdinal("last_activity"))
        };
    }

    public async Task UpsertPlayerActivityAsync(
        string steamId,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO rezrank_players (steam_id, last_known_name, last_activity)
            VALUES (@steamId, @playerName, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                last_known_name = VALUES(last_known_name),
                last_activity = UTC_TIMESTAMP(6);
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@steamId", steamId);
        command.Parameters.AddWithValue("@playerName", playerName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteInactivePlayersAsync(
        int inactiveDays,
        CancellationToken cancellationToken = default)
    {
        if (inactiveDays < 1)
            throw new ArgumentOutOfRangeException(nameof(inactiveDays));

        const string sql = """
            DELETE FROM rezrank_players
            WHERE last_activity <= UTC_TIMESTAMP(6) - INTERVAL @inactiveDays DAY;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@inactiveDays", inactiveDays);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
