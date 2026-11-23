using MySqlConnector;
using RezRank.Configuration;

namespace RezRank.Database;

public readonly record struct KillXpResult(long PreviousXp, long CurrentXp);
public readonly record struct AchievementStats(int DoubleKills, int TripleKills, int QuadKills, int Aces);
public sealed record AchievementLeaderboardEntry(string SteamId, string PlayerName, int DoubleKills, int TripleKills, int QuadKills, int Aces, int Total);

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

        await using (var command = new MySqlCommand(sql, connection))
            await command.ExecuteNonQueryAsync(cancellationToken);

        const string achievementsSql = """
            CREATE TABLE IF NOT EXISTS rezrank_achievements (
                steam_id VARCHAR(32) NOT NULL,
                double_kills INT NOT NULL DEFAULT 0,
                triple_kills INT NOT NULL DEFAULT 0,
                quad_kills INT NOT NULL DEFAULT 0,
                aces INT NOT NULL DEFAULT 0,
                PRIMARY KEY (steam_id),
                CONSTRAINT fk_rezrank_achievements_player
                    FOREIGN KEY (steam_id) REFERENCES rezrank_players (steam_id)
                    ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        await using (var command = new MySqlCommand(achievementsSql, connection))
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

    public async Task<AchievementStats> GetAchievementStatsAsync(
        string steamId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT double_kills, triple_kills, quad_kills, aces
            FROM rezrank_achievements
            WHERE steam_id = @steamId
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@steamId", steamId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new AchievementStats(0, 0, 0, 0);

        return new AchievementStats(
            reader.GetInt32(reader.GetOrdinal("double_kills")),
            reader.GetInt32(reader.GetOrdinal("triple_kills")),
            reader.GetInt32(reader.GetOrdinal("quad_kills")),
            reader.GetInt32(reader.GetOrdinal("aces")));
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
        string? achievementName,
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

        var achievementColumn = achievementName switch
        {
            "Double Kill" or "Дабл-килл" => "double_kills",
            "Triple Kill" or "Трипл-килл" => "triple_kills",
            "Quad Kill" or "Квад-килл" => "quad_kills",
            "Ace" or "Эйс" => "aces",
            _ => null
        };

        if (achievementColumn != null)
        {
            var achievementSql = $"""
                INSERT INTO rezrank_achievements (steam_id, {achievementColumn})
                VALUES (@steamId, 1)
                ON DUPLICATE KEY UPDATE {achievementColumn} = {achievementColumn} + 1;
                """;

            await using var command = new MySqlCommand(achievementSql, connection, transaction);
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

    public async Task<List<AchievementLeaderboardEntry>> GetTopAchievementPlayersAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        const string sql = """
            SELECT p.steam_id, p.last_known_name,
                   a.double_kills, a.triple_kills, a.quad_kills, a.aces,
                   (a.double_kills + a.triple_kills + a.quad_kills + a.aces) AS total
            FROM rezrank_achievements a
            INNER JOIN rezrank_players p ON p.steam_id = a.steam_id
            WHERE (a.double_kills + a.triple_kills + a.quad_kills + a.aces) > 0
            ORDER BY total DESC, a.aces DESC, a.quad_kills DESC,
                     a.triple_kills DESC, a.double_kills DESC, p.last_known_name ASC
            LIMIT @limit;
            """;

        var players = new List<AchievementLeaderboardEntry>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            players.Add(new AchievementLeaderboardEntry(
                reader.GetString(reader.GetOrdinal("steam_id")),
                reader.GetString(reader.GetOrdinal("last_known_name")),
                reader.GetInt32(reader.GetOrdinal("double_kills")),
                reader.GetInt32(reader.GetOrdinal("triple_kills")),
                reader.GetInt32(reader.GetOrdinal("quad_kills")),
                reader.GetInt32(reader.GetOrdinal("aces")),
                reader.GetInt32(reader.GetOrdinal("total"))));
        }

        return players;
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

    public async Task<KillXpResult> AdjustPlayerXpAsync(
        string steamId,
        string playerName,
        long amount,
        CancellationToken cancellationToken = default)
    {
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
            command.Parameters.AddWithValue("@steamId", steamId);
            command.Parameters.AddWithValue("@playerName", playerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long previousXp;
        await using (var command = new MySqlCommand(
            "SELECT xp FROM rezrank_players WHERE steam_id = @steamId FOR UPDATE;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@steamId", steamId);
            previousXp = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        var currentXp = Math.Max(0L, checked(previousXp + amount));
        await using (var command = new MySqlCommand(
            """
            UPDATE rezrank_players
            SET xp = @xp, last_known_name = @playerName, last_activity = UTC_TIMESTAMP(6)
            WHERE steam_id = @steamId;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@xp", currentXp);
            command.Parameters.AddWithValue("@playerName", playerName);
            command.Parameters.AddWithValue("@steamId", steamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new KillXpResult(previousXp, currentXp);
    }

    public async Task<KillXpResult> SetPlayerXpAsync(
        string steamId,
        string playerName,
        long xp,
        CancellationToken cancellationToken = default)
    {
        if (xp < 0)
            throw new ArgumentOutOfRangeException(nameof(xp));

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
            command.Parameters.AddWithValue("@steamId", steamId);
            command.Parameters.AddWithValue("@playerName", playerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long previousXp;
        await using (var command = new MySqlCommand(
            "SELECT xp FROM rezrank_players WHERE steam_id = @steamId FOR UPDATE;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@steamId", steamId);
            previousXp = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        await using (var command = new MySqlCommand(
            """
            UPDATE rezrank_players
            SET xp = @xp, last_known_name = @playerName, last_activity = UTC_TIMESTAMP(6)
            WHERE steam_id = @steamId;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@xp", xp);
            command.Parameters.AddWithValue("@playerName", playerName);
            command.Parameters.AddWithValue("@steamId", steamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new KillXpResult(previousXp, xp);
    }

    public async Task ResetPlayerProgressAsync(
        string steamId,
        string playerName,
        CancellationToken cancellationToken = default)
    {
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
            command.Parameters.AddWithValue("@steamId", steamId);
            command.Parameters.AddWithValue("@playerName", playerName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new MySqlCommand(
            """
            UPDATE rezrank_players
            SET xp = 0, kills = 0, deaths = 0,
                last_known_name = @playerName, last_activity = UTC_TIMESTAMP(6)
            WHERE steam_id = @steamId;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@playerName", playerName);
            command.Parameters.AddWithValue("@steamId", steamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new MySqlCommand(
            "DELETE FROM rezrank_achievements WHERE steam_id = @steamId;",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@steamId", steamId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
