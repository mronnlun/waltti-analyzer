using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Core.Data;

namespace WalttiAnalyzer.Core.Services;

public class AnalyzerService
{
    private readonly WalttiDbContext _context;
    private readonly ILogger<AnalyzerService> _logger;

    public const int OutlierThreshold = 1800; // 30 minutes

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

    /// <summary>Converts "2026-04-02" to 20260402.</summary>
    public static int ParseDateToInt(string date) => int.Parse(date.Replace("-", ""));

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
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null)
    {
        int start = ParseDateToInt(startDate), end = ParseDateToInt(endDate);

        var sql = @"SELECT o.departure_delay, o.delay_source, o.service_date, rs.name AS realtime_state
                    FROM observations o
                    JOIN trips t ON o.trip_id=t.id
                    JOIN stops s ON o.stop_id=s.id
                    JOIN routes r ON t.route_id=r.id
                    LEFT JOIN realtime_states rs ON o.realtime_state_id=rs.id
                    WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);

        var rows = await QueryRawAsync(sql, parms, r => (
            delay: r.IsDBNull(0) ? (int?)null : r.GetInt32(0),
            delaySource: r.GetInt32(1),
            serviceDate: r.GetInt32(2),
            state: r.IsDBNull(3) ? null : r.GetString(3)
        ));

        if (rows.Count == 0)
            return new Dictionary<string, object?>
            {
                ["period"] = new { start = startDate, end = endDate },
                ["total_departures"] = 0,
                ["message"] = "No observations found"
            };

        int total = rows.Count;
        var measured = rows.Where(r => r.delaySource == 2).ToList();
        int propagated = rows.Count(r => r.delaySource == 1);
        int canceled = rows.Count(r => r.state == "CANCELED");
        int skipped = rows.Count(r => r.state == "SKIPPED");
        int staticOnly = rows.Count(r => r.delaySource == 0);

        // Use only MEASURED data for delay statistics
        var allDelays = measured
            .Where(r => r.state != "CANCELED" && r.state != "SKIPPED")
            .Where(r => r.delay.HasValue)
            .Select(r => r.delay!.Value).ToList();
        var outliers = allDelays.Where(d => Math.Abs(d) > OutlierThreshold).ToList();
        var delays = allDelays.Where(d => Math.Abs(d) <= OutlierThreshold).ToList();

        var onTime = delays.Count(d => d >= 0 && d <= 180);
        var slightlyLate = delays.Count(d => d > 180 && d <= 600);
        var veryLate = delays.Count(d => d > 600);
        var slightlyEarly = delays.Count(d => d >= -60 && d < 0);
        var veryEarly = delays.Count(d => d < -60);

        var lateDelays = delays.Where(d => d > 0).ToList();
        var earlyDelays = delays.Where(d => d < 0).ToList();
        var serviceDates = rows.Select(r => r.serviceDate).Distinct().OrderBy(d => d).ToList();

        return new Dictionary<string, object?>
        {
            ["period"] = new { start = startDate, end = endDate },
            ["service_days"] = serviceDates.Count,
            ["total_departures"] = total,
            ["measured"] = measured.Count,
            ["measured_pct"] = total > 0 ? Math.Round((double)measured.Count / total * 100, 1) : 0,
            ["propagated"] = propagated,
            ["canceled"] = canceled,
            ["skipped"] = skipped,
            ["static_only"] = staticOnly,
            ["on_time"] = onTime,
            ["on_time_pct"] = delays.Count > 0 ? Math.Round((double)onTime / delays.Count * 100, 1) : 0,
            ["slightly_late"] = slightlyLate,
            ["very_late"] = veryLate,
            ["slightly_early"] = slightlyEarly,
            ["very_early"] = veryEarly,
            ["suspect_gps"] = outliers.Count,
            ["avg_late_seconds"] = lateDelays.Count > 0 ? Math.Round(lateDelays.Average(), 1) : 0,
            ["avg_early_seconds"] = earlyDelays.Count > 0 ? Math.Round(earlyDelays.Average(), 1) : 0,
            ["median_delay_seconds"] = delays.Count > 0 ? Math.Round(Median(delays), 1) : 0,
            ["max_late_seconds"] = delays.Count > 0 ? delays.Max() : 0,
            ["max_early_seconds"] = delays.Count > 0 ? delays.Min() : 0,
        };
    }

    public async Task<List<Dictionary<string, object?>>> GetRouteBreakdownAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null)
    {
        int start = ParseDateToInt(startDate), end = ParseDateToInt(endDate);

        var sql = @"SELECT r.short_name, o.departure_delay, o.delay_source, rs.name AS realtime_state
                    FROM observations o
                    JOIN trips t ON o.trip_id=t.id
                    JOIN stops s ON o.stop_id=s.id
                    JOIN routes r ON t.route_id=r.id
                    LEFT JOIN realtime_states rs ON o.realtime_state_id=rs.id
                    WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);

        var raw = await QueryRawAsync(sql, parms, r => (
            routeName: r.IsDBNull(0) ? "" : r.GetString(0),
            delay: r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
            delaySource: r.GetInt32(2),
            state: r.IsDBNull(3) ? null : r.GetString(3)
        ));

        var byRoute = raw.GroupBy(r => r.routeName);
        var result = new List<Dictionary<string, object?>>();

        foreach (var grp in byRoute.OrderBy(g => g.Key))
        {
            var rr = grp.ToList();
            int totalCount = rr.Count;
            var measuredRows = rr.Where(r => r.delaySource == 2).ToList();
            var allDelays = measuredRows
                .Where(r => r.state != "CANCELED" && r.state != "SKIPPED")
                .Where(r => r.delay.HasValue).Select(r => r.delay!.Value).ToList();
            var clean = allDelays.Where(d => Math.Abs(d) <= OutlierThreshold).ToList();
            int suspect = allDelays.Count - clean.Count;
            var late = clean.Where(d => d > 0).ToList();
            var early = clean.Where(d => d < 0).ToList();
            int onTime = clean.Count(d => d >= 0 && d <= 180);
            double onTimePct = clean.Count > 0 ? Math.Round((double)onTime / clean.Count * 100, 1) : 0;

            result.Add(new Dictionary<string, object?>
            {
                ["route"] = grp.Key,
                ["departures"] = totalCount,
                ["measured"] = measuredRows.Count,
                ["on_time_pct"] = onTimePct,
                ["avg_late_seconds"] = late.Count > 0 ? Math.Round(late.Average(), 1) : 0,
                ["avg_early_seconds"] = early.Count > 0 ? Math.Round(early.Average(), 1) : 0,
                ["max_late_seconds"] = clean.Count > 0 ? clean.Max() : 0,
                ["max_early_seconds"] = clean.Count > 0 ? clean.Min() : 0,
                ["suspect_gps"] = suspect,
            });
        }
        return result;
    }

    public async Task<List<Dictionary<string, object?>>> GetDelayByHourAsync(string? stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null, string? feedId = null, string? headsign = null)
    {
        int start = ParseDateToInt(startDate), end = ParseDateToInt(endDate);

        var sql = @"SELECT (o.scheduled_departure / 3600) AS hour, o.departure_delay, o.delay_source, rs.name AS realtime_state
                    FROM observations o
                    JOIN trips t ON o.trip_id=t.id
                    JOIN stops s ON o.stop_id=s.id
                    JOIN routes r ON t.route_id=r.id
                    LEFT JOIN realtime_states rs ON o.realtime_state_id=rs.id
                    WHERE o.service_date>=@start AND o.service_date<=@end";
        var parms = new List<(string, object?)> { ("@start", start), ("@end", end) };
        AppendStopFilter(ref sql, parms, stopId, feedId);
        AppendFilters(ref sql, parms, route, timeFrom, timeTo, headsign);
        AppendPastOnlyFilter(ref sql, parms);

        var raw = await QueryRawAsync(sql, parms, r => (
            hour: r.GetInt32(0),
            delay: r.IsDBNull(1) ? (int?)null : r.GetInt32(1),
            delaySource: r.GetInt32(2),
            state: r.IsDBNull(3) ? null : r.GetString(3)
        ));

        var result = new List<Dictionary<string, object?>>();
        foreach (var grp in raw.GroupBy(r => r.hour).OrderBy(g => g.Key))
        {
            var rr = grp.ToList();
            var measuredRows = rr.Where(r => r.delaySource == 2).ToList();
            var allDelays = measuredRows
                .Where(r => r.state != "CANCELED" && r.state != "SKIPPED")
                .Where(r => r.delay.HasValue).Select(r => r.delay!.Value).ToList();
            var clean = allDelays.Where(d => Math.Abs(d) <= OutlierThreshold).ToList();
            var late = clean.Where(d => d > 0).ToList();
            var early = clean.Where(d => d < 0).ToList();

            result.Add(new Dictionary<string, object?>
            {
                ["hour"] = grp.Key,
                ["departures"] = rr.Count,
                ["measured"] = measuredRows.Count,
                ["avg_late_seconds"] = late.Count > 0 ? Math.Round(late.Average(), 1) : 0,
                ["avg_early_seconds"] = early.Count > 0 ? Math.Round(early.Average(), 1) : 0,
                ["avg_delay_seconds"] = clean.Count > 0 ? Math.Round(clean.Average(), 1) : 0,
            });
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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

    private async Task<List<T>> QueryRawAsync<T>(string sql,
        List<(string Name, object? Value)> parms, Func<DbDataReader, T> mapper)
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
            var result = new List<T>();
            while (await reader.ReadAsync())
                result.Add(mapper(reader));
            return result;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    private static double Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
