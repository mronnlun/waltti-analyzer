using System.Diagnostics;
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
    /// <summary>ActivitySource for distributed tracing of sync cycles.</summary>
    public static readonly ActivitySource ActivitySource = new("WalttiAnalyzer.Sync", "1.0.0");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataSyncBackgroundService> _logger;
    private readonly WalttiSettings _settings;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    /// <summary>Minimum minutes between stop discovery runs within the same day.</summary>
    private const int DiscoveryIntervalMinutes = 50;

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

        using var cycleActivity = ActivitySource.StartActivity("SyncCycle");

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
                shouldDiscover = lastTime.Date < now.Date || (now - lastTime).TotalMinutes >= DiscoveryIntervalMinutes;
            }

            if (shouldDiscover)
            {
                _logger.LogInformation("Running stop discovery");
                using var discoverActivity = ActivitySource.StartActivity("DiscoverStops");
                var stops = await collector.DiscoverStopsAsync();
                discoverActivity?.SetTag("result.status", stops.GetValueOrDefault("status"));
                _logger.LogInformation("Discovery result: {@Result}", stops);
            }

            _logger.LogInformation("Running daily collection");
            using (var dailyActivity = ActivitySource.StartActivity("CollectDaily"))
            {
                var daily = await collector.CollectDailyAsync();
                dailyActivity?.SetTag("result.status", daily.GetValueOrDefault("status"));
                dailyActivity?.SetTag("result.departures", daily.GetValueOrDefault("departures"));
                _logger.LogInformation("Daily collection result: {@Result}", daily);
            }

            _logger.LogInformation("Running realtime poll");
            using (var realtimeActivity = ActivitySource.StartActivity("PollRealtime"))
            {
                var realtime = await collector.PollRealtimeOnceAsync();
                realtimeActivity?.SetTag("result.status", realtime.GetValueOrDefault("status"));
                realtimeActivity?.SetTag("result.updated", realtime.GetValueOrDefault("updated"));
                _logger.LogInformation("Realtime poll result: {@Result}", realtime);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            cycleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Sync cycle failed");
        }
    }
}
