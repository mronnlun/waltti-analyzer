using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WalttiAnalyzer.Functions.Models;
using WalttiAnalyzer.Functions.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

builder.ConfigureFunctionsWebApplication();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.Configure<WalttiSettings>(builder.Configuration.GetSection("Waltti"));
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<DigitransitClient>();
builder.Services.AddSingleton<CollectorService>();
builder.Services.AddSingleton<AnalyzerService>();

builder.Build().Run();
