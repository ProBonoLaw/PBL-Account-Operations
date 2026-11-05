using SharePointSiteStarter.Domain;

namespace SharePointSiteStarter.Services;

public interface ISharePointSiteStarterWorkflow
{
    Task StartAsync(Guid operationId, StartSiteRequest request, CancellationToken cancellationToken);
}

public interface ISharePointSiteProvisioner
{
    Task ProvisionAsync(StartSiteRequest request, CancellationToken cancellationToken);
}

public sealed class SharePointSiteStarterWorkflow(
    ISharePointSiteProvisioner provisioner,
    ILogger<SharePointSiteStarterWorkflow> logger) : ISharePointSiteStarterWorkflow
{
    private readonly SemaphoreSlim provisioningGate = new(1, 1);

    public async Task StartAsync(Guid operationId, StartSiteRequest request,
        CancellationToken cancellationToken)
    {
        using var operationScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OperationId"] = operationId,
            ["AccountId"] = request.AccountId
        });
        logger.LogInformation("SharePoint site starter queued for operation {OperationId}, account {AccountId}",
            operationId, request.AccountId);
        try
        {
            await provisioningGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SharePoint site starter canceled while waiting for provisioning lock for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
            throw;
        }

        try
        {
            logger.LogInformation("SharePoint site starter acquired provisioning lock for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
            await provisioner.ProvisionAsync(request, cancellationToken);
            logger.LogInformation("SharePoint site starter completed for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SharePoint site starter canceled for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SharePoint site starter failed for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
            throw;
        }
        finally
        {
            logger.LogInformation("SharePoint site starter released provisioning lock for operation {OperationId}, account {AccountId}",
                operationId, request.AccountId);
            provisioningGate.Release();
        }
    }
}
