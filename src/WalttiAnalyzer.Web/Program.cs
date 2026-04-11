using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Core.Data;
using WalttiAnalyzer.Core.Models;
using WalttiAnalyzer.Core.Services;
using WalttiAnalyzer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------

builder.Services.Configure<WalttiSettings>(builder.Configuration.GetSection("Waltti"));
var walttiSettings = builder.Configuration.GetSection("Waltti").Get<WalttiSettings>() ?? new WalttiSettings();

// -----------------------------------------------------------------------
// Database (SQL Server if DATABASE connection string set, else SQLite)
// -----------------------------------------------------------------------

var dbConnectionString = builder.Configuration.GetConnectionString("DATABASE");
if (!string.IsNullOrEmpty(dbConnectionString))
{
    builder.Services.AddDbContext<WalttiDbContext>(options =>
        options.UseSqlServer(dbConnectionString));
}
else
{
    var dbDir = Path.GetDirectoryName(Path.GetFullPath(walttiSettings.DatabasePath));
    if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
    builder.Services.AddDbContext<WalttiDbContext>(options =>
        options.UseSqlite($"Data Source={walttiSettings.DatabasePath}"));
}

// -----------------------------------------------------------------------
// Application services
// -----------------------------------------------------------------------

builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<AnalyzerService>();
builder.Services.AddScoped<CollectorService>();

builder.Services.AddHttpClient<DigitransitClient>((sp, client) =>
{
    var s = sp.GetRequiredService<IOptions<WalttiSettings>>().Value;
    client.BaseAddress = new Uri(s.DigitransitApiUrl);
    if (!string.IsNullOrEmpty(s.DigitransitApiKey))
        client.DefaultRequestHeaders.Add("digitransit-subscription-key", s.DigitransitApiKey);
});

builder.Services.AddHostedService<DataSyncBackgroundService>();

// -----------------------------------------------------------------------
// OpenTelemetry with Azure Monitor / Application Insights
// -----------------------------------------------------------------------

builder.Services.AddOpenTelemetry()
    .UseAzureMonitor()
    .WithTracing(tracing => tracing.AddSource(DataSyncBackgroundService.ActivitySource.Name));

// -----------------------------------------------------------------------
// Health checks
// -----------------------------------------------------------------------

builder.Services.AddHealthChecks()
    .AddDbContextCheck<WalttiDbContext>();

// -----------------------------------------------------------------------
// HTTP / JSON / CORS
// -----------------------------------------------------------------------

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// -----------------------------------------------------------------------
// Build app
// -----------------------------------------------------------------------

var app = builder.Build();

// Initialize DB schema on startup
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DatabaseService>().EnsureCreated();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var ext = Path.GetExtension(ctx.File.Name);
        if (ext is ".js" or ".css")
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapHealthChecks("/health");

// -----------------------------------------------------------------------
// API routes
// -----------------------------------------------------------------------

app.MapGet("/api/status", async (DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    var s = opts.Value;
    var discover = await db.GetLatestCollectionAsync(s.FeedId, "discover");
    var realtime = await db.GetLatestCollectionAsync(s.FeedId, "realtime");
    return Results.Ok(new { feed_id = s.FeedId, default_stop_id = s.DefaultStopId, last_discover = discover, last_realtime = realtime });
});

app.MapGet("/api/stops", async (DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    var stops = await db.GetAllStopsAsync(opts.Value.FeedId);
    return Results.Ok(stops);
});

app.MapGet("/api/routes", async (DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    var routes = await db.GetAllRoutesAsync(opts.Value.FeedId);
    return Results.Ok(routes);
});

app.MapGet("/api/routes-for-stop", async (string? stop_id, DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    if (string.IsNullOrEmpty(stop_id))
    {
        var allRoutes = await db.GetAllRoutesAsync(opts.Value.FeedId);
        return Results.Ok(allRoutes);
    }
    var routes = await db.GetRoutesForStopAsync(stop_id);
    return Results.Ok(routes);
});

app.MapGet("/api/headsigns", async (DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    var headsigns = await db.GetAllHeadsignsAsync(opts.Value.FeedId);
    return Results.Ok(headsigns);
});

app.MapGet("/api/headsigns-for-stop", async (string? stop_id, DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    if (string.IsNullOrEmpty(stop_id))
    {
        var allHeadsigns = await db.GetAllHeadsignsAsync(opts.Value.FeedId);
        return Results.Ok(allHeadsigns);
    }
    var headsigns = await db.GetHeadsignsForStopAsync(stop_id);
    return Results.Ok(headsigns);
});

app.MapGet("/api/observations", async (
    string? stop_id, string? date, string? from, string? to,
    string? route, string? time_from, string? time_to, string? headsign,
    DatabaseService db, AnalyzerService analyzer, IOptions<WalttiSettings> opts) =>
{
    var startDate = from ?? date ?? "";
    var endDate = to ?? date ?? "";
    if (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate))
        return Results.BadRequest(new { error = "date or from/to parameters required" });

    var start = AnalyzerService.TryParseDateToInt(startDate);
    var end = AnalyzerService.TryParseDateToInt(endDate);
    if (!start.HasValue || !end.HasValue)
        return Results.BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd." });

    var feedId = string.IsNullOrEmpty(stop_id) ? opts.Value.FeedId : null;
    var rows = await db.GetObservationsAsync(stop_id, start.Value, end.Value, route,
        AnalyzerService.ParseTime(time_from), AnalyzerService.ParseTime(time_to), feedId, headsign);
    return Results.Ok(rows);
});

app.MapGet("/api/latest-observations", async (DatabaseService db, IOptions<WalttiSettings> opts) =>
{
    var rows = await db.GetLatestObservationsAsync(300, opts.Value.FeedId);
    return Results.Ok(rows);
});

app.MapGet("/api/summary", async (
    string? stop_id, string? from, string? to, string? route,
    string? time_from, string? time_to, string? headsign,
    AnalyzerService analyzer, IOptions<WalttiSettings> opts) =>
{
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        return Results.BadRequest(new { error = "from and to parameters required" });

    var feedId = string.IsNullOrEmpty(stop_id) ? opts.Value.FeedId : null;
    var result = await analyzer.GetSummaryAsync(stop_id, from, to, route,
        AnalyzerService.ParseTime(time_from), AnalyzerService.ParseTime(time_to), feedId, headsign);
    return Results.Ok(result);
});

app.MapGet("/api/route-breakdown", async (
    string? stop_id, string? from, string? to, string? route,
    string? time_from, string? time_to, string? headsign,
    AnalyzerService analyzer, IOptions<WalttiSettings> opts) =>
{
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        return Results.BadRequest(new { error = "from and to parameters required" });

    var feedId = string.IsNullOrEmpty(stop_id) ? opts.Value.FeedId : null;
    var result = await analyzer.GetRouteBreakdownAsync(stop_id, from, to, route,
        AnalyzerService.ParseTime(time_from), AnalyzerService.ParseTime(time_to), feedId, headsign);
    return Results.Ok(result);
});

app.MapGet("/api/delay-by-hour", async (
    string? stop_id, string? from, string? to, string? route,
    string? time_from, string? time_to, string? headsign,
    AnalyzerService analyzer, IOptions<WalttiSettings> opts) =>
{
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        return Results.BadRequest(new { error = "from and to parameters required" });

    var feedId = string.IsNullOrEmpty(stop_id) ? opts.Value.FeedId : null;
    var result = await analyzer.GetDelayByHourAsync(stop_id, from, to, route,
        AnalyzerService.ParseTime(time_from), AnalyzerService.ParseTime(time_to), feedId, headsign);
    return Results.Ok(result);
});

app.MapPost("/api/collect/poll", async (CollectorService collector) =>
{
    var result = await collector.PollSlidingWindowAsync();
    return Results.Ok(result);
});

app.MapPost("/api/discover", async (CollectorService collector) =>
{
    var result = await collector.DiscoverStopsAsync();
    return Results.Ok(result);
});

app.MapFallbackToFile("index.html");

app.Run();
