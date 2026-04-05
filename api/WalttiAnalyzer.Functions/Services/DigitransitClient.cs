using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalttiAnalyzer.Functions.Services;

public class DigitransitClient
{
    private readonly ILogger<DigitransitClient> _logger;
    private readonly HttpClient _httpClient;

    private static readonly TimeZoneInfo HelsinkiTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");

    public DigitransitClient(ILogger<DigitransitClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public void Configure(string apiUrl, string apiKey)
    {
        _httpClient.BaseAddress = new Uri(apiUrl);
        _httpClient.DefaultRequestHeaders.Remove("digitransit-subscription-key");
        _httpClient.DefaultRequestHeaders.Add("digitransit-subscription-key", apiKey);
    }

    // -----------------------------------------------------------------------
    // GraphQL queries
    // -----------------------------------------------------------------------

    private const string QueryBulkDaily = @"
{{
  stops(ids: {0}) {{
    gtfsId
    name
    code
    lat
    lon
    stoptimesForServiceDate(date: ""{1}"") {{
      pattern {{
        route {{ shortName longName mode }}
        directionId
      }}
      stoptimes {{
        scheduledArrival
        scheduledDeparture
        realtimeArrival
        realtimeDeparture
        arrivalDelay
        departureDelay
        realtime
        realtimeState
        headsign
        trip {{ gtfsId }}
      }}
    }}
  }}
}}";

    private const string QueryBulkRealtime = @"
{{
  stops(ids: {0}) {{
    gtfsId
    name
    stoptimesWithoutPatterns(
      startTime: {1},
      timeRange: 7200,
      numberOfDepartures: 50
    ) {{
      serviceDay
      scheduledArrival
      realtimeArrival
      arrivalDelay
      scheduledDeparture
      realtimeDeparture
      departureDelay
      realtime
      realtimeState
      headsign
      trip {{
        gtfsId
        route {{ shortName longName }}
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

    public async Task<List<JsonElement>> FetchBulkDailyAsync(List<string> stopIds, DateOnly? serviceDate = null)
    {
        var date = serviceDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HelsinkiTz));
        var idsJson = JsonSerializer.Serialize(stopIds);
        var query = string.Format(QueryBulkDaily, idsJson, date.ToString("yyyy-MM-dd"));
        var data = await QueryAsync(query, retries: 2, timeoutSeconds: 120);
        return GetArrayProperty(data, "stops");
    }

    public async Task<List<JsonElement>> FetchBulkRealtimeAsync(List<string> stopIds)
    {
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var idsJson = JsonSerializer.Serialize(stopIds);
        var query = string.Format(QueryBulkRealtime, idsJson, nowUtc);
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
                {
                    _logger.LogError("GraphQL errors: {Errors}", errors.GetRawText());
                }
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

    private static List<JsonElement> GetArrayProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return new List<JsonElement>();
        if (!element.TryGetProperty(name, out var prop)) return new List<JsonElement>();
        if (prop.ValueKind != JsonValueKind.Array) return new List<JsonElement>();
        return prop.EnumerateArray().ToList();
    }
}
