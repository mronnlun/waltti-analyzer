using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Core.Data;
using WalttiAnalyzer.Core.Models;

namespace WalttiAnalyzer.Core.Services;

public class DatabaseService
{
    private readonly WalttiDbContext _context;
    private readonly ILogger<DatabaseService> _logger;

    private bool IsSqlite => _context.Database.ProviderName?.Contains("Sqlite") ?? false;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public DatabaseService(WalttiDbContext context, ILogger<DatabaseService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Schema
    // -----------------------------------------------------------------------

    public void EnsureCreated() => _context.Database.EnsureCreated();

    // -----------------------------------------------------------------------
    // Stop operations
    // -----------------------------------------------------------------------

    public async Task UpsertStopAsync(string gtfsId, string name, string? code, double? lat, double? lon)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (IsSqlite)
        {
            await _context.Database.ExecuteSqlAsync($@"
                INSERT INTO stops (gtfs_id, name, code, lat, lon, updated_at)
                VALUES ({gtfsId}, {name}, {code}, {lat}, {lon}, {now})
                ON CONFLICT(gtfs_id) DO UPDATE SET
                    name=excluded.name, code=excluded.code,
                    lat=excluded.lat, lon=excluded.lon, updated_at=excluded.updated_at");
        }
        else
        {
            await _context.Database.ExecuteSqlAsync($@"
                MERGE stops AS t
                USING (SELECT {gtfsId} AS gtfs_id, {name} AS name, {code} AS code,
                              {lat} AS lat, {lon} AS lon, {now} AS updated_at) AS s
                ON t.gtfs_id = s.gtfs_id
                WHEN MATCHED THEN UPDATE SET
                    name=s.name, code=s.code, lat=s.lat, lon=s.lon, updated_at=s.updated_at
                WHEN NOT MATCHED THEN INSERT (gtfs_id, name, code, lat, lon, updated_at)
                    VALUES (s.gtfs_id, s.name, s.code, s.lat, s.lon, s.updated_at);");
        }
    }

    public async Task UpsertStopsBatchAsync(List<Dictionary<string, object?>> stops)
    {
        foreach (var s in stops)
            await UpsertStopAsync(
                (string)s["gtfs_id"]!,
                (string)s["name"]!,
                s.GetValueOrDefault("code") as string,
                s.GetValueOrDefault("lat") as double?,
                s.GetValueOrDefault("lon") as double?);
    }

    public async Task<Stop?> GetStopAsync(string gtfsId) =>
        await _context.Stops.FirstOrDefaultAsync(s => s.GtfsId == gtfsId);

    public async Task<List<Stop>> GetAllStopsAsync(string? feedId = null)
    {
        var q = _context.Stops.AsQueryable();
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(s => s.GtfsId.StartsWith(feedId + ":"));
        return await q.OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<List<string>> GetAllStopIdsAsync(string? feedId = null) =>
        (await GetAllStopsAsync(feedId)).Select(s => s.GtfsId).ToList();

    public async Task<List<string>> GetRoutesForStopAsync(string stopId) =>
        await _context.Observations
            .Where(o => o.Stop!.GtfsId == stopId && o.Trip!.RouteShortName != null)
            .Select(o => o.Trip!.RouteShortName!)
            .Distinct().OrderBy(r => r).ToListAsync();

    public async Task<List<string>> GetAllRoutesAsync(string? feedId = null)
    {
        var q = _context.Observations.Where(o => o.Trip!.RouteShortName != null);
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(o => o.Stop!.GtfsId.StartsWith(feedId + ":"));
        return await q.Select(o => o.Trip!.RouteShortName!).Distinct().OrderBy(r => r).ToListAsync();
    }

    public async Task<List<string>> GetHeadsignsForStopAsync(string stopId) =>
        await _context.Observations
            .Where(o => o.Stop!.GtfsId == stopId && o.Trip!.Headsign != null)
            .Select(o => o.Trip!.Headsign!)
            .Distinct().OrderBy(h => h).ToListAsync();

    public async Task<List<string>> GetAllHeadsignsAsync(string? feedId = null)
    {
        var q = _context.Observations.Where(o => o.Trip!.Headsign != null);
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(o => o.Stop!.GtfsId.StartsWith(feedId + ":"));
        return await q.Select(o => o.Trip!.Headsign!).Distinct().OrderBy(h => h).ToListAsync();
    }

    // -----------------------------------------------------------------------
    // Trip operations
    // -----------------------------------------------------------------------

    public async Task UpsertTripsBatchAsync(List<Dictionary<string, object?>> trips)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in trips)
        {
            var gid = (string)t["gtfs_id"]!;
            var rsn = t.GetValueOrDefault("route_short_name") as string;
            var rln = t.GetValueOrDefault("route_long_name") as string;
            var mode = t.GetValueOrDefault("mode") as string;
            var hs = t.GetValueOrDefault("headsign") as string;
            var did = t.GetValueOrDefault("direction_id") as int?;

            if (IsSqlite)
            {
                await _context.Database.ExecuteSqlAsync($@"
                    INSERT INTO trips (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
                    VALUES ({gid}, {rsn}, {rln}, {mode}, {hs}, {did}, {now})
                    ON CONFLICT(gtfs_id) DO UPDATE SET
                        route_short_name=excluded.route_short_name, route_long_name=excluded.route_long_name,
                        mode=excluded.mode, headsign=excluded.headsign,
                        direction_id=excluded.direction_id, updated_at=excluded.updated_at");
            }
            else
            {
                await _context.Database.ExecuteSqlAsync($@"
                    MERGE trips AS t
                    USING (SELECT {gid} AS gtfs_id, {rsn} AS route_short_name, {rln} AS route_long_name,
                                  {mode} AS mode, {hs} AS headsign, {did} AS direction_id, {now} AS updated_at) AS s
                    ON t.gtfs_id = s.gtfs_id
                    WHEN MATCHED THEN UPDATE SET
                        route_short_name=s.route_short_name, route_long_name=s.route_long_name,
                        mode=s.mode, headsign=s.headsign, direction_id=s.direction_id, updated_at=s.updated_at
                    WHEN NOT MATCHED THEN INSERT (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
                        VALUES (s.gtfs_id, s.route_short_name, s.route_long_name, s.mode, s.headsign, s.direction_id, s.updated_at);");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Observation operations
    // -----------------------------------------------------------------------

    public async Task UpsertObservationsBatchAsync(List<Dictionary<string, object?>> observations)
    {
        if (observations.Count == 0) return;

        var stopGtfsIds = observations.Select(o => (string)o["stop_gtfs_id"]!).Distinct().ToList();
        var tripGtfsIds = observations.Select(o => (string)o["trip_gtfs_id"]!).Distinct().ToList();

        var stops = await _context.Stops
            .Where(s => stopGtfsIds.Contains(s.GtfsId))
            .ToDictionaryAsync(s => s.GtfsId, s => s.Id);
        var trips = await _context.Trips
            .Where(t => tripGtfsIds.Contains(t.GtfsId))
            .ToDictionaryAsync(t => t.GtfsId, t => t.Id);
        var states = await _context.RealtimeStates
            .ToDictionaryAsync(rs => rs.Name, rs => rs.Id);

        foreach (var obs in observations)
        {
            var stopGtfsId = (string)obs["stop_gtfs_id"]!;
            var tripGtfsId = (string)obs["trip_gtfs_id"]!;
            if (!stops.TryGetValue(stopGtfsId, out var stopId)) continue;
            if (!trips.TryGetValue(tripGtfsId, out var tripId)) continue;

            var sd = (string)obs["service_date"]!;
            var sDep = Convert.ToInt32(obs["scheduled_departure"]);
            int? sArr = GetNullableInt(obs, "scheduled_arrival");
            int? rtArr = GetNullableInt(obs, "realtime_arrival");
            int? rtDep = GetNullableInt(obs, "realtime_departure");
            int? aDelay = GetNullableInt(obs, "arrival_delay");
            int? dDelay = GetNullableInt(obs, "departure_delay");
            var rt = Convert.ToInt32(obs["realtime"]);
            var stateStr = obs.GetValueOrDefault("realtime_state") as string;
            int? stateId = stateStr != null && states.TryGetValue(stateStr, out var sId) ? sId : null;
            var qa = Convert.ToInt64(obs["queried_at"]);

            if (IsSqlite)
            {
                await _context.Database.ExecuteSqlAsync($@"
                    INSERT INTO observations (stop_id, trip_id, service_date,
                        scheduled_arrival, scheduled_departure, realtime_arrival, realtime_departure,
                        arrival_delay, departure_delay, realtime, realtime_state_id, queried_at)
                    VALUES ({stopId}, {tripId}, {sd}, {sArr}, {sDep}, {rtArr}, {rtDep},
                            {aDelay}, {dDelay}, {rt}, {stateId}, {qa})
                    ON CONFLICT(stop_id, trip_id, service_date) DO UPDATE SET
                        scheduled_arrival=excluded.scheduled_arrival,
                        scheduled_departure=excluded.scheduled_departure,
                        realtime_arrival=CASE WHEN excluded.realtime=1 THEN excluded.realtime_arrival ELSE realtime_arrival END,
                        realtime_departure=CASE WHEN excluded.realtime=1 THEN excluded.realtime_departure ELSE realtime_departure END,
                        arrival_delay=CASE WHEN excluded.realtime=1 THEN excluded.arrival_delay ELSE arrival_delay END,
                        departure_delay=CASE WHEN excluded.realtime=1 THEN excluded.departure_delay ELSE departure_delay END,
                        realtime=CASE WHEN excluded.realtime=1 THEN excluded.realtime ELSE realtime END,
                        realtime_state_id=CASE WHEN excluded.realtime=1 THEN excluded.realtime_state_id ELSE realtime_state_id END,
                        queried_at=excluded.queried_at");
            }
            else
            {
                await _context.Database.ExecuteSqlAsync($@"
                    MERGE observations AS t
                    USING (SELECT {stopId} AS stop_id, {tripId} AS trip_id, {sd} AS service_date,
                                  {sArr} AS scheduled_arrival, {sDep} AS scheduled_departure,
                                  {rtArr} AS realtime_arrival, {rtDep} AS realtime_departure,
                                  {aDelay} AS arrival_delay, {dDelay} AS departure_delay,
                                  {rt} AS realtime, {stateId} AS realtime_state_id, {qa} AS queried_at) AS s
                    ON t.stop_id=s.stop_id AND t.trip_id=s.trip_id AND t.service_date=s.service_date
                    WHEN MATCHED THEN UPDATE SET
                        scheduled_arrival=s.scheduled_arrival, scheduled_departure=s.scheduled_departure,
                        realtime_arrival=CASE WHEN s.realtime=1 THEN s.realtime_arrival ELSE t.realtime_arrival END,
                        realtime_departure=CASE WHEN s.realtime=1 THEN s.realtime_departure ELSE t.realtime_departure END,
                        arrival_delay=CASE WHEN s.realtime=1 THEN s.arrival_delay ELSE t.arrival_delay END,
                        departure_delay=CASE WHEN s.realtime=1 THEN s.departure_delay ELSE t.departure_delay END,
                        realtime=CASE WHEN s.realtime=1 THEN s.realtime ELSE t.realtime END,
                        realtime_state_id=CASE WHEN s.realtime=1 THEN s.realtime_state_id ELSE t.realtime_state_id END,
                        queried_at=s.queried_at
                    WHEN NOT MATCHED THEN INSERT (stop_id, trip_id, service_date, scheduled_arrival,
                        scheduled_departure, realtime_arrival, realtime_departure, arrival_delay,
                        departure_delay, realtime, realtime_state_id, queried_at)
                    VALUES (s.stop_id, s.trip_id, s.service_date, s.scheduled_arrival,
                        s.scheduled_departure, s.realtime_arrival, s.realtime_departure, s.arrival_delay,
                        s.departure_delay, s.realtime, s.realtime_state_id, s.queried_at);");
            }
        }
    }

    public async Task<List<Observation>> GetObservationsAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null)
    {
        var allStops = string.IsNullOrEmpty(stopId);
        var parms = new List<(string Name, object? Value)>
        {
            ("@start", startDate), ("@end", endDate)
        };
        var selectCols = allStops ? $"SELECT {ObsColumns}, s.name AS stop_name" : $"SELECT {ObsColumns}";
        var sql = $"{selectCols} {ObsJoins} WHERE o.service_date>=@start AND o.service_date<=@end";
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);
        sql += " ORDER BY o.service_date DESC, o.scheduled_departure DESC";
        sql += IsSqlite ? " LIMIT 300" : " OFFSET 0 ROWS FETCH NEXT 300 ROWS ONLY";
        return await ReadObservationsRawAsync(sql, parms, includeStopName: allStops);
    }

    public async Task<List<Observation>> GetLatestObservationsAsync(int limit = 300, string? feedId = null)
    {
        var parms = new List<(string Name, object? Value)>();
        var sql = $"SELECT {ObsColumns}, s.name AS stop_name {ObsJoins} WHERE o.realtime=1";
        if (!string.IsNullOrEmpty(feedId)) { sql += " AND s.gtfs_id LIKE @feed"; parms.Add(("@feed", $"{feedId}:%")); }
        sql += " ORDER BY o.queried_at DESC, o.service_date DESC, o.scheduled_departure DESC";
        sql += IsSqlite ? " LIMIT @lim" : " OFFSET 0 ROWS FETCH NEXT @lim ROWS ONLY";
        parms.Add(("@lim", limit));
        return await ReadObservationsRawAsync(sql, parms, includeStopName: true);
    }

    // -----------------------------------------------------------------------
    // Collection log
    // -----------------------------------------------------------------------

    public async Task LogCollectionAsync(string stopGtfsId, string queryType,
        string? serviceDate = null, int departuresFound = 0, int noService = 0, string? error = null)
    {
        _context.CollectionLog.Add(new CollectionLogEntry
        {
            QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StopGtfsId = stopGtfsId,
            QueryType = queryType,
            ServiceDate = serviceDate,
            DeparturesFound = departuresFound,
            NoService = noService,
            Error = error,
        });
        await _context.SaveChangesAsync();
    }

    public async Task<CollectionLogEntry?> GetLatestCollectionAsync(string stopId, string queryType) =>
        await _context.CollectionLog
            .Where(l => l.StopGtfsId == stopId && l.QueryType == queryType)
            .OrderByDescending(l => l.QueriedAt)
            .FirstOrDefaultAsync();

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private const string ObsColumns = @"
        o.id, s.gtfs_id AS stop_gtfs_id, t.gtfs_id AS trip_gtfs_id,
        o.service_date, o.scheduled_arrival, o.scheduled_departure,
        o.realtime_arrival, o.realtime_departure, o.arrival_delay, o.departure_delay,
        o.realtime, rs.name AS realtime_state, o.queried_at,
        t.route_short_name, t.route_long_name, t.mode, t.headsign, t.direction_id";

    private const string ObsJoins = @"
        FROM observations o
        JOIN stops s ON o.stop_id=s.id
        JOIN trips t ON o.trip_id=t.id
        LEFT JOIN realtime_states rs ON o.realtime_state_id=rs.id";

    private static void AppendStopFilter(ref string sql, List<(string Name, object? Value)> parms,
        string? stopId, string? feedId)
    {
        if (!string.IsNullOrEmpty(stopId)) { sql += " AND s.gtfs_id=@sid"; parms.Add(("@sid", stopId)); }
        else if (!string.IsNullOrEmpty(feedId)) { sql += " AND s.gtfs_id LIKE @feed"; parms.Add(("@feed", $"{feedId}:%")); }
    }

    private static void AppendFilters(ref string sql, List<(string Name, object? Value)> parms,
        string? route, int? timeFrom, int? timeTo, string? headsign = null)
    {
        if (!string.IsNullOrEmpty(route)) { sql += " AND t.route_short_name=@route"; parms.Add(("@route", route)); }
        if (!string.IsNullOrEmpty(headsign)) { sql += " AND t.headsign=@headsign"; parms.Add(("@headsign", headsign)); }
        if (timeFrom.HasValue) { sql += " AND o.scheduled_departure>=@tf"; parms.Add(("@tf", timeFrom.Value)); }
        if (timeTo.HasValue) { sql += " AND o.scheduled_departure<=@tt"; parms.Add(("@tt", timeTo.Value)); }
    }

    private static void AppendPastOnlyFilter(ref string sql, List<(string Name, object? Value)> parms)
    {
        var helsinkiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
        var today = helsinkiNow.ToString("yyyy-MM-dd");
        var nowSecs = (int)helsinkiNow.TimeOfDay.TotalSeconds;
        sql += " AND (o.service_date < @today OR o.scheduled_departure <= @now_secs)";
        parms.Add(("@today", today));
        parms.Add(("@now_secs", nowSecs));
    }

    private static int? GetNullableInt(Dictionary<string, object?> dict, string key)
    {
        var val = dict.GetValueOrDefault(key);
        return val != null ? Convert.ToInt32(val) : null;
    }

    private async Task<List<Observation>> ReadObservationsRawAsync(
        string sql, List<(string Name, object? Value)> parms, bool includeStopName)
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = val ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<Observation>();
            while (await reader.ReadAsync())
            {
                var obs = new Observation
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    StopGtfsId = reader.GetString(reader.GetOrdinal("stop_gtfs_id")),
                    TripGtfsId = reader.GetString(reader.GetOrdinal("trip_gtfs_id")),
                    ServiceDate = reader.GetString(reader.GetOrdinal("service_date")),
                    ScheduledArrival = reader.IsDBNull(reader.GetOrdinal("scheduled_arrival")) ? null : reader.GetInt32(reader.GetOrdinal("scheduled_arrival")),
                    ScheduledDeparture = reader.GetInt32(reader.GetOrdinal("scheduled_departure")),
                    RealtimeArrival = reader.IsDBNull(reader.GetOrdinal("realtime_arrival")) ? null : reader.GetInt32(reader.GetOrdinal("realtime_arrival")),
                    RealtimeDeparture = reader.IsDBNull(reader.GetOrdinal("realtime_departure")) ? null : reader.GetInt32(reader.GetOrdinal("realtime_departure")),
                    ArrivalDelay = reader.IsDBNull(reader.GetOrdinal("arrival_delay")) ? null : reader.GetInt32(reader.GetOrdinal("arrival_delay")),
                    DepartureDelay = reader.IsDBNull(reader.GetOrdinal("departure_delay")) ? null : reader.GetInt32(reader.GetOrdinal("departure_delay")),
                    Realtime = reader.GetInt32(reader.GetOrdinal("realtime")),
                    RealtimeState = reader.IsDBNull(reader.GetOrdinal("realtime_state")) ? null : reader.GetString(reader.GetOrdinal("realtime_state")),
                    QueriedAt = reader.GetInt64(reader.GetOrdinal("queried_at")),
                    RouteShortName = reader.IsDBNull(reader.GetOrdinal("route_short_name")) ? null : reader.GetString(reader.GetOrdinal("route_short_name")),
                    RouteLongName = reader.IsDBNull(reader.GetOrdinal("route_long_name")) ? null : reader.GetString(reader.GetOrdinal("route_long_name")),
                    Mode = reader.IsDBNull(reader.GetOrdinal("mode")) ? null : reader.GetString(reader.GetOrdinal("mode")),
                    Headsign = reader.IsDBNull(reader.GetOrdinal("headsign")) ? null : reader.GetString(reader.GetOrdinal("headsign")),
                    DirectionId = reader.IsDBNull(reader.GetOrdinal("direction_id")) ? null : reader.GetInt32(reader.GetOrdinal("direction_id")),
                };
                if (includeStopName)
                {
                    int stopNameOrd = reader.GetOrdinal("stop_name");
                    obs.StopName = reader.IsDBNull(stopNameOrd) ? null : reader.GetString(stopNameOrd);
                }
                result.Add(obs);
            }
            return result;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }
}
