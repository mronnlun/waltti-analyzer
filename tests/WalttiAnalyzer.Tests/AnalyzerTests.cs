using Microsoft.Extensions.Logging.Abstractions;
using WalttiAnalyzer.Core.Services;
using Xunit;

namespace WalttiAnalyzer.Tests;

public class AnalyzerTests : IDisposable
{
    private readonly TestDbFixture _fixture = new();
    private readonly AnalyzerService _analyzer;

    public AnalyzerTests()
    {
        _analyzer = new AnalyzerService(_fixture.Context, NullLogger<AnalyzerService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SummaryEmpty()
    {
        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-01", "2026-04-30");
        Assert.Equal(0, summary["total_departures"]);
    }

    [Fact]
    public async Task SummaryWithData()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "3", 25800, 120, true),
            ("trip3", "3", 27600, 300, true),
            ("trip4", "9", 29400, 0, true),
            ("trip5", "3", 31200, 0, false),
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(5, summary["total_departures"]);
        Assert.Equal(4, summary["with_realtime"]);
        Assert.Equal(1, summary["static_only"]);
        Assert.Equal(3, summary["on_time"]);
        Assert.Equal(1, summary["slightly_late"]);
    }

    [Fact]
    public async Task SummaryRouteFilter()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "9", 25800, 120, true),
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02", route: "3");
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["with_realtime"]);
    }

    [Fact]
    public async Task RouteBreakdown()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "3", 25800, 300, true),
            ("trip3", "9", 27600, 0, true),
        });

        var breakdown = await _analyzer.GetRouteBreakdownAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, breakdown.Count);

        var route3 = breakdown.First(r => (string)r["route"]! == "3");
        Assert.Equal(2, route3["departures"]);
        Assert.Equal(2, route3["with_realtime"]);

        var route9 = breakdown.First(r => (string)r["route"]! == "9");
        Assert.Equal(1, route9["departures"]);
    }

    [Fact]
    public async Task DelayByHour()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "3", 6 * 3600 + 400, 60, true),
            ("trip2", "3", 6 * 3600 + 1800, 120, true),
            ("trip3", "3", 8 * 3600 + 100, 300, true),
        });

        var hourly = await _analyzer.GetDelayByHourAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, hourly.Count);

        var hour6 = hourly.First(h => (int)h["hour"]! == 6);
        Assert.Equal(2, hour6["departures"]);
        Assert.Equal(90.0, hour6["avg_late_seconds"]);
    }

    [Fact]
    public async Task SummaryTimeFilter()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("early", "3", 6 * 3600, 30, true),
            ("target", "3", 16 * 3600 + 300, 120, true),
            ("late", "3", 20 * 3600, 0, true),
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02",
            timeFrom: 16 * 3600 + 240, timeTo: 16 * 3600 + 360);
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["with_realtime"]);
    }

    [Fact]
    public async Task SummaryExcludesFutureDeparturesOfToday()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        var helsinkiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = helsinkiNow.ToString("yyyy-MM-dd");
        var nowSecs = (int)helsinkiNow.TimeOfDay.TotalSeconds;

        // A past departure (1 hour ago), clamped to at least 3600 to avoid midnight issues
        var pastSecs = Math.Max(3600, nowSecs - 3600);
        // A future departure (1 hour from now), clamped to at most 82800 (23:00)
        var futureSecs = Math.Min(82800, nowSecs + 3600);

        await _fixture.Db.UpsertTripsBatchAsync([
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:past_trip", ["route_short_name"] = "3",
                ["route_long_name"] = "R", ["mode"] = "BUS", ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:future_trip", ["route_short_name"] = "3",
                ["route_long_name"] = "R", ["mode"] = "BUS", ["headsign"] = "H", ["direction_id"] = 0 },
        ]);
        await _fixture.Db.UpsertObservationsBatchAsync([
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:past_trip",
                ["service_date"] = today, ["scheduled_arrival"] = pastSecs - 100,
                ["scheduled_departure"] = pastSecs, ["realtime_arrival"] = pastSecs - 100,
                ["realtime_departure"] = pastSecs + 60, ["arrival_delay"] = 0, ["departure_delay"] = 60,
                ["realtime"] = 1, ["realtime_state"] = "UPDATED",
                ["queried_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:future_trip",
                ["service_date"] = today, ["scheduled_arrival"] = futureSecs - 100,
                ["scheduled_departure"] = futureSecs, ["realtime_arrival"] = futureSecs - 70,
                ["realtime_departure"] = futureSecs + 30, ["arrival_delay"] = 30, ["departure_delay"] = 30,
                ["realtime"] = 1, ["realtime_state"] = "UPDATED",
                ["queried_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        ]);

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", today, today);
        // Only the past departure should be counted
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["with_realtime"]);
    }

    [Fact]
    public void ParseTime()
    {
        Assert.Equal(16 * 3600 + 5 * 60, AnalyzerService.ParseTime("16:05"));
        Assert.Equal(0, AnalyzerService.ParseTime("00:00"));
        Assert.Null(AnalyzerService.ParseTime(""));
        Assert.Null(AnalyzerService.ParseTime(null));
        Assert.Null(AnalyzerService.ParseTime("invalid"));
    }

    [Fact]
    public void FormatDelay()
    {
        Assert.Equal("+0s", AnalyzerService.FormatDelay(0));
        Assert.Equal("+1m 30s", AnalyzerService.FormatDelay(90));
        Assert.Equal("-45s", AnalyzerService.FormatDelay(-45));
        Assert.Equal("+11m 00s", AnalyzerService.FormatDelay(660));
        Assert.Equal("N/A", AnalyzerService.FormatDelay(null));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task SetupTripsAndObsAsync(
        (string tripId, string route, int scheduledDep, int delay, bool realtime)[] specs)
    {
        var trips = new Dictionary<string, Dictionary<string, object?>>();
        var observations = new List<Dictionary<string, object?>>();

        foreach (var (tripId, route, scheduledDep, delay, realtime) in specs)
        {
            var fullTripId = $"Vaasa:{tripId}";
            if (!trips.ContainsKey(fullTripId))
            {
                trips[fullTripId] = new Dictionary<string, object?>
                {
                    ["gtfs_id"] = fullTripId,
                    ["route_short_name"] = route,
                    ["route_long_name"] = $"Route {route}",
                    ["mode"] = "BUS",
                    ["headsign"] = "Test",
                    ["direction_id"] = 1,
                };
            }
            observations.Add(new Dictionary<string, object?>
            {
                ["stop_gtfs_id"] = "Vaasa:309392",
                ["trip_gtfs_id"] = fullTripId,
                ["service_date"] = "2026-04-02",
                ["scheduled_arrival"] = scheduledDep - 100,
                ["scheduled_departure"] = scheduledDep,
                ["realtime_arrival"] = realtime ? scheduledDep - 100 + delay : null,
                ["realtime_departure"] = realtime ? scheduledDep + delay : null,
                ["arrival_delay"] = realtime ? delay : 0,
                ["departure_delay"] = realtime ? delay : 0,
                ["realtime"] = realtime ? 1 : 0,
                ["realtime_state"] = realtime ? "UPDATED" : "SCHEDULED",
                ["queried_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }

        await _fixture.Db.UpsertTripsBatchAsync(trips.Values.ToList());
        await _fixture.Db.UpsertObservationsBatchAsync(observations);
    }
}
