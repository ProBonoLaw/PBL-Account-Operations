using System.ComponentModel.DataAnnotations;

namespace ResourceManager.Configuration;

public enum AzurePrincipalType
{
    ServicePrincipal,
    User,
    Group
}

public sealed class ResourceManagerOptions
{
    public const string SectionName = "ResourceManager";

    public Guid SubscriptionId { get; init; }

    [Required]
    public string DeploymentResourceGroup { get; init; } = "";

    [Required]
    public string Location { get; init; } = "";

    [Required]
    public string SearchServiceNamePrefix { get; init; } = "";

    [Range(0, 32)]
    public int SubnetPrefixLength { get; init; } = 24;

    [Required]
    public string ExistingVnetResourceGroup { get; init; } = "";

    [Required]
    public string ExistingVnetName { get; init; } = "";

    [Required]
    public string PrivateDnsZoneResourceGroup { get; init; } = "";

    [Required]
    public string PrivateEndpointLocation { get; init; } = "westcentralus";

    public AzurePrincipalType PrincipalType { get; init; } = AzurePrincipalType.ServicePrincipal;

    public Guid[] IndexDataReaderPrincipalIds { get; init; } = [];

    public Guid[] IndexAdminPrincipalIds { get; init; } = [];

    public Guid[] IndexDataReaderWriterPrincipalIds { get; init; } = [];

    [Required]
    public string AdminApiKey { get; init; } = "";

    public long MinimumCapacityHeadroomBytes { get; init; } = 1_000_000_000;

    public TimeSpan DeploymentPollInterval { get; init; } = TimeSpan.FromSeconds(15);

}
