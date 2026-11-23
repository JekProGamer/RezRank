using Newtonsoft.Json;

namespace RezRank.Configuration;

public sealed class RezRankConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public DatabaseCleanupConfig DatabaseCleanup { get; set; } = new();
    public RankProgressionConfig RankProgression { get; set; } = new();
    public BypassConfig Bypass { get; set; } = new();
    public ChatMessagesConfig ChatMessages { get; set; } = new();

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

public sealed class BypassConfig
{
    public bool Enabled { get; set; } = true;
    public string RankName { get; set; } = "BYPASS";
    public string ImagePath { get; set; } = "materials/rezrank/bypass.png";
    public List<string> SteamIds { get; set; } = new();
}

public sealed class RankProgressionConfig
{
    public int XpPerKill { get; set; } = 10;

    public List<RankDefinition> Ranks { get; set; } = new()
    {
        new() { Name = "Silver I", MinXp = 0 },
        new() { Name = "Silver II", MinXp = 100 },
        new() { Name = "Silver III", MinXp = 250 },
        new() { Name = "Silver IV", MinXp = 450 },
        new() { Name = "Silver Elite", MinXp = 700 },
        new() { Name = "Silver Elite Master", MinXp = 1000 },
        new() { Name = "Gold Nova I", MinXp = 1400 },
        new() { Name = "Gold Nova II", MinXp = 1900 },
        new() { Name = "Gold Nova III", MinXp = 2500 },
        new() { Name = "Gold Nova Master", MinXp = 3200 },
        new() { Name = "Master Guardian I", MinXp = 4000 },
        new() { Name = "Master Guardian II", MinXp = 5000 },
        new() { Name = "Master Guardian Elite", MinXp = 6200 },
        new() { Name = "Distinguished Master Guardian", MinXp = 7600 },
        new() { Name = "Legendary Eagle", MinXp = 9200 },
        new() { Name = "Legendary Eagle Master", MinXp = 11000 },
        new() { Name = "Supreme Master First Class", MinXp = 13000 },
        new() { Name = "The Global Elite", MinXp = 15500 }
    };
}

public sealed class RankDefinition
{
    public string Name { get; set; } = "Silver I";
    public long MinXp { get; set; }
}


public sealed class ChatMessagesConfig
{
    public string YourRank { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}Ваш ранг: {BLUE}%RANK% {SILVER}| {YELLOW}XP: {GREEN}%XP%";
    public string PlayerStatus { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}Статус: {BLUE}%RANK% {SILVER}| {YELLOW}XP: {GREEN}%XP%";
    public string PlayerStats { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}Убийства: {GREEN}%KILLS% {SILVER}| {WHITE}Смерти: {RED}%DEATHS% {SILVER}| {WHITE}K/D: {YELLOW}%KD%";
    public string NextRank { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}До {BLUE}%NEXT_RANK%: {GREEN}%XP_LEFT% XP";
    public string MaxRank { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {GREEN}У вас максимальный ранг!";
    public string RankUp { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}%PLAYER% получил звание {BLUE}%RANK%{WHITE}!";
    public string NoData { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}Статистика пока не найдена. Сыграйте немного, чтобы создать запись.";
    public string Error { get; set; } = "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {RED}Не удалось получить статус. Попробуйте позже.";
}
