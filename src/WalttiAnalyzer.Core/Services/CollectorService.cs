using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Core.Models;

namespace WalttiAnalyzer.Core.Services;

public class CollectorService
{
    private readonly ILogger<CollectorService> _logger;
    private readonly DatabaseService _db;
    private readonly DigitransitClient _client;
    private readonly WalttiSettings _settings;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    private const int RealtimeBatchSize = 50;

    public CollectorService(ILogger<CollectorService> logger, DatabaseService db,
        DigitransitClient client, IOptions<WalttiSettings> settings)
    {
        _logger = logger;
        _db = db;
        _client = client;
        _settings = settings.Value;
    }

    public async Task<Dictionary<string, object?>> DiscoverStopsAsync()
    {
        var feedId = _settings.FeedId;
        try
        {
            var (stops, routes) = await _client.DiscoverFeedStopsAsync(feedId);
            await _db.UpsertStopsBatchAsync(stops);

            // Upsert routes discovered from patterns
            var routeDicts = routes.Select(r => new Dictionary<string, object?>
            {
                ["gtfs_id"] = r["gtfs_id"],
                ["short_name"] = r["short_name"],
                ["long_name"] = r["long_name"],
                ["mode"] = r["mode"],
            }).ToList();
            await _db.UpsertRoutesBatchAsync(routeDicts);

            await _db.LogCollectionAsync(feedId, "discover", departuresFound: stops.Count);
            _logger.LogInformation("Discovered {Stops} stops and {Routes} routes for feed {Feed}",
                stops.Count, routes.Count, feedId);
            return new Dictionary<string, object?>
            {
                ["status"] = "ok", ["stops"] = stops.Count, ["routes"] = routes.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop discovery failed for feed {Feed}", feedId);
            await _db.LogCollectionAsync(feedId, "discover", error: ex.Message);
            return new Dictionary<string, object?> { ["status"] = "error", ["message"] = ex.Message };
        }
    }

    /// <summary>
    /// Poll realtime data using a sliding window: from lastPollTime-60s to now+futureSeconds.
    /// Classifies each stoptime as MEASURED (past) or PROPAGATED (future).
    /// </summary>
    public async Task<Dictionary<string, object?>> PollSlidingWindowAsync(
        long? lastPollUnixUtc = null, int futureSeconds = 600)
    {
        var feedId = _settings.FeedId;
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var helsinkiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
        var nowSecsSinceMidnight = (int)helsinkiNow.TimeOfDay.TotalSeconds;

        // Window: from 60s before last poll (or 10min ago on first run) to futureSeconds from now
        long windowStart = lastPollUnixUtc.HasValue
            ? lastPollUnixUtc.Value - 60
            : nowUtc - 600;
        int timeRange = (int)(nowUtc - windowStart) + futureSeconds;

        try
        {
            var queryIds = await _db.GetAllStopIdsAsync(feedId);
            if (queryIds.Count == 0)
            {
                await DiscoverStopsAsync();
                queryIds = await _db.GetAllStopIdsAsync(feedId);
                if (queryIds.Count == 0)
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok", ["measured"] = 0, ["propagated"] = 0,
                        ["message"] = "No stops discovered yet."
                    };
            }

            int totalMeasured = 0, totalPropagated = 0, totalStopsPolled = 0;

            for (int i = 0; i < queryIds.Count; i += RealtimeBatchSize)
            {
                var batchIds = queryIds.Skip(i).Take(RealtimeBatchSize).ToList();
                var stopsData = await _client.FetchSlidingWindowAsync(batchIds, windowStart, timeRange);
                totalStopsPolled += stopsData.Count;

                var (routes, trips, observations, measured, propagated) =
                    ProcessSlidingWindowBatch(stopsData, nowSecsSinceMidnight);

                if (routes.Count > 0) await _db.UpsertRoutesBatchAsync(routes.Values.ToList());
                if (trips.Count > 0) await _db.UpsertTripsBatchAsync(trips.Values.ToList());
                if (observations.Count > 0) await _db.UpsertObservationsBatchAsync(observations);

                totalMeasured += measured;
                totalPropagated += propagated;
            }

            await _db.LogCollectionAsync(feedId, "realtime", departuresFound: totalMeasured + totalPropagated);
            _logger.LogInformation(
                "Sliding window poll: {Measured} measured + {Propagated} propagated across {Stops} stops",
                totalMeasured, totalPropagated, totalStopsPolled);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["measured"] = totalMeasured,
                ["propagated"] = totalPropagated,
                ["stops_polled"] = totalStopsPolled
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sliding window poll failed");
            await _db.LogCollectionAsync(feedId, "realtime", error: ex.Message);
            return new Dictionary<string, object?> { ["status"] = "error", ["message"] = ex.Message };
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ServiceDayToDateStr(long serviceDayUnix)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(serviceDayUnix);
        var helsinki = TimeZoneInfo.ConvertTime(dt, HelsinkiTz);
        return helsinki.ToString("yyyyMMdd");
    }

    private static int ServiceDayToDateInt(long serviceDayUnix)
    {
        return int.Parse(ServiceDayToDateStr(serviceDayUnix));
    }

    private static (
        Dictionary<string, Dictionary<string, object?>> Routes,
        Dictionary<string, Dictionary<string, object?>> Trips,
        List<Dictionary<string, object?>> Observations,
        int Measured, int Propagated)
        ProcessSlidingWindowBatch(List<JsonElement> stopsData, int nowSecsSinceMidnight)
    {
        var routes = new Dictionary<string, Dictionary<string, object?>>();
        var trips = new Dictionary<string, Dictionary<string, object?>>();
        var observations = new List<Dictionary<string, object?>>();
        int measured = 0, propagated = 0;

        foreach (var stopData in stopsData)
        {
            var stopGtfsId = stopData.GetProperty("gtfsId").GetString()!;
            var stoptimes = DigitransitClient.GetArrayProperty(stopData, "stoptimesWithoutPatterns");

            foreach (var st in stoptimes)
            {
                // Must have realtime data to be useful
                if (!st.TryGetProperty("realtime", out var rtVal) || !rtVal.GetBoolean())
                    continue;

                var trip = st.GetProperty("trip");
                var tripId = trip.GetProperty("gtfsId").GetString()!;

                if (!st.TryGetProperty("serviceDay", out var sd) || sd.ValueKind == JsonValueKind.Null)
                    continue;
                var svcDate = ServiceDayToDateInt(sd.GetInt64());

                // Extract route info
                var route = trip.TryGetProperty("route", out var r) ? r : default;
                string? routeGtfsId = null;
                if (route.ValueKind != JsonValueKind.Undefined && route.ValueKind != JsonValueKind.Null)
                {
                    routeGtfsId = route.TryGetProperty("gtfsId", out var rgid) ? rgid.GetString() : null;
                    if (routeGtfsId != null && !routes.ContainsKey(routeGtfsId))
                    {
                        routes[routeGtfsId] = new Dictionary<string, object?>
                        {
                            ["gtfs_id"] = routeGtfsId,
                            ["short_name"] = route.TryGetProperty("shortName", out var sn) ? sn.GetString() : null,
                            ["long_name"] = route.TryGetProperty("longName", out var ln) ? ln.GetString() : null,
                            ["mode"] = route.TryGetProperty("mode", out var m) ? m.GetString() : null,
                        };
                    }
                }

                // Trip info
                if (!trips.ContainsKey(tripId))
                {
                    trips[tripId] = new Dictionary<string, object?>
                    {
                        ["gtfs_id"] = tripId,
                        ["route_gtfs_id"] = routeGtfsId,
                        ["headsign"] = st.TryGetProperty("headsign", out var hs) ? hs.GetString() : null,
                        ["direction_id"] = (int?)null,
                    };
                }

                var scheduledDep = st.GetProperty("scheduledDeparture").GetInt32();
                var depDelay = st.TryGetProperty("departureDelay", out var dd) && dd.ValueKind != JsonValueKind.Null
                    ? dd.GetInt32() : 0;
                var realtimeState = st.TryGetProperty("realtimeState", out var rs) ? rs.GetString() : null;

                // Classify: past departure = MEASURED, future = PROPAGATED
                int delaySource = scheduledDep <= nowSecsSinceMidnight ? 2 : 1;
                if (delaySource == 2) measured++; else propagated++;

                observations.Add(new Dictionary<string, object?>
                {
                    ["stop_gtfs_id"] = stopGtfsId,
                    ["trip_gtfs_id"] = tripId,
                    ["service_date"] = svcDate,
                    ["scheduled_departure"] = scheduledDep,
                    ["departure_delay"] = depDelay,
                    ["delay_source"] = delaySource,
                    ["realtime_state"] = realtimeState,
                });
            }
        }

        return (routes, trips, observations, measured, propagated);
    }
}
