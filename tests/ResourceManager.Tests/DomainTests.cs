using ResourceManager.Domain;
using Xunit;

namespace ResourceManager.Tests;

public sealed class DomainTests
{
    [Fact]
    public void CapacitySelectorReturnsFirstServiceMeetingAllThreeLimits()
    {
        var services = new[]
        {
            Service(storageRemaining: 2_000_000_000, vectorRemaining: 2_000_000_000, indexes: 1, indexLimit: 1),
            Service(storageRemaining: 1_000_000_000, vectorRemaining: 1_000_000_000, indexes: 2, indexLimit: 3),
            Service(storageRemaining: 5_000_000_000, vectorRemaining: 5_000_000_000, indexes: 2, indexLimit: 3)
        };

        var result = SearchCapacitySelector.SelectFirstAvailable(services, 1_000_000_000);

        Assert.Equal("search-2", result?.ServiceName);
    }

    [Theory]
    [InlineData(999_999_999, 2_000_000_000, 0, 1, false)]
    [InlineData(1_000_000_000, 999_999_999, 0, 1, false)]
    [InlineData(1_000_000_000, 1_000_000_000, 1, 1, false)]
    [InlineData(1_000_000_000, 1_000_000_000, 0, 1, true)]
    public void CapacityChecksStorageVectorAndIndexHeadroom(long storageRemaining, long vectorRemaining,
        long indexes, long indexLimit, bool expected)
    {
        var service = Service(storageRemaining, vectorRemaining, indexes, indexLimit);

        Assert.Equal(expected, service.HasCapacity(1_000_000_000));
    }

    [Fact]
    public void CapacitySelectorReturnsNullWhenNoneAreAvailable()
    {
        Assert.Null(SearchCapacitySelector.SelectFirstAvailable([], 1));
    }

    [Fact]
    public void CapacitySelectorRejectsNegativeHeadroom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchCapacitySelector.SelectFirstAvailable([], -1));
    }

    [Theory]
    [InlineData("10.0.0.17/24", 26, "10.0.0.0/26")]
    [InlineData("10.0.0.0/24", 26, "10.0.0.0/26")]
    [InlineData("255.255.255.255/32", 32, "255.255.255.255/32")]
    public void AllocatorReturnsFirstAlignedBlock(string addressSpace, int prefix, string expected)
    {
        Assert.Equal(expected,
            Ipv4CidrAllocator.FindFirstAvailableSubnet([addressSpace], [], prefix));
    }

    [Fact]
    public void AllocatorSkipsEveryExistingOverlapAndSearchesNextAddressSpace()
    {
        var result = Ipv4CidrAllocator.FindFirstAvailableSubnet(
            ["10.0.0.0/24", "192.168.1.0/24"],
            ["10.0.0.0/25", "10.0.0.128/26", "10.0.0.192/26", "192.168.1.0/26"],
            26);

        Assert.Equal("192.168.1.64/26", result);
    }

    [Fact]
    public void AllocatorIgnoresIpv6AndSubnetsOutsideTheAddressSpace()
    {
        var result = Ipv4CidrAllocator.FindFirstAvailableSubnet(
            ["2001:db8::/64", "10.1.0.0/24"],
            ["10.2.0.0/24", "2001:db8:1::/64"], 24);

        Assert.Equal("10.1.0.0/24", result);
    }

    [Fact]
    public void AllocatorRejectsInvalidPrefixAndMalformedIpv4Cidr()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ipv4CidrAllocator.FindFirstAvailableSubnet(["10.0.0.0/24"], [], 33));
        Assert.Throws<FormatException>(() =>
            Ipv4CidrAllocator.FindFirstAvailableSubnet(["10.0.0.0/24"], ["10.0.0.0/99"], 24));
    }

    [Fact]
    public void AllocatorThrowsWhenTheRequestedBlockDoesNotFit()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Ipv4CidrAllocator.FindFirstAvailableSubnet(["10.0.0.0/24"], ["10.0.0.0/24"], 24));
    }

    [Fact]
    public void ServiceNameIsStableAndSanitizedFromTheOperationId()
    {
        var id = Guid.Parse("22222222-3333-4444-8555-666666666666");

        var name = SearchServiceName.ForOperation("probono-search", id);

        Assert.Equal("probono-search-222222223333", name);
        Assert.True(SearchServiceName.IsValid(name));
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("ab-cd", true)]
    [InlineData("a1-2b", true)]
    [InlineData("a-123", false)]
    [InlineData("aa--bb", false)]
    [InlineData("a", false)]
    public void ServiceNameValidatorEnforcesAzureNamingRules(string name, bool expected) =>
        Assert.Equal(expected, SearchServiceName.IsValid(name));

    [Theory]
    [InlineData("bad-prefix-")]
    [InlineData("a")]
    [InlineData("aa--bb")]
    [InlineData("prefix_with_underscore")]
    [InlineData("this-prefix-is-far-too-long-to-fit-in-a-search-service-name")]
    public void ServiceNameRejectsInvalidPrefixes(string prefix)
    {
        Assert.Throws<ArgumentException>(() => SearchServiceName.ForOperation(prefix, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("not-an-ip", false)]
    public void PrivateIpClassificationUsesIpv4Rfc1918Ranges(string address, bool expected)
    {
        Assert.Equal(expected, IpAddressClassifier.IsPrivate(address));
    }

    private static SearchServiceCapacity Service(long storageRemaining, long vectorRemaining,
        long indexes, long indexLimit) => new(
        Guid.Parse("11111111-2222-4333-8444-555555555555"), "rg", $"search-{indexes}",
        new Uri("https://search.example"), "basic", 0, storageRemaining, 0, vectorRemaining,
        indexes, indexLimit);
}
