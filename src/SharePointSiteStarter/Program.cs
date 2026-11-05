using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PnP.Core.Auth;
using PnP.Core.Auth.Services.Builder.Configuration;
using PnP.Core.Services;
using SharePointSiteStarter.Api;
using SharePointSiteStarter.Configuration;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Infrastructure;
using SharePointSiteStarter.Services;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SimpleConsoleFormatterOptions>(logging => logging.IncludeScopes = true);
var settingsFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE") ??
                   "/mnt/keyvault/sharepoint-site-starter-appsettings.json";
builder.Configuration.AddJsonFile(settingsFile, optional: true, reloadOnChange: true);

builder.Services.AddSingleton<IValidateOptions<SharePointSiteStarterOptions>, SharePointSiteStarterOptionsValidator>();
builder.Services.AddOptions<SharePointSiteStarterOptions>()
    .Bind(builder.Configuration.GetSection(SharePointSiteStarterOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StartSiteRequestValidator>();
builder.Services.AddSingleton<ISharePointSiteStarterWorkflow, SharePointSiteStarterWorkflow>();
builder.Services.AddSingleton<ISharePointSiteProvisioner, SharePointSiteProvisioner>();
builder.Services.AddHealthChecks();

if (!builder.Environment.IsEnvironment("Testing"))
{
    var startupOptions = builder.Configuration.GetSection(SharePointSiteStarterOptions.SectionName)
        .Get<SharePointSiteStarterOptions>() ?? new SharePointSiteStarterOptions();
    var validation = new SharePointSiteStarterOptionsValidator().Validate(null, startupOptions);
    if (validation.Failed)
        throw new OptionsValidationException(SharePointSiteStarterOptions.SectionName,
            typeof(SharePointSiteStarterOptions), validation.Failures);

    TokenCredential credential = new DefaultAzureCredential();
    var secretClient = new SecretClient(new Uri(startupOptions.CertificateKeyVaultUri), credential);
    var certificateSecret = (await secretClient.GetSecretAsync(startupOptions.CertificateSecretName,
        cancellationToken: CancellationToken.None)).Value.Value;
    var certificateBytes = Convert.FromBase64String(certificateSecret);
    var certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, null,
        X509KeyStorageFlags.EphemeralKeySet);

    builder.Services.AddSingleton(credential);
    builder.Services.AddSingleton(certificate);
    builder.Services.AddPnPCore(pnpOptions =>
    {
        pnpOptions.DefaultAuthenticationProvider = new X509CertificateAuthenticationProvider(
            startupOptions.ClientId.ToString("D"), startupOptions.TenantId.ToString("D"),
            new PnPCoreAuthenticationX509CertificateOptions { Certificate = certificate });
    });
}

var exportOpenTelemetry = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
if (exportOpenTelemetry)
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.ParseStateValues = true;
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("sharepoint-site-starter"));
        logging.AddOtlpExporter();
    });
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("sharepoint-site-starter"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(SharePointSiteStarterTelemetry.InstrumentationName);
        if (exportOpenTelemetry)
            tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(SharePointSiteStarterTelemetry.InstrumentationName);
        if (exportOpenTelemetry)
            metrics.AddOtlpExporter();
    });

var app = builder.Build();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapSharePointSiteStarterEndpoints();
await app.RunAsync();

public partial class Program;
