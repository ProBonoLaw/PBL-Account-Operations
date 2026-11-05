using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace SharePointSiteStarter.Services;

public static class SharePointSiteStarterTelemetry
{
    public const string InstrumentationName = "SharePointSiteStarter";
    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName);
    public static readonly Counter<long> OperationsStarted = Meter.CreateCounter<long>("sharepoint.site_starter.started");
    public static readonly Counter<long> OperationsSucceeded = Meter.CreateCounter<long>("sharepoint.site_starter.succeeded");
    public static readonly Counter<long> OperationsFailed = Meter.CreateCounter<long>("sharepoint.site_starter.failed");
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("sharepoint.site_starter.duration", "s");
}
