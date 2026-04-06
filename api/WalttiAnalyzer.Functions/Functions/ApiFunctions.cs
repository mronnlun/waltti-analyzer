using System.IO;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Functions.Models;
using WalttiAnalyzer.Functions.Services;

namespace WalttiAnalyzer.Functions.Functions;

public class ApiFunctions
{
    private readonly ILogger<ApiFunctions> _logger;
    private readonly DatabaseService _db;
    private readonly AnalyzerService _analyzer;
    private readonly CollectorService _collector;
    private readonly WalttiSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ApiFunctions(ILogger<ApiFunctions> logger,
        DatabaseService db, AnalyzerService analyzer, CollectorService collector,
        IOptions<WalttiSettings> settings)
    {
        _logger = logger;
        _db = db;
        _analyzer = analyzer;
        _collector = collector;
        _settings = settings.Value;
    }

    private string DbPath => _settings.DatabasePath;
    private string FeedId => _settings.FeedId;
    private string ApiUrl => _settings.DigitransitApiUrl;
    private string ApiKey => _settings.DigitransitApiKey;

    private void EnsureDb() => _db.InitDb(DbPath);

    // -----------------------------------------------------------------------
    // GET /api/status
    // -----------------------------------------------------------------------

    [Function("Status")]
    public IActionResult Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequest req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var daily = _db.GetLatestCollection(conn, FeedId, "daily");
        var realtime = _db.GetLatestCollection(conn, FeedId, "realtime");
        return JsonResponse(new
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
    public IActionResult Stops(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stops")] HttpRequest req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var stops = _db.GetAllStops(conn, FeedId);
        return JsonResponse(stops);
    }

    // -----------------------------------------------------------------------
    // GET /api/routes
    // -----------------------------------------------------------------------

    [Function("Routes")]
    public IActionResult Routes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes")] HttpRequest req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var routes = _db.GetAllRoutes(conn, FeedId);
        return JsonResponse(routes);
    }

    // -----------------------------------------------------------------------
    // GET /api/routes-for-stop?stop_id=X
    // -----------------------------------------------------------------------

    [Function("RoutesForStop")]
    public IActionResult RoutesForStop(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes-for-stop")] HttpRequest req)
    {
        var stopId = GetQuery(req, "stop_id");
        if (string.IsNullOrEmpty(stopId))
            return JsonResponse(new { error = "stop_id parameter required" }, 400);

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var routes = _db.GetRoutesForStop(conn, stopId);
        return JsonResponse(routes);
    }

    // -----------------------------------------------------------------------
    // GET /api/observations?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("Observations")]
    public IActionResult Observations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "observations")] HttpRequest req)
    {
        var stopId = GetQuery(req, "stop_id");
        var dateStr = GetQuery(req, "date");
        var startDate = GetQuery(req, "from") ?? dateStr ?? "";
        var endDate = GetQuery(req, "to") ?? dateStr ?? "";

        if (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate))
            return JsonResponse(new { error = "date or from/to parameters required" }, 400);
        if (string.IsNullOrEmpty(stopId))
            return JsonResponse(new { error = "stop_id parameter required" }, 400);

        var route = GetQuery(req, "route");
        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var rows = _db.GetObservations(conn, stopId, startDate, endDate, route, timeFrom, timeTo);
        return JsonResponse(rows);
    }

    // -----------------------------------------------------------------------
    // GET /api/latest-observations
    // -----------------------------------------------------------------------

    [Function("LatestObservations")]
    public IActionResult LatestObservations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "latest-observations")] HttpRequest req)
    {
        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var rows = _db.GetLatestObservations(conn, 100, FeedId);
        return JsonResponse(rows);
    }

    // -----------------------------------------------------------------------
    // GET /api/summary?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("Summary")]
    public IActionResult Summary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "summary")] HttpRequest req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetSummary(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // GET /api/route-breakdown?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("RouteBreakdown")]
    public IActionResult RouteBreakdown(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "route-breakdown")] HttpRequest req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetRouteBreakdown(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // GET /api/delay-by-hour?stop_id=X&from=Y&to=Z
    // -----------------------------------------------------------------------

    [Function("DelayByHour")]
    public IActionResult DelayByHour(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delay-by-hour")] HttpRequest req)
    {
        var stopId = GetQuery(req, "stop_id");
        var fromDate = GetQuery(req, "from");
        var toDate = GetQuery(req, "to");
        var route = GetQuery(req, "route");

        if (string.IsNullOrEmpty(fromDate) || string.IsNullOrEmpty(toDate) || string.IsNullOrEmpty(stopId))
            return JsonResponse(new { error = "stop_id, from, and to parameters required" }, 400);

        var timeFrom = AnalyzerService.ParseTime(GetQuery(req, "time_from"));
        var timeTo = AnalyzerService.ParseTime(GetQuery(req, "time_to"));

        EnsureDb();
        using var conn = _db.Connect(DbPath);
        var result = _analyzer.GetDelayByHour(conn, stopId, fromDate, toDate, route, timeFrom, timeTo);
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/collect/daily
    // -----------------------------------------------------------------------

    [Function("CollectDaily")]
    public async Task<IActionResult> CollectDaily(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "collect/daily")] HttpRequest req)
    {
        EnsureDb();
        string? dateStr = null, stopId = null;
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
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
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/collect/realtime
    // -----------------------------------------------------------------------

    [Function("CollectRealtime")]
    public async Task<IActionResult> CollectRealtime(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "collect/realtime")] HttpRequest req)
    {
        EnsureDb();
        string? stopId = null;
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            if (!string.IsNullOrEmpty(body))
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("stop_id", out var s)) stopId = s.GetString();
            }
        }
        catch { /* ignore parse errors */ }

        var result = await _collector.PollRealtimeOnceAsync(DbPath, ApiUrl, ApiKey,
            stopId: stopId, feedId: FeedId);
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // POST /api/discover
    // -----------------------------------------------------------------------

    [Function("Discover")]
    public async Task<IActionResult> Discover(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discover")] HttpRequest req)
    {
        EnsureDb();
        var result = await _collector.DiscoverStopsAsync(DbPath, ApiUrl, ApiKey, FeedId);
        return JsonResponse(result);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string? GetQuery(HttpRequest req, string name)
    {
        var val = req.Query[name].FirstOrDefault();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static IActionResult JsonResponse(object body, int statusCode = 200)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return new ContentResult
        {
            Content = json,
            ContentType = "application/json",
            StatusCode = statusCode
        };
    }
}
