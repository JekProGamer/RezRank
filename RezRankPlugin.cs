using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Capabilities;
using WASDSharedAPI;
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

            if (_config.DatabaseCleanup.CheckIntervalHours < 1)
                throw new InvalidOperationException("DatabaseCleanup.CheckIntervalHours must be at least 1.");
            if (_config.DatabaseCleanup.InactiveDays < 1)
                throw new InvalidOperationException("DatabaseCleanup.InactiveDays must be at least 1.");

            _database = new RezRankDatabase(_config.Database);
            _databaseReadyTask = InitializeDatabaseAsync(cancellationToken);

            Logger.LogInformation("RezRank: WASDMenuAPI integration enabled; players can open the menu with !rank.");

            if (_config.DatabaseCleanup.Enabled)
                _ = RunDatabaseCleanupLoopAsync(cancellationToken);

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


    [ConsoleCommand("css_rank")]
    public void OpenRankMenu(CCSPlayerController? player, CommandInfo? info)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        var manager = new PluginCapability<IWasdMenuManager>("wasdmenu:manager").Get();
        if (manager == null)
        {
            player.PrintToChat("[RezRank] Меню недоступно: WASDMenuAPI не загружен.");
            return;
        }

        var menu = manager.CreateMenu("RezRank — меню игрока");
        menu.Add("Статус (вывести в чат)", (p, option) =>
        {
            manager.CloseMenu(p);
            _ = SendPlayerStatusAsync(p, includeStats: true, _shutdown?.Token ?? CancellationToken.None);
        });
        menu.Add("Мой ранг", (p, option) =>
        {
            manager.CloseMenu(p);
            _ = SendPlayerStatusAsync(p, includeStats: false, _shutdown?.Token ?? CancellationToken.None);
        });
        menu.Add("Закрыть", (p, option) => manager.CloseMenu(p));

        manager.OpenMainMenu(player, menu);
    }

    private async Task SendPlayerStatusAsync(
        CCSPlayerController player,
        bool includeStats,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            var steamId = player.SteamID.ToString();
            await _databaseReadyTask;
            var record = await _database.GetPlayerAsync(steamId, cancellationToken);

            Server.NextFrame(() =>
            {
                if (!player.IsValid || player.SteamID.ToString() != steamId)
                    return;

                if (record == null)
                {
                    player.PrintToChat("[RezRank] Статистика пока не найдена. Сыграйте немного, чтобы создать запись.");
                    return;
                }

                var isBypass = IsBypass(steamId);
                var rank = isBypass ? _config.Bypass.RankName : GetRank(record.Xp).Name;

                if (!includeStats)
                {
                    player.PrintToChat($"[RezRank] Ваш ранг: {rank} | XP: {record.Xp}");
                    return;
                }

                var nextRank = _config.RankProgression.Ranks
                    .Where(r => r != null && r.MinXp > record.Xp)
                    .OrderBy(r => r.MinXp)
                    .FirstOrDefault();

                var progressText = nextRank == null
                    ? "Максимальный ранг"
                    : $"До {nextRank.Name}: {nextRank.MinXp - record.Xp} XP";

                var kd = record.Deaths == 0
                    ? record.Kills.ToString()
                    : ((double)record.Kills / record.Deaths).ToString("0.00");

                player.PrintToChat($"[RezRank] Статус: {rank} | XP: {record.Xp}");
                player.PrintToChat($"[RezRank] Убийства: {record.Kills} | Смерти: {record.Deaths} | K/D: {kd}");
                player.PrintToChat($"[RezRank] {progressText}");
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to display player status.");
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    player.PrintToChat("[RezRank] Не удалось получить статус. Попробуйте позже.");
            });
        }
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

    private async Task RunDatabaseCleanupLoopAsync(CancellationToken cancellationToken)
    {
        if (_config == null || _database == null)
            return;

        var interval = TimeSpan.FromHours(_config.DatabaseCleanup.CheckIntervalHours);
        var inactiveDays = _config.DatabaseCleanup.InactiveDays;
        var excludedSteamIds = _config.DatabaseCleanup.ExcludeBypassPlayers && _config.Bypass.Enabled
            ? _config.Bypass.SteamIds
            : new List<string>();

        Logger.LogInformation(
            "RezRank: inactive-player cleanup scheduled every {IntervalHours} hours; retention: {InactiveDays} days.",
            _config.DatabaseCleanup.CheckIntervalHours,
            inactiveDays);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                await _databaseReadyTask;

                var deletedCount = await _database.DeleteInactivePlayersAsync(
                    inactiveDays,
                    excludedSteamIds,
                    cancellationToken);

                Logger.LogInformation(
                    "RezRank: daily inactive-player cleanup completed. Deleted {DeletedCount} player(s); Bypass exclusions: {BypassCount}.",
                    deletedCount,
                    excludedSteamIds.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: inactive-player cleanup failed.");
        }
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
