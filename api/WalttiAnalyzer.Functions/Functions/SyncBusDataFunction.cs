using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Functions.Models;
using WalttiAnalyzer.Functions.Services;

namespace WalttiAnalyzer.Functions.Functions;

public class SyncBusDataFunction
{
    private readonly ILogger<SyncBusDataFunction> _logger;
    private readonly DatabaseService _db;
    private readonly CollectorService _collector;
    private readonly WalttiSettings _settings;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public SyncBusDataFunction(ILogger<SyncBusDataFunction> logger,
        DatabaseService db, CollectorService collector, IOptions<WalttiSettings> settings)
    {
        _logger = logger;
        _db = db;
        _collector = collector;
        _settings = settings.Value;
    }

    /// <summary>
    /// Runs every 10 minutes (and on startup). Always polls realtime data.
    /// At 03:xx/23:xx runs daily collection.
    /// Discovers stops once per hour, or immediately if not yet fetched today.
    /// </summary>
    // NCRONTAB: sec min hour day month dayOfWeek
    [Function("SyncBusData")]
    public async Task Run([TimerTrigger("0 */10 * * * *", RunOnStartup = true)] TimerInfo timer)
    {
        var apiUrl = _settings.DigitransitApiUrl;
        var apiKey = _settings.DigitransitApiKey;
        var feedId = _settings.FeedId;
        var dbPath = _settings.DatabasePath;

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("DIGITRANSIT_API_KEY not set — skipping sync");
            return;
        }

        _db.InitDb(dbPath);

        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
        int hour = now.Hour;
        int minute = now.Minute;

        // Stop discovery: once per hour, or immediately if not yet fetched today
        bool shouldDiscover = false;
        using (var conn = _db.Connect(dbPath))
        {
            var lastDiscovery = _db.GetLatestCollection(conn, feedId, "discover");
            if (lastDiscovery == null)
            {
                shouldDiscover = true;
            }
            else
            {
                var lastTime = DateTimeOffset.FromUnixTimeSeconds(lastDiscovery.QueriedAt);
                var lastHelsinki = TimeZoneInfo.ConvertTime(lastTime, HelsinkiTz);
                if (lastHelsinki.Date < now.Date)
                {
                    // Not yet fetched today — run immediately
                    shouldDiscover = true;
                }
                else if (minute < 5)
                {
                    // Already fetched today — run once per hour (at the top of the hour)
                    shouldDiscover = true;
                }
            }
        }

        _logger.LogInformation("Running stop discovery");
        var stops = await _collector.DiscoverStopsAsync(dbPath, apiUrl, apiKey, feedId);
        _logger.LogInformation("Discovery result: {@Result}", stops);

        _logger.LogInformation("Running daily collection at {Hour}:{Minute:D2}", hour, minute);
        var result = await _collector.CollectDailyAsync(dbPath, apiUrl, apiKey, feedId: feedId);
        _logger.LogInformation("Daily collection result: {@Result}", result);

        // Realtime polling: every invocation
        var realtimeResult = await _collector.PollRealtimeOnceAsync(dbPath, apiUrl, apiKey, feedId: feedId);
        _logger.LogInformation("Realtime poll result: {@Result}", realtimeResult);
    }
}
