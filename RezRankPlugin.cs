using System.Collections.Concurrent;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.UserMessages;
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
    private readonly Dictionary<string, int> _killStreaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _streakVictims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _onlineTabRanks = new(StringComparer.Ordinal);
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

            // The public !rank menu must work even when MySQL is disabled.
            // Register its tick handler before any database-related early return
            // so the menu remains visible and its navigation keys are processed.
            RegisterListener<Listeners.OnTick>(OnRankMenuTick);

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
            if (_config.RankProgression.XpLossPerDeath < 0)
                throw new InvalidOperationException("RankProgression.XpLossPerDeath cannot be negative.");

            if (_config.Achievements.DoubleKillXp < 0 ||
                _config.Achievements.TripleKillXp < 0 ||
                _config.Achievements.QuadKillXp < 0 ||
                _config.Achievements.AceXp < 0)
                throw new InvalidOperationException("Achievement XP rewards cannot be negative.");

            if (_config.DatabaseCleanup.CheckIntervalHours < 1)
                throw new InvalidOperationException("DatabaseCleanup.CheckIntervalHours must be at least 1.");
            if (_config.DatabaseCleanup.InactiveDays < 1)
                throw new InvalidOperationException("DatabaseCleanup.InactiveDays must be at least 1.");

            _database = new RezRankDatabase(_config.Database);
            _databaseReadyTask = InitializeDatabaseAsync(cancellationToken);

            RegisterListener<Listeners.OnTick>(OnCompetitiveRankTick);

            // EventPlayerConnectFull is not raised again for players already online
            // when the plugin is loaded or hot-reloaded. Seed their TAB ranks too.
            foreach (var onlinePlayer in Utilities.GetPlayers())
            {
                if (!onlinePlayer.IsValid || onlinePlayer.IsBot || onlinePlayer.IsHLTV)
                    continue;

                _ = RefreshTabRankAfterConnectAsync(
                    onlinePlayer.SteamID.ToString(),
                    onlinePlayer.PlayerName,
                    cancellationToken);
            }
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
                _ = RefreshTabRankAfterConnectAsync(steamId, playerName, cancellationToken);

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

                    _killStreaks.Remove(@event.Xuid.ToString());
                    _streakVictims.Remove(@event.Xuid.ToString());
                    _onlineTabRanks.TryRemove(@event.Xuid.ToString(), out _);
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
                var attackerIsRankAdmin = hasValidEnemyAttacker && IsRankAdmin(attacker!);
                var victimXpPenalty = hasValidEnemyAttacker && !attackerIsRankAdmin
                    ? _config!.RankProgression.XpLossPerDeath
                    : 0;
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

                    // Any real death ends the victim's current kill streak,
                    // including suicides and world deaths. Their unique-victim
                    // history is life-scoped and must be cleared as well.
                    _killStreaks.Remove(victimSteamId);
                    _streakVictims.Remove(victimSteamId);

                    if (!hasValidEnemyAttacker)
                    {
                        // Count environmental deaths and suicides, but never penalize XP.
                        _ = RecordDeathAsync(victimSteamId, victimName, cancellationToken);
                        return;
                    }

                    string? achievementName = null;
                    var achievementXp = 0;
                    if (_config!.Achievements.Enabled && !attackerIsBypass)
                    {
                        if (!_streakVictims.TryGetValue(attackerSteamId, out var victims))
                        {
                            victims = new HashSet<string>(StringComparer.Ordinal);
                            _streakVictims[attackerSteamId] = victims;
                        }

                        // Count each opponent only once during this attacker's
                        // current life. Re-killing the same opponent still goes
                        // through ProcessKillAsync and grants the normal kill XP,
                        // but cannot advance or reward the achievement streak.
                        if (victims.Add(victimSteamId))
                        {
                            var streak = _killStreaks.GetValueOrDefault(attackerSteamId) + 1;
                            _killStreaks[attackerSteamId] = streak;

                            (achievementName, achievementXp) = streak switch
                            {
                                2 => ("Дабл-килл", _config.Achievements.DoubleKillXp),
                                3 => ("Трипл-килл", _config.Achievements.TripleKillXp),
                                4 => ("Квад-килл", _config.Achievements.QuadKillXp),
                                5 => ("Эйс", _config.Achievements.AceXp),
                                _ => (null, 0)
                            };
                        }
                    }

                    _ = ProcessKillAsync(
                        attackerSteamId,
                        attackerName,
                        victimSteamId,
                        victimName,
                        victimIsBypass ? 20 : _config.RankProgression.XpPerKill,
                        victimXpPenalty,
                        attackerIsBypass,
                        achievementName,
                        achievementXp,
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


    [ConsoleCommand("css_top")]
    public void ShowTopAchievements(CCSPlayerController? player, CommandInfo? info)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        _ = SendAchievementLeaderboardAsync(player, _shutdown?.Token ?? CancellationToken.None);
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
        state.Options.Add(new RezRankMenuOption("Мой ранг", p =>
        {
            CloseRankMenu(p);
            _ = SendPlayerStatusAsync(p, includeStats: true, _shutdown?.Token ?? CancellationToken.None);
        }));
        state.Options.Add(new RezRankMenuOption("Следующий Ранг", p =>
        {
            CloseRankMenu(p);
            _ = SendRankProgressAsync(p, _shutdown?.Token ?? CancellationToken.None);
        }));
        state.Options.Add(new RezRankMenuOption("Достижения", p =>
        {
            CloseRankMenu(p);
            _ = SendAchievementStatsAsync(p, _shutdown?.Token ?? CancellationToken.None);
        }));
        state.Options.Add(new RezRankMenuOption("Таблица лидеров", p =>
        {
            CloseRankMenu(p);
            _ = SendLeaderboardAsync(p, _shutdown?.Token ?? CancellationToken.None);
        }));

        // The administration entry is shown only to players with the
        // CounterStrikeSharp generic admin permission.
        if (IsRankAdmin(player))
        {
            state.Options.Add(new RezRankMenuOption("Администрация", p =>
            {
                // Re-check permissions at the moment of selection as well.
                if (!IsRankAdmin(p))
                {
                    CloseRankMenu(p);
                    return;
                }

                CloseRankMenu(p);
                OpenAdminMenu(p);
            }, requiresAdmin: true));
        }

        _rankMenus[player.Slot] = state;
        RenderRankMenu(player, state);
    }

    private void OnRankMenuTick()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !_rankMenus.TryGetValue(player.Slot, out var state))
                continue;

            // If admin permissions are revoked while the menu is open,
            // immediately close the menu containing admin-only entries.
            if (state.Options.Any(option => option.RequiresAdmin) && !IsRankAdmin(player))
            {
                CloseRankMenu(player);
                continue;
            }

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
        html.Append($"<b><font color='red' class='fontSize-m'>{state.Title}</font></b> <font color='yellow' class='fontSize-sm'>{selected + 1}</font>/<font color='orange' class='fontSize-sm'>{total}</font><br>");
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

                var nextRank = _config.RankProgression.Ranks
                    .Where(r => r != null && r.MinXp > record.Xp)
                    .OrderBy(r => r.MinXp)
                    .FirstOrDefault();

                var kd = record.Deaths == 0
                    ? record.Kills.ToString()
                    : ((double)record.Kills / record.Deaths).ToString("0.00");

                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{GOLD}          МОЙ РАНГ"));
                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat(_config.ChatMessages.YourRank, ("%RANK%", rank), ("%XP%", record.Xp.ToString())));
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
                player.PrintToChat(FormatChat("{SILVER}####################"));
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
                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{GOLD}     СЛЕДУЮЩИЙ РАНГ"));
                player.PrintToChat(FormatChat("{SILVER}####################"));
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
                    player.PrintToChat(FormatChat("{SILVER}####################"));
                    return;
                }

                player.PrintToChat(FormatChat(
                    _config.ChatMessages.NextRank,
                    ("%NEXT_RANK%", nextRank.Name),
                    ("%XP_LEFT%", (nextRank.MinXp - record.Xp).ToString())));
                player.PrintToChat(FormatChat("{SILVER}####################"));
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

    private async Task SendAchievementStatsAsync(
        CCSPlayerController player,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            var steamId = player.SteamID.ToString();
            await _databaseReadyTask;
            var stats = await _database.GetAchievementStatsAsync(steamId, cancellationToken);

            Server.NextFrame(() =>
            {
                if (!player.IsValid || player.SteamID.ToString() != steamId)
                    return;

                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{GOLD}     МОИ ДОСТИЖЕНИЯ"));
                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{WHITE}Дабл-килл: {GREEN}%COUNT%", ("%COUNT%", stats.DoubleKills.ToString())));
                player.PrintToChat(FormatChat("{WHITE}Трипл-килл: {GREEN}%COUNT%", ("%COUNT%", stats.TripleKills.ToString())));
                player.PrintToChat(FormatChat("{WHITE}Квад-килл: {GREEN}%COUNT%", ("%COUNT%", stats.QuadKills.ToString())));
                player.PrintToChat(FormatChat("{WHITE}Эйс: {GREEN}%COUNT%", ("%COUNT%", stats.Aces.ToString())));
                player.PrintToChat(FormatChat("{SILVER}####################"));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to display achievement statistics.");
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    player.PrintToChat(FormatChat(_config?.ChatMessages.Error ?? "{RED}Не удалось получить достижения."));
            });
        }
    }

    private async Task SendAchievementLeaderboardAsync(
        CCSPlayerController player,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            var steamId = player.SteamID.ToString();
            await _databaseReadyTask;
            var topPlayers = await _database.GetTopAchievementPlayersAsync(5, cancellationToken);

            Server.NextFrame(() =>
            {
                if (!player.IsValid || player.SteamID.ToString() != steamId)
                    return;

                if (topPlayers.Count == 0)
                {
                    player.PrintToChat(FormatChat(_config.ChatMessages.NoData));
                    return;
                }

                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{GOLD}       ТОП-5 ДОСТИЖЕНИЙ"));
                player.PrintToChat(FormatChat("{SILVER}####################"));

                for (var index = 0; index < topPlayers.Count; index++)
                {
                    var record = topPlayers[index];
                    var playerName = string.IsNullOrWhiteSpace(record.PlayerName)
                        ? "Неизвестный игрок"
                        : record.PlayerName;
                    var placeColor = index == 0 ? "{GOLD}" : index == 1 ? "{SILVER}" : index == 2 ? "{ORANGE}" : "{LIGHTBLUE}";

                    player.PrintToChat(FormatChat(
                        "%PLACE_COLOR%#%PLACE% {WHITE}%PLAYER% {SILVER}| {GREEN}%TOTAL% наград",
                        ("%PLACE_COLOR%", placeColor),
                        ("%PLACE%", (index + 1).ToString()),
                        ("%PLAYER%", playerName),
                        ("%TOTAL%", record.Total.ToString())));
                    player.PrintToChat(FormatChat(
                        "{GREY}   Дабл-килл: %DOUBLE% | Трипл-килл: %TRIPLE% | Квад-килл: %QUAD% | Эйс: %ACE%",
                        ("%DOUBLE%", record.DoubleKills.ToString()),
                        ("%TRIPLE%", record.TripleKills.ToString()),
                        ("%QUAD%", record.QuadKills.ToString()),
                        ("%ACE%", record.Aces.ToString())));
                }

                player.PrintToChat(FormatChat("{SILVER}####################"));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to display the achievement leaderboard.");
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    player.PrintToChat(FormatChat(_config?.ChatMessages.Error ?? "{RED}Не удалось получить топ достижений."));
            });
        }
    }

    private async Task SendLeaderboardAsync(
        CCSPlayerController player,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            var steamId = player.SteamID.ToString();
            await _databaseReadyTask;
            var topPlayers = await _database.GetTopPlayersAsync(10, cancellationToken);

            Server.NextFrame(() =>
            {
                if (!player.IsValid || player.SteamID.ToString() != steamId)
                    return;

                if (topPlayers.Count == 0)
                {
                    player.PrintToChat(FormatChat(_config.ChatMessages.NoData));
                    return;
                }

                player.PrintToChat(FormatChat("{SILVER}####################"));
                player.PrintToChat(FormatChat("{GOLD}       TOP 10 REZRANK"));
                player.PrintToChat(FormatChat("{SILVER}####################"));

                for (var index = 0; index < topPlayers.Count; index++)
                {
                    var record = topPlayers[index];
                    var rank = IsBypass(record.SteamId)
                        ? _config.Bypass.RankName
                        : GetRank(record.Xp).Name;
                    var playerName = string.IsNullOrWhiteSpace(record.LastKnownName)
                        ? "Неизвестный игрок"
                        : record.LastKnownName;
                    var placeColor = index == 0 ? "{GOLD}" : index == 1 ? "{SILVER}" : index == 2 ? "{ORANGE}" : "{LIGHTBLUE}";

                    player.PrintToChat(FormatChat(
                        "%PLACE_COLOR%#%PLACE% {WHITE}%PLAYER% {SILVER}| {BLUE}%RANK% {SILVER}| {YELLOW}XP: {GREEN}%XP%",
                        ("%PLACE_COLOR%", placeColor),
                        ("%PLACE%", (index + 1).ToString()),
                        ("%PLAYER%", playerName),
                        ("%RANK%", rank),
                        ("%XP%", record.Xp.ToString())));
                }

                player.PrintToChat(FormatChat("{SILVER}####################"));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to display the XP leaderboard.");
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                    player.PrintToChat(FormatChat(_config?.ChatMessages.Error ?? "{RED}Не удалось получить таблицу лидеров."));
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


    private async Task RefreshTabRankAfterConnectAsync(string steamId, string playerName, CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            await _database.UpsertPlayerActivityAsync(steamId, playerName, cancellationToken);
            var record = await _database.GetPlayerAsync(steamId, cancellationToken);
            UpdateOnlineTabRank(steamId, record?.Xp ?? 0);
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to load TAB rank for {SteamId}.", steamId);
        }
    }

    private void UpdateOnlineTabRank(string steamId, long xp)
    {
        if (_config == null)
            return;

        var rankIndex = IsBypass(steamId)
            ? 17
            : _config.RankProgression.Ranks
                .Where(rank => rank != null)
                .OrderBy(rank => rank.MinXp)
                .TakeWhile(rank => rank.MinXp <= xp)
                .Count() - 1;

        var competitiveRanking = 1 + Math.Clamp(rankIndex, 0, 17);

        // Cache the desired standard CS2 competitive rank (Silver I = 1, Global Elite = 18).
        // The tick listener reapplies it if the game overwrites the player's rank fields.
        Server.NextFrame(() => _onlineTabRanks[steamId] = competitiveRanking);
        Logger.LogDebug(
            "RezRank: queued TAB rank {CompetitiveRanking} for {SteamId} (XP: {Xp}).",
            competitiveRanking,
            steamId,
            xp);
    }

    private void OnCompetitiveRankTick()
    {
        const sbyte competitiveRankType = 12;
        var recipients = new RecipientFilter();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            var steamId = player.SteamID.ToString();
            if (!_onlineTabRanks.TryGetValue(steamId, out var competitiveRanking))
                continue;

            if (player.CompetitiveRankType == competitiveRankType &&
                player.CompetitiveRanking == competitiveRanking)
                continue;

            player.CompetitiveWins = 777;
            player.CompetitiveRankType = competitiveRankType;
            player.CompetitiveRanking = competitiveRanking;
            recipients.Add(player);
        }

        if (recipients.Count > 0)
            UserMessage.FromId(350).Send(recipients);
    }

    private async Task ProcessKillAsync(
        string attackerSteamId,
        string attackerName,
        string victimSteamId,
        string victimName,
        int xpReward,
        int victimXpPenalty,
        bool attackerIsBypass,
        string? achievementName,
        int achievementXp,
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
                attackerIsBypass ? 0 : checked(xpReward + achievementXp),
                achievementName,
                cancellationToken);

            UpdateOnlineTabRank(attackerSteamId, result.CurrentXp);

            var killXpAwarded = attackerIsBypass ? 0 : xpReward;
            if (killXpAwarded > 0)
            {
                Server.NextFrame(() =>
                {
                    var player = Utilities.GetPlayers().FirstOrDefault(candidate =>
                        candidate.IsValid &&
                        !candidate.IsBot &&
                        !candidate.IsHLTV &&
                        candidate.SteamID.ToString() == attackerSteamId);
                    player?.PrintToChat(FormatChat(
                        _config!.ChatMessages.KillReward,
                        ("%XP%", killXpAwarded.ToString())));
                });
            }

            if (victimXpPenalty > 0)
            {
                var victimResult = await _database.AdjustPlayerXpAsync(
                    victimSteamId,
                    victimName,
                    -victimXpPenalty,
                    cancellationToken);
                var actualXpLost = victimResult.PreviousXp - victimResult.CurrentXp;
                UpdateOnlineTabRank(victimSteamId, victimResult.CurrentXp);

                var victimOldRank = GetRank(victimResult.PreviousXp);
                var victimNewRank = GetRank(victimResult.CurrentXp);
                var victimRankedDown = victimNewRank.MinXp < victimOldRank.MinXp;

                if (victimRankedDown)
                {
                    Server.NextFrame(() =>
                    {
                        var victim = Utilities.GetPlayers().FirstOrDefault(candidate =>
                            candidate.IsValid &&
                            !candidate.IsBot &&
                            !candidate.IsHLTV &&
                            candidate.SteamID.ToString() == victimSteamId);
                        if (victim != null)
                            PlayRankDownSound(victim);
                    });
                }

                if (actualXpLost > 0)
                {
                    Server.NextFrame(() =>
                    {
                        var victim = Utilities.GetPlayers().FirstOrDefault(candidate =>
                            candidate.IsValid &&
                            !candidate.IsBot &&
                            !candidate.IsHLTV &&
                            candidate.SteamID.ToString() == victimSteamId);
                        victim?.PrintToChat(FormatChat(
                            "{SILVER}[{ORANGE}R{RED}Rank{SILVER}] {WHITE}За смерть от игрока снято {RED}-%XP% XP{WHITE}. Осталось: {YELLOW}%TOTAL% XP",
                            ("%XP%", actualXpLost.ToString()),
                            ("%TOTAL%", victimResult.CurrentXp.ToString())));
                    });
                }

                Logger.LogInformation(
                    "RezRank: {PlayerName} lost {XpLost} XP for being killed by an ordinary enemy player ({Xp} XP remain).",
                    victimName,
                    actualXpLost,
                    victimResult.CurrentXp);
            }

            if (attackerIsBypass)
                return;

            if (!string.IsNullOrWhiteSpace(achievementName))
            {
                Server.NextFrame(() =>
                {
                    var player = Utilities.GetPlayers().FirstOrDefault(candidate =>
                        candidate.IsValid &&
                        !candidate.IsBot &&
                        candidate.SteamID.ToString() == attackerSteamId);
                    player?.PrintToChat(FormatChat(
                        _config!.Achievements.Message,
                        ("%ACHIEVEMENT%", achievementName),
                        ("%XP%", achievementXp.ToString())));
                });
            }

            var oldRank = GetRank(result.PreviousXp);
            var newRank = GetRank(result.CurrentXp);

            if (!string.Equals(oldRank.Name, newRank.Name, StringComparison.Ordinal))
            {
                var lastRank = _config.RankProgression.Ranks
                    .Where(rank => rank != null)
                    .OrderBy(rank => rank.MinXp)
                    .LastOrDefault();
                var reachedMaximumRank = lastRank != null &&
                    string.Equals(newRank.Name, lastRank.Name, StringComparison.Ordinal);

                Server.NextFrame(() =>
                {
                    var player = Utilities.GetPlayers().FirstOrDefault(candidate =>
                        candidate.IsValid &&
                        !candidate.IsBot &&
                        !candidate.IsHLTV &&
                        candidate.SteamID.ToString() == attackerSteamId);

                    if (player != null)
                    {
                        if (newRank.MinXp > oldRank.MinXp)
                            PlayRankUpSound(player);
                        else if (newRank.MinXp < oldRank.MinXp)
                            PlayRankDownSound(player);
                    }

                    if (reachedMaximumRank && _config.Announcements.BroadcastMaxRank)
                    {
                        Server.PrintToChatAll(FormatChat(
                            _config.Announcements.MaxRankReached,
                            ("%PLAYER%", attackerName),
                            ("%RANK%", newRank.Name)));
                        return;
                    }

                    player?.PrintToChat(FormatChat(
                        _config.ChatMessages.RankUp,
                        ("%PLAYER%", attackerName),
                        ("%RANK%", newRank.Name)));
                });

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


    private void PlayRankUpSound(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot || player.IsHLTV)
            return;

        try
        {
            // Built-in CS2 rank-promotion sound; no custom Workshop addon is required.
            player.ExecuteClientCommand("play sounds/ui/premier_eom_rankup");
        }
        catch (Exception exception)
        {
            // A sound playback failure must never interrupt rank progression.
            Logger.LogDebug(exception, "RezRank: failed to play rank-up sound for player {SteamId}.", player.SteamID);
        }
    }

    private void PlayRankDownSound(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot || player.IsHLTV)
            return;

        try
        {
            // Built-in CS2 negative feedback sound for rank demotion.
            player.ExecuteClientCommand("play sounds/ui/weapon_cant_buy.vsnd_c");
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "RezRank: failed to play rank-down sound for player {SteamId}.", player.SteamID);
        }
    }

    private static string FormatChat(string template, params (string Token, string Value)[] replacements)
    {
        foreach (var (token, value) in replacements)
            template = template.Replace(token, value, StringComparison.Ordinal);

        // Keep a leading space before the first color control character.
        // In CS2 chat, a color code at character zero can be consumed as the
        // message's initial control byte, leaving the first visible text white.
        // PrintToChat does not parse {COLOR} tags, so convert them to CSS codes.
        return " " + template
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

    private enum AdminAction
    {
        AddXp,
        RemoveXp,
        SetXp,
        SetRank,
        RemoveRank,
        ResetProgress,
        PlayerInfo
    }

    private sealed record PendingAdminXpChange(
        string TargetSteamId,
        string TargetName,
        AdminAction Action);

    private void OpenAdminMenu(CCSPlayerController admin)
    {
        if (!IsRankAdmin(admin))
        {
            CloseRankMenu(admin);
            return;
        }

        var state = new RezRankMenuState
        {
            Title = "RezRank — администрация",
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = admin.Buttons.ToString()
        };

        state.Options.Add(new RezRankMenuOption("Выдать XP", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.AddXp);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Снять XP", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.RemoveXp);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Установить XP", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.SetXp);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Сбросить прогресс", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.ResetProgress);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Выдать звание", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.SetRank);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Забрать звание", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.RemoveRank);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Информация об игроке", p =>
        {
            CloseRankMenu(p);
            OpenAdminPlayerSelection(p, AdminAction.PlayerInfo);
        }, requiresAdmin: true));
        state.Options.Add(new RezRankMenuOption("Назад", p =>
        {
            CloseRankMenu(p);
            OpenRankMenu(p, null);
        }, requiresAdmin: true));

        _rankMenus[admin.Slot] = state;
        RenderRankMenu(admin, state);
    }

    private void OpenAdminPlayerSelection(CCSPlayerController admin, AdminAction action)
    {
        if (!IsRankAdmin(admin))
        {
            CloseRankMenu(admin);
            return;
        }

        var state = new RezRankMenuState
        {
            Title = "RezRank — выбор игрока",
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = admin.Buttons.ToString()
        };

        foreach (var target in Utilities.GetPlayers()
                     .Where(candidate => candidate.IsValid && !candidate.IsBot && !candidate.IsHLTV)
                     .OrderBy(candidate => candidate.PlayerName, StringComparer.OrdinalIgnoreCase))
        {
            var targetSteamId = target.SteamID.ToString();
            var targetName = target.PlayerName;
            state.Options.Add(new RezRankMenuOption(targetName, p =>
            {
                if (!IsRankAdmin(p))
                {
                    CloseRankMenu(p);
                    return;
                }

                if (action is AdminAction.AddXp or AdminAction.RemoveXp or AdminAction.SetXp)
                {
                    OpenAdminXpAmountSelection(
                        p,
                        new PendingAdminXpChange(targetSteamId, targetName, action));
                    return;
                }

                if (action == AdminAction.SetRank)
                {
                    OpenAdminRankSelection(p, targetSteamId, targetName);
                    return;
                }

                if (action == AdminAction.RemoveRank)
                {
                    CloseRankMenu(p);
                    _ = ApplyAdminRemoveRankAsync(
                        p,
                        targetSteamId,
                        targetName,
                        _shutdown?.Token ?? CancellationToken.None);
                    return;
                }

                CloseRankMenu(p);
                if (action == AdminAction.ResetProgress)
                    _ = ResetAdminPlayerProgressAsync(p, targetSteamId, targetName, _shutdown?.Token ?? CancellationToken.None);
                else
                    _ = SendAdminPlayerInfoAsync(p, targetSteamId, targetName, _shutdown?.Token ?? CancellationToken.None);
            }, requiresAdmin: true));
        }

        state.Options.Add(new RezRankMenuOption("Назад", p =>
        {
            CloseRankMenu(p);
            OpenAdminMenu(p);
        }, requiresAdmin: true));

        _rankMenus[admin.Slot] = state;
        RenderRankMenu(admin, state);
    }

    private void OpenAdminXpAmountSelection(
        CCSPlayerController admin,
        PendingAdminXpChange pending)
    {
        if (!IsRankAdmin(admin))
        {
            CloseRankMenu(admin);
            return;
        }

        var state = new RezRankMenuState
        {
            Title = $"RezRank — XP для {pending.TargetName}",
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = admin.Buttons.ToString()
        };

        foreach (var amount in new long[] { 30, 100, 200 })
        {
            var selectedAmount = amount;
            state.Options.Add(new RezRankMenuOption($"{selectedAmount} XP", p =>
            {
                if (!IsRankAdmin(p))
                {
                    CloseRankMenu(p);
                    return;
                }

                _ = ApplyAdminXpAmountAsync(
                    p,
                    pending,
                    selectedAmount,
                    _shutdown?.Token ?? CancellationToken.None);
                OpenAdminMenu(p);
            }, requiresAdmin: true));
        }

        state.Options.Add(new RezRankMenuOption("Ввести своё количество", p =>
        {
            OpenAdminXpCustomInputPrompt(p, pending);
        }, requiresAdmin: true));

        state.Options.Add(new RezRankMenuOption("Назад", p =>
        {
            OpenAdminPlayerSelection(p, pending.Action);
        }, requiresAdmin: true));

        _rankMenus[admin.Slot] = state;
        RenderRankMenu(admin, state);
    }

    private void OpenAdminXpCustomInputPrompt(
        CCSPlayerController admin,
        PendingAdminXpChange pending,
        long amount = 0)
    {
        if (!IsRankAdmin(admin))
        {
            CloseRankMenu(admin);
            return;
        }

        amount = Math.Max(0, amount);
        var state = new RezRankMenuState
        {
            Title = $"Своя сумма XP: {amount.ToString(CultureInfo.InvariantCulture)}",
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = admin.Buttons.ToString()
        };

        foreach (var step in new long[] { 10000, 1000, 100, 10, 1 })
        {
            var selectedStep = step;
            state.Options.Add(new RezRankMenuOption($"+{selectedStep} XP", p =>
            {
                var nextAmount = amount > long.MaxValue - selectedStep
                    ? long.MaxValue
                    : amount + selectedStep;
                OpenAdminXpCustomInputPrompt(p, pending, nextAmount);
            }, requiresAdmin: true));
        }

        foreach (var step in new long[] { 10000, 1000, 100, 10, 1 })
        {
            var selectedStep = step;
            state.Options.Add(new RezRankMenuOption($"−{selectedStep} XP", p =>
            {
                var nextAmount = Math.Max(0, amount - selectedStep);
                OpenAdminXpCustomInputPrompt(p, pending, nextAmount);
            }, requiresAdmin: true));
        }

        state.Options.Add(new RezRankMenuOption(
            $"Подтвердить: {amount.ToString(CultureInfo.InvariantCulture)} XP",
            p =>
            {
                if (!IsRankAdmin(p))
                {
                    CloseRankMenu(p);
                    return;
                }

                _ = ApplyAdminXpAmountAsync(
                    p,
                    pending,
                    amount,
                    _shutdown?.Token ?? CancellationToken.None);
                OpenAdminMenu(p);
            },
            requiresAdmin: true));

        state.Options.Add(new RezRankMenuOption("Назад", p =>
        {
            OpenAdminXpAmountSelection(p, pending);
        }, requiresAdmin: true));

        _rankMenus[admin.Slot] = state;
        RenderRankMenu(admin, state);
    }

    private void OpenAdminRankSelection(
        CCSPlayerController admin,
        string targetSteamId,
        string targetName)
    {
        if (!IsRankAdmin(admin) || _config == null)
        {
            CloseRankMenu(admin);
            return;
        }

        var state = new RezRankMenuState
        {
            Title = $"RezRank — звание для {targetName}",
            SelectedIndex = 0,
            OpenedAtUtc = DateTime.UtcNow,
            LastInputUtc = DateTime.UtcNow,
            PreviousButtons = admin.Buttons.ToString()
        };

        foreach (var rank in _config.RankProgression.Ranks
                     .Where(rank => rank != null && !string.IsNullOrWhiteSpace(rank.Name))
                     .GroupBy(rank => rank.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderBy(rank => rank.MinXp).First())
                     .OrderBy(rank => rank.MinXp))
        {
            var selectedRank = rank;
            state.Options.Add(new RezRankMenuOption(selectedRank.Name, p =>
            {
                if (!IsRankAdmin(p))
                {
                    CloseRankMenu(p);
                    return;
                }

                _ = ApplyAdminRankAsync(
                    p,
                    targetSteamId,
                    targetName,
                    selectedRank,
                    _shutdown?.Token ?? CancellationToken.None);
                OpenAdminMenu(p);
            }, requiresAdmin: true));
        }

        state.Options.Add(new RezRankMenuOption("Назад", p =>
        {
            OpenAdminPlayerSelection(p, AdminAction.SetRank);
        }, requiresAdmin: true));

        _rankMenus[admin.Slot] = state;
        RenderRankMenu(admin, state);
    }

    private async Task ApplyAdminRankAsync(
        CCSPlayerController admin,
        string targetSteamId,
        string targetName,
        RankDefinition rank,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            var result = await _database.SetPlayerXpAsync(
                targetSteamId,
                targetName,
                rank.MinXp,
                cancellationToken);

            UpdateOnlineTabRank(targetSteamId, result.CurrentXp);

            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                {
                    admin.PrintToChat(FormatChat(
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {YELLOW}Вы установили игроку {SILVER}%PLAYER% {YELLOW}звание {BLUE}%RANK%{GREEN}! {YELLOW}XP: %NEW%.",
                        ("%PLAYER%", targetName),
                        ("%RANK%", rank.Name),
                        ("%NEW%", result.CurrentXp.ToString(CultureInfo.InvariantCulture))));
                }

                var target = Utilities.GetPlayers().FirstOrDefault(candidate =>
                    candidate.IsValid &&
                    !candidate.IsBot &&
                    !candidate.IsHLTV &&
                    candidate.SteamID.ToString() == targetSteamId);

                if (target != null)
                {
                    var previousRank = GetRank(result.PreviousXp);
                    var currentRank = GetRank(result.CurrentXp);
                    if (currentRank.MinXp > previousRank.MinXp)
                        PlayRankUpSound(target);
                    else if (currentRank.MinXp < previousRank.MinXp)
                        PlayRankDownSound(target);
                }

                target?.PrintToChat(FormatChat(
                    "{SILVER}[{ORANGE}RezRank{SILVER}] {WHITE}Администратор установил вам звание {BLUE}%RANK%{WHITE}. Ваш опыт: {YELLOW}%XP% XP.",
                    ("%RANK%", rank.Name),
                    ("%XP%", result.CurrentXp.ToString(CultureInfo.InvariantCulture))));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: admin rank assignment failed for {SteamId}.", targetSteamId);
            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat("{SILVER}[{ORANGE}RezRank{SILVER}] {RED}Не удалось выдать звание игроку."));
            });
        }
    }

    private async Task ApplyAdminRemoveRankAsync(
        CCSPlayerController admin,
        string targetSteamId,
        string targetName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            await _databaseReadyTask;

            // Removing a rank resets XP to the Silver I threshold only.
            // SetPlayerXpAsync updates XP and player activity, preserving kills,
            // deaths, and all achievement records.
            var silverRank = _config.RankProgression.Ranks
                .Where(rank => rank != null && !string.IsNullOrWhiteSpace(rank.Name))
                .OrderBy(rank => rank.MinXp)
                .FirstOrDefault();

            if (silverRank == null)
                throw new InvalidOperationException("No ranks are configured.");

            var result = await _database.SetPlayerXpAsync(
                targetSteamId,
                targetName,
                0,
                cancellationToken);

            UpdateOnlineTabRank(targetSteamId, result.CurrentXp);

            var previousRank = GetRank(result.PreviousXp);
            var currentRank = GetRank(result.CurrentXp);
            var rankedDown = currentRank.MinXp < previousRank.MinXp;

            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                {
                    admin.PrintToChat(FormatChat(
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {GREEN}Вы забрали звание у игрока {SILVER}%PLAYER%{GREEN}. Ему установлен ранг {BLUE}%RANK%{GREEN}, XP: {YELLOW}0.",
                        ("%PLAYER%", targetName),
                        ("%RANK%", silverRank.Name)));
                }

                var target = Utilities.GetPlayers().FirstOrDefault(candidate =>
                    candidate.IsValid &&
                    !candidate.IsBot &&
                    !candidate.IsHLTV &&
                    candidate.SteamID.ToString() == targetSteamId);

                if (target == null)
                    return;

                if (rankedDown)
                    PlayRankDownSound(target);

                target.PrintToChat(FormatChat(
                    "{SILVER}[{ORANGE}RezRank{SILVER}] {WHITE}Администратор забрал у вас звание. Вам установлен ранг {BLUE}%RANK%{WHITE}, ваш опыт: {YELLOW}0 XP.",
                    ("%RANK%", silverRank.Name)));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to remove rank from {SteamId}.", targetSteamId);
            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat(
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {RED}Не удалось забрать звание у игрока."));
            });
        }
    }

    private async Task ApplyAdminXpAmountAsync(
        CCSPlayerController admin,
        PendingAdminXpChange pending,
        long amount,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            var result = pending.Action switch
            {
                AdminAction.AddXp => await _database.AdjustPlayerXpAsync(
                    pending.TargetSteamId, pending.TargetName, amount, cancellationToken),
                AdminAction.RemoveXp => await _database.AdjustPlayerXpAsync(
                    pending.TargetSteamId, pending.TargetName, -amount, cancellationToken),
                AdminAction.SetXp => await _database.SetPlayerXpAsync(
                    pending.TargetSteamId, pending.TargetName, amount, cancellationToken),
                _ => throw new InvalidOperationException("Unsupported admin XP operation.")
            };

            UpdateOnlineTabRank(pending.TargetSteamId, result.CurrentXp);

            var previousRank = GetRank(result.PreviousXp);
            var currentRank = GetRank(result.CurrentXp);
            var rankedUp = currentRank.MinXp > previousRank.MinXp;
            var rankedDown = currentRank.MinXp < previousRank.MinXp;

            Server.NextFrame(() =>
            {
                var isAdd = pending.Action == AdminAction.AddXp;
                var isRemove = pending.Action == AdminAction.RemoveXp;
                var displayedAmount = isAdd || isRemove
                    ? Math.Abs(result.CurrentXp - result.PreviousXp)
                    : result.CurrentXp;

                if (admin.IsValid && IsRankAdmin(admin))
                {
                    var adminMessage = pending.Action switch
                    {
                        AdminAction.AddXp =>
                            "{SILVER}[{ORANGE}RezRank{SILVER}] {YELLOW}Вы выдали игроку {SILVER}%PLAYER% {YELLOW}%AMOUNT% XP{GREEN}!",
                        AdminAction.RemoveXp =>
                            "{SILVER}[{ORANGE}RezRank{SILVER}] {YELLOW}Вы сняли у игрока {SILVER}%PLAYER% {YELLOW}%AMOUNT% XP{GREEN}!",
                        _ =>
                            "{SILVER}[{ORANGE}RezRank{SILVER}] {YELLOW}Вы установили игроку {SILVER}%PLAYER% {YELLOW}%AMOUNT% XP{GREEN}!"
                    };

                    admin.PrintToChat(FormatChat(
                        adminMessage + " {GREY}Было: %OLD%, стало: %NEW%.",
                        ("%PLAYER%", pending.TargetName),
                        ("%AMOUNT%", displayedAmount.ToString(CultureInfo.InvariantCulture)),
                        ("%OLD%", result.PreviousXp.ToString(CultureInfo.InvariantCulture)),
                        ("%NEW%", result.CurrentXp.ToString(CultureInfo.InvariantCulture))));
                }

                var target = Utilities.GetPlayers().FirstOrDefault(candidate =>
                    candidate.IsValid &&
                    !candidate.IsBot &&
                    !candidate.IsHLTV &&
                    candidate.SteamID.ToString() == pending.TargetSteamId);

                if (target == null)
                    return;

                if (rankedUp)
                    PlayRankUpSound(target);
                else if (rankedDown)
                    PlayRankDownSound(target);

                var targetMessage = pending.Action switch
                {
                    AdminAction.AddXp =>
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {WHITE}Администратор выдал вам {YELLOW}%AMOUNT% XP{GREEN}!",
                    AdminAction.RemoveXp =>
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {WHITE}Администратор снял у вас {YELLOW}%AMOUNT% XP{GREEN}!",
                    _ =>
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {WHITE}Администратор установил вам {YELLOW}%AMOUNT% XP{GREEN}!"
                };

                target.PrintToChat(FormatChat(
                    targetMessage,
                    ("%AMOUNT%", displayedAmount.ToString(CultureInfo.InvariantCulture))));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: admin XP operation failed for {SteamId}.", pending.TargetSteamId);
            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat("{SILVER}[{ORANGE}RezRank{SILVER}] {RED}Не удалось изменить XP игрока."));
            });
        }
    }

    private async Task ResetAdminPlayerProgressAsync(
        CCSPlayerController admin,
        string targetSteamId,
        string targetName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null)
                return;

            await _databaseReadyTask;
            await _database.ResetPlayerProgressAsync(targetSteamId, targetName, cancellationToken);
            UpdateOnlineTabRank(targetSteamId, 0);
            Server.NextFrame(() =>
            {
                _killStreaks.Remove(targetSteamId);
                _streakVictims.Remove(targetSteamId);
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat(
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {GREEN}Прогресс игрока {YELLOW}%PLAYER% {GREEN}сброшен: XP, убийства, смерти и достижения обнулены.",
                        ("%PLAYER%", targetName)));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to reset progress for {SteamId}.", targetSteamId);
            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat("{SILVER}[{ORANGE}RezRank{SILVER}] {RED}Не удалось сбросить прогресс игрока."));
            });
        }
    }

    private async Task SendAdminPlayerInfoAsync(
        CCSPlayerController admin,
        string targetSteamId,
        string targetName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_database == null || _config == null)
                return;

            await _databaseReadyTask;
            var record = await _database.GetPlayerAsync(targetSteamId, cancellationToken);
            var achievements = await _database.GetAchievementStatsAsync(targetSteamId, cancellationToken);
            Server.NextFrame(() =>
            {
                if (!admin.IsValid || !IsRankAdmin(admin))
                    return;

                if (record == null)
                {
                    admin.PrintToChat(FormatChat(
                        "{SILVER}[{ORANGE}RezRank{SILVER}] {YELLOW}У игрока %PLAYER% пока нет записи в базе.",
                        ("%PLAYER%", targetName)));
                    return;
                }

                var rank = IsBypass(targetSteamId) ? _config.Bypass.RankName : GetRank(record.Xp).Name;
                admin.PrintToChat(FormatChat("{SILVER}######## {GOLD}ИНФОРМАЦИЯ ОБ ИГРОКЕ {SILVER}########"));
                admin.PrintToChat(FormatChat("{YELLOW}Игрок: {GREEN}%PLAYER% {SILVER}| {YELLOW}SteamID: {GREEN}%STEAMID%",
                    ("%PLAYER%", record.LastKnownName),
                    ("%STEAMID%", record.SteamId)));
                admin.PrintToChat(FormatChat("{YELLOW}Ранг: {GREEN}%RANK% {SILVER}| {YELLOW}XP: {GREEN}%XP%",
                    ("%RANK%", rank),
                    ("%XP%", record.Xp.ToString(CultureInfo.InvariantCulture))));
                admin.PrintToChat(FormatChat("{YELLOW}Убийства: {GREEN}%KILLS% {SILVER}| {YELLOW}Смерти: {GREEN}%DEATHS%",
                    ("%KILLS%", record.Kills.ToString(CultureInfo.InvariantCulture)),
                    ("%DEATHS%", record.Deaths.ToString(CultureInfo.InvariantCulture))));
                admin.PrintToChat(FormatChat("{YELLOW}Дабл: {GREEN}%DOUBLE% {SILVER}| {YELLOW}Трипл: {GREEN}%TRIPLE% {SILVER}| {YELLOW}Квад: {GREEN}%QUAD% {SILVER}| {YELLOW}Эйс: {GREEN}%ACE%",
                    ("%DOUBLE%", achievements.DoubleKills.ToString(CultureInfo.InvariantCulture)),
                    ("%TRIPLE%", achievements.TripleKills.ToString(CultureInfo.InvariantCulture)),
                    ("%QUAD%", achievements.QuadKills.ToString(CultureInfo.InvariantCulture)),
                    ("%ACE%", achievements.Aces.ToString(CultureInfo.InvariantCulture))));
                admin.PrintToChat(FormatChat("{SILVER}########################################"));
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to read admin player info for {SteamId}.", targetSteamId);
            Server.NextFrame(() =>
            {
                if (admin.IsValid && IsRankAdmin(admin))
                    admin.PrintToChat(FormatChat("{SILVER}[{ORANGE}RezRank{SILVER}] {RED}Не удалось получить информацию об игроке."));
            });
        }
    }

    private static bool IsRankAdmin(CCSPlayerController player)
    {
        return player.IsValid &&
               !player.IsBot &&
               !player.IsHLTV &&
               AdminManager.PlayerHasPermissions(player, "@css/generic");
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


internal sealed class RezRankMenuOption(
    string text,
    Action<CCSPlayerController> action,
    bool requiresAdmin = false)
{
    public string Text { get; } = text;
    public Action<CCSPlayerController> Action { get; } = action;
    public bool RequiresAdmin { get; } = requiresAdmin;
}

internal sealed class RezRankMenuState
{
    public List<RezRankMenuOption> Options { get; } = new();
    public string Title { get; set; } = "RezRank — меню игрока";
    public int SelectedIndex { get; set;}
    public DateTime OpenedAtUtc { get; set; }
    public DateTime LastInputUtc { get; set; }
    public string PreviousButtons { get; set; } = string.Empty;
}