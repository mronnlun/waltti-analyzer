using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using WalttiAnalyzer.Functions.Services;

namespace WalttiAnalyzer.Functions.Functions;

public class ApiFunctions
{
    private readonly ILogger<ApiFunctions> _logger;
    private readonly DatabaseService _db;
    private readonly AnalyzerService _analyzer;
    private readonly CollectorService _collector;

    public ApiFunctions(ILogger<ApiFunctions> logger,
        DatabaseService db, AnalyzerService analyzer, CollectorService collector)
    {
        _logger = logger;
        _db = db;
        _analyzer = analyzer;
        _collector = collector;
    }

    private string DbPath => Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "data/waltti.db";
    private string FeedId => Environment.GetEnvironmentVariable("FEED_ID") ?? "Vaasa";
    private string ApiUrl => Environment.GetEnvironmentVariable("DIGITRANSIT_API_URL")
        ?? "https://api.digitransit.fi/routing/v2/waltti/gtfs/v1";
    private string ApiKey => Environment.GetEnvironmentVariable("DIGITRANSIT_API_KEY") ?? "";

    private void EnsureDb() => _db.InitDb(DbPath);

    // -----------------------------------------------------------------------
    // GET /api/status
    // -----------------------------------------------------------------------

    [Function("Status")]
    public HttpResponseData Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequestData req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var daily = _db.GetLatestCollection(conn, FeedId, "daily");
        var realtime = _db.GetLatestCollection(conn, FeedId, "realtime");
        return JsonResponse(req, new
        {
            feed_id = FeedId,
            last_daily = daily,
            last_realtime = realtime
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/stops
    // -----------------------------------------------------------------------

    [Function("Stops")]
    public HttpResponseData Stops(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stops")] HttpRequestData req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var stops = _db.GetAllStops(conn, FeedId);
        return JsonResponse(req, stops);
    }

    // -----------------------------------------------------------------------
    // GET /api/routes
    // -----------------------------------------------------------------------

    [Function("Routes")]
    public HttpResponseData Routes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes")] HttpRequestData req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var routes = _db.GetAllRoutes(conn, FeedId);
        return JsonResponse(req, routes);
    }

    // -----------------------------------------------------------------------
    // GET /api/routes-for-stop?stop_id=X
    // -----------------------------------------------------------------------

    [Function("RoutesForStop")]
    public HttpResponseData RoutesForStop(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes-for-stop")] HttpRequestData req)
    {
        var stopId = GetQuery(req, "stop_id");
        if (string.IsNullOrEmpty(stopId))
            return JsonResponse(req, new { error = "stop_id parameter required" }, 400);

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var routes = _db.GetRoutesForStop(conn, stopId);
        return JsonResponse(req, routes);
    }

    // -----------------------------------------------------------------------
    // GET /api/observations?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("Observations")]
    public HttpResponseData Observations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "observations")] HttpRequestData req)
    {
        var stopId = GetQuery(req, "stop_id");
        var dateStr = GetQuery(req, "date");
        var startDate = GetQuery(req, "from") ?? dateStr ?? "";
        var endDate = GetQuery(req, "to") ?? dateStr ?? "";

        if (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate))
            return JsonResponse(req, new { error = "date or from/to parameters required" }, 400);
        if (string.IsNullOrEmpty(stopId))
            return JsonResponse(req, new { error = "stop_id parameter required" }, 400);

        var route = GetQuery(req, "route");
        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var rows = _db.GetObservations(conn, stopId, startDate, endDate, route, timeFrom, timeTo);
        return JsonResponse(req, rows);
    }

    // -----------------------------------------------------------------------
    // GET /api/latest-observations
    // -----------------------------------------------------------------------

    [Function("LatestObservations")]
    public HttpResponseData LatestObservations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "latest-observations")] HttpRequestData req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var rows = _db.GetLatestObservations(conn, 100, FeedId);
        return JsonResponse(req, rows);
    }

    // -----------------------------------------------------------------------
    // GET /api/summary?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("Summary")]
    public HttpResponseData Summary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "summary")] HttpRequestData req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(req, new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetSummary(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // GET /api/route-breakdown?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("RouteBreakdown")]
    public HttpResponseData RouteBreakdown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "route-breakdown")] HttpRequestData req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(req, new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetRouteBreakdown(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // GET /api/delay-by-hour?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("DelayByHour")]
    public HttpResponseData DelayByHour(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delay-by-hour")] HttpRequestData req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(req, new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetDelayByHour(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // POST /api/collect/daily
    // -----------------------------------------------------------------------

    [Function("CollectDaily")]
    public async Task<HttpResponseData> CollectDaily(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "collect/daily")] HttpRequestData req)
    {
        EnsureDb();
        string? dateStr = null, stopId = null;
        try
        {
            var body = await req.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(body))
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("date", out var d)) dateStr = d.GetString();
                if (json.RootElement.TryGetProperty("stop_id", out var s)) stopId = s.GetString();
            }
        }
        catch { /* ignore parse errors */ }

        var result = await _collector.CollectDailyAsync(DbPath, ApiUrl, ApiKey,
            stopId: stopId, serviceDate: dateStr, feedId: FeedId);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // POST /api/collect/realtime
    // -----------------------------------------------------------------------

    [Function("CollectRealtime")]
    public async Task<HttpResponseData> CollectRealtime(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "collect/realtime")] HttpRequestData req)
    {
        EnsureDb();
        string? stopId = null;
        try
        {
            var body = await req.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(body))
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("stop_id", out var s)) stopId = s.GetString();
            }
        }
        catch { /* ignore parse errors */ }

        var result = await _collector.PollRealtimeOnceAsync(DbPath, ApiUrl, ApiKey,
            stopId: stopId, feedId: FeedId);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // POST /api/discover
    // -----------------------------------------------------------------------

    [Function("Discover")]
    public async Task<HttpResponseData> Discover(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discover")] HttpRequestData req)
    {
        EnsureDb();
        var result = await _collector.DiscoverStopsAsync(DbPath, ApiUrl, ApiKey, FeedId);
        return JsonResponse(req, result);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string? GetQuery(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var val = query[name];
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static HttpResponseData JsonResponse(HttpRequestData req, object body, int statusCode = 200)
    {
        var response = req.CreateResponse((System.Net.HttpStatusCode)statusCode);
        response.Headers.Add("Content-Type", "application/json");
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        response.WriteString(json);
        return response;
    }
}
