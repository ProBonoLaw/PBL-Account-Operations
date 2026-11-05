using ResourceManager.Configuration;
using Xunit;

namespace ResourceManager.Tests;

public sealed class ConfigurationAndHealthTests
{
    private static readonly ResourceManagerOptions ValidOptions = new()
    {
        SubscriptionId = Guid.Parse("11111111-2222-4333-8444-555555555555"),
        DeploymentResourceGroup = "search-resources",
        Location = "centralus",
        SearchServiceNamePrefix = "probono-search",
        ExistingVnetResourceGroup = "networking",
        ExistingVnetName = "shared-vnet",
        PrivateDnsZoneResourceGroup = "dns-zones",
        PrivateEndpointLocation = "westcentralus",
        AdminApiKey = "admin-key-with-at-least-thirty-two-characters"
    };

    [Fact]
    public void ValidatorAcceptsCompleteConfiguration()
    {
        var result = new ResourceManagerOptionsValidator().Validate(null, ValidOptions);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidatorReportsMissingValuesAndInvalidKeyAndNameSettings()
    {
        var result = new ResourceManagerOptionsValidator().Validate(null, new ResourceManagerOptions
        {
            AdminApiKey = "short",
            SearchServiceNamePrefix = "bad-prefix-"
        });
        var failures = result.Failures?.ToArray() ?? [];

        Assert.False(result.Succeeded);
        Assert.Contains(failures, failure => failure.Contains("SubscriptionId"));
        Assert.Contains(failures, failure => failure.Contains("DeploymentResourceGroup"));
        Assert.Contains(failures, failure => failure.Contains("Location"));
        Assert.Contains(failures, failure => failure.Contains("ExistingVnetResourceGroup"));
        Assert.Contains(failures, failure => failure.Contains("ExistingVnetName"));
        Assert.Contains(failures, failure => failure.Contains("PrivateDnsZoneResourceGroup"));
        Assert.Contains(failures, failure => failure.Contains("AdminApiKey"));
        Assert.Contains(failures, failure => failure.Contains("SearchServiceNamePrefix"));
    }

    [Fact]
    public void ValidatorRejectsInvalidPrincipalTypeIntervalsThresholdAndEmptyPrincipalIds()
    {
        var options = new ResourceManagerOptions
        {
            SubscriptionId = ValidOptions.SubscriptionId,
            DeploymentResourceGroup = ValidOptions.DeploymentResourceGroup,
            Location = ValidOptions.Location,
            SearchServiceNamePrefix = ValidOptions.SearchServiceNamePrefix,
            ExistingVnetResourceGroup = ValidOptions.ExistingVnetResourceGroup,
            ExistingVnetName = ValidOptions.ExistingVnetName,
            PrivateDnsZoneResourceGroup = ValidOptions.PrivateDnsZoneResourceGroup,
            PrivateEndpointLocation = ValidOptions.PrivateEndpointLocation,
            AdminApiKey = ValidOptions.AdminApiKey,
            PrincipalType = (AzurePrincipalType)999,
            MinimumCapacityHeadroomBytes = -1,
            DeploymentPollInterval = TimeSpan.Zero,
            IndexAdminPrincipalIds = [Guid.Empty]
        };
        var result = new ResourceManagerOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.Failures?.Count());
    }
}
