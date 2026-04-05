using WalttiAnalyzer.Functions.Services;
using Xunit;

namespace WalttiAnalyzer.Tests;

public class DatabaseTests : IDisposable
{
    private readonly TestDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UpsertAndGetStop()
    {
        using var db = _fixture.Connect();
        _fixture.Db.UpsertStop(db, "Vaasa:309392", "Gerbynmäentie", null, 63.14, 21.57);
        var stop = _fixture.Db.GetStop(db, "Vaasa:309392");
        Assert.NotNull(stop);
        Assert.Equal("Gerbynmäentie", stop.Name);
        Assert.Equal(63.14, stop.Lat);
    }

    [Fact]
    public void UpsertStopUpdates()
    {
        using var db = _fixture.Connect();
        _fixture.Db.UpsertStop(db, "Vaasa:309392", "Old Name", null, 63.14, 21.57);
        _fixture.Db.UpsertStop(db, "Vaasa:309392", "New Name", null, 63.14, 21.57);
        var stop = _fixture.Db.GetStop(db, "Vaasa:309392");
        Assert.Equal("New Name", stop!.Name);
    }

    [Fact]
    public void UpsertObservation()
    {
        using var db = _fixture.Connect();
        EnsureTrip(db, "Vaasa:trip1");
        var obs = MakeObs("Vaasa:trip1");
        _fixture.Db.UpsertObservation(db, obs);
        var rows = _fixture.Db.GetObservations(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Single(rows);
        Assert.Equal("3", rows[0].RouteShortName);
    }

    [Fact]
    public void UpsertObservationUpdatesOnConflict()
    {
        using var db = _fixture.Connect();
        EnsureTrip(db, "Vaasa:trip1");
        var obs = MakeObs("Vaasa:trip1");
        _fixture.Db.UpsertObservation(db, obs);

        // Update with realtime data
        obs["realtime"] = 1;
        obs["departure_delay"] = 120;
        obs["realtime_departure"] = 24220;
        obs["realtime_state"] = "UPDATED";
        _fixture.Db.UpsertObservation(db, obs);

        var rows = _fixture.Db.GetObservations(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Realtime);
        Assert.Equal(120, rows[0].DepartureDelay);
    }

    [Fact]
    public void BatchUpsert()
    {
        using var db = _fixture.Connect();
        var trips = Enumerable.Range(0, 5).Select(i => new Dictionary<string, object?>
        {
            ["gtfs_id"] = $"Vaasa:trip{i}",
            ["route_short_name"] = "3",
            ["route_long_name"] = "Gerby - Keskusta",
            ["mode"] = "BUS",
            ["headsign"] = "Keskusta",
            ["direction_id"] = 1,
        }).ToList();
        _fixture.Db.UpsertTripsBatch(db, trips);

        var observations = Enumerable.Range(0, 5).Select(i => MakeObs($"Vaasa:trip{i}", scheduledDep: 24100 + i * 1800)).ToList();
        _fixture.Db.UpsertObservationsBatch(db, observations);

        var rows = _fixture.Db.GetObservations(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void GetObservationsWithRouteFilter()
    {
        using var db = _fixture.Connect();
        EnsureTrip(db, "Vaasa:trip_a", "3");
        EnsureTrip(db, "Vaasa:trip_b", "9");
        _fixture.Db.UpsertObservation(db, MakeObs("Vaasa:trip_a"));
        _fixture.Db.UpsertObservation(db, MakeObs("Vaasa:trip_b"));

        var all = _fixture.Db.GetObservations(db, "Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, all.Count);

        var route3 = _fixture.Db.GetObservations(db, "Vaasa:309392", "2026-04-02", "2026-04-02", route: "3");
        Assert.Single(route3);
        Assert.Equal("3", route3[0].RouteShortName);
    }

    [Fact]
    public void CollectionLog()
    {
        using var db = _fixture.Connect();
        _fixture.Db.LogCollection(db, "Vaasa:309392", "daily", "2026-04-02", departuresFound: 20);
        _fixture.Db.LogCollection(db, "Vaasa:309392", "realtime", departuresFound: 5);

        var daily = _fixture.Db.GetLatestCollection(db, "Vaasa:309392", "daily");
        Assert.NotNull(daily);
        Assert.Equal(20, daily.DeparturesFound);

        var rt = _fixture.Db.GetLatestCollection(db, "Vaasa:309392", "realtime");
        Assert.NotNull(rt);
        Assert.Equal(5, rt.DeparturesFound);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void EnsureTrip(Microsoft.Data.Sqlite.SqliteConnection db, string tripId, string route = "3")
    {
        _fixture.Db.UpsertTrip(db, tripId, route, "Gerby - Keskusta", "BUS", "Keskusta", 1);
    }

    private static Dictionary<string, object?> MakeObs(string tripId,
        int scheduledDep = 24100, int delay = 0, int realtime = 0)
    {
        return new Dictionary<string, object?>
        {
            ["stop_gtfs_id"] = "Vaasa:309392",
            ["trip_gtfs_id"] = tripId,
            ["service_date"] = "2026-04-02",
            ["scheduled_arrival"] = 24000,
            ["scheduled_departure"] = scheduledDep,
            ["realtime_arrival"] = null,
            ["realtime_departure"] = null,
            ["arrival_delay"] = delay,
            ["departure_delay"] = delay,
            ["realtime"] = realtime,
            ["realtime_state"] = "SCHEDULED",
            ["queried_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}
