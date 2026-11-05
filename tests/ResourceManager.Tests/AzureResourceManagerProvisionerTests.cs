using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.Configuration;
using ResourceManager.Infrastructure;
using Xunit;

namespace ResourceManager.Tests;

public sealed class AzureResourceManagerProvisionerTests
{
    private static readonly Guid SubscriptionId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact]
    public async Task ListsServicesAcrossPagesAndReadsTypedCapacityCounters()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new CallbackHandler((request, _) =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/searchServices", StringComparison.Ordinal))
            {
                Assert.Equal(
                    $"/subscriptions/{SubscriptionId}/resourceGroups/search-resource-group/providers/Microsoft.Search/searchServices",
                    path);
                return JsonResponse("""
                    {"value":[{"id":"/subscriptions/11111111-2222-4333-8444-555555555555/resourceGroups/search-resource-group/providers/Microsoft.Search/searchServices/search-test-one","name":"search-test-one","sku":{"name":"basic"},"properties":{"hostName":"search-test-one.search.windows.net"}},{"id":"/subscriptions/11111111-2222-4333-8444-555555555555/resourceGroups/search-resource-group/providers/Microsoft.Search/searchServices/unmanaged-search","name":"unmanaged-search","sku":{"name":"basic"},"properties":{"hostName":"unmanaged-search.search.windows.net"}}],"nextLink":"https://management.azure.com/next-page"}
                    """);
            }
            if (path == "/next-page")
                return JsonResponse("""
                    {"value":[{"id":"/subscriptions/11111111-2222-4333-8444-555555555555/resourceGroups/search-resource-group/providers/Microsoft.Search/searchServices/search-test-two","name":"search-test-two","sku":{"name":"standard"},"properties":{}}]}
                    """);
            if (request.RequestUri!.Host == "search-test-one.search.windows.net")
                return JsonResponse("""
                    {"counters":{"storageSize":{"usage":10,"quota":100},"vectorIndexSize":{"usage":20,"quota":200},"indexesCount":{"usage":3,"quota":5}}}
                    """);
            if (request.RequestUri!.Host == "search-test-two.search.windows.net")
                return JsonResponse("""
                    {"counters":{"storageSize":{"usage":1,"quota":9},"vectorIndexSize":{"usage":2,"quota":8},"indexesCount":{"usage":4,"quota":6}}}
                    """);
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        });
        var credential = new FakeTokenCredential();
        var provisioner = CreateProvisioner(handler, credential);

        var services = await provisioner.ListSearchServicesAsync(CancellationToken.None);

        Assert.Equal(2, services.Count);
        Assert.Equal("search-resource-group", services[0].ResourceGroup);
        Assert.Equal("search-test-one", services[0].ServiceName);
        Assert.Equal("https://search-test-one.search.windows.net/", services[0].Endpoint.ToString());
        Assert.Equal(10, services[0].StorageUsedBytes);
        Assert.Equal(200, services[0].VectorQuotaBytes);
        Assert.Equal(3, services[0].IndexCount);
        Assert.Equal("search-resource-group", services[1].ResourceGroup);
        Assert.Equal("search-test-two", services[1].ServiceName);
        Assert.Equal("https://search-test-two.search.windows.net/", services[1].Endpoint.ToString());
        Assert.Equal(4, services[1].IndexCount);
        Assert.Equal(4, requests.Count);
        Assert.All(requests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
        Assert.Contains(credential.Scopes, scopes => scopes.Single() == "https://management.azure.com/.default");
        Assert.Contains(credential.Scopes, scopes => scopes.Single() == "https://search.azure.com/.default");
    }

    [Fact]
    public async Task AllocatesPrivateEndpointSubnetFromVnetAndExistingSubnets()
    {
        var handler = new CallbackHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith(
                "/virtualNetworks/shared-vnet", StringComparison.Ordinal)
            ? JsonResponse("""
                {"properties":{"addressSpace":{"addressPrefixes":["10.0.0.17/24","2001:db8::/64"]}}}
                """)
            : JsonResponse("""
                {"value":[{"properties":{"addressPrefix":"10.0.0.0/25"}},{"properties":{"addressPrefixes":["10.0.0.128/26"]}}]}
                """));
        var provisioner = CreateProvisioner(handler, prefixLength: 26);

        var prefix = await provisioner.AllocatePrivateEndpointSubnetAsync(CancellationToken.None);

        Assert.Equal("10.0.0.192/26", prefix);
    }

    [Fact]
    public async Task ValidatesAndStartsDeploymentWithTypedParameters()
    {
        var requests = new List<(string Method, string Path, string? Body)>();
        var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return JsonResponse("{}");
        });
        var provisioner = CreateProvisioner(handler);
        var request = new SearchDeploymentRequest("search-managed-test", "10.0.0.0/24",
            [Guid.Parse("f1d33e91-1111-4b4b-bbbb-123456789abc")],
            [Guid.Parse("f1d33e91-2222-4b4b-bbbb-123456789abc")],
            [Guid.Parse("f1d33e91-3333-4b4b-bbbb-123456789abc")]);

        await provisioner.StartDeploymentAsync(request, CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.Equal("POST", requests[0].Method);
        Assert.EndsWith("/validate", requests[0].Path);
        Assert.Equal("PUT", requests[1].Method);
        Assert.EndsWith("/search-managed-test", requests[1].Path);
        using var payload = JsonDocument.Parse(requests[1].Body!);
        var properties = payload.RootElement.GetProperty("properties");
        Assert.Equal("Incremental", properties.GetProperty("mode").GetString());
        Assert.Equal("search-managed-test",
            properties.GetProperty("parameters").GetProperty("searchServiceName").GetProperty("value").GetString());
        Assert.Equal("10.0.0.0/24",
            properties.GetProperty("parameters").GetProperty("newLaSubnetPrefix").GetProperty("value").GetString());
        Assert.Equal("Group",
            properties.GetProperty("parameters").GetProperty("principalType").GetProperty("value").GetString());
        Assert.True(properties.GetProperty("template").GetProperty("resources").TryGetProperty("search", out _));
        Assert.True(properties.GetProperty("template").GetProperty("resources").TryGetProperty("peLa", out _));
    }

    [Fact]
    public async Task ReadsDeploymentProvisioningStateAndErrorDetails()
    {
        var handler = new CallbackHandler((_, _) => JsonResponse("""
            {"properties":{"provisioningState":"Failed","error":{"code":"DeploymentFailed","message":"private endpoint failed"}}}
            """));
        var provisioner = CreateProvisioner(handler);

        var status = await provisioner.GetDeploymentStatusAsync("search-failed", CancellationToken.None);

        Assert.Equal("Failed", status.ProvisioningState);
        Assert.Contains("DeploymentFailed", status.Error);
        Assert.Contains("private endpoint failed", status.Error);
    }

    [Fact]
    public async Task PropagatesAzureHttpErrorStatusAndBody()
    {
        var handler = new CallbackHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("access denied")
        });
        var provisioner = CreateProvisioner(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provisioner.GetDeploymentStatusAsync("search-failed", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("access denied", exception.Message);
    }

    private static AzureResourceManagerProvisioner CreateProvisioner(HttpMessageHandler handler,
        FakeTokenCredential? credential = null, int prefixLength = 24, Guid? principalId = null)
    {
        var reader = principalId ?? Guid.Parse("f1d33e91-1111-4b4b-bbbb-123456789abc");
        var options = Options.Create(new ResourceManagerOptions
        {
            SubscriptionId = SubscriptionId,
            DeploymentResourceGroup = "search-resource-group",
            Location = "centralus",
            SearchServiceNamePrefix = "search-test",
            SubnetPrefixLength = prefixLength,
            ExistingVnetResourceGroup = "network-resource-group",
            ExistingVnetName = "shared-vnet",
            PrivateDnsZoneResourceGroup = "dns-resource-group",
            PrivateEndpointLocation = "westcentralus",
            PrincipalType = AzurePrincipalType.Group,
            IndexDataReaderPrincipalIds = [reader]
        });
        return new AzureResourceManagerProvisioner(new HttpClient(handler),
            credential ?? new FakeTokenCredential(), options,
            NullLogger<AzureResourceManagerProvisioner>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public CallbackHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback)
            : this((request, token) => Task.FromResult(callback(request, token)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.Add(requestContext.Scopes.ToArray());
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Scopes.Add(requestContext.Scopes.ToArray());
            return ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
