using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using RezRank.Configuration;
using RezRank.Database;

namespace RezRank;

public sealed class RezRankPlugin : BasePlugin
{
    public override string ModuleName => "RezRank";
    public override string ModuleVersion => "0.2.0";
    public override string ModuleAuthor => "JekProGamer";
    public override string ModuleDescription => "Custom XP ranks and player progression for CS2.";

    private RezRankConfig? _config;
    private RezRankDatabase? _database;
    private CancellationTokenSource? _shutdown;
    private Task _databaseReadyTask = Task.CompletedTask;
    private readonly HashSet<string> _disconnectingPlayers = new(StringComparer.Ordinal);

    public override void Load(bool hotReload)
    {
        _shutdown = new CancellationTokenSource();
        var cancellationToken = _shutdown.Token;

        try
        {
            var configPath = Path.Combine(ModuleDirectory, "config.json");
            _config = RezRankConfig.Load(configPath);

            if (!_config.Database.Enabled)
            {
                Logger.LogInformation("RezRank: MySQL is disabled in config.");
                return;
            }

            _config.Bypass.SteamIds ??= new List<string>();
            _config.Bypass.SteamIds = _config.Bypass.SteamIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (_config.RankProgression.XpPerKill < 0)
                throw new InvalidOperationException("RankProgression.XpPerKill cannot be negative.");

            _database = new RezRankDatabase(_config.Database);
            _databaseReadyTask = InitializeDatabaseAsync(cancellationToken);

            RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
            {
                var player = @event.Userid;

                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                    return HookResult.Continue;

                var steamId = player.SteamID.ToString();
                var playerName = player.PlayerName;
                _disconnectingPlayers.Remove(steamId);
                _ = SavePlayerActivityAsync(steamId, playerName, cancellationToken);

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
            {
                // Remember disconnects so a player_death event emitted during
                // the same disconnect sequence is not counted as an in-game death.
                if (@event.Xuid != 0)
                    _disconnectingPlayers.Add(@event.Xuid.ToString());

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                var victim = @event.Userid;
                if (victim == null || !victim.IsValid || victim.IsBot || victim.IsHLTV)
                    return HookResult.Continue;

                // Copy all event data now; game-event objects are only valid
                // for the duration of this callback.
                var victimSteamId = victim.SteamID.ToString();
                var victimName = victim.PlayerName;
                var attacker = @event.Attacker;
                var hasValidEnemyAttacker =
                    attacker != null &&
                    attacker.IsValid &&
                    !attacker.IsBot &&
                    !attacker.IsHLTV &&
                    attacker.SteamID != victim.SteamID &&
                    attacker.TeamNum != victim.TeamNum;

                var attackerSteamId = hasValidEnemyAttacker ? attacker!.SteamID.ToString() : string.Empty;
                var attackerName = hasValidEnemyAttacker ? attacker!.PlayerName : string.Empty;
                var attackerIsBypass = hasValidEnemyAttacker && IsBypass(attackerSteamId);
                var victimIsBypass = IsBypass(victimSteamId);

                // Defer processing by one frame so player_disconnect can mark
                // the SteamID first if the engine emits both events on exit.
                Server.NextFrame(() =>
                {
                    if (_disconnectingPlayers.Contains(victimSteamId))
                    {
                        Logger.LogDebug(
                            "RezRank: ignored death event for {PlayerName} ({SteamId}) because the player disconnected.",
                            victimName,
                            victimSteamId);
                        return;
                    }

                    if (!hasValidEnemyAttacker)
                    {
                        // World damage, falls, suicides, disconnects, bots and
                        // non-enemy deaths do not affect player rank statistics.
                        return;
                    }

                    _ = ProcessKillAsync(
                        attackerSteamId,
                        attackerName,
                        victimSteamId,
                        victimName,
                        victimIsBypass ? 20 : _config!.RankProgression.XpPerKill,
                        attackerIsBypass,
                        cancellationToken);
                });

                return HookResult.Continue;
            });

            Logger.LogInformation("RezRank: plugin loaded. XP per kill: {XpPerKill}.", _config.RankProgression.XpPerKill);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to load configuration or register handlers.");
        }
    }

    public override void Unload(bool hotReload)
    {
        _shutdown?.Cancel();
        _shutdown?.Dispose();
        _shutdown = null;
    }

    private async Task SavePlayerActivityAsync(
        string steamId,
        string playerName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            await _database.UpsertPlayerActivityAsync(steamId, playerName, cancellationToken);
            Logger.LogInformation("RezRank: player {PlayerName} ({SteamId}) saved to MySQL.", playerName, steamId);
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to save player {PlayerName} ({SteamId}).", playerName, steamId);
        }
    }

    private async Task ProcessKillAsync(
        string attackerSteamId,
        string attackerName,
        string victimSteamId,
        string victimName,
        int xpReward,
        bool attackerIsBypass,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            await _databaseReadyTask;
            var result = await _database.ApplyKillAsync(
                attackerSteamId,
                attackerName,
                victimSteamId,
                victimName,
                attackerIsBypass ? 0 : xpReward,
                cancellationToken);

            if (attackerIsBypass)
                return;

            var oldRank = GetRank(result.PreviousXp);
            var newRank = GetRank(result.CurrentXp);

            if (!string.Equals(oldRank.Name, newRank.Name, StringComparison.Ordinal))
            {
                Server.NextFrame(() => Server.PrintToChatAll(
                    $"[RezRank] {attackerName} получил звание {newRank.Name}!"));
                Logger.LogInformation(
                    "RezRank: {PlayerName} ranked up from {OldRank} to {NewRank} ({Xp} XP).",
                    attackerName,
                    oldRank.Name,
                    newRank.Name,
                    result.CurrentXp);
            }
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "RezRank: failed to process kill for {PlayerName} ({SteamId}).",
                attackerName,
                attackerSteamId);
        }
    }

    private async Task RecordDeathAsync(
        string victimSteamId,
        string victimName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            await _database.RecordDeathAsync(victimSteamId, victimName, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to record death for {PlayerName} ({SteamId}).", victimName, victimSteamId);
        }
    }

    private bool IsBypass(string steamId)
    {
        return _config?.Bypass.Enabled == true &&
               _config.Bypass.SteamIds.Contains(steamId, StringComparer.Ordinal);
    }

    private RankDefinition GetRank(long xp)
    {
        var ranks = _config?.RankProgression.Ranks;
        if (ranks == null || ranks.Count == 0)
            return new RankDefinition { Name = "Unranked", MinXp = 0 };

        return ranks
            .Where(rank => rank != null && rank.MinXp <= xp)
            .OrderBy(rank => rank.MinXp)
            .LastOrDefault()
            ?? ranks.OrderBy(rank => rank.MinXp).First();
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database!.InitializeAsync(cancellationToken);
            Logger.LogInformation("RezRank: MySQL connection successful; player table is ready.");
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: MySQL initialization failed.");
            throw;
        }
    }
}
