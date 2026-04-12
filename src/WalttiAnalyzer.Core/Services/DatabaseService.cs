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

    public void EnsureCreated()
    {
        _context.Database.EnsureCreated();
        ApplySchemaMigrations();
        SeedRealtimeStates();
    }

    // -----------------------------------------------------------------------
    // Schema migrations
    // (EnsureCreated only creates tables for a blank DB; these patches apply
    //  to existing databases that have an older schema.)
    // -----------------------------------------------------------------------

    private void ApplySchemaMigrations()
    {
        // v2: routes table, trips.route_id FK, observations rebuilt with new schema
        if (!TableExists("routes"))
        {
            _logger.LogInformation("Schema migration: creating routes table");
            if (IsSqlite)
            {
                _context.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS routes (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        gtfs_id TEXT NOT NULL UNIQUE,
                        short_name TEXT,
                        long_name TEXT,
                        mode TEXT,
                        updated_at INTEGER NOT NULL DEFAULT 0
                    )");
            }
            else
            {
                _context.Database.ExecuteSqlRaw(@"
                    CREATE TABLE routes (
                        id BIGINT NOT NULL IDENTITY(1,1),
                        gtfs_id NVARCHAR(450) NOT NULL,
                        short_name NVARCHAR(MAX) NULL,
                        long_name NVARCHAR(MAX) NULL,
                        mode NVARCHAR(MAX) NULL,
                        updated_at BIGINT NOT NULL DEFAULT 0,
                        CONSTRAINT PK_routes PRIMARY KEY (id),
                        CONSTRAINT UQ_routes_gtfs_id UNIQUE (gtfs_id)
                    )");
            }
        }

        if (!ColumnExists("trips", "route_id"))
        {
            _logger.LogInformation("Schema migration: adding trips.route_id");
            if (IsSqlite)
                _context.Database.ExecuteSqlRaw("ALTER TABLE trips ADD COLUMN route_id INTEGER NOT NULL DEFAULT 0");
            else
                _context.Database.ExecuteSqlRaw("ALTER TABLE trips ADD route_id BIGINT NOT NULL DEFAULT 0");
        }

        // service_date changed from string to int, and queried_at (NOT NULL, no default) was
        // removed. Both make the old table incompatible. Rebuild it when service_date is the
        // wrong type — old data is unusable anyway (wrong format, missing delay_source).
        if (ObservationsServiceDateIsString())
        {
            _logger.LogInformation("Schema migration: rebuilding observations table (service_date type change + column cleanup)");
            RebuildObservationsTable();
        }
        else if (!ColumnExists("observations", "delay_source"))
        {
            _logger.LogInformation("Schema migration: adding observations.delay_source");
            if (IsSqlite)
                _context.Database.ExecuteSqlRaw("ALTER TABLE observations ADD COLUMN delay_source INTEGER NOT NULL DEFAULT 0");
            else
                _context.Database.ExecuteSqlRaw("ALTER TABLE observations ADD delay_source INT NOT NULL DEFAULT 0");
        }
    }

    /// <summary>Returns true when the observations.service_date column is not a numeric type,
    /// meaning the table was created with the old schema (string service dates).</summary>
    private bool ObservationsServiceDateIsString()
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = IsSqlite
                ? "SELECT type FROM pragma_table_info('observations') WHERE name='service_date'"
                : "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='observations' AND COLUMN_NAME='service_date'";
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return false;
            var typeStr = result.ToString()!.ToUpperInvariant();
            // SQLite stores as "INTEGER"; SQL Server stores as "int"
            return !typeStr.Contains("INT");
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    private void RebuildObservationsTable()
    {
        if (IsSqlite)
        {
            _context.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS observations");
            _context.Database.ExecuteSqlRaw(@"
                CREATE TABLE observations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stop_id INTEGER NOT NULL,
                    trip_id INTEGER NOT NULL,
                    service_date INTEGER NOT NULL,
                    scheduled_departure INTEGER NOT NULL,
                    departure_delay INTEGER NULL,
                    delay_source INTEGER NOT NULL DEFAULT 0,
                    realtime_state_id INTEGER NULL,
                    FOREIGN KEY (stop_id) REFERENCES stops(id),
                    FOREIGN KEY (trip_id) REFERENCES trips(id),
                    FOREIGN KEY (realtime_state_id) REFERENCES realtime_states(id),
                    UNIQUE (stop_id, trip_id, service_date)
                )");
            _context.Database.ExecuteSqlRaw(
                "CREATE INDEX idx_obs_stop_date ON observations (stop_id, service_date)");
        }
        else
        {
            _context.Database.ExecuteSqlRaw("DROP TABLE observations");
            _context.Database.ExecuteSqlRaw(@"
                CREATE TABLE observations (
                    id BIGINT NOT NULL IDENTITY(1,1),
                    stop_id BIGINT NOT NULL,
                    trip_id BIGINT NOT NULL,
                    service_date INT NOT NULL,
                    scheduled_departure INT NOT NULL,
                    departure_delay INT NULL,
                    delay_source INT NOT NULL DEFAULT 0,
                    realtime_state_id INT NULL,
                    CONSTRAINT PK_observations PRIMARY KEY (id),
                    CONSTRAINT FK_obs_stops FOREIGN KEY (stop_id) REFERENCES stops(id),
                    CONSTRAINT FK_obs_trips FOREIGN KEY (trip_id) REFERENCES trips(id),
                    CONSTRAINT FK_obs_states FOREIGN KEY (realtime_state_id) REFERENCES realtime_states(id),
                    CONSTRAINT uq_obs_stop_trip_date UNIQUE (stop_id, trip_id, service_date)
                )");
            _context.Database.ExecuteSqlRaw(
                "CREATE INDEX idx_obs_stop_date ON observations (stop_id, service_date)");
        }
    }

    private bool TableExists(string tableName)
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = IsSqlite
                ? $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'"
                : $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{tableName}' AND TABLE_TYPE='BASE TABLE'";
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value;
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    private bool ColumnExists(string tableName, string columnName)
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = IsSqlite
                ? $"SELECT name FROM pragma_table_info('{tableName}') WHERE name='{columnName}'"
                : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{tableName}' AND COLUMN_NAME='{columnName}'";
            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value;
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    private void SeedRealtimeStates()
    {
        var wanted = new (int Id, string Name)[]
        {
            (0, "SCHEDULED"),
            (1, "UPDATED"),
            (2, "CANCELED"),
            (3, "SKIPPED"),
            (4, "ADDED"),
            (5, "MODIFIED"),
        };
        var existing = _context.RealtimeStates.Select(rs => rs.Name).ToHashSet();
        var toAdd = wanted.Where(w => !existing.Contains(w.Name)).ToList();
        if (toAdd.Count == 0) return;
        foreach (var (id, name) in toAdd)
            _context.RealtimeStates.Add(new RealtimeState { Id = id, Name = name });
        _context.SaveChanges();
    }

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

    public async Task<List<Stop>> GetAllStopsAsync(string? feedId = null, CancellationToken ct = default)
    {
        var q = _context.Stops.AsQueryable();
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(s => s.GtfsId.StartsWith(feedId + ":"));
        return await q.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<List<string>> GetAllStopIdsAsync(string? feedId = null) =>
        (await GetAllStopsAsync(feedId)).Select(s => s.GtfsId).ToList();

    // -----------------------------------------------------------------------
    // Route operations
    // -----------------------------------------------------------------------

    public async Task UpsertRouteAsync(string gtfsId, string? shortName, string? longName, string? mode)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (IsSqlite)
        {
            await _context.Database.ExecuteSqlAsync($@"
                INSERT INTO routes (gtfs_id, short_name, long_name, mode, updated_at)
                VALUES ({gtfsId}, {shortName}, {longName}, {mode}, {now})
                ON CONFLICT(gtfs_id) DO UPDATE SET
                    short_name=excluded.short_name, long_name=excluded.long_name,
                    mode=excluded.mode, updated_at=excluded.updated_at");
        }
        else
        {
            await _context.Database.ExecuteSqlAsync($@"
                MERGE routes AS t
                USING (SELECT {gtfsId} AS gtfs_id, {shortName} AS short_name,
                              {longName} AS long_name, {mode} AS mode, {now} AS updated_at) AS s
                ON t.gtfs_id = s.gtfs_id
                WHEN MATCHED THEN UPDATE SET
                    short_name=s.short_name, long_name=s.long_name, mode=s.mode, updated_at=s.updated_at
                WHEN NOT MATCHED THEN INSERT (gtfs_id, short_name, long_name, mode, updated_at)
                    VALUES (s.gtfs_id, s.short_name, s.long_name, s.mode, s.updated_at);");
        }
    }

    public async Task UpsertRoutesBatchAsync(List<Dictionary<string, object?>> routes)
    {
        foreach (var r in routes)
            await UpsertRouteAsync(
                (string)r["gtfs_id"]!,
                r.GetValueOrDefault("short_name") as string,
                r.GetValueOrDefault("long_name") as string,
                r.GetValueOrDefault("mode") as string);
    }

    public async Task<List<string>> GetAllRoutesAsync(string? feedId = null, CancellationToken ct = default)
    {
        var q = _context.Routes.Where(r => r.ShortName != null);
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(r => r.GtfsId.StartsWith(feedId + ":"));
        return await q.Select(r => r.ShortName!).Distinct().OrderBy(r => r).ToListAsync(ct);
    }

    public async Task<List<string>> GetRoutesForStopAsync(string stopId, CancellationToken ct = default)
    {
        return await _context.Observations
            .Where(o => o.Stop!.GtfsId == stopId)
            .Select(o => o.Trip!.Route!.ShortName!)
            .Where(n => n != null)
            .Distinct().OrderBy(r => r).ToListAsync(ct);
    }

    public async Task<List<string>> GetAllHeadsignsAsync(string? feedId = null, CancellationToken ct = default)
    {
        var q = _context.Trips.Where(t => t.Headsign != null);
        if (!string.IsNullOrEmpty(feedId))
            q = q.Where(t => t.Route!.GtfsId.StartsWith(feedId + ":"));
        return await q.Select(t => t.Headsign!).Distinct().OrderBy(h => h).ToListAsync(ct);
    }

    public async Task<List<string>> GetHeadsignsForStopAsync(string stopId, CancellationToken ct = default)
    {
        return await _context.Observations
            .Where(o => o.Stop!.GtfsId == stopId)
            .Select(o => o.Trip!.Headsign!)
            .Where(h => h != null)
            .Distinct().OrderBy(h => h).ToListAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Trip operations
    // -----------------------------------------------------------------------

    public async Task UpsertTripsBatchAsync(List<Dictionary<string, object?>> trips)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Resolve route IDs
        var routeGtfsIds = trips
            .Select(t => t.GetValueOrDefault("route_gtfs_id") as string)
            .Where(id => id != null).Distinct().ToList();
        var routeMap = await _context.Routes
            .Where(r => routeGtfsIds.Contains(r.GtfsId))
            .ToDictionaryAsync(r => r.GtfsId, r => r.Id);

        foreach (var t in trips)
        {
            var gid = (string)t["gtfs_id"]!;
            var routeGtfsId = t.GetValueOrDefault("route_gtfs_id") as string;
            long? routeId = routeGtfsId != null && routeMap.TryGetValue(routeGtfsId, out var rid) ? rid : null;
            var hs = t.GetValueOrDefault("headsign") as string;
            var did = t.GetValueOrDefault("direction_id") as int?;

            if (routeId == null) continue; // Can't store trip without route

            if (IsSqlite)
            {
                await _context.Database.ExecuteSqlAsync($@"
                    INSERT INTO trips (gtfs_id, route_id, headsign, direction_id, updated_at)
                    VALUES ({gid}, {routeId}, {hs}, {did}, {now})
                    ON CONFLICT(gtfs_id) DO UPDATE SET
                        route_id=excluded.route_id, headsign=excluded.headsign,
                        direction_id=excluded.direction_id, updated_at=excluded.updated_at");
            }
            else
            {
                await _context.Database.ExecuteSqlAsync($@"
                    MERGE trips AS t
                    USING (SELECT {gid} AS gtfs_id, {routeId} AS route_id,
                                  {hs} AS headsign, {did} AS direction_id, {now} AS updated_at) AS s
                    ON t.gtfs_id = s.gtfs_id
                    WHEN MATCHED THEN UPDATE SET
                        route_id=s.route_id, headsign=s.headsign,
                        direction_id=s.direction_id, updated_at=s.updated_at
                    WHEN NOT MATCHED THEN INSERT (gtfs_id, route_id, headsign, direction_id, updated_at)
                        VALUES (s.gtfs_id, s.route_id, s.headsign, s.direction_id, s.updated_at);");
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

            var sd = Convert.ToInt32(obs["service_date"]);
            var sDep = Convert.ToInt32(obs["scheduled_departure"]);
            int? dDelay = GetNullableInt(obs, "departure_delay");
            var delaySrc = Convert.ToInt32(obs["delay_source"]);
            var stateStr = obs.GetValueOrDefault("realtime_state") as string;
            int? stateId = stateStr != null && states.TryGetValue(stateStr, out var sId) ? sId : null;

            if (IsSqlite)
            {
                // Higher delay_source always wins: MEASURED(2) > PROPAGATED(1) > SCHEDULED(0)
                await _context.Database.ExecuteSqlAsync($@"
                    INSERT INTO observations (stop_id, trip_id, service_date,
                        scheduled_departure, departure_delay, delay_source, realtime_state_id)
                    VALUES ({stopId}, {tripId}, {sd}, {sDep}, {dDelay}, {delaySrc}, {stateId})
                    ON CONFLICT(stop_id, trip_id, service_date) DO UPDATE SET
                        scheduled_departure=excluded.scheduled_departure,
                        departure_delay=CASE WHEN excluded.delay_source >= delay_source
                            THEN excluded.departure_delay ELSE departure_delay END,
                        delay_source=CASE WHEN excluded.delay_source >= delay_source
                            THEN excluded.delay_source ELSE delay_source END,
                        realtime_state_id=CASE WHEN excluded.delay_source >= delay_source
                            THEN excluded.realtime_state_id ELSE realtime_state_id END");
            }
            else
            {
                await _context.Database.ExecuteSqlAsync($@"
                    MERGE observations AS t
                    USING (SELECT {stopId} AS stop_id, {tripId} AS trip_id, {sd} AS service_date,
                                  {sDep} AS scheduled_departure, {dDelay} AS departure_delay,
                                  {delaySrc} AS delay_source, {stateId} AS realtime_state_id) AS s
                    ON t.stop_id=s.stop_id AND t.trip_id=s.trip_id AND t.service_date=s.service_date
                    WHEN MATCHED THEN UPDATE SET
                        scheduled_departure=s.scheduled_departure,
                        departure_delay=CASE WHEN s.delay_source >= t.delay_source
                            THEN s.departure_delay ELSE t.departure_delay END,
                        delay_source=CASE WHEN s.delay_source >= t.delay_source
                            THEN s.delay_source ELSE t.delay_source END,
                        realtime_state_id=CASE WHEN s.delay_source >= t.delay_source
                            THEN s.realtime_state_id ELSE t.realtime_state_id END
                    WHEN NOT MATCHED THEN INSERT (stop_id, trip_id, service_date, scheduled_departure,
                        departure_delay, delay_source, realtime_state_id)
                    VALUES (s.stop_id, s.trip_id, s.service_date, s.scheduled_departure,
                        s.departure_delay, s.delay_source, s.realtime_state_id);");
            }
        }
    }

    public async Task<List<Observation>> GetObservationsAsync(string? stopId,
        int startDate, int endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null,
        CancellationToken ct = default)
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
        return await ReadObservationsRawAsync(sql, parms, includeStopName: allStops, ct: ct);
    }

    public async Task<List<Observation>> GetLatestObservationsAsync(int limit = 300, string? feedId = null,
        CancellationToken ct = default)
    {
        var parms = new List<(string Name, object? Value)>();
        var sql = $"SELECT {ObsColumns}, s.name AS stop_name {ObsJoins} WHERE o.delay_source>=1";
        if (!string.IsNullOrEmpty(feedId)) { sql += " AND s.gtfs_id LIKE @feed"; parms.Add(("@feed", $"{feedId}:%")); }
        sql += " ORDER BY o.service_date DESC, o.scheduled_departure DESC";
        sql += IsSqlite ? " LIMIT @lim" : " OFFSET 0 ROWS FETCH NEXT @lim ROWS ONLY";
        parms.Add(("@lim", limit));
        return await ReadObservationsRawAsync(sql, parms, includeStopName: true, ct: ct);
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

    public async Task<CollectionLogEntry?> GetLatestCollectionAsync(string stopId, string queryType,
        CancellationToken ct = default) =>
        await _context.CollectionLog
            .Where(l => l.StopGtfsId == stopId && l.QueryType == queryType)
            .OrderByDescending(l => l.QueriedAt)
            .FirstOrDefaultAsync(ct);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private const string ObsColumns = @"
        o.id, s.gtfs_id AS stop_gtfs_id, t.gtfs_id AS trip_gtfs_id,
        o.service_date, o.scheduled_departure,
        o.departure_delay, o.delay_source,
        rs.name AS realtime_state,
        r.short_name AS route_short_name, t.headsign, t.direction_id";

    private const string ObsJoins = @"
        FROM observations o
        JOIN stops s ON o.stop_id=s.id
        JOIN trips t ON o.trip_id=t.id
        JOIN routes r ON t.route_id=r.id
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
        if (!string.IsNullOrEmpty(route)) { sql += " AND r.short_name=@route"; parms.Add(("@route", route)); }
        if (!string.IsNullOrEmpty(headsign)) { sql += " AND t.headsign=@headsign"; parms.Add(("@headsign", headsign)); }
        if (timeFrom.HasValue) { sql += " AND o.scheduled_departure>=@tf"; parms.Add(("@tf", timeFrom.Value)); }
        if (timeTo.HasValue) { sql += " AND o.scheduled_departure<=@tt"; parms.Add(("@tt", timeTo.Value)); }
    }

    private static void AppendPastOnlyFilter(ref string sql, List<(string Name, object? Value)> parms)
    {
        var helsinkiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz);
        var today = int.Parse(helsinkiNow.ToString("yyyyMMdd"));
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
        string sql, List<(string Name, object? Value)> parms, bool includeStopName,
        CancellationToken ct = default)
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
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
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<Observation>();
            while (await reader.ReadAsync(ct))
            {
                var obs = new Observation
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    StopGtfsId = reader.GetString(reader.GetOrdinal("stop_gtfs_id")),
                    TripGtfsId = reader.GetString(reader.GetOrdinal("trip_gtfs_id")),
                    ServiceDate = reader.GetInt32(reader.GetOrdinal("service_date")),
                    ScheduledDeparture = reader.GetInt32(reader.GetOrdinal("scheduled_departure")),
                    DepartureDelay = reader.IsDBNull(reader.GetOrdinal("departure_delay")) ? null : reader.GetInt32(reader.GetOrdinal("departure_delay")),
                    DelaySource = reader.GetInt32(reader.GetOrdinal("delay_source")),
                    RealtimeState = reader.IsDBNull(reader.GetOrdinal("realtime_state")) ? null : reader.GetString(reader.GetOrdinal("realtime_state")),
                    RouteShortName = reader.IsDBNull(reader.GetOrdinal("route_short_name")) ? null : reader.GetString(reader.GetOrdinal("route_short_name")),
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

    private async Task<List<T>> QueryRawAsync<T>(string sql,
        List<(string Name, object? Value)> parms, Func<DbDataReader, T> mapper,
        CancellationToken ct = default)
    {
        var conn = _context.Database.GetDbConnection();
        bool wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
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
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<T>();
            while (await reader.ReadAsync(ct))
                result.Add(mapper(reader));
            return result;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Faceted filter options
    // -----------------------------------------------------------------------

    public async Task<object> GetFacetsAsync(string? stopId, string? route, string? headsign,
        int start, int end, int? timeFrom, int? timeTo, string? feedId,
        CancellationToken ct = default)
    {
        const string baseJoins = @"FROM observations o
            JOIN stops s ON o.stop_id = s.id
            JOIN trips t ON o.trip_id = t.id
            JOIN routes r ON t.route_id = r.id";

        // Stops facet: filtered by route + headsign (not by stop), scoped to feedId
        var stopsSql = $"SELECT DISTINCT s.gtfs_id, s.name {baseJoins} WHERE o.service_date>=@start AND o.service_date<=@end";
        var stopsParms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        if (!string.IsNullOrEmpty(feedId)) { stopsSql += " AND s.gtfs_id LIKE @feed"; stopsParms.Add(("@feed", $"{feedId}:%")); }
        AppendFilters(ref stopsSql, stopsParms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref stopsSql, stopsParms);
        stopsSql += " ORDER BY s.name";

        // Routes facet: filtered by stop + headsign (not by route)
        var routesSql = $"SELECT DISTINCT r.short_name {baseJoins} WHERE r.short_name IS NOT NULL AND o.service_date>=@start AND o.service_date<=@end";
        var routesParms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        AppendStopFilter(ref routesSql, routesParms, stopId, feedId);
        AppendFilters(ref routesSql, routesParms, null, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref routesSql, routesParms);
        routesSql += " ORDER BY r.short_name";

        // Headsigns facet: filtered by stop + route (not by headsign)
        var headsignsSql = $"SELECT DISTINCT t.headsign {baseJoins} WHERE t.headsign IS NOT NULL AND o.service_date>=@start AND o.service_date<=@end";
        var headsignsParms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        AppendStopFilter(ref headsignsSql, headsignsParms, stopId, feedId);
        AppendFilters(ref headsignsSql, headsignsParms, route, timeFrom, timeTo, null);
        AppendPastOnlyFilter(ref headsignsSql, headsignsParms);
        headsignsSql += " ORDER BY t.headsign";

        var stops = await QueryRawAsync(stopsSql, stopsParms,
            r => new { value = r.GetString(0), name = r.GetString(1) }, ct);
        var routes = await QueryRawAsync(routesSql, routesParms,
            r => r.GetString(0), ct);
        var headsigns = await QueryRawAsync(headsignsSql, headsignsParms,
            r => r.IsDBNull(0) ? null : r.GetString(0), ct);

        return new
        {
            stops,
            routes,
            headsigns = headsigns.Where(h => h != null).ToList()
        };
    }
}
