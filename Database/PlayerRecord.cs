namespace RezRank.Database;

public sealed class PlayerRecord
{
    public required string SteamId { get; init; }
    public string LastKnownName { get; set; } = string.Empty;
    public long Xp { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public DateTime LastActivity { get; set; }
}
