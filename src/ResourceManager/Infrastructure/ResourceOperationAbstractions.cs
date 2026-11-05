using ResourceManager.Domain;

namespace ResourceManager.Infrastructure;

public interface IResourceManagerProvisioner
{
    Task<IReadOnlyList<SearchServiceCapacity>> ListSearchServicesAsync(CancellationToken cancellationToken);
    Task<string> AllocatePrivateEndpointSubnetAsync(CancellationToken cancellationToken);
    Task StartDeploymentAsync(SearchDeploymentRequest request, CancellationToken cancellationToken);
    Task<ArmDeploymentStatus> GetDeploymentStatusAsync(string deploymentName,
        CancellationToken cancellationToken);
}

public sealed record SearchDeploymentRequest(
    string DeploymentName,
    string SubnetPrefix,
    Guid[] IndexDataReaderPrincipalIds,
    Guid[] IndexDataReaderWriterPrincipalIds,
    Guid[] IndexAdminPrincipalIds);

public sealed record ArmDeploymentStatus(string ProvisioningState, string? Error);
