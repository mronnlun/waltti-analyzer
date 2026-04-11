using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalttiAnalyzer.Core.Services;

public class DigitransitClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DigitransitClient> _logger;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public DigitransitClient(HttpClient httpClient, ILogger<DigitransitClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // GraphQL queries
    // -----------------------------------------------------------------------

    /// <summary>Sliding-window realtime query. Returns stoptimes within [startTime, startTime+timeRange].</summary>
    private const string QuerySlidingWindow = @"
{{
  stops(ids: {0}) {{
    gtfsId
    name
    stoptimesWithoutPatterns(
      startTime: {1},
      timeRange: {2},
      numberOfDepartures: 100
    ) {{
      serviceDay
      scheduledDeparture
      departureDelay
      realtime
      realtimeState
      headsign
      trip {{
        gtfsId
        route {{ gtfsId shortName longName mode }}
      }}
    }}
  }}
}}";

    private const string QueryFeedRoutes = @"
{
  routes {
    gtfsId
    shortName
    longName
    mode
    patterns {
      directionId
      stops {
        gtfsId
        name
        code
        lat
        lon
      }
    }
  }
}";

    // -----------------------------------------------------------------------
    // API methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetch stoptimes within a sliding window for the given stops.
    /// </summary>
    /// <param name="stopIds">GTFS stop IDs to query.</param>
    /// <param name="startTime">Unix timestamp for window start.</param>
    /// <param name="timeRange">Window size in seconds.</param>
    public async Task<List<JsonElement>> FetchSlidingWindowAsync(
        List<string> stopIds, long startTime, int timeRange)
    {
        var idsJson = JsonSerializer.Serialize(stopIds);
        var query = string.Format(QuerySlidingWindow, idsJson, startTime, timeRange);
        var data = await QueryAsync(query, retries: 2, timeoutSeconds: 120);
        return GetArrayProperty(data, "stops");
    }

    public async Task<(List<Dictionary<string, object?>> Stops, List<Dictionary<string, object?>> Routes)>
        DiscoverFeedStopsAsync(string feedId)
    {
        var data = await QueryAsync(QueryFeedRoutes);
        var allRoutes = GetArrayProperty(data, "routes");

        var stops = new Dictionary<string, Dictionary<string, object?>>();
        var routes = new List<Dictionary<string, object?>>();

        foreach (var r in allRoutes)
        {
            var routeGtfsId = r.GetProperty("gtfsId").GetString()!;
            if (!routeGtfsId.StartsWith($"{feedId}:")) continue;

            var routeStopIds = new HashSet<string>();
            foreach (var p in GetArrayProperty(r, "patterns"))
            {
                foreach (var s in GetArrayProperty(p, "stops"))
                {
                    var sid = s.GetProperty("gtfsId").GetString()!;
                    if (!sid.StartsWith($"{feedId}:")) continue;
                    if (!stops.ContainsKey(sid))
                    {
                        stops[sid] = new Dictionary<string, object?>
                        {
                            ["gtfs_id"] = sid,
                            ["name"] = s.GetProperty("name").GetString()!,
                            ["code"] = s.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
                            ["lat"] = s.TryGetProperty("lat", out var la) && la.ValueKind != JsonValueKind.Null ? la.GetDouble() : (double?)null,
                            ["lon"] = s.TryGetProperty("lon", out var lo) && lo.ValueKind != JsonValueKind.Null ? lo.GetDouble() : (double?)null,
                        };
                    }
                    routeStopIds.Add(sid);
                }
            }

            routes.Add(new Dictionary<string, object?>
            {
                ["gtfs_id"] = routeGtfsId,
                ["short_name"] = r.TryGetProperty("shortName", out var sn) ? sn.GetString() : null,
                ["long_name"] = r.TryGetProperty("longName", out var ln) ? ln.GetString() : null,
                ["mode"] = r.TryGetProperty("mode", out var m) ? m.GetString() : null,
                ["stop_ids"] = routeStopIds.ToList(),
            });
        }

        return (stops.Values.ToList(), routes);
    }

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------

    private async Task<JsonElement> QueryAsync(string graphql, int retries = 1, int timeoutSeconds = 30)
    {
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { query = graphql });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var resp = await _httpClient.PostAsync("", content, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("API {Status} (attempt {Attempt}): {Text}",
                        (int)resp.StatusCode, attempt + 1, text[..Math.Min(text.Length, 500)]);
                }
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("errors", out var errors))
                    _logger.LogError("GraphQL errors: {Errors}", errors.GetRawText());
                if (doc.RootElement.TryGetProperty("data", out var data))
                    return data;
                return default;
            }
            catch (Exception ex) when (attempt < retries)
            {
                _logger.LogWarning("API request failed (attempt {Attempt}): {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
        return default;
    }

    internal static List<JsonElement> GetArrayProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return new List<JsonElement>();
        if (!element.TryGetProperty(name, out var prop)) return new List<JsonElement>();
        if (prop.ValueKind != JsonValueKind.Array) return new List<JsonElement>();
        return prop.EnumerateArray().ToList();
    }
}
