using Ilmari.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Logging.Services.Configure<LoggerFilterOptions>(UnfilterLogLevelInfo);

builder.UseMiddleware<ExceptionLoggingMiddleware>();

builder.Build().Run();
return;

bool IsAppiProvider(LoggerFilterRule rule) => rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";
void UnfilterLogLevelInfo(LoggerFilterOptions options)
{
    // The Application Insights SDK adds a default logging filter that instructs ILogger to capture only Warning and more severe logs.
    // Application Insights requires an explicit override.
    var defaultRule = options.Rules.FirstOrDefault(IsAppiProvider);
    if (defaultRule is not null) options.Rules.Remove(defaultRule);
}