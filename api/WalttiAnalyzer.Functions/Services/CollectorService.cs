using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalttiAnalyzer.Functions.Services;

public class CollectorService
{
    private readonly ILogger<CollectorService> _logger;
    private readonly DatabaseService _db;
    private readonly DigitransitClient _client;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    private const int RealtimeBatchSize = 50;

    public CollectorService(ILogger<CollectorService> logger, DatabaseService db, DigitransitClient client)
    {
        _logger = logger;
        _db = db;
        _client = client;
    }

    public async Task<Dictionary<string, object?>> DiscoverStopsAsync(
        string dbPath, string apiUrl, string apiKey, string feedId)
    {
        _client.Configure(apiUrl, apiKey);
        using var conn = _db.Connect(dbPath);
        try
        {
            var (stops, routes) = await _client.DiscoverFeedStopsAsync(feedId);
            _db.UpsertStopsBatch(conn, stops);
            _db.LogCollection(conn, feedId, "discover", departuresFound: stops.Count);
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
            _db.LogCollection(conn, feedId, "discover", error: ex.Message);
            return new Dictionary<string, object?> { ["status"] = "error", ["message"] = ex.Message };
        }
    }

    public async Task<Dictionary<string, object?>> CollectDailyAsync(
        string dbPath, string apiUrl, string apiKey,
        string? stopId = null, string? serviceDate = null, string? feedId = null)
    {
        _client.Configure(apiUrl, apiKey);
        using var conn = _db.Connect(dbPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var targetDate = serviceDate
            ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz).ToString("yyyy-MM-dd");

        try
        {
            List<string> queryIds;
            if (!string.IsNullOrEmpty(stopId))
            {
                queryIds = new List<string> { stopId };
            }
            else
            {
                queryIds = _db.GetAllStopIds(conn, feedId);
                if (queryIds.Count == 0)
                {
                    await DiscoverStopsAsync(dbPath, apiUrl, apiKey, feedId ?? "Vaasa");
                    using var conn2 = _db.Connect(dbPath);
                    queryIds = _db.GetAllStopIds(conn2, feedId);
                }
            }

            var stopsData = await _client.FetchBulkDailyAsync(queryIds, DateOnly.Parse(targetDate));

            var allTrips = new Dictionary<string, Dictionary<string, object?>>();
            var allObservations = new List<Dictionary<string, object?>>();
            int stopsWithService = 0;

            foreach (var stopData in stopsData)
            {
                var gtfsId = stopData.GetProperty("gtfsId").GetString()!;
                var name = stopData.GetProperty("name").GetString()!;
                string? code = stopData.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
                double? lat = stopData.TryGetProperty("lat", out var la) && la.ValueKind != JsonValueKind.Null ? la.GetDouble() : null;
                double? lon = stopData.TryGetProperty("lon", out var lo) && lo.ValueKind != JsonValueKind.Null ? lo.GetDouble() : null;

                _db.UpsertStop(conn, gtfsId, name, code, lat, lon);

                var (trips, observations) = ProcessDailyStop(stopData, targetDate, now);
                foreach (var kv in trips) allTrips[kv.Key] = kv.Value;
                allObservations.AddRange(observations);
                if (observations.Count > 0) stopsWithService++;
            }

            if (allTrips.Count > 0) _db.UpsertTripsBatch(conn, allTrips.Values.ToList());
            if (allObservations.Count > 0) _db.UpsertObservationsBatch(conn, allObservations);

            _db.LogCollection(conn, stopId ?? feedId ?? "all", "daily", targetDate, allObservations.Count);
            _logger.LogInformation(
                "Daily collection: {Deps} departures across {With}/{Total} stops for {Date}",
                allObservations.Count, stopsWithService, stopsData.Count, targetDate);

            if (!string.IsNullOrEmpty(stopId) && allObservations.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["status"] = "no_service", ["date"] = targetDate,
                    ["message"] = $"No service on {targetDate}"
                };

            return new Dictionary<string, object?>
            {
                ["status"] = "ok", ["date"] = targetDate,
                ["departures"] = allObservations.Count,
                ["stops_with_service"] = stopsWithService,
                ["total_stops"] = stopsData.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily collection failed for {Date}", targetDate);
            _db.LogCollection(conn, stopId ?? feedId ?? "all", "daily", targetDate, error: ex.Message);
            return new Dictionary<string, object?> { ["status"] = "error", ["message"] = ex.Message };
        }
    }

    public async Task<Dictionary<string, object?>> PollRealtimeOnceAsync(
        string dbPath, string apiUrl, string apiKey,
        string? stopId = null, string? feedId = null)
    {
        _client.Configure(apiUrl, apiKey);
        using var conn = _db.Connect(dbPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            List<string> queryIds;
            if (!string.IsNullOrEmpty(stopId))
            {
                queryIds = new List<string> { stopId };
            }
            else
            {
                queryIds = _db.GetAllStopIds(conn, feedId);
                if (queryIds.Count == 0)
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok", ["updated"] = 0,
                        ["message"] = "No stops discovered yet."
                    };
            }

            int totalUpdated = 0, totalStopsWithData = 0, totalStopsPolled = 0;

            for (int i = 0; i < queryIds.Count; i += RealtimeBatchSize)
            {
                var batchIds = queryIds.Skip(i).Take(RealtimeBatchSize).ToList();
                var stopsData = await _client.FetchBulkRealtimeAsync(batchIds);
                totalStopsPolled += stopsData.Count;

                var (trips, observations, stopsWithData) = ProcessRealtimeBatch(stopsData, now);

                if (trips.Count > 0) _db.UpsertTripsBatch(conn, trips.Values.ToList());
                if (observations.Count > 0) _db.UpsertObservationsBatch(conn, observations);

                totalUpdated += observations.Count;
                totalStopsWithData += stopsWithData;
            }

            _db.LogCollection(conn, stopId ?? feedId ?? "all", "realtime", departuresFound: totalUpdated);
            _logger.LogInformation(
                "Realtime poll: {Deps} departures across {With}/{Total} stops",
                totalUpdated, totalStopsWithData, totalStopsPolled);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok", ["updated"] = totalUpdated,
                ["stops_polled"] = totalStopsPolled,
                ["stops_with_data"] = totalStopsWithData
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime poll failed");
            _db.LogCollection(conn, stopId ?? feedId ?? "all", "realtime", error: ex.Message);
            return new Dictionary<string, object?> { ["status"] = "error", ["message"] = ex.Message };
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ServiceDayToDate(long serviceDayUnix)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(serviceDayUnix);
        var helsinki = TimeZoneInfo.ConvertTime(dt, HelsinkiTz);
        return helsinki.ToString("yyyy-MM-dd");
    }

    private static (Dictionary<string, Dictionary<string, object?>> Trips, List<Dictionary<string, object?>> Observations)
        ProcessDailyStop(JsonElement stopData, string targetDate, long now)
    {
        var trips = new Dictionary<string, Dictionary<string, object?>>();
        var observations = new List<Dictionary<string, object?>>();
        var gtfsId = stopData.GetProperty("gtfsId").GetString()!;

        foreach (var pattern in GetArray(stopData, "stoptimesForServiceDate"))
        {
            var route = pattern.GetProperty("pattern").GetProperty("route");
            int? directionId = pattern.GetProperty("pattern").TryGetProperty("directionId", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.GetInt32() : null;

            foreach (var st in GetArray(pattern, "stoptimes"))
            {
                var tripId = st.GetProperty("trip").GetProperty("gtfsId").GetString()!;
                trips[tripId] = new Dictionary<string, object?>
                {
                    ["gtfs_id"] = tripId,
                    ["route_short_name"] = GetStringOrNull(route, "shortName"),
                    ["route_long_name"] = GetStringOrNull(route, "longName"),
                    ["mode"] = GetStringOrNull(route, "mode"),
                    ["headsign"] = GetStringOrNull(st, "headsign"),
                    ["direction_id"] = directionId,
                };
                observations.Add(new Dictionary<string, object?>
                {
                    ["stop_gtfs_id"] = gtfsId,
                    ["trip_gtfs_id"] = tripId,
                    ["service_date"] = targetDate,
                    ["scheduled_arrival"] = GetIntOrNull(st, "scheduledArrival"),
                    ["scheduled_departure"] = st.GetProperty("scheduledDeparture").GetInt32(),
                    ["realtime_arrival"] = GetIntOrNull(st, "realtimeArrival"),
                    ["realtime_departure"] = GetIntOrNull(st, "realtimeDeparture"),
                    ["arrival_delay"] = GetIntOrDefault(st, "arrivalDelay", 0),
                    ["departure_delay"] = GetIntOrDefault(st, "departureDelay", 0),
                    ["realtime"] = st.TryGetProperty("realtime", out var rt) && rt.GetBoolean() ? 1 : 0,
                    ["realtime_state"] = GetStringOrNull(st, "realtimeState"),
                    ["queried_at"] = now,
                });
            }
        }
        return (trips, observations);
    }

    private static (Dictionary<string, Dictionary<string, object?>> Trips,
        List<Dictionary<string, object?>> Observations, int StopsWithData)
        ProcessRealtimeBatch(List<JsonElement> stopsData, long now)
    {
        var trips = new Dictionary<string, Dictionary<string, object?>>();
        var observations = new List<Dictionary<string, object?>>();
        int stopsWithData = 0;

        foreach (var stopData in stopsData)
        {
            var sid = stopData.GetProperty("gtfsId").GetString()!;
            var stoptimes = GetArray(stopData, "stoptimesWithoutPatterns");
            if (stoptimes.Count == 0) continue;

            bool hasData = false;
            foreach (var st in stoptimes)
            {
                var trip = st.GetProperty("trip");
                var tripId = trip.GetProperty("gtfsId").GetString()!;
                var route = trip.TryGetProperty("route", out var r) ? r : default;

                if (!st.TryGetProperty("serviceDay", out var sd) || sd.ValueKind == JsonValueKind.Null)
                    continue;
                var svcDate = ServiceDayToDate(sd.GetInt64());

                trips[tripId] = new Dictionary<string, object?>
                {
                    ["gtfs_id"] = tripId,
                    ["route_short_name"] = route.ValueKind != JsonValueKind.Undefined ? GetStringOrNull(route, "shortName") : null,
                    ["route_long_name"] = route.ValueKind != JsonValueKind.Undefined ? GetStringOrNull(route, "longName") : null,
                    ["mode"] = null,
                    ["headsign"] = GetStringOrNull(st, "headsign"),
                    ["direction_id"] = null,
                };
                observations.Add(new Dictionary<string, object?>
                {
                    ["stop_gtfs_id"] = sid,
                    ["trip_gtfs_id"] = tripId,
                    ["service_date"] = svcDate,
                    ["scheduled_arrival"] = GetIntOrNull(st, "scheduledArrival"),
                    ["scheduled_departure"] = st.GetProperty("scheduledDeparture").GetInt32(),
                    ["realtime_arrival"] = GetIntOrNull(st, "realtimeArrival"),
                    ["realtime_departure"] = GetIntOrNull(st, "realtimeDeparture"),
                    ["arrival_delay"] = GetIntOrDefault(st, "arrivalDelay", 0),
                    ["departure_delay"] = GetIntOrDefault(st, "departureDelay", 0),
                    ["realtime"] = st.TryGetProperty("realtime", out var rt) && rt.GetBoolean() ? 1 : 0,
                    ["realtime_state"] = GetStringOrNull(st, "realtimeState"),
                    ["queried_at"] = now,
                });
                hasData = true;
            }
            if (hasData) stopsWithData++;
        }
        return (trips, observations, stopsWithData);
    }

    private static List<JsonElement> GetArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return new List<JsonElement>();
        return prop.EnumerateArray().ToList();
    }

    private static string? GetStringOrNull(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        return p.GetString();
    }

    private static int? GetIntOrNull(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        return p.GetInt32();
    }

    private static int GetIntOrDefault(JsonElement el, string name, int def)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return def;
        return p.GetInt32();
    }
}
