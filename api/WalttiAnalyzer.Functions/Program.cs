using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WalttiAnalyzer.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<DigitransitClient>();
        services.AddSingleton<CollectorService>();
        services.AddSingleton<AnalyzerService>();
    })
    .Build();

host.Run();
