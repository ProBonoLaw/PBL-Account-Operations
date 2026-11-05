using Microsoft.Extensions.Options;
using ResourceManager.Configuration;
using ResourceManager.Domain;
using ResourceManager.Infrastructure;

namespace ResourceManager.Services;

public interface IResourceManagerWorkflow
{
    Task<Uri> GetSearchEndpointAsync(Guid operationId, CancellationToken cancellationToken);
}

public sealed class ResourceManagerWorkflow(
    IResourceManagerProvisioner provisioner,
    IOptions<ResourceManagerOptions> options,
    TimeProvider timeProvider,
    ILogger<ResourceManagerWorkflow> logger) : IResourceManagerWorkflow
{
    private readonly ResourceManagerOptions settings = options.Value;
    private readonly SemaphoreSlim allocationGate = new(1, 1);

    public async Task<Uri> GetSearchEndpointAsync(Guid operationId, CancellationToken cancellationToken)
    {
        using var operationScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OperationId"] = operationId
        });
        var lockRequestedAt = timeProvider.GetTimestamp();
        logger.LogInformation("Search endpoint workflow queued for operation {OperationId}", operationId);
        try
        {
            await allocationGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Search endpoint workflow canceled while waiting for allocation lock for operation {OperationId}",
                operationId);
            throw;
        }

        try
        {
            logger.LogInformation(
                "Search endpoint workflow acquired allocation lock for operation {OperationId} after {LockWaitMilliseconds} ms",
                operationId, timeProvider.GetElapsedTime(lockRequestedAt).TotalMilliseconds);
            var endpoint = await GetSearchEndpointUnderGateAsync(operationId, cancellationToken);
            logger.LogInformation("Search endpoint workflow completed for operation {OperationId} with endpoint {SearchEndpoint}",
                operationId, endpoint);
            return endpoint;
        }
        finally
        {
            logger.LogInformation("Search endpoint workflow released allocation lock for operation {OperationId}",
                operationId);
            allocationGate.Release();
        }
    }

    private async Task<Uri> GetSearchEndpointUnderGateAsync(Guid operationId,
        CancellationToken cancellationToken)
    {
        var serviceName = SearchServiceName.ForOperation(settings.SearchServiceNamePrefix, operationId);
        logger.LogInformation("Search capacity inventory started for operation {OperationId}", operationId);
        var services = await provisioner.ListSearchServicesAsync(cancellationToken);
        logger.LogInformation("Search capacity inventory completed for operation {OperationId}; found {ServiceCount} managed services",
            operationId, services.Count);
        foreach (var service in services)
        {
            logger.LogInformation(
                "Search capacity observed for operation {OperationId}, service {ServiceName}: storage {StorageUsed}/{StorageQuota}, vector {VectorUsed}/{VectorQuota}, indexes {IndexCount}/{IndexQuota}",
                operationId, service.ServiceName, service.StorageUsedBytes, service.StorageQuotaBytes,
                service.VectorUsedBytes, service.VectorQuotaBytes, service.IndexCount, service.IndexQuota);
        }

        var available = SearchCapacitySelector.SelectFirstAvailable(services,
            settings.MinimumCapacityHeadroomBytes);
        if (available is not null)
        {
            logger.LogInformation(
                "Reusing Search service {ServiceName} for operation {OperationId} with endpoint {SearchEndpoint}",
                available.ServiceName, operationId, available.Endpoint);
            return available.Endpoint;
        }

        logger.LogInformation(
            "No existing Search service has sufficient capacity for operation {OperationId}; provisioning {SearchServiceName}",
            operationId, serviceName);
        using var activity = ResourceManagerTelemetry.ActivitySource.StartActivity("search.provision");
        activity?.SetTag("resource.operation_id", operationId);
        activity?.SetTag("azure.search.service_name", serviceName);

        logger.LogInformation("Private endpoint subnet allocation started for operation {OperationId}, service {SearchServiceName}",
            operationId, serviceName);
        var subnetPrefix = await provisioner.AllocatePrivateEndpointSubnetAsync(cancellationToken);
        logger.LogInformation(
            "Private endpoint subnet allocated for operation {OperationId}, service {SearchServiceName}: {SubnetPrefix}",
            operationId, serviceName, subnetPrefix);
        logger.LogInformation("Search service deployment submission started for operation {OperationId}, service {SearchServiceName}",
            operationId, serviceName);
        await provisioner.StartDeploymentAsync(new SearchDeploymentRequest(
            serviceName,
            subnetPrefix,
            settings.IndexDataReaderPrincipalIds,
            settings.IndexDataReaderWriterPrincipalIds,
            settings.IndexAdminPrincipalIds), cancellationToken);
        logger.LogInformation("Search service deployment submission completed for operation {OperationId}, service {SearchServiceName}",
            operationId, serviceName);

        return await WaitForDeploymentAsync(serviceName, operationId, cancellationToken);
    }

    private async Task<Uri> WaitForDeploymentAsync(string serviceName, Guid operationId,
        CancellationToken cancellationToken)
    {
        using var activity = ResourceManagerTelemetry.ActivitySource.StartActivity("search.deployment.wait");
        activity?.SetTag("resource.operation_id", operationId);
        activity?.SetTag("azure.search.service_name", serviceName);
        var pollNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollNumber++;
            var deployment = await provisioner.GetDeploymentStatusAsync(serviceName, cancellationToken);
            logger.LogInformation(
                "Search deployment status for operation {OperationId}, service {SearchServiceName}, poll {PollNumber}: {ProvisioningState}",
                operationId, serviceName, pollNumber, deployment.ProvisioningState);
            switch (deployment.ProvisioningState.ToLowerInvariant())
            {
                case "succeeded":
                    var endpoint = new Uri($"https://{serviceName}.search.windows.net");
                    logger.LogInformation(
                        "Search service deployment succeeded for operation {OperationId}, service {SearchServiceName}, endpoint {SearchEndpoint}",
                        operationId, serviceName, endpoint);
                    return endpoint;
                case "failed":
                case "canceled":
                    logger.LogError(
                        "Search service deployment ended in state {ProvisioningState} for operation {OperationId}, service {SearchServiceName}: {DeploymentError}",
                        deployment.ProvisioningState, operationId, serviceName, deployment.Error);
                    throw new InvalidOperationException(
                        $"Deployment {serviceName} {deployment.ProvisioningState}: {deployment.Error ?? "no error details"}");
            }

            await Task.Delay(settings.DeploymentPollInterval, timeProvider, cancellationToken);
        }
    }
}
