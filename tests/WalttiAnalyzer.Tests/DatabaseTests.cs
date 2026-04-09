using WalttiAnalyzer.Core.Services;
using Xunit;

namespace WalttiAnalyzer.Tests;

public class DatabaseTests : IDisposable
{
    private readonly TestDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task UpsertAndGetStop()
    {
        await _fixture.Db.UpsertStopAsync("Vaasa:309392", "Gerbynmäentie", null, 63.14, 21.57);
        var stop = await _fixture.Db.GetStopAsync("Vaasa:309392");
        Assert.NotNull(stop);
        Assert.Equal("Gerbynmäentie", stop.Name);
        Assert.Equal(63.14, stop.Lat);
    }

    [Fact]
    public async Task UpsertStopUpdates()
    {
        await _fixture.Db.UpsertStopAsync("Vaasa:309392", "Old Name", null, 63.14, 21.57);
        await _fixture.Db.UpsertStopAsync("Vaasa:309392", "New Name", null, 63.14, 21.57);
        var stop = await _fixture.Db.GetStopAsync("Vaasa:309392");
        Assert.Equal("New Name", stop!.Name);
    }

    [Fact]
    public async Task UpsertObservation()
    {
        await EnsureTripAsync("Vaasa:trip1");
        await _fixture.Db.UpsertObservationsBatchAsync([MakeObs("Vaasa:trip1")]);
        var rows = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Single(rows);
        Assert.Equal("3", rows[0].RouteShortName);
    }

    [Fact]
    public async Task UpsertObservationUpdatesOnConflict()
    {
        await EnsureTripAsync("Vaasa:trip1");
        var obs = MakeObs("Vaasa:trip1");
        await _fixture.Db.UpsertObservationsBatchAsync([obs]);

        // Update with realtime data
        obs["realtime"] = 1;
        obs["departure_delay"] = 120;
        obs["realtime_departure"] = 24220;
        obs["realtime_state"] = "UPDATED";
        await _fixture.Db.UpsertObservationsBatchAsync([obs]);

        var rows = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Realtime);
        Assert.Equal(120, rows[0].DepartureDelay);
    }

    [Fact]
    public async Task UpsertObservationPreservesRealtimeWhenOverwrittenWithStatic()
    {
        await EnsureTripAsync("Vaasa:trip1");

        // First upsert: static schedule only
        var obs = MakeObs("Vaasa:trip1");
        await _fixture.Db.UpsertObservationsBatchAsync([obs]);

        // Second upsert: realtime data captured
        obs["realtime"] = 1;
        obs["departure_delay"] = 90;
        obs["realtime_departure"] = 24190;
        obs["realtime_state"] = "UPDATED";
        await _fixture.Db.UpsertObservationsBatchAsync([obs]);

        // Third upsert: daily collection re-runs and API returns realtime=false for past departure
        obs["realtime"] = 0;
        obs["departure_delay"] = 0;
        obs["realtime_departure"] = null;
        obs["realtime_state"] = "SCHEDULED";
        await _fixture.Db.UpsertObservationsBatchAsync([obs]);

        // Realtime data captured in second upsert should be preserved
        var rows = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Realtime);
        Assert.Equal(90, rows[0].DepartureDelay);
        Assert.Equal(24190, rows[0].RealtimeDeparture);
    }

    [Fact]
    public async Task BatchUpsert()
    {
        var trips = Enumerable.Range(0, 5).Select(i => new Dictionary<string, object?>
        {
            ["gtfs_id"] = $"Vaasa:trip{i}",
            ["route_short_name"] = "3",
            ["route_long_name"] = "Gerby - Keskusta",
            ["mode"] = "BUS",
            ["headsign"] = "Keskusta",
            ["direction_id"] = 1,
        }).ToList();
        await _fixture.Db.UpsertTripsBatchAsync(trips);

        var observations = Enumerable.Range(0, 5)
            .Select(i => MakeObs($"Vaasa:trip{i}", scheduledDep: 24100 + i * 1800))
            .ToList();
        await _fixture.Db.UpsertObservationsBatchAsync(observations);

        var rows = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public async Task GetObservationsWithHeadsignFilter()
    {
        await _fixture.Db.UpsertTripsBatchAsync([new Dictionary<string, object?>
        {
            ["gtfs_id"] = "Vaasa:trip_hs_a",
            ["route_short_name"] = "3",
            ["route_long_name"] = "Gerby - Keskusta",
            ["mode"] = "BUS",
            ["headsign"] = "Keskusta",
            ["direction_id"] = 1,
        }]);
        await _fixture.Db.UpsertTripsBatchAsync([new Dictionary<string, object?>
        {
            ["gtfs_id"] = "Vaasa:trip_hs_b",
            ["route_short_name"] = "3",
            ["route_long_name"] = "Gerby - Keskusta",
            ["mode"] = "BUS",
            ["headsign"] = "Gerby",
            ["direction_id"] = 0,
        }]);
        await _fixture.Db.UpsertObservationsBatchAsync([MakeObs("Vaasa:trip_hs_a")]);
        await _fixture.Db.UpsertObservationsBatchAsync([MakeObs("Vaasa:trip_hs_b", scheduledDep: 25000)]);

        var all = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, all.Count);

        var filtered = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02", headsign: "Keskusta");
        Assert.Single(filtered);
        Assert.Equal("Keskusta", filtered[0].Headsign);
    }

    [Fact]
    public async Task GetObservationsWithRouteFilter()
    {
        await EnsureTripAsync("Vaasa:trip_a", "3");
        await EnsureTripAsync("Vaasa:trip_b", "9");
        await _fixture.Db.UpsertObservationsBatchAsync([MakeObs("Vaasa:trip_a")]);
        await _fixture.Db.UpsertObservationsBatchAsync([MakeObs("Vaasa:trip_b")]);

        var all = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02");
        Assert.Equal(2, all.Count);

        var route3 = await _fixture.Db.GetObservationsAsync("Vaasa:309392", "2026-04-02", "2026-04-02", route: "3");
        Assert.Single(route3);
        Assert.Equal("3", route3[0].RouteShortName);
    }

    [Fact]
    public async Task CollectionLog()
    {
        await _fixture.Db.LogCollectionAsync("Vaasa:309392", "daily", "2026-04-02", departuresFound: 20);
        await _fixture.Db.LogCollectionAsync("Vaasa:309392", "realtime", departuresFound: 5);

        var daily = await _fixture.Db.GetLatestCollectionAsync("Vaasa:309392", "daily");
        Assert.NotNull(daily);
        Assert.Equal(20, daily.DeparturesFound);

        var rt = await _fixture.Db.GetLatestCollectionAsync("Vaasa:309392", "realtime");
        Assert.NotNull(rt);
        Assert.Equal(5, rt.DeparturesFound);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task EnsureTripAsync(string tripId, string route = "3")
    {
        await _fixture.Db.UpsertTripsBatchAsync([new Dictionary<string, object?>
        {
            ["gtfs_id"] = tripId,
            ["route_short_name"] = route,
            ["route_long_name"] = "Gerby - Keskusta",
            ["mode"] = "BUS",
            ["headsign"] = "Keskusta",
            ["direction_id"] = 1,
        }]);
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
