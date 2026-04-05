using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Functions.Models;

namespace WalttiAnalyzer.Functions.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    private bool _initialized;

    private const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS stops (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    gtfs_id     TEXT UNIQUE NOT NULL,
    name        TEXT NOT NULL,
    code        TEXT,
    lat         REAL,
    lon         REAL,
    updated_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS trips (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    gtfs_id          TEXT UNIQUE NOT NULL,
    route_short_name TEXT,
    route_long_name  TEXT,
    mode             TEXT,
    headsign         TEXT,
    direction_id     INTEGER,
    updated_at       INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS realtime_states (
    id   INTEGER PRIMARY KEY,
    name TEXT UNIQUE NOT NULL
);

INSERT OR IGNORE INTO realtime_states (id, name) VALUES (0, 'SCHEDULED');
INSERT OR IGNORE INTO realtime_states (id, name) VALUES (1, 'UPDATED');
INSERT OR IGNORE INTO realtime_states (id, name) VALUES (2, 'CANCELED');

CREATE TABLE IF NOT EXISTS observations (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    stop_id              INTEGER NOT NULL REFERENCES stops(id),
    trip_id              INTEGER NOT NULL REFERENCES trips(id),
    service_date         TEXT NOT NULL,
    scheduled_arrival    INTEGER,
    scheduled_departure  INTEGER NOT NULL,
    realtime_arrival     INTEGER,
    realtime_departure   INTEGER,
    arrival_delay        INTEGER,
    departure_delay      INTEGER,
    realtime             INTEGER NOT NULL DEFAULT 0,
    realtime_state_id    INTEGER REFERENCES realtime_states(id),
    queried_at           INTEGER NOT NULL,
    UNIQUE(stop_id, trip_id, service_date)
);

CREATE INDEX IF NOT EXISTS idx_obs_stop_date ON observations(stop_id, service_date);

CREATE TABLE IF NOT EXISTS collection_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    queried_at      INTEGER NOT NULL,
    stop_gtfs_id    TEXT NOT NULL,
    query_type      TEXT NOT NULL,
    service_date    TEXT,
    departures_found INTEGER,
    no_service      INTEGER DEFAULT 0,
    error           TEXT
);
";

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public SqliteConnection Connect(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void InitDb(string dbPath)
    {
        if (_initialized) return;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = Connect(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();
        _initialized = true;
    }

    // -----------------------------------------------------------------------
    // Stop operations
    // -----------------------------------------------------------------------

    public void UpsertStop(SqliteConnection db, string gtfsId, string name, string? code, double? lat, double? lon)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO stops (gtfs_id, name, code, lat, lon, updated_at)
            VALUES ($gtfs_id, $name, $code, $lat, $lon, $updated_at)
            ON CONFLICT(gtfs_id) DO UPDATE SET
                name = excluded.name,
                code = excluded.code,
                lat = excluded.lat,
                lon = excluded.lon,
                updated_at = excluded.updated_at";
        cmd.Parameters.AddWithValue("$gtfs_id", gtfsId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void UpsertStopsBatch(SqliteConnection db, List<Dictionary<string, object?>> stops)
    {
        using var tx = db.BeginTransaction();
        foreach (var s in stops)
        {
            UpsertStop(db,
                (string)s["gtfs_id"]!,
                (string)s["name"]!,
                s.GetValueOrDefault("code") as string,
                s.GetValueOrDefault("lat") as double?,
                s.GetValueOrDefault("lon") as double?);
        }
        tx.Commit();
    }

    public Stop? GetStop(SqliteConnection db, string gtfsId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM stops WHERE gtfs_id = $id";
        cmd.Parameters.AddWithValue("$id", gtfsId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadStop(reader);
    }

    public List<Stop> GetAllStops(SqliteConnection db, string? feedId = null)
    {
        using var cmd = db.CreateCommand();
        if (!string.IsNullOrEmpty(feedId))
        {
            cmd.CommandText = "SELECT * FROM stops WHERE gtfs_id LIKE $feed ORDER BY name";
            cmd.Parameters.AddWithValue("$feed", $"{feedId}:%");
        }
        else
        {
            cmd.CommandText = "SELECT * FROM stops ORDER BY name";
        }
        using var reader = cmd.ExecuteReader();
        var result = new List<Stop>();
        while (reader.Read())
            result.Add(ReadStop(reader));
        return result;
    }

    public List<string> GetAllStopIds(SqliteConnection db, string? feedId = null)
    {
        return GetAllStops(db, feedId).Select(s => s.GtfsId).ToList();
    }

    public List<string> GetRoutesForStop(SqliteConnection db, string stopId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT t.route_short_name
            FROM observations o
            JOIN trips t ON o.trip_id = t.id
            JOIN stops s ON o.stop_id = s.id
            WHERE s.gtfs_id = $sid AND t.route_short_name IS NOT NULL
            ORDER BY t.route_short_name";
        cmd.Parameters.AddWithValue("$sid", stopId);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public List<string> GetAllRoutes(SqliteConnection db, string? feedId = null)
    {
        using var cmd = db.CreateCommand();
        var sql = @"SELECT DISTINCT t.route_short_name
                    FROM observations o
                    JOIN trips t ON o.trip_id = t.id
                    WHERE t.route_short_name IS NOT NULL";
        if (!string.IsNullOrEmpty(feedId))
        {
            sql += " AND o.stop_id IN (SELECT id FROM stops WHERE gtfs_id LIKE $feed)";
        }
        sql += " ORDER BY t.route_short_name";
        cmd.CommandText = sql;
        if (!string.IsNullOrEmpty(feedId))
            cmd.Parameters.AddWithValue("$feed", $"{feedId}:%");
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    // -----------------------------------------------------------------------
    // Trip operations
    // -----------------------------------------------------------------------

    public void UpsertTrip(SqliteConnection db, string gtfsId, string? routeShortName,
        string? routeLongName, string? mode, string? headsign, int? directionId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO trips (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
            VALUES ($gtfs_id, $route_short_name, $route_long_name, $mode, $headsign, $direction_id, $updated_at)
            ON CONFLICT(gtfs_id) DO UPDATE SET
                route_short_name = excluded.route_short_name,
                route_long_name = excluded.route_long_name,
                mode = excluded.mode,
                headsign = excluded.headsign,
                direction_id = excluded.direction_id,
                updated_at = excluded.updated_at";
        cmd.Parameters.AddWithValue("$gtfs_id", gtfsId);
        cmd.Parameters.AddWithValue("$route_short_name", (object?)routeShortName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$route_long_name", (object?)routeLongName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", (object?)mode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headsign", (object?)headsign ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$direction_id", (object?)directionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void UpsertTripsBatch(SqliteConnection db, List<Dictionary<string, object?>> trips)
    {
        using var tx = db.BeginTransaction();
        foreach (var t in trips)
        {
            UpsertTrip(db,
                (string)t["gtfs_id"]!,
                t.GetValueOrDefault("route_short_name") as string,
                t.GetValueOrDefault("route_long_name") as string,
                t.GetValueOrDefault("mode") as string,
                t.GetValueOrDefault("headsign") as string,
                t.GetValueOrDefault("direction_id") as int?);
        }
        tx.Commit();
    }

    // -----------------------------------------------------------------------
    // Observation operations
    // -----------------------------------------------------------------------

    public void UpsertObservation(SqliteConnection db, Dictionary<string, object?> obs)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO observations (
                stop_id, trip_id, service_date,
                scheduled_arrival, scheduled_departure, realtime_arrival, realtime_departure,
                arrival_delay, departure_delay, realtime, realtime_state_id, queried_at
            ) VALUES (
                (SELECT id FROM stops WHERE gtfs_id = $stop_gtfs_id),
                (SELECT id FROM trips WHERE gtfs_id = $trip_gtfs_id),
                $service_date,
                $scheduled_arrival, $scheduled_departure, $realtime_arrival, $realtime_departure,
                $arrival_delay, $departure_delay, $realtime,
                (SELECT id FROM realtime_states WHERE name = $realtime_state),
                $queried_at
            )
            ON CONFLICT(stop_id, trip_id, service_date) DO UPDATE SET
                scheduled_arrival = excluded.scheduled_arrival,
                scheduled_departure = excluded.scheduled_departure,
                realtime_arrival = excluded.realtime_arrival,
                realtime_departure = excluded.realtime_departure,
                arrival_delay = excluded.arrival_delay,
                departure_delay = excluded.departure_delay,
                realtime = excluded.realtime,
                realtime_state_id = excluded.realtime_state_id,
                queried_at = excluded.queried_at";
        AddObsParams(cmd, obs);
        cmd.ExecuteNonQuery();
    }

    public void UpsertObservationsBatch(SqliteConnection db, List<Dictionary<string, object?>> observations)
    {
        using var tx = db.BeginTransaction();
        foreach (var obs in observations)
        {
            UpsertObservation(db, obs);
        }
        tx.Commit();
    }

    public List<Observation> GetObservations(SqliteConnection db, string stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null)
    {
        using var cmd = db.CreateCommand();
        var sql = $@"SELECT {ObsColumns}
                     {ObsJoins}
                     WHERE s.gtfs_id = $sid AND o.service_date >= $start AND o.service_date <= $end";
        var parms = new List<(string, object)>
        {
            ("$sid", stopId), ("$start", startDate), ("$end", endDate)
        };
        if (!string.IsNullOrEmpty(route)) { sql += " AND t.route_short_name = $route"; parms.Add(("$route", route)); }
        if (timeFrom.HasValue) { sql += " AND o.scheduled_departure >= $tf"; parms.Add(("$tf", timeFrom.Value)); }
        if (timeTo.HasValue) { sql += " AND o.scheduled_departure <= $tt"; parms.Add(("$tt", timeTo.Value)); }
        sql += " ORDER BY o.service_date DESC, o.scheduled_departure DESC";
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);
        return ReadObservations(cmd);
    }

    public List<Observation> GetLatestObservations(SqliteConnection db, int limit = 100, string? feedId = null)
    {
        using var cmd = db.CreateCommand();
        var sql = $@"SELECT {ObsColumns}, s.name AS stop_name
                     {ObsJoins}
                     WHERE o.realtime = 1";
        if (!string.IsNullOrEmpty(feedId))
        {
            sql += " AND s.gtfs_id LIKE $feed";
            cmd.Parameters.AddWithValue("$feed", $"{feedId}:%");
        }
        sql += " ORDER BY o.queried_at DESC, o.service_date DESC, o.scheduled_departure DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.CommandText = sql;
        return ReadObservations(cmd);
    }

    // -----------------------------------------------------------------------
    // Collection log
    // -----------------------------------------------------------------------

    public void LogCollection(SqliteConnection db, string stopGtfsId, string queryType,
        string? serviceDate = null, int departuresFound = 0, int noService = 0, string? error = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO collection_log
            (queried_at, stop_gtfs_id, query_type, service_date, departures_found, no_service, error)
            VALUES ($qa, $sid, $qt, $sd, $df, $ns, $err)";
        cmd.Parameters.AddWithValue("$qa", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$sid", stopGtfsId);
        cmd.Parameters.AddWithValue("$qt", queryType);
        cmd.Parameters.AddWithValue("$sd", (object?)serviceDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$df", departuresFound);
        cmd.Parameters.AddWithValue("$ns", noService);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public CollectionLogEntry? GetLatestCollection(SqliteConnection db, string stopId, string queryType)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM collection_log
            WHERE stop_gtfs_id = $sid AND query_type = $qt
            ORDER BY queried_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", stopId);
        cmd.Parameters.AddWithValue("$qt", queryType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new CollectionLogEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            QueriedAt = reader.GetInt64(reader.GetOrdinal("queried_at")),
            StopGtfsId = reader.GetString(reader.GetOrdinal("stop_gtfs_id")),
            QueryType = reader.GetString(reader.GetOrdinal("query_type")),
            ServiceDate = reader.IsDBNull(reader.GetOrdinal("service_date")) ? null : reader.GetString(reader.GetOrdinal("service_date")),
            DeparturesFound = reader.GetInt32(reader.GetOrdinal("departures_found")),
            NoService = reader.GetInt32(reader.GetOrdinal("no_service")),
            Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private const string ObsColumns = @"
        o.id, s.gtfs_id AS stop_gtfs_id, t.gtfs_id AS trip_gtfs_id,
        o.service_date, o.scheduled_arrival, o.scheduled_departure,
        o.realtime_arrival, o.realtime_departure,
        o.arrival_delay, o.departure_delay, o.realtime,
        rs.name AS realtime_state, o.queried_at,
        t.route_short_name, t.route_long_name, t.mode, t.headsign, t.direction_id";

    private const string ObsJoins = @"
        FROM observations o
        JOIN stops s ON o.stop_id = s.id
        JOIN trips t ON o.trip_id = t.id
        LEFT JOIN realtime_states rs ON o.realtime_state_id = rs.id";

    private static Stop ReadStop(SqliteDataReader reader)
    {
        return new Stop
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            GtfsId = reader.GetString(reader.GetOrdinal("gtfs_id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Code = reader.IsDBNull(reader.GetOrdinal("code")) ? null : reader.GetString(reader.GetOrdinal("code")),
            Lat = reader.IsDBNull(reader.GetOrdinal("lat")) ? null : reader.GetDouble(reader.GetOrdinal("lat")),
            Lon = reader.IsDBNull(reader.GetOrdinal("lon")) ? null : reader.GetDouble(reader.GetOrdinal("lon")),
            UpdatedAt = reader.GetInt64(reader.GetOrdinal("updated_at")),
        };
    }

    private static List<Observation> ReadObservations(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<Observation>();
        while (reader.Read())
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
            // stop_name may or may not be in the result set
            try { obs.StopName = reader.IsDBNull(reader.GetOrdinal("stop_name")) ? null : reader.GetString(reader.GetOrdinal("stop_name")); }
            catch (ArgumentOutOfRangeException) { /* column not present */ }
            result.Add(obs);
        }
        return result;
    }

    private static void AddObsParams(SqliteCommand cmd, Dictionary<string, object?> obs)
    {
        cmd.Parameters.AddWithValue("$stop_gtfs_id", obs["stop_gtfs_id"]!);
        cmd.Parameters.AddWithValue("$trip_gtfs_id", obs["trip_gtfs_id"]!);
        cmd.Parameters.AddWithValue("$service_date", obs["service_date"]!);
        cmd.Parameters.AddWithValue("$scheduled_arrival", obs["scheduled_arrival"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scheduled_departure", obs["scheduled_departure"]!);
        cmd.Parameters.AddWithValue("$realtime_arrival", obs["realtime_arrival"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$realtime_departure", obs["realtime_departure"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$arrival_delay", obs["arrival_delay"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$departure_delay", obs["departure_delay"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$realtime", obs["realtime"]!);
        cmd.Parameters.AddWithValue("$realtime_state", obs["realtime_state"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$queried_at", obs["queried_at"]!);
    }
}
