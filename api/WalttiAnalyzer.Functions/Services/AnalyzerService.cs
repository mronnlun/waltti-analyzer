using Microsoft.Data.Sqlite;

namespace WalttiAnalyzer.Functions.Services;

public class AnalyzerService
{
    /// <summary>Delays beyond this threshold (seconds) are flagged as suspect GPS data.</summary>
    public const int OutlierThreshold = 1800; // 30 minutes

    public static int? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return h * 3600 + m * 60;
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

    public Dictionary<string, object?> GetSummary(SqliteConnection db, string stopId,
        string startDate, string endDate, string? route = null,
        int? timeFrom = null, int? timeTo = null)
    {
        var sql = @"SELECT o.departure_delay, o.realtime, o.service_date,
                           rs.name AS realtime_state
                    FROM observations o
                    JOIN trips t ON o.trip_id = t.id
                    JOIN stops s ON o.stop_id = s.id
                    LEFT JOIN realtime_states rs ON o.realtime_state_id = rs.id
                    WHERE s.gtfs_id = $sid AND o.service_date >= $start AND o.service_date <= $end";
        using var cmd = db.CreateCommand();
        var parms = new List<(string, object)>
        {
            ("$sid", stopId), ("$start", startDate), ("$end", endDate)
        };
        AppendFilters(ref sql, parms, route, timeFrom, timeTo);
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);

        var rows = new List<(int? delay, int realtime, string serviceDate, string? state)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        if (rows.Count == 0)
            return new Dictionary<string, object?>
            {
                ["period"] = new { start = startDate, end = endDate },
                ["total_departures"] = 0,
                ["message"] = "No observations found"
            };

        int total = rows.Count;
        var withRealtime = rows.Where(r => r.realtime != 0).ToList();
        int canceled = rows.Count(r => r.state == "CANCELED");
        int staticOnly = total - withRealtime.Count;

        var allDelays = withRealtime
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
            ["with_realtime"] = withRealtime.Count,
            ["with_realtime_pct"] = total > 0 ? Math.Round((double)withRealtime.Count / total * 100, 1) : 0,
            ["canceled"] = canceled,
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

    public List<Dictionary<string, object?>> GetRouteBreakdown(SqliteConnection db,
        string stopId, string startDate, string endDate,
        string? route = null, int? timeFrom = null, int? timeTo = null)
    {
        var sql = @"SELECT t.route_short_name, o.departure_delay, o.realtime
                    FROM observations o
                    JOIN trips t ON o.trip_id = t.id
                    JOIN stops s ON o.stop_id = s.id
                    WHERE s.gtfs_id = $sid AND o.service_date >= $start AND o.service_date <= $end";
        using var cmd = db.CreateCommand();
        var parms = new List<(string, object)>
        {
            ("$sid", stopId), ("$start", startDate), ("$end", endDate)
        };
        AppendFilters(ref sql, parms, route, timeFrom, timeTo);
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);

        var byRoute = new Dictionary<string, List<(int? delay, int realtime)>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var routeName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var delay = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            var rt = reader.GetInt32(2);
            if (!byRoute.ContainsKey(routeName))
                byRoute[routeName] = new List<(int?, int)>();
            byRoute[routeName].Add((delay, rt));
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var routeName in byRoute.Keys.OrderBy(k => k))
        {
            var rows = byRoute[routeName];
            int totalCount = rows.Count;
            var rtRows = rows.Where(r => r.realtime != 0).ToList();
            int rtCount = rtRows.Count;
            var allDelays = rtRows.Where(r => r.delay.HasValue).Select(r => r.delay!.Value).ToList();
            var clean = allDelays.Where(d => Math.Abs(d) <= OutlierThreshold).ToList();
            int suspect = allDelays.Count - clean.Count;
            var late = clean.Where(d => d > 0).ToList();
            var early = clean.Where(d => d < 0).ToList();
            int onTime = clean.Count(d => d >= 0 && d <= 180);
            double onTimePct = clean.Count > 0 ? Math.Round((double)onTime / clean.Count * 100, 1) : 0;

            result.Add(new Dictionary<string, object?>
            {
                ["route"] = routeName,
                ["departures"] = totalCount,
                ["with_realtime"] = rtCount,
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

    public List<Dictionary<string, object?>> GetDelayByHour(SqliteConnection db,
        string stopId, string startDate, string endDate,
        string? route = null, int? timeFrom = null, int? timeTo = null)
    {
        var sql = @"SELECT (o.scheduled_departure / 3600) as hour,
                           o.departure_delay, o.realtime
                    FROM observations o
                    JOIN trips t ON o.trip_id = t.id
                    JOIN stops s ON o.stop_id = s.id
                    WHERE s.gtfs_id = $sid AND o.service_date >= $start AND o.service_date <= $end";
        using var cmd = db.CreateCommand();
        var parms = new List<(string, object)>
        {
            ("$sid", stopId), ("$start", startDate), ("$end", endDate)
        };
        AppendFilters(ref sql, parms, route, timeFrom, timeTo);
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);

        var byHour = new Dictionary<int, List<(int? delay, int realtime)>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int hour = reader.GetInt32(0);
            var delay = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            var rt = reader.GetInt32(2);
            if (!byHour.ContainsKey(hour))
                byHour[hour] = new List<(int?, int)>();
            byHour[hour].Add((delay, rt));
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var hour in byHour.Keys.OrderBy(h => h))
        {
            var rows = byHour[hour];
            int totalCount = rows.Count;
            var rtRows = rows.Where(r => r.realtime != 0).ToList();
            var allDelays = rtRows.Where(r => r.delay.HasValue).Select(r => r.delay!.Value).ToList();
            var clean = allDelays.Where(d => Math.Abs(d) <= OutlierThreshold).ToList();
            var late = clean.Where(d => d > 0).ToList();
            var early = clean.Where(d => d < 0).ToList();

            result.Add(new Dictionary<string, object?>
            {
                ["hour"] = hour,
                ["departures"] = totalCount,
                ["with_realtime"] = rtRows.Count,
                ["avg_late_seconds"] = late.Count > 0 ? Math.Round(late.Average(), 1) : 0,
                ["avg_early_seconds"] = early.Count > 0 ? Math.Round(early.Average(), 1) : 0,
            });
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void AppendFilters(ref string sql, List<(string, object)> parms,
        string? route, int? timeFrom, int? timeTo)
    {
        if (!string.IsNullOrEmpty(route)) { sql += " AND t.route_short_name = $route"; parms.Add(("$route", route)); }
        if (timeFrom.HasValue) { sql += " AND o.scheduled_departure >= $tf"; parms.Add(("$tf", timeFrom.Value)); }
        if (timeTo.HasValue) { sql += " AND o.scheduled_departure <= $tt"; parms.Add(("$tt", timeTo.Value)); }
    }

    private static double Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        if (sorted.Count % 2 == 0)
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        return sorted[mid];
    }
}
