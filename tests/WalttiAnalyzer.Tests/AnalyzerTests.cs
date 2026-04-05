using WalttiAnalyzer.Functions.Services;
using Xunit;

namespace WalttiAnalyzer.Tests;

public class AnalyzerTests : IDisposable
{
    private readonly TestDbFixture _fixture = new();
    private readonly AnalyzerService _analyzer = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SummaryEmpty()
    {
        using var db = _fixture.Connect();
        var summary = _analyzer.GetSummary(db, "Vaasa:309392", "2026-04-01", "2026-04-30");
        Assert.Equal(0, summary["total_departures"]);
    }

    [Fact]
    public void SummaryWithData()
    {
        using var db = _fixture.Connect();
        SetupTripsAndObs(db, new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "3", 25800, 120, true),
            ("trip3", "3", 27600, 300, true),
            ("trip4", "9", 29400, 0, true),
            ("trip5", "3", 31200, 0, false),
        });

        var summary = _analyzer.GetSummary(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(5, summary["total_departures"]);
        Assert.Equal(4, summary["with_realtime"]);
        Assert.Equal(1, summary["static_only"]);
        Assert.Equal(3, summary["on_time"]);
        Assert.Equal(1, summary["slightly_late"]);
    }

    [Fact]
    public void SummaryRouteFilter()
    {
        using var db = _fixture.Connect();
        SetupTripsAndObs(db, new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "9", 25800, 120, true),
        });

        var summary = _analyzer.GetSummary(db, "Vaasa:309392", "2026-04-02", "2026-04-02", route: "3");
        Assert.Equal(1, summary["total_departures"]);
        Assert.Equal(1, summary["with_realtime"]);
    }

    [Fact]
    public void RouteBreakdown()
    {
        using var db = _fixture.Connect();
        SetupTripsAndObs(db, new[]
        {
            ("trip1", "3", 24000, 30, true),
            ("trip2", "3", 25800, 300, true),
            ("trip3", "9", 27600, 0, true),
        });

        var breakdown = _analyzer.GetRouteBreakdown(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, breakdown.Count);

        var route3 = breakdown.First(r => (string)r["route"]! == "3");
        Assert.Equal(2, route3["departures"]);
        Assert.Equal(2, route3["with_realtime"]);

        var route9 = breakdown.First(r => (string)r["route"]! == "9");
        Assert.Equal(1, route9["departures"]);
    }

    [Fact]
    public void DelayByHour()
    {
        using var db = _fixture.Connect();
        SetupTripsAndObs(db, new[]
        {
            ("trip1", "3", 6 * 3600 + 400, 60, true),
            ("trip2", "3", 6 * 3600 + 1800, 120, true),
            ("trip3", "3", 8 * 3600 + 100, 300, true),
        });

        var hourly = _analyzer.GetDelayByHour(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, hourly.Count);

        var hour6 = hourly.First(h => (int)h["hour"]! == 6);
        Assert.Equal(2, hour6["departures"]);
        Assert.Equal(90.0, hour6["avg_late_seconds"]);
    }

    [Fact]
    public void SummaryTimeFilter()
    {
        using var db = _fixture.Connect();
        SetupTripsAndObs(db, new[]
        {
            ("early", "3", 6 * 3600, 30, true),
            ("target", "3", 16 * 3600 + 300, 120, true),
            ("late", "3", 20 * 3600, 0, true),
        });

        var summary = _analyzer.GetSummary(db, "Vaasa:309392", "2026-04-02", "2026-04-02",
            timeFrom: 16 * 3600 + 240, timeTo: 16 * 3600 + 360);
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

    private void SetupTripsAndObs(Microsoft.Data.Sqlite.SqliteConnection db,
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

        _fixture.Db.UpsertTripsBatch(db, trips.Values.ToList());
        _fixture.Db.UpsertObservationsBatch(db, observations);
    }
}
