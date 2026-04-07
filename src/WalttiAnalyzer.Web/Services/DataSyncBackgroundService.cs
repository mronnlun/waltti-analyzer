using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Core.Models;
using WalttiAnalyzer.Core.Services;

namespace WalttiAnalyzer.Web.Services;

/// <summary>
/// Runs data synchronization on startup and then every 10 minutes.
/// Performs stop discovery, daily scheduled collection, and realtime polling.
/// </summary>
public class DataSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataSyncBackgroundService> _logger;
    private readonly WalttiSettings _settings;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public DataSyncBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<DataSyncBackgroundService> logger, IOptions<WalttiSettings> settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on startup then repeat every 10 minutes.
        await RunSyncCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSyncCycleAsync(stoppingToken);
    }

    private async Task RunSyncCycleAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.DigitransitApiKey))
        {
            _logger.LogWarning("DIGITRANSIT_API_KEY not set — skipping sync");
            return;
        }

        _logger.LogInformation("Sync cycle started at {Time}", DateTimeOffset.Now);

        using var scope = _scopeFactory.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<CollectorService>();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
            var feedId = _settings.FeedId;

            // Stop discovery: once per hour (at top of hour), or immediately if never done today
            bool shouldDiscover;
            var lastDiscovery = await db.GetLatestCollectionAsync(feedId, "discover");
            if (lastDiscovery == null)
            {
                shouldDiscover = true;
            }
            else
            {
                var lastTime = TimeZoneInfo.ConvertTime(
                    DateTimeOffset.FromUnixTimeSeconds(lastDiscovery.QueriedAt), HelsinkiTz);
                shouldDiscover = lastTime.Date < now.Date || (now - lastTime).TotalMinutes >= 50;
            }

            if (shouldDiscover)
            {
                _logger.LogInformation("Running stop discovery");
                var stops = await collector.DiscoverStopsAsync();
                _logger.LogInformation("Discovery result: {@Result}", stops);
            }

            _logger.LogInformation("Running daily collection");
            var daily = await collector.CollectDailyAsync();
            _logger.LogInformation("Daily collection result: {@Result}", daily);

            _logger.LogInformation("Running realtime poll");
            var realtime = await collector.PollRealtimeOnceAsync();
            _logger.LogInformation("Realtime poll result: {@Result}", realtime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync cycle failed");
        }
    }
}
