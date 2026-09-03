using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Core.Data;

namespace WalttiAnalyzer.Core.Services;

public class AnalyzerService
{
    private readonly WalttiDbContext _context;
    private readonly ILogger<AnalyzerService> _logger;

    public const int OutlierThreshold = 1800; // 30 minutes
    private const int OnTimeEarlyBoundary = -60;
    private const int OnTimeLateBoundary = 180;
    private const int SlightlyLateBoundary = 480;
    private const int VeryEarlyBoundary = -180;

    private bool IsSqlite => _context.Database.ProviderName?.Contains("Sqlite") ?? false;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public AnalyzerService(WalttiDbContext context, ILogger<AnalyzerService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public static int? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return h * 3600 + m * 60;
    }

    /// <summary>Converts "2026-04-02" to 20260402. Returns null if the format is invalid.</summary>
    public static int? TryParseDateToInt(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) return null;
        return d.Year * 10000 + d.Month * 100 + d.Day;
    }

    public static string FormatDelay(int? seconds)
    {
        if (seconds == null) return "N/A";
        if (seconds == 0) return "+0s";
        var sign = seconds >= 0 ? "+" : "-";
        var total = Math.Abs(seconds.Value);
        var minutes = total / 60;
        var secs = total % 60;
        return minutes > 0 ? $"{sign}{minutes}m {secs:D2}s" : $"{sign}{secs}s";
    }

    public async Task<Dictionary<string, object?>> GetSummaryAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null,
        CancellationToken ct = default)
    {
        var startParsed = TryParseDateToInt(startDate);
        var endParsed = TryParseDateToInt(endDate);
        if (!startParsed.HasValue || !endParsed.HasValue)
            return new Dictionary<string, object?> { ["total_departures"] = 0, ["message"] = "Invalid date format" };

        var fromWhereSql = $@"{BuildJoins(includeTrip: NeedsTrip(route, headsign), includeRoute: !string.IsNullOrEmpty(route))}
            WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = NewDateParameters(startParsed.Value, endParsed.Value);
        AppendStopFilter(ref fromWhereSql, parms, stopId, feedId);
        AppendFilters(ref fromWhereSql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref fromWhereSql, parms);

        var sql = $@"
            SELECT COUNT(*) AS total_departures,
                   COUNT(DISTINCT o.service_date) AS service_days,
                   SUM(CASE WHEN o.delay_source=2 THEN 1 ELSE 0 END) AS measured,
                   SUM(CASE WHEN o.delay_source=1 THEN 1 ELSE 0 END) AS propagated,
                   SUM(CASE WHEN o.delay_source=0 THEN 1 ELSE 0 END) AS static_only,
                   SUM(CASE WHEN o.realtime_state_id=2 THEN 1 ELSE 0 END) AS canceled,
                   SUM(CASE WHEN o.realtime_state_id=3 THEN 1 ELSE 0 END) AS skipped,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay IS NOT NULL
                                 AND ABS(o.departure_delay)>{OutlierThreshold} THEN 1 ELSE 0 END) AS suspect_gps,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>{OnTimeEarlyBoundary}
                                 AND o.departure_delay<{OnTimeLateBoundary} THEN 1 ELSE 0 END) AS on_time,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>={OnTimeLateBoundary}
                                 AND o.departure_delay<={SlightlyLateBoundary} THEN 1 ELSE 0 END) AS slightly_late,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>{SlightlyLateBoundary}
                                 AND o.departure_delay<={OutlierThreshold} THEN 1 ELSE 0 END) AS very_late,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>={VeryEarlyBoundary}
                                 AND o.departure_delay<={OnTimeEarlyBoundary} THEN 1 ELSE 0 END) AS slightly_early,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>=-{OutlierThreshold}
                                 AND o.departure_delay<{VeryEarlyBoundary} THEN 1 ELSE 0 END) AS very_early,
                   SUM(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND ABS(o.departure_delay)<={OutlierThreshold} THEN 1 ELSE 0 END) AS clean_count,
                   AVG(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay>0 AND o.departure_delay<={OutlierThreshold}
                            THEN CAST(o.departure_delay AS FLOAT) END) AS avg_late,
                   AVG(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND o.departure_delay<0 AND o.departure_delay>=-{OutlierThreshold}
                            THEN CAST(o.departure_delay AS FLOAT) END) AS avg_early,
                   MAX(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND ABS(o.departure_delay)<={OutlierThreshold} THEN o.departure_delay END) AS max_late,
                   MIN(CASE WHEN o.delay_source=2
                                 AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                 AND ABS(o.departure_delay)<={OutlierThreshold} THEN o.departure_delay END) AS max_early
            {fromWhereSql}";

        var rows = await QueryRawAsync("summary", sql, parms, r => new SummaryAggregate(
            Int(r, 0), Int(r, 1), Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5), Int(r, 6),
            Int(r, 7), Int(r, 8), Int(r, 9), Int(r, 10), Int(r, 11), Int(r, 12), Int(r, 13),
            NullableDouble(r, 14), NullableDouble(r, 15), NullableInt(r, 16), NullableInt(r, 17)), ct);

        var aggregate = rows.Single();
        if (aggregate.Total == 0)
            return new Dictionary<string, object?>
            {
                ["period"] = new { start = startDate, end = endDate },
                ["total_departures"] = 0,
                ["message"] = "No observations found"
            };

        parms.Add(("@median_offset", (aggregate.CleanCount - 1) / 2));
        parms.Add(("@median_take", aggregate.CleanCount % 2 == 0 ? 2 : 1));
        var medianPage = IsSqlite
            ? "LIMIT @median_take OFFSET @median_offset"
            : "OFFSET @median_offset ROWS FETCH NEXT @median_take ROWS ONLY";
        var medianSql = $@"
            SELECT AVG(CAST(departure_delay AS FLOAT))
            FROM (
                SELECT o.departure_delay
                {fromWhereSql}
                  AND o.delay_source=2
                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                  AND o.departure_delay IS NOT NULL
                  AND ABS(o.departure_delay)<={OutlierThreshold}
                ORDER BY o.departure_delay
                {medianPage}
            ) AS median_rows";
        var medianDelay = (await QueryRawAsync("summary median", medianSql, parms,
            r => NullableDouble(r, 0), ct)).SingleOrDefault();

        return new Dictionary<string, object?>
        {
            ["period"] = new { start = startDate, end = endDate },
            ["service_days"] = aggregate.ServiceDays,
            ["total_departures"] = aggregate.Total,
            ["measured"] = aggregate.Measured,
            ["measured_pct"] = Math.Round((double)aggregate.Measured / aggregate.Total * 100, 1),
            ["propagated"] = aggregate.Propagated,
            ["canceled"] = aggregate.Canceled,
            ["skipped"] = aggregate.Skipped,
            ["static_only"] = aggregate.StaticOnly,
            ["on_time"] = aggregate.OnTime,
            ["on_time_pct"] = aggregate.CleanCount > 0
                ? Math.Round((double)aggregate.OnTime / aggregate.CleanCount * 100, 1) : 0,
            ["slightly_late"] = aggregate.SlightlyLate,
            ["very_late"] = aggregate.VeryLate,
            ["slightly_early"] = aggregate.SlightlyEarly,
            ["very_early"] = aggregate.VeryEarly,
            ["suspect_gps"] = aggregate.SuspectGps,
            ["avg_late_seconds"] = Math.Round(aggregate.AvgLate ?? 0, 1),
            ["avg_early_seconds"] = Math.Round(aggregate.AvgEarly ?? 0, 1),
            ["median_delay_seconds"] = Math.Round(medianDelay ?? 0, 1),
            ["max_late_seconds"] = aggregate.MaxLate ?? 0,
            ["max_early_seconds"] = aggregate.MaxEarly ?? 0,
        };
    }

    public async Task<List<Dictionary<string, object?>>> GetRouteBreakdownAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null,
        CancellationToken ct = default)
    {
        var startParsed = TryParseDateToInt(startDate);
        var endParsed = TryParseDateToInt(endDate);
        if (!startParsed.HasValue || !endParsed.HasValue) return [];

        var sql = $@"SELECT r.short_name,
                    COUNT(*) AS departures,
                    SUM(CASE WHEN o.delay_source=2 THEN 1 ELSE 0 END) AS measured,
                    SUM(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND ABS(o.departure_delay)<={OutlierThreshold}
                                  AND o.departure_delay>{OnTimeEarlyBoundary}
                                  AND o.departure_delay<{OnTimeLateBoundary} THEN 1 ELSE 0 END) AS on_time,
                    SUM(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND ABS(o.departure_delay)<={OutlierThreshold} THEN 1 ELSE 0 END) AS clean_count,
                    AVG(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND o.departure_delay>0 AND o.departure_delay<={OutlierThreshold}
                             THEN CAST(o.departure_delay AS FLOAT) END) AS avg_late,
                    AVG(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND o.departure_delay<0 AND o.departure_delay>=-{OutlierThreshold}
                             THEN CAST(o.departure_delay AS FLOAT) END) AS avg_early,
                    MAX(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND ABS(o.departure_delay)<={OutlierThreshold} THEN o.departure_delay END) AS max_late,
                    MIN(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND ABS(o.departure_delay)<={OutlierThreshold} THEN o.departure_delay END) AS max_early,
                    SUM(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND o.departure_delay IS NOT NULL
                                  AND ABS(o.departure_delay)>{OutlierThreshold} THEN 1 ELSE 0 END) AS suspect_gps
                    {BuildJoins(includeTrip: true, includeRoute: true)}
                    WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = NewDateParameters(startParsed.Value, endParsed.Value);
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);
        sql += " GROUP BY r.short_name ORDER BY r.short_name";

        return await QueryRawAsync("route breakdown", sql, parms, r =>
        {
            var departures = Int(r, 1);
            var cleanCount = Int(r, 4);
            var onTime = Int(r, 3);
            return new Dictionary<string, object?>
            {
                ["route"] = r.IsDBNull(0) ? "" : r.GetString(0),
                ["departures"] = departures,
                ["measured"] = Int(r, 2),
                ["on_time_pct"] = cleanCount > 0 ? Math.Round((double)onTime / cleanCount * 100, 1) : 0,
                ["avg_late_seconds"] = Math.Round(NullableDouble(r, 5) ?? 0, 1),
                ["avg_early_seconds"] = Math.Round(NullableDouble(r, 6) ?? 0, 1),
                ["max_late_seconds"] = NullableInt(r, 7) ?? 0,
                ["max_early_seconds"] = NullableInt(r, 8) ?? 0,
                ["suspect_gps"] = Int(r, 9),
            };
        }, ct);
    }

    public async Task<List<Dictionary<string, object?>>> GetDelayByHourAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null,
        CancellationToken ct = default)
    {
        var startParsed = TryParseDateToInt(startDate);
        var endParsed = TryParseDateToInt(endDate);
        if (!startParsed.HasValue || !endParsed.HasValue) return [];

        var sql = $@"SELECT (o.scheduled_departure / 3600) AS hour,
                    COUNT(*) AS departures,
                    SUM(CASE WHEN o.delay_source=2 THEN 1 ELSE 0 END) AS measured,
                    AVG(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND ABS(o.departure_delay)<={OutlierThreshold}
                             THEN CAST(o.departure_delay AS FLOAT) END) AS avg_delay,
                    AVG(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND o.departure_delay>0 AND o.departure_delay<={OutlierThreshold}
                             THEN CAST(o.departure_delay AS FLOAT) END) AS avg_late,
                    AVG(CASE WHEN o.delay_source=2
                                  AND (o.realtime_state_id IS NULL OR o.realtime_state_id NOT IN (2, 3))
                                  AND o.departure_delay<0 AND o.departure_delay>=-{OutlierThreshold}
                             THEN CAST(o.departure_delay AS FLOAT) END) AS avg_early
                    {BuildJoins(includeTrip: NeedsTrip(route, headsign), includeRoute: !string.IsNullOrEmpty(route))}
                    WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = NewDateParameters(startParsed.Value, endParsed.Value);
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);
        sql += " GROUP BY (o.scheduled_departure / 3600) ORDER BY (o.scheduled_departure / 3600)";

        return await QueryRawAsync("delay by hour", sql, parms, r => new Dictionary<string, object?>
        {
            ["hour"] = Int(r, 0),
            ["departures"] = Int(r, 1),
            ["measured"] = Int(r, 2),
            ["avg_late_seconds"] = Math.Round(NullableDouble(r, 4) ?? 0, 1),
            ["avg_early_seconds"] = Math.Round(NullableDouble(r, 5) ?? 0, 1),
            ["avg_delay_seconds"] = Math.Round(NullableDouble(r, 3) ?? 0, 1),
        }, ct);
    }

    private static string BuildJoins(bool includeTrip, bool includeRoute)
    {
        var sql = "FROM observations o JOIN stops s ON o.stop_id=s.id";
        if (includeTrip || includeRoute) sql += " JOIN trips t ON o.trip_id=t.id";
        if (includeRoute) sql += " JOIN routes r ON t.route_id=r.id";
        return sql;
    }

    private static bool NeedsTrip(string? route, string? headsign) =>
        !string.IsNullOrEmpty(route) || !string.IsNullOrEmpty(headsign);

    private static List<(string Name, object? Value)> NewDateParameters(int start, int end) =>
        [("@start", start), ("@end", end)];

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

    private async Task<List<T>> QueryRawAsync<T>(string operation, string sql,
        List<(string Name, object? Value)> parms, Func<DbDataReader, T> mapper,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
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
            while (await reader.ReadAsync(ct)) result.Add(mapper(reader));
            _logger.LogInformation("Database report query {Operation} completed in {ElapsedMs} ms with {RowCount} result rows",
                operation, stopwatch.ElapsedMilliseconds, result.Count);
            return result;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    private static int Int(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static int? NullableInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));

    private static double? NullableDouble(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));

    private sealed record SummaryAggregate(
        int Total, int ServiceDays, int Measured, int Propagated, int StaticOnly,
        int Canceled, int Skipped, int SuspectGps, int OnTime, int SlightlyLate,
        int VeryLate, int SlightlyEarly, int VeryEarly, int CleanCount,
        double? AvgLate, double? AvgEarly, int? MaxLate, int? MaxEarly);
}
