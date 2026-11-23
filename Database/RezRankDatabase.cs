using MySqlConnector;
using RezRank.Configuration;

namespace RezRank.Database;

public readonly record struct KillXpResult(long PreviousXp, long CurrentXp);

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

            CREATE TABLE IF NOT EXISTS rezrank_kill_pairs (
                attacker_steam_id VARCHAR(32) NOT NULL,
                victim_steam_id VARCHAR(32) NOT NULL,
                last_reward DATETIME(6) NOT NULL,
                PRIMARY KEY (attacker_steam_id, victim_steam_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryClaimKillRewardAsync(
        string attackerSteamId,
        string victimSteamId,
        int cooldownMinutes,
        CancellationToken cancellationToken = default)
    {
        if (cooldownMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(cooldownMinutes));

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string insertSql = """
            INSERT IGNORE INTO rezrank_kill_pairs
                (attacker_steam_id, victim_steam_id, last_reward)
            VALUES (@attackerSteamId, @victimSteamId, '1970-01-01 00:00:00.000000');
            """;
        await using (var insert = new MySqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("@attackerSteamId", attackerSteamId);
            insert.Parameters.AddWithValue("@victimSteamId", victimSteamId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string selectSql = """
            SELECT last_reward
            FROM rezrank_kill_pairs
            WHERE attacker_steam_id = @attackerSteamId
              AND victim_steam_id = @victimSteamId
            FOR UPDATE;
            """;
        DateTime lastReward;
        await using (var select = new MySqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("@attackerSteamId", attackerSteamId);
            select.Parameters.AddWithValue("@victimSteamId", victimSteamId);
            lastReward = Convert.ToDateTime(await select.ExecuteScalarAsync(cancellationToken));
        }

        var now = DateTime.UtcNow;
        var canReward = cooldownMinutes == 0 || lastReward <= now.AddMinutes(-cooldownMinutes);
        if (canReward)
        {
            const string updateSql = """
                UPDATE rezrank_kill_pairs
                SET last_reward = UTC_TIMESTAMP(6)
                WHERE attacker_steam_id = @attackerSteamId
                  AND victim_steam_id = @victimSteamId;
                """;
            await using var update = new MySqlCommand(updateSql, connection, transaction);
            update.Parameters.AddWithValue("@attackerSteamId", attackerSteamId);
            update.Parameters.AddWithValue("@victimSteamId", victimSteamId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return canReward;
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


    public async Task<List<PlayerRecord>> GetTopPlayersAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        const string sql = """
            SELECT steam_id, last_known_name, xp, kills, deaths, last_activity
            FROM rezrank_players
            ORDER BY xp DESC, kills DESC, deaths ASC, last_activity ASC
            LIMIT @limit;
            """;

        var players = new List<PlayerRecord>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            players.Add(new PlayerRecord
            {
                SteamId = reader.GetString(reader.GetOrdinal("steam_id")),
                LastKnownName = reader.GetString(reader.GetOrdinal("last_known_name")),
                Xp = reader.GetInt64(reader.GetOrdinal("xp")),
                Kills = reader.GetInt32(reader.GetOrdinal("kills")),
                Deaths = reader.GetInt32(reader.GetOrdinal("deaths")),
                LastActivity = reader.GetDateTime(reader.GetOrdinal("last_activity"))
            });
        }

        return players;
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

    public async Task<KillXpResult> ApplyKillAsync(
        string attackerSteamId,
        string attackerName,
        string victimSteamId,
        string victimName,
        int xpReward,
        CancellationToken cancellationToken = default)
    {
        if (xpReward < 0)
            throw new ArgumentOutOfRangeException(nameof(xpReward));

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string ensurePlayerSql = """
            INSERT INTO rezrank_players (steam_id, last_known_name, last_activity)
            VALUES (@steamId, @playerName, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                last_known_name = VALUES(last_known_name),
                last_activity = UTC_TIMESTAMP(6);
            """;

        await using (var command = new MySqlCommand(ensurePlayerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@steamId", attackerSteamId);
            command.Parameters.AddWithValue("@playerName", attackerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new MySqlCommand(ensurePlayerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@steamId", victimSteamId);
            command.Parameters.AddWithValue("@playerName", victimName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long previousXp;
        await using (var command = new MySqlCommand(
            "SELECT xp FROM rezrank_players WHERE steam_id = @steamId FOR UPDATE;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@steamId", attackerSteamId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            previousXp = Convert.ToInt64(value);
        }

        const string updateKillerSql = """
            UPDATE rezrank_players
            SET xp = xp + @xpReward,
                kills = kills + 1,
                last_known_name = @playerName,
                last_activity = UTC_TIMESTAMP(6)
            WHERE steam_id = @steamId;
            """;

        await using (var command = new MySqlCommand(updateKillerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@xpReward", xpReward);
            command.Parameters.AddWithValue("@playerName", attackerName);
            command.Parameters.AddWithValue("@steamId", attackerSteamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateVictimSql = """
            UPDATE rezrank_players
            SET deaths = deaths + 1,
                last_known_name = @playerName,
                last_activity = UTC_TIMESTAMP(6)
            WHERE steam_id = @steamId;
            """;

        await using (var command = new MySqlCommand(updateVictimSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@playerName", victimName);
            command.Parameters.AddWithValue("@steamId", victimSteamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new KillXpResult(previousXp, previousXp + xpReward);
    }

    public async Task RecordDeathAsync(
        string steamId,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO rezrank_players (steam_id, last_known_name, deaths, last_activity)
            VALUES (@steamId, @playerName, 1, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                last_known_name = VALUES(last_known_name),
                deaths = deaths + 1,
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
        IReadOnlyCollection<string>? excludedSteamIds = null,
        CancellationToken cancellationToken = default)
    {
        if (inactiveDays < 1)
            throw new ArgumentOutOfRangeException(nameof(inactiveDays));

        var excludedIds = excludedSteamIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        var exclusionSql = excludedIds.Length == 0
            ? string.Empty
            : $" AND steam_id NOT IN ({string.Join(", ", excludedIds.Select((_, index) => $"@excluded{index}"))})";

        var sql = $"""
            DELETE FROM rezrank_players
            WHERE last_activity <= UTC_TIMESTAMP(6) - INTERVAL @inactiveDays DAY{exclusionSql};
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@inactiveDays", inactiveDays);

        for (var index = 0; index < excludedIds.Length; index++)
            command.Parameters.AddWithValue($"@excluded{index}", excludedIds[index]);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
