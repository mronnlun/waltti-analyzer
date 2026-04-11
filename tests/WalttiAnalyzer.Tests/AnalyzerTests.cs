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
            ("trip1", "Vaasa:3", 24000, 30, 2),    // measured
            ("trip2", "Vaasa:3", 25800, 120, 2),    // measured
            ("trip3", "Vaasa:3", 27600, 300, 2),    // measured
            ("trip4", "Vaasa:9", 29400, 0, 2),      // measured
            ("trip5", "Vaasa:3", 31200, 0, 0),      // scheduled (no GPS)
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(5, summary["total_departures"]);
        Assert.Equal(4, summary["measured"]);
        Assert.Equal(1, summary["static_only"]);
        Assert.Equal(3, summary["on_time"]);
        Assert.Equal(1, summary["slightly_late"]);
    }

    [Fact]
    public async Task SummaryRouteFilter()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "Vaasa:3", 24000, 30, 2),
            ("trip2", "Vaasa:9", 25800, 120, 2),
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02", route: "3");
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["measured"]);
    }

    [Fact]
    public async Task RouteBreakdown()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "Vaasa:3", 24000, 30, 2),
            ("trip2", "Vaasa:3", 25800, 300, 2),
            ("trip3", "Vaasa:9", 27600, 0, 2),
        });

        var breakdown = await _analyzer.GetRouteBreakdownAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, breakdown.Count);

        var route3 = breakdown.First(r => (string)r["route"]! == "3");
        Assert.Equal(2, route3["departures"]);
        Assert.Equal(2, route3["measured"]);

        var route9 = breakdown.First(r => (string)r["route"]! == "9");
        Assert.Equal(1, route9["departures"]);
    }

    [Fact]
    public async Task DelayByHour()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("trip1", "Vaasa:3", 6 * 3600 + 400, 60, 2),
            ("trip2", "Vaasa:3", 6 * 3600 + 1800, 120, 2),
            ("trip3", "Vaasa:3", 8 * 3600 + 100, 300, 2),
        });

        var hourly = await _analyzer.GetDelayByHourAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, hourly.Count);

        var hour6 = hourly.First(h => (int)h["hour"]! == 6);
        Assert.Equal(2, hour6["departures"]);
        Assert.Equal(90.0, hour6["avg_late_seconds"]);
        Assert.Equal(90.0, hour6["avg_delay_seconds"]);
    }

    [Fact]
    public async Task SummaryTimeFilter()
    {
        await SetupTripsAndObsAsync(new[]
        {
            ("early", "Vaasa:3", 6 * 3600, 30, 2),
            ("target", "Vaasa:3", 16 * 3600 + 300, 120, 2),
            ("late", "Vaasa:3", 20 * 3600, 0, 2),
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02",
            timeFrom: 16 * 3600 + 240, timeTo: 16 * 3600 + 360);
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["measured"]);
    }

    [Fact]
    public async Task SummaryExcludesFutureDeparturesOfToday()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        var helsinkiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var todayInt = int.Parse(helsinkiNow.ToString("yyyyMMdd"));
        var nowSecs = (int)helsinkiNow.TimeOfDay.TotalSeconds;

        // A past departure (1 hour ago), clamped to at least 3600 to avoid midnight issues
        var pastSecs = Math.Max(3600, nowSecs - 3600);
        // A future departure (1 hour from now), clamped to at most 82800 (23:00).
        // After 23:00 Helsinki we can't place a future departure within today, so skip.
        var futureSecs = Math.Min(82800, nowSecs + 3600);
        if (futureSecs <= nowSecs)
            return; // After 23:00 Helsinki: can't place a valid future departure within today

        await _fixture.Db.UpsertTripsBatchAsync([
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:past_trip", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:future_trip", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
        ]);
        await _fixture.Db.UpsertObservationsBatchAsync([
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:past_trip",
                ["service_date"] = todayInt, ["scheduled_departure"] = pastSecs,
                ["departure_delay"] = 60, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:future_trip",
                ["service_date"] = todayInt, ["scheduled_departure"] = futureSecs,
                ["departure_delay"] = 30, ["delay_source"] = 1,
                ["realtime_state"] = "UPDATED",
            },
        ]);

        var today = helsinkiNow.ToString("yyyy-MM-dd");
        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", today, today);
        // Only the past departure should be counted
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["measured"]);
    }

    [Fact]
    public async Task SummaryAllStops()
    {
        // Seed a second stop and observations for both stops
        await _fixture.Db.UpsertStopAsync("Vaasa:999999", "OtherStop", null, 63.0, 21.5);
        await _fixture.Db.UpsertRouteAsync("Vaasa:5", "5", "R5", "BUS");
        await _fixture.Db.UpsertRouteAsync("Vaasa:7", "7", "R7", "BUS");
        await _fixture.Db.UpsertTripsBatchAsync([
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:alltrip1", ["route_gtfs_id"] = "Vaasa:5",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:alltrip2", ["route_gtfs_id"] = "Vaasa:7",
                ["headsign"] = "H", ["direction_id"] = 0 },
        ]);
        await _fixture.Db.UpsertObservationsBatchAsync([
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:alltrip1",
                ["service_date"] = 20260402, ["scheduled_departure"] = 24100,
                ["departure_delay"] = 60, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:999999", ["trip_gtfs_id"] = "Vaasa:alltrip2",
                ["service_date"] = 20260402, ["scheduled_departure"] = 25100,
                ["departure_delay"] = 120, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
        ]);

        // All stops via feedId
        var summary = await _analyzer.GetSummaryAsync(null, "2026-04-02", "2026-04-02", feedId: "Vaasa");
        Assert.Equal(2, summary["total_departures"]);

        // Single stop still works
        var single = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(1, single["total_departures"]);
    }

    [Fact]
    public async Task RouteBreakdownAllStops()
    {
        await _fixture.Db.UpsertStopAsync("Vaasa:888888", "ThirdStop", null, 63.1, 21.6);
        await _fixture.Db.UpsertRouteAsync("Vaasa:11", "11", "R11", "BUS");
        await _fixture.Db.UpsertRouteAsync("Vaasa:12", "12", "R12", "BUS");
        await _fixture.Db.UpsertTripsBatchAsync([
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:rbtrip1", ["route_gtfs_id"] = "Vaasa:11",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:rbtrip2", ["route_gtfs_id"] = "Vaasa:12",
                ["headsign"] = "H", ["direction_id"] = 0 },
        ]);
        await _fixture.Db.UpsertObservationsBatchAsync([
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:rbtrip1",
                ["service_date"] = 20260403, ["scheduled_departure"] = 24100,
                ["departure_delay"] = 60, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:888888", ["trip_gtfs_id"] = "Vaasa:rbtrip2",
                ["service_date"] = 20260403, ["scheduled_departure"] = 25100,
                ["departure_delay"] = 120, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
        ]);

        var breakdown = await _analyzer.GetRouteBreakdownAsync(null, "2026-04-03", "2026-04-03", feedId: "Vaasa");
        Assert.Equal(2, breakdown.Count);
        Assert.Contains(breakdown, r => (string)r["route"]! == "11");
        Assert.Contains(breakdown, r => (string)r["route"]! == "12");
    }

    [Fact]
    public async Task SummaryExcludesSkippedFromDelayStats()
    {
        // Three normal MEASURED rows + one SKIPPED row whose delay should be ignored.
        await _fixture.Db.UpsertTripsBatchAsync([
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:sk_t1", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:sk_t2", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:sk_t3", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
            new Dictionary<string, object?> { ["gtfs_id"] = "Vaasa:sk_t4", ["route_gtfs_id"] = "Vaasa:3",
                ["headsign"] = "H", ["direction_id"] = 0 },
        ]);
        await _fixture.Db.UpsertObservationsBatchAsync([
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:sk_t1",
                ["service_date"] = 20260402, ["scheduled_departure"] = 24100,
                ["departure_delay"] = 30, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:sk_t2",
                ["service_date"] = 20260402, ["scheduled_departure"] = 25100,
                ["departure_delay"] = 90, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:sk_t3",
                ["service_date"] = 20260402, ["scheduled_departure"] = 26100,
                ["departure_delay"] = 150, ["delay_source"] = 2,
                ["realtime_state"] = "UPDATED",
            },
            // SKIPPED row with an extreme "delay" that must not influence stats.
            new Dictionary<string, object?> {
                ["stop_gtfs_id"] = "Vaasa:309392", ["trip_gtfs_id"] = "Vaasa:sk_t4",
                ["service_date"] = 20260402, ["scheduled_departure"] = 27100,
                ["departure_delay"] = 99999, ["delay_source"] = 2,
                ["realtime_state"] = "SKIPPED",
            },
        ]);

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(4, summary["total_departures"]);
        Assert.Equal(4, summary["measured"]);
        Assert.Equal(1, summary["skipped"]);
        // avg_late should only reflect the 3 real rows: (30+90+150)/3 = 90
        Assert.Equal(90.0, summary["avg_late_seconds"]);
        // max_late must not be the SKIPPED row's 99999s
        Assert.Equal(150, summary["max_late_seconds"]);
        // suspect_gps must not count the SKIPPED row
        Assert.Equal(0, summary["suspect_gps"]);
    }

    [Fact]
    public async Task SummaryOnlyUsesMeasuredForStats()
    {
        // Test that propagated data is NOT included in delay statistics
        await SetupTripsAndObsAsync(new[]
        {
            ("m_trip1", "Vaasa:3", 24000, 60, 2),   // measured: 60s late
            ("m_trip2", "Vaasa:3", 25800, 120, 2),   // measured: 120s late
            ("p_trip1", "Vaasa:3", 27600, 9999, 1),  // propagated: should be excluded from stats
        });

        var summary = await _analyzer.GetSummaryAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(3, summary["total_departures"]);
        Assert.Equal(2, summary["measured"]);
        Assert.Equal(1, summary["propagated"]);
        // avg_late should only reflect measured: (60+120)/2 = 90
        Assert.Equal(90.0, summary["avg_late_seconds"]);
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

    [Fact]
    public void ParseDateToInt()
    {
        Assert.Equal(20260402, AnalyzerService.ParseDateToInt("2026-04-02"));
        Assert.Equal(20261231, AnalyzerService.ParseDateToInt("2026-12-31"));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task SetupTripsAndObsAsync(
        (string tripId, string routeGtfsId, int scheduledDep, int delay, int delaySource)[] specs)
    {
        var trips = new Dictionary<string, Dictionary<string, object?>>();
        var observations = new List<Dictionary<string, object?>>();

        foreach (var (tripId, routeGtfsId, scheduledDep, delay, delaySource) in specs)
        {
            var fullTripId = $"Vaasa:{tripId}";
            if (!trips.ContainsKey(fullTripId))
            {
                trips[fullTripId] = new Dictionary<string, object?>
                {
                    ["gtfs_id"] = fullTripId,
                    ["route_gtfs_id"] = routeGtfsId,
                    ["headsign"] = "Test",
                    ["direction_id"] = 1,
                };
            }
            observations.Add(new Dictionary<string, object?>
            {
                ["stop_gtfs_id"] = "Vaasa:309392",
                ["trip_gtfs_id"] = fullTripId,
                ["service_date"] = 20260402,
                ["scheduled_departure"] = scheduledDep,
                ["departure_delay"] = delay,
                ["delay_source"] = delaySource,
                ["realtime_state"] = delaySource >= 1 ? "UPDATED" : "SCHEDULED",
            });
        }

        await _fixture.Db.UpsertTripsBatchAsync(trips.Values.ToList());
        await _fixture.Db.UpsertObservationsBatchAsync(observations);
    }
}
