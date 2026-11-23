using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
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
    private readonly Dictionary<int, RezRankMenuState> _rankMenus = new();
    private static readonly Dictionary<string, PlayerButtons> RankMenuButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = PlayerButtons.Forward,
        ["S"] = PlayerButtons.Back,
        ["E"] = PlayerButtons.Use,
        ["A"] = PlayerButtons.Moveleft,
        ["R"] = PlayerButtons.Reload
    };

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

            RegisterListener<Listeners.OnTick>(OnRankMenuTick);
            Logger.LogInformation("RezRank: internal AdminPlus-style menu enabled; players can open it with !rank.");

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
                {
                    _disconnectingPlayers.Add(@event.Xuid.ToString());
                    var player = @event.Userid;
                    if (player != null)
                        _rankMenus.Remove(player.Slot);
                }

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

        var state = new RezRankMenuState
        {
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = player.Buttons.ToString()
        };
        state.Options.Add(new RezRankMenuOption("Статус игрока", p =>
        {
            CloseRankMenu(p);
            _ = SendPlayerStatusAsync(p, includeStats: true, _shutdown?.Token ?? CancellationToken.None);
        }));
        state.Options.Add(new RezRankMenuOption("Мой ранг", p =>
        {
            CloseRankMenu(p);
            _ = SendPlayerStatusAsync(p, includeStats: false, _shutdown?.Token ?? CancellationToken.None);
        }));
        state.Options.Add(new RezRankMenuOption("Прогресс до следующего ранга", p =>
        {
            CloseRankMenu(p);
            _ = SendRankProgressAsync(p, _shutdown?.Token ?? CancellationToken.None);
        }));

        _rankMenus[player.Slot] = state;
        RenderRankMenu(player, state);
    }

    private void OnRankMenuTick()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !_rankMenus.TryGetValue(player.Slot, out var state))
                continue;

            var snapshot = player.Buttons.ToString();
            if (snapshot != state.PreviousButtons)
            {
                state.PreviousButtons = snapshot;
                if (DateTime.UtcNow - state.OpenedAtUtc >= TimeSpan.FromMilliseconds(400) &&
                    DateTime.UtcNow - state.LastInputUtc >= TimeSpan.FromMilliseconds(120))
                {
                    var buttons = player.Buttons;
                    bool handled = false;

                    if (HasRankMenuButton("R", buttons))
                    {
                        CloseRankMenu(player);
                        continue;
                    }
                    if (HasRankMenuButton("W", buttons))
                    {
                        MoveRankMenuSelection(state, -1);
                        handled = true;
                    }
                    else if (HasRankMenuButton("S", buttons))
                    {
                        MoveRankMenuSelection(state, 1);
                        handled = true;
                    }
                    else if (HasRankMenuButton("A", buttons))
                    {
                        CloseRankMenu(player);
                        continue;
                    }
                    else if (HasRankMenuButton("E", buttons))
                    {
                        var option = state.Options[state.SelectedIndex];
                        option.Action(player);
                        handled = true;
                    }

                    if (handled)
                        state.LastInputUtc = DateTime.UtcNow;
                }
            }

            if (_rankMenus.ContainsKey(player.Slot))
                RenderRankMenu(player, state);
        }
    }

    private static bool HasRankMenuButton(string key, PlayerButtons buttons)
    {
        return RankMenuButtons.TryGetValue(key, out var mapped) && buttons.HasFlag(mapped);
    }

    private static void MoveRankMenuSelection(RezRankMenuState state, int direction)
    {
        if (state.Options.Count == 0) return;
        state.SelectedIndex = (state.SelectedIndex + direction + state.Options.Count) % state.Options.Count;
    }

    private void CloseRankMenu(CCSPlayerController player)
    {
        _rankMenus.Remove(player.Slot);
        if (player.IsValid)
            player.PrintToCenter(" ");
    }

    private static void RenderRankMenu(CCSPlayerController player, RezRankMenuState state)
    {
        const int visible = 5;
        int total = state.Options.Count;
        int selected = Math.Clamp(state.SelectedIndex, 0, Math.Max(0, total - 1));
        int start = Math.Max(0, selected - visible / 2);
        if (start + visible > total) start = Math.Max(0, total - visible);
        int end = Math.Min(total, start + visible);

        var html = new System.Text.StringBuilder();
        html.Append($"<b><font color='red' class='fontSize-m'>RezRank — меню игрока</font></b> <font color='yellow' class='fontSize-sm'>{selected + 1}</font>/<font color='orange' class='fontSize-sm'>{total}</font><br>");
        for (int i = start; i < end; i++)
        {
            bool isSelected = i == selected;
            string color = isSelected ? "#9acd32" : "white";
            if (isSelected)
                html.Append($"<b><font color='yellow'>►[</font> <font color='{color}' class='fontSize-m'>{state.Options[i].Text}</font> <font color='yellow'>]◄</font></b><br>");
            else
                html.Append($"<font color='{color}' class='fontSize-m'>{state.Options[i].Text}</font><br>");
        }

        html.Append("<font color='#ff3333' class='fontSize-sm'>Навигация: <font color='#f5a142'>[W/S]</font> | Выбрать: <font color='#f5a142'>[E]</font> | Назад: <font color='#f5a142'>[A]</font> | Выход: <font color='#f5a142'>[R]</font></font>");
        player.PrintToCenterHtml(html.ToString());
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
                    player.PrintToChat(FormatChat(_config.ChatMessages.NoData));
                    return;
                }

                var isBypass = IsBypass(steamId);
                var rank = isBypass ? _config.Bypass.RankName : GetRank(record.Xp).Name;

                if (!includeStats)
                {
                    player.PrintToChat(FormatChat(_config.ChatMessages.YourRank, ("%RANK%", rank), ("%XP%", record.Xp.ToString())));
                    return;
                }

                var nextRank = _config.RankProgression.Ranks
                    .Where(r => r != null && r.MinXp > record.Xp)
                    .OrderBy(r => r.MinXp)
                    .FirstOrDefault();

                var kd = record.Deaths == 0
                    ? record.Kills.ToString()
                    : ((double)record.Kills / record.Deaths).ToString("0.00");

                player.PrintToChat(FormatChat(_config.ChatMessages.PlayerStatus, ("%RANK%", rank), ("%XP%", record.Xp.ToString())));
                player.PrintToChat(FormatChat(_config.ChatMessages.PlayerStats,
                    ("%KILLS%", record.Kills.ToString()),
                    ("%DEATHS%", record.Deaths.ToString()),
                    ("%KD%", kd)));

                if (nextRank == null)
                    player.PrintToChat(FormatChat(_config.ChatMessages.MaxRank));
                else
                    player.PrintToChat(FormatChat(_config.ChatMessages.NextRank,
                        ("%NEXT_RANK%", nextRank.Name),
                        ("%XP_LEFT%", (nextRank.MinXp - record.Xp).ToString())));
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
                    player.PrintToChat(FormatChat(_config?.ChatMessages.Error ?? "{RED}Не удалось получить статус."));
            });
        }
    }

    private async Task SendRankProgressAsync(
        CCSPlayerController player,
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
                    player.PrintToChat(FormatChat(_config.ChatMessages.NoData));
                    return;
                }

                var rank = IsBypass(steamId) ? _config.Bypass.RankName : GetRank(record.Xp).Name;
                player.PrintToChat(FormatChat(
                    _config.ChatMessages.PlayerStatus,
                    ("%RANK%", rank),
                    ("%XP%", record.Xp.ToString())));

                var nextRank = _config.RankProgression.Ranks
                    .Where(r => r != null && r.MinXp > record.Xp)
                    .OrderBy(r => r.MinXp)
                    .FirstOrDefault();

                if (nextRank == null)
                {
                    player.PrintToChat(FormatChat(_config.ChatMessages.MaxRank));
                    return;
                }

                player.PrintToChat(FormatChat(
                    _config.ChatMessages.NextRank,
                    ("%NEXT_RANK%", nextRank.Name),
                    ("%XP_LEFT%", (nextRank.MinXp - record.Xp).ToString())));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to display rank progress.");
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    player.PrintToChat(FormatChat(_config?.ChatMessages.Error ?? "{RED}Не удалось получить статус."));
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
                    FormatChat(_config.ChatMessages.RankUp, ("%PLAYER%", attackerName), ("%RANK%", newRank.Name))));
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


    private static string FormatChat(string template, params (string Token, string Value)[] replacements)
    {
        foreach (var (token, value) in replacements)
            template = template.Replace(token, value, StringComparison.Ordinal);

        // PrintToChat does not parse {COLOR} text tags. Convert them to the
        // control characters understood by CounterStrikeSharp's chat renderer.
        return template
            .Replace("{DEFAULT}", ChatColors.Default.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{WHITE}", ChatColors.White.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{SILVER}", ChatColors.Silver.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{ORANGE}", ChatColors.Orange.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{GOLD}", ChatColors.Gold.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{RED}", ChatColors.Red.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{DARKRED}", ChatColors.DarkRed.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LIGHTRED}", ChatColors.LightRed.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{BLUE}", ChatColors.Blue.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LIGHTBLUE}", ChatColors.LightBlue.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{DARKBLUE}", ChatColors.DarkBlue.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{GREEN}", ChatColors.Green.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LIME}", ChatColors.Lime.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{YELLOW}", ChatColors.Yellow.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LIGHTYELLOW}", ChatColors.LightYellow.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{PURPLE}", ChatColors.Purple.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{MAGENTA}", ChatColors.Magenta.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{LIGHTPURPLE}", ChatColors.LightPurple.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{GREY}", ChatColors.Grey.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{GRAY}", ChatColors.Grey.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{OLIVE}", ChatColors.Olive.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{BLUEGREY}", ChatColors.BlueGrey.ToString(), StringComparison.OrdinalIgnoreCase);
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


internal sealed class RezRankMenuOption(string text, Action<CCSPlayerController> action)
{
    public string Text { get; } = text;
    public Action<CCSPlayerController> Action { get; } = action;
}

internal sealed class RezRankMenuState
{
    public List<RezRankMenuOption> Options { get; } = new();
    public int SelectedIndex { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime LastInputUtc { get; set; }
    public string PreviousButtons { get; set; } = string.Empty;
}
