using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResourceManager.Services;

public static class ResourceManagerTelemetry
{
    public const string InstrumentationName = "ProBonoLaw.ResourceManager";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    public static readonly Meter Meter = new(InstrumentationName);
    public static readonly Counter<long> OperationsStarted = Meter.CreateCounter<long>(
        "resource_manager.operations.started");
    public static readonly Counter<long> OperationsSucceeded = Meter.CreateCounter<long>(
        "resource_manager.operations.succeeded");
    public static readonly Counter<long> OperationsFailed = Meter.CreateCounter<long>(
        "resource_manager.operations.failed");
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "resource_manager.operations.duration", "s");
}
