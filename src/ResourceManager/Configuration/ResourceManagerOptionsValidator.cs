using Microsoft.Extensions.Options;
using ResourceManager.Domain;

namespace ResourceManager.Configuration;

public sealed class ResourceManagerOptionsValidator : IValidateOptions<ResourceManagerOptions>
{
    public ValidateOptionsResult Validate(string? name, ResourceManagerOptions options)
    {
        var failures = new List<string>();
        if (options.SubscriptionId == Guid.Empty)
            failures.Add("ResourceManager:SubscriptionId must be a non-empty GUID.");
        Require(options.DeploymentResourceGroup, "DeploymentResourceGroup", failures);
        Require(options.Location, "Location", failures);
        Require(options.ExistingVnetResourceGroup, "ExistingVnetResourceGroup", failures);
        Require(options.ExistingVnetName, "ExistingVnetName", failures);
        Require(options.PrivateDnsZoneResourceGroup, "PrivateDnsZoneResourceGroup", failures);
        Require(options.PrivateEndpointLocation, "PrivateEndpointLocation", failures);

        if (string.IsNullOrWhiteSpace(options.AdminApiKey) || options.AdminApiKey.Length < 32)
            failures.Add("ResourceManager:AdminApiKey must contain at least 32 characters.");
        if (!Enum.IsDefined(options.PrincipalType))
            failures.Add("ResourceManager:PrincipalType must be ServicePrincipal, User, or Group.");
        if (options.MinimumCapacityHeadroomBytes < 0)
            failures.Add("ResourceManager:MinimumCapacityHeadroomBytes cannot be negative.");
        if (options.DeploymentPollInterval <= TimeSpan.Zero)
            failures.Add("ResourceManager:DeploymentPollInterval must be positive.");

        try
        {
            _ = SearchServiceName.ForOperation(options.SearchServiceNamePrefix, Guid.Parse("00000000000000000000000000000001"));
        }
        catch (ArgumentException)
        {
            failures.Add("ResourceManager:SearchServiceNamePrefix must produce a valid Azure AI Search name.");
        }

        if (options.IndexDataReaderPrincipalIds.Concat(options.IndexAdminPrincipalIds)
            .Concat(options.IndexDataReaderWriterPrincipalIds).Any(id => id == Guid.Empty))
            failures.Add("ResourceManager principal ID lists cannot contain empty GUID values.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(string? value, string property, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"ResourceManager:{property} is required.");
    }
}
