using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ResourceManager.Api;
using ResourceManager.Configuration;
using ResourceManager.Infrastructure;
using ResourceManager.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SimpleConsoleFormatterOptions>(logging => logging.IncludeScopes = true);
var settingsFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE") ??
                   "/mnt/keyvault/resource-manager-appsettings.json";
builder.Configuration.AddJsonFile(settingsFile, optional: true, reloadOnChange: true);

builder.Services.AddSingleton<IValidateOptions<ResourceManagerOptions>, ResourceManagerOptionsValidator>();
builder.Services.AddOptions<ResourceManagerOptions>()
    .Bind(builder.Configuration.GetSection(ResourceManagerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddHttpClient<AzureResourceManagerProvisioner>(client =>
    client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IResourceManagerProvisioner>(services =>
    services.GetRequiredService<AzureResourceManagerProvisioner>());
builder.Services.AddSingleton<IResourceManagerWorkflow, ResourceManagerWorkflow>();
builder.Services.AddSingleton<IHostAddressResolver, HostAddressResolver>();
builder.Services.AddHealthChecks();

var exportOpenTelemetry = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
if (exportOpenTelemetry)
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.ParseStateValues = true;
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("resource-manager"));
        logging.AddOtlpExporter();
    });
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("resource-manager"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(ResourceManagerTelemetry.InstrumentationName);
        if (exportOpenTelemetry)
            tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(ResourceManagerTelemetry.InstrumentationName);
        if (exportOpenTelemetry)
            metrics.AddOtlpExporter();
    });

var app = builder.Build();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapResourceManagerEndpoints();
await app.RunAsync();

public partial class Program;
