using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.Api;
using ResourceManager.Services;
using Xunit;

namespace ResourceManager.Tests;

public sealed class ResourceManagerApiTests
{
    private const string AdminKey = "test-admin-api-key-at-least-32-characters";

    [Fact]
    public async Task PostGetSearchInstanceRejectsRequestsWithoutAdminKey()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/getSearchInstance", ValidRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Workflow.Calls);
    }

    [Fact]
    public async Task PostGetSearchInstanceRejectsInvalidAdminKey()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", "incorrect-key");

        var response = await client.PostAsJsonAsync("/api/getSearchInstance", ValidRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Workflow.Calls);
    }

    [Fact]
    public async Task PostGetSearchInstanceCallsWorkflowAndReturnsEndpointWithTraceableOperationId()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        var response = await client.PostAsJsonAsync("/api/getSearchInstance", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Workflow.Calls);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operationId = body.RootElement.GetProperty("operationId").GetGuid();
        Assert.Equal(factory.Workflow.OperationId, operationId);
        Assert.Equal(factory.Workflow.Endpoint,
            new Uri(body.RootElement.GetProperty("searchEndpoint").GetString()!));
        Assert.Equal(ValidRequest().AccountId,
            body.RootElement.GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task PostGetSearchInstanceReturnsSupportOperationIdOnWorkflowFailure()
    {
        using var factory = new ApiFactory();
        factory.Workflow.Exception = new InvalidOperationException("deployment error details");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        var response = await client.PostAsJsonAsync("/api/getSearchInstance", ValidRequest());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(factory.Workflow.OperationId, body.RootElement.GetProperty("operationId").GetGuid());
        Assert.DoesNotContain("deployment error details", body.RootElement.GetRawText());
    }

    [Fact]
    public async Task PostGetSearchInstanceRejectsEmptyAccountId()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        var response = await client.PostAsJsonAsync("/api/getSearchInstance",
            new GetSearchInstanceRequest(Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Workflow.Calls);
    }

    [Fact]
    public async Task NoDurableOperationStatusRouteIsExposed()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/operations/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ServiceIpEndpointRequiresAdminKeyBeforeResolvingDns()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/getServiceIp", new { serviceName = "search-test" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.AddressResolver.Calls);
    }

    [Fact]
    public async Task ServiceIpEndpointReturnsResolvedPrivateAndPublicAddresses()
    {
        using var factory = new ApiFactory();
        factory.AddressResolver.Addresses = [IPAddress.Parse("10.1.2.3"), IPAddress.Parse("8.8.8.8")];
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        var response = await client.PostAsJsonAsync("/api/getServiceIp", new { serviceName = "search-test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("search-test.search.windows.net", factory.AddressResolver.Host);
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Operation-Id").Single(), out _));
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lookup = result.GetProperty("lookup").EnumerateArray().ToArray();
        Assert.Equal(2, lookup.Length);
        Assert.True(lookup[0].GetProperty("private").GetBoolean());
        Assert.False(lookup[1].GetProperty("private").GetBoolean());
    }

    [Theory]
    [InlineData("bad_name")]
    [InlineData("a-123")]
    [InlineData("aa--bb")]
    public async Task ServiceIpEndpointRejectsInvalidNames(string serviceName)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        var invalid = await client.PostAsJsonAsync("/api/getServiceIp", new { serviceName });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, factory.AddressResolver.Calls);
    }

    [Fact]
    public async Task ServiceIpEndpointReturnsBadGatewayForDnsErrors()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);

        factory.AddressResolver.ThrowSocketException = true;
        var unresolved = await client.PostAsJsonAsync("/api/getServiceIp", new { serviceName = "search-test" });
        Assert.Equal(HttpStatusCode.BadGateway, unresolved.StatusCode);
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public FakeResourceManagerWorkflow Workflow { get; } = new();
        public FakeHostAddressResolver AddressResolver { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ResourceManager:SubscriptionId"] = "11111111-2222-4333-8444-555555555555",
                    ["ResourceManager:DeploymentResourceGroup"] = "search-resources",
                    ["ResourceManager:Location"] = "centralus",
                    ["ResourceManager:SearchServiceNamePrefix"] = "search-test",
                    ["ResourceManager:ExistingVnetResourceGroup"] = "networking",
                    ["ResourceManager:ExistingVnetName"] = "shared-vnet",
                    ["ResourceManager:PrivateDnsZoneResourceGroup"] = "dns-zones",
                    ["ResourceManager:PrivateEndpointLocation"] = "westcentralus",
                    ["ResourceManager:AdminApiKey"] = AdminKey
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IResourceManagerWorkflow>();
                services.AddSingleton<IResourceManagerWorkflow>(Workflow);
                services.RemoveAll<IHostAddressResolver>();
                services.AddSingleton<IHostAddressResolver>(AddressResolver);
            });
        }
    }

    private static GetSearchInstanceRequest ValidRequest() =>
        new(Guid.Parse("22222222-3333-4444-8555-666666666666"));
}
