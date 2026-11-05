using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Services;
using Xunit;

namespace SharePointSiteStarter.Tests;

public sealed class ApiTests
{
    private const string AdminKey = "a-sharepoint-site-starter-key-with-32-characters";

    [Fact]
    public async Task StartEndpointRejectsMissingOrInvalidAdminKey()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var missing = await client.PostAsJsonAsync("/api/start", ValidRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        client.DefaultRequestHeaders.Add("x-functions-key", "incorrect");
        var invalid = await client.PostAsJsonAsync("/api/start", ValidRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(0, factory.Workflow.Calls);
    }

    [Fact]
    public async Task StartEndpointRejectsRequestsForAnotherSharePointHost()
    {
        using var factory = new ApiFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/api/start", ValidRequest() with
        {
            SharePointSiteUri = new Uri("https://attacker.example/sites/22222222-3333-4444-8555-666666666666")
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Workflow.Calls);
    }

    [Fact]
    public async Task StartEndpointReturnsCorrelatedOperationIdAfterProvisioning()
    {
        using var factory = new ApiFactory();
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/api/start", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Operation-Id").Single(), out _));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operationId = body.RootElement.GetProperty("operationId").GetGuid();
        Assert.Equal(operationId.ToString("D"), response.Headers.GetValues("X-Operation-Id").Single());
        Assert.Equal(factory.Workflow.OperationId, operationId);
        Assert.Equal(ValidRequest().AccountId,
            body.RootElement.GetProperty("accountId").GetGuid());
        Assert.Equal("Completed", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartEndpointHidesProvisioningDetailsButReturnsSupportOperationId()
    {
        using var factory = new ApiFactory();
        factory.Workflow.Exception = new InvalidOperationException("sensitive SharePoint response");
        using var client = AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/api/start", ValidRequest());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(factory.Workflow.OperationId,
            body.RootElement.GetProperty("operationId").GetGuid());
        Assert.DoesNotContain("sensitive SharePoint response", body.RootElement.GetRawText());
    }

    [Fact]
    public async Task HealthEndpointsDoNotRequireAdminAuthentication()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private static HttpClient AuthenticatedClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-functions-key", AdminKey);
        return client;
    }

    private static StartSiteRequest ValidRequest() => new()
    {
        AccountId = Guid.Parse("22222222-3333-4444-8555-666666666666"),
        CaseNumber = "CASE-123",
        FirstName = "Taylor",
        LastName = "Smith",
        PreferredName = "Taylor",
        Email = "taylor@example.com",
        SharePointSiteUri = new Uri("https://tenant.example.sharepoint.com/sites/22222222-3333-4444-8555-666666666666")
    };

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public FakeWorkflow Workflow { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["SharePointSiteStarter:SharePointHost"] = "tenant.example.sharepoint.com",
                    ["SharePointSiteStarter:TenantId"] = "66666666-7777-4888-8999-aaaaaaaaaaaa",
                    ["SharePointSiteStarter:ClientId"] = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
                    ["SharePointSiteStarter:CertificateKeyVaultUri"] = "https://example.vault.azure.net",
                    ["SharePointSiteStarter:CertificateSecretName"] = "SharePointConditioner",
                    ["SharePointSiteStarter:AdminApiKey"] = AdminKey,
                    ["SharePointSiteStarter:DocumentsLibraryFolders:0"] = "Client Intake",
                    ["SharePointSiteStarter:DocumentsLibraryFolders:1"] = "Client Uploads"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISharePointSiteStarterWorkflow>();
                services.AddSingleton<ISharePointSiteStarterWorkflow>(Workflow);
            });
        }
    }

    private sealed class FakeWorkflow : ISharePointSiteStarterWorkflow
    {
        public Exception? Exception { get; set; }
        public Guid? OperationId { get; private set; }
        public int Calls { get; private set; }

        public Task StartAsync(Guid operationId, StartSiteRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            OperationId = operationId;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }
}
