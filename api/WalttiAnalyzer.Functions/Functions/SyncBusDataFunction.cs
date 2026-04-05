using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Functions.Services;

namespace WalttiAnalyzer.Functions.Functions;

public class SyncBusDataFunction
{
    private readonly ILogger<SyncBusDataFunction> _logger;
    private readonly DatabaseService _db;
    private readonly CollectorService _collector;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public SyncBusDataFunction(ILogger<SyncBusDataFunction> logger,
        DatabaseService db, CollectorService collector)
    {
        _logger = logger;
        _db = db;
        _collector = collector;
    }

    /// <summary>
    /// Runs every 3 minutes. Always polls realtime data.
    /// At 03:xx/23:xx runs daily collection. Monday 02:xx runs weekly discovery.
    /// </summary>
    // NCRONTAB: sec min hour day month dayOfWeek
    [Function("SyncBusData")]
    public async Task Run([TimerTrigger("0 */3 * * * *")] TimerInfo timer)
    {
        var apiUrl = Environment.GetEnvironmentVariable("DIGITRANSIT_API_URL")
            ?? "https://api.digitransit.fi/routing/v2/waltti/gtfs/v1";
        var apiKey = Environment.GetEnvironmentVariable("DIGITRANSIT_API_KEY") ?? "";
        var feedId = Environment.GetEnvironmentVariable("FEED_ID") ?? "Vaasa";
        var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "data/waltti.db";

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("DIGITRANSIT_API_KEY not set — skipping sync");
            return;
        }

        _db.InitDb(dbPath);

        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
        int hour = now.Hour;
        int minute = now.Minute;
        int weekday = (int)now.DayOfWeek; // 0 = Sunday in .NET

        // Weekly discovery: Monday 02:00–02:02
        if (weekday == 1 && hour == 2 && minute < 3)
        {
            _logger.LogInformation("Running weekly stop discovery");
            var result = await _collector.DiscoverStopsAsync(dbPath, apiUrl, apiKey, feedId);
            _logger.LogInformation("Discovery result: {@Result}", result);
        }

        // Daily collection: 03:00–03:02 and 23:00–23:02
        if ((hour == 3 || hour == 23) && minute < 3)
        {
            _logger.LogInformation("Running daily collection at {Hour}:{Minute:D2}", hour, minute);
            var result = await _collector.CollectDailyAsync(dbPath, apiUrl, apiKey, feedId: feedId);
            _logger.LogInformation("Daily collection result: {@Result}", result);
        }

        // Realtime polling: every invocation
        var realtimeResult = await _collector.PollRealtimeOnceAsync(dbPath, apiUrl, apiKey, feedId: feedId);
        _logger.LogInformation("Realtime poll result: {@Result}", realtimeResult);
    }
}
