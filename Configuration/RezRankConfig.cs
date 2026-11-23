using Newtonsoft.Json;

namespace RezRank.Configuration;

public sealed class RezRankConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public DatabaseCleanupConfig DatabaseCleanup { get; set; } = new();

    public static RezRankConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new RezRankConfig();
            File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<RezRankConfig>(json) ?? new RezRankConfig();
    }
}

public sealed class DatabaseConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public uint Port { get; set; } = 3306;
    public string Database { get; set; } = "rezrank";
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "CHANGE_ME";
    public string SslMode { get; set; } = "Preferred";

    public string BuildConnectionString()
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = Port,
            Database = Database,
            UserID = Username,
            Password = Password,
            SslMode = Enum.TryParse<MySqlConnector.MySqlSslMode>(SslMode, true, out var mode)
                ? mode
                : MySqlConnector.MySqlSslMode.Preferred,
            Pooling = true,
            ConnectionTimeout = 10
        };

        return builder.ConnectionString;
    }
}

public sealed class DatabaseCleanupConfig
{
    public bool Enabled { get; set; } = true;
    public int InactiveDays { get; set; } = 4;
    public int CheckIntervalHours { get; set; } = 24;
    public bool ExcludeBypassPlayers { get; set; } = true;
}
