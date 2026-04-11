using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Core.Models;
using WalttiAnalyzer.Core.Services;

namespace WalttiAnalyzer.Web.Services;

/// <summary>
/// Runs data synchronization on startup and then every 3 minutes.
/// Performs stop/route discovery and sliding-window realtime polling.
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

    /// <summary>Tracks the last poll time so the sliding window overlaps correctly.</summary>
    private long? _lastPollUnixUtc;

    public DataSyncBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<DataSyncBackgroundService> logger, IOptions<WalttiSettings> settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on startup then repeat every 3 minutes.
        await RunSyncCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(3));
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

            // Stop/route discovery: once per hour (at top of hour), or immediately if never done today
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
                _logger.LogInformation("Running stop/route discovery");
                using var discoverActivity = ActivitySource.StartActivity("DiscoverStops");
                var stops = await collector.DiscoverStopsAsync();
                discoverActivity?.SetTag("result.status", stops.GetValueOrDefault("status"));
                _logger.LogInformation("Discovery result: {@Result}", stops);
            }

            // Sliding window realtime poll
            _logger.LogInformation("Running sliding window poll (lastPoll={LastPoll})", _lastPollUnixUtc);
            using (var pollActivity = ActivitySource.StartActivity("PollSlidingWindow"))
            {
                var beforePoll = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var result = await collector.PollSlidingWindowAsync(_lastPollUnixUtc);
                _lastPollUnixUtc = beforePoll;

                pollActivity?.SetTag("result.status", result.GetValueOrDefault("status"));
                pollActivity?.SetTag("result.measured", result.GetValueOrDefault("measured"));
                pollActivity?.SetTag("result.propagated", result.GetValueOrDefault("propagated"));
                _logger.LogInformation("Sliding window poll result: {@Result}", result);
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
