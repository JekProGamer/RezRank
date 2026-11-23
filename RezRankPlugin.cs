using CounterStrikeSharp.API.Core;
using Newtonsoft.Json;
using RezRank.Configuration;
using RezRank.Database;

namespace RezRank;

public sealed class RezRankPlugin : BasePlugin
{
    public override string ModuleName => "RezRank";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "JekProGamer";
    public override string ModuleDescription => "Custom XP ranks and player progression for CS2.";

    private RezRankConfig? _config;
    private RezRankDatabase? _database;
    private CancellationTokenSource? _shutdown;

    public override void Load(bool hotReload)
    {
        _shutdown = new CancellationTokenSource();

        try
        {
            var configPath = Path.Combine(ModuleDirectory, "config.json");
            _config = RezRankConfig.Load(configPath);

            if (!_config.Database.Enabled)
            {
                Logger.LogInformation("RezRank: MySQL is disabled in config.");
                return;
            }

            _database = new RezRankDatabase(_config.Database);
            _ = InitializeDatabaseAsync(_shutdown.Token);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: failed to load configuration.");
        }
    }

    public override void Unload(bool hotReload)
    {
        _shutdown?.Cancel();
        _shutdown?.Dispose();
        _shutdown = null;
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
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "RezRank: MySQL initialization failed.");
        }
    }
}
