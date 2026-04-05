using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WalttiAnalyzer.Functions.Models;
using WalttiAnalyzer.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.Configure<WalttiSettings>(context.Configuration.GetSection("Waltti"));
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<DigitransitClient>();
        services.AddSingleton<CollectorService>();
        services.AddSingleton<AnalyzerService>();
    })
    .Build();

host.Run();
