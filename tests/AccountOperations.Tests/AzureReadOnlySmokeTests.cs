using AccountOperations;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AccountOperations.Tests;

public sealed class AzureReadOnlySmokeTests
{
    [Fact]
    [Trait("Category", "AzureSmoke")]
    public async Task TestDependenciesAcceptConfiguredCredentials()
    {
        if (Environment.GetEnvironmentVariable("RUN_AZURE_TEST_SMOKE") != "1") return;

        var builder = Host.CreateApplicationBuilder();
        Settings.Configure(builder.Configuration);
        Assert.Equal("account-operations", Settings.Required("ACCOUNT_EVENTHUB_NAME"));
        Assert.Equal("email-operations", Settings.Required("EMAIL_EVENTHUB_NAME"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var graph = new GraphClient(http);
        var sharePoint = new SharePointClient(http, graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var site = await sharePoint.SendAsync(HttpMethod.Get,
            $"https://{Settings.Required("SHAREPOINT_HOST")}",
            "_api/web?$select=Title", null, timeout.Token);
        Assert.NotNull(site["d"]?["Title"]);

        var graphSite = await graph.GetAsync(
            $"sites/{Settings.Required("SHAREPOINT_HOST")}?$select=sharepointIds", timeout.Token);
        var sharePointSite = await sharePoint.SendAsync(HttpMethod.Get,
            $"https://{Settings.Required("SHAREPOINT_HOST")}", "_api/site?$select=Id",
            null, timeout.Token);
        Assert.Equal(graphSite["sharepointIds"]?["siteId"]?.Value<string>(),
            sharePointSite["d"]?["Id"]?.Value<string>(), ignoreCase: true);

        var tenant = await sharePoint.TenantAsync(HttpMethod.Get, "_api/SPO.Tenant", null,
            timeout.Token);
        Assert.NotNull(tenant["d"]);

        using var accounts = new AccountStore();
        var root = "00000000-0000-0000-0000-000000000000";
        await accounts.QueryAsync("accounts", "SELECT TOP 1 * FROM c WHERE c.accountId = '" +
            root + "'", root, timeout.Token);
    }
}
