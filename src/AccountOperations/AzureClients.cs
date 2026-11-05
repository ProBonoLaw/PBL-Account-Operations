using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AccountOperations;

public sealed class AccountStore : IDisposable, IAccountStore
{
    private readonly CosmosClient client = new(Settings.Required("COSMOS_CONNECTION_STRING"));

    private Container Container(string name) =>
        client.GetContainer(Settings.Required("COSMOS_DATABASE"), name);

    public async Task<List<JObject>> QueryAsync(string containerName, string query,
        string? partitionKey, CancellationToken token)
    {
        var options = new QueryRequestOptions();
        if (partitionKey is not null) options.PartitionKey = new PartitionKey(partitionKey);
        using var iterator = Container(containerName).GetItemQueryIterator<JObject>(
            new QueryDefinition(query), requestOptions: options);
        var items = new List<JObject>();
        while (iterator.HasMoreResults)
            items.AddRange(await iterator.ReadNextAsync(token));
        return items;
    }

    public async Task<List<JObject>> QueryAsync(string containerName, QueryDefinition query,
        string? partitionKey, CancellationToken token)
    {
        var options = new QueryRequestOptions();
        if (partitionKey is not null) options.PartitionKey = new PartitionKey(partitionKey);
        using var iterator = Container(containerName).GetItemQueryIterator<JObject>(query,
            requestOptions: options);
        var items = new List<JObject>();
        while (iterator.HasMoreResults)
            items.AddRange(await iterator.ReadNextAsync(token));
        return items;
    }

    public async Task UpsertAsync(string containerName, JObject item,
        string partitionKey, CancellationToken token) =>
        await Container(containerName).UpsertItemAsync(item, new PartitionKey(partitionKey),
            cancellationToken: token);

    public async Task CreateAsync(string containerName, JObject item,
        string partitionKey, CancellationToken token) =>
        await Container(containerName).CreateItemAsync(item, new PartitionKey(partitionKey),
            cancellationToken: token);

    public async Task PatchAsync(string containerName, string id,
        string partitionKey, IReadOnlyList<PatchOperation> operations, CancellationToken token) =>
        await Container(containerName).PatchItemAsync<JObject>(id, new PartitionKey(partitionKey),
            operations, cancellationToken: token);

    public void Dispose() => client.Dispose();
}

public sealed class GraphClient(HttpClient http) : IGraphClient
{
    private ClientSecretCredential? credential;
    private ClientCertificateCredential? sharePointCredential;
    private readonly SemaphoreSlim credentialLock = new(1, 1);

    private async Task<ClientSecretCredential> CredentialAsync(CancellationToken token)
    {
        if (credential is not null) return credential;
        await credentialLock.WaitAsync(token);
        try
        {
            if (credential is not null) return credential;
            var secretClient = new SecretClient(new Uri(Settings.Required("GRAPH_KEYVAULT_URI")),
                Settings.Credential);
            var secret = (await secretClient.GetSecretAsync(Settings.Required("GRAPH_SECRET_NAME"),
                cancellationToken: token)).Value.Value;
            return credential = new ClientSecretCredential(Settings.Required("GRAPH_TENANT_ID"),
                Settings.Required("GRAPH_CLIENT_ID"), secret);
        }
        finally { credentialLock.Release(); }
    }

    public async Task<string> TokenAsync(string scope, CancellationToken token) =>
        (await (await CredentialAsync(token)).GetTokenAsync(new TokenRequestContext([scope]),
            token)).Token;

    public async Task<string> SharePointTokenAsync(string scope, CancellationToken token)
    {
        if (sharePointCredential is null)
        {
            await credentialLock.WaitAsync(token);
            try
            {
                if (sharePointCredential is null)
                {
                    var secretClient = new SecretClient(
                        new Uri(Settings.Required("SHAREPOINT_CERTIFICATE_VAULT_URI")),
                        Settings.Credential);
                    var secret = (await secretClient.GetSecretAsync(
                        Settings.Required("SHAREPOINT_CERTIFICATE_SECRET_NAME"),
                        cancellationToken: token)).Value.Value;
                    var certificate = X509CertificateLoader.LoadPkcs12(
                        Convert.FromBase64String(secret), null, X509KeyStorageFlags.EphemeralKeySet);
                    sharePointCredential = new ClientCertificateCredential(
                        Settings.Required("GRAPH_TENANT_ID"),
                        Settings.Required("GRAPH_CLIENT_ID"), certificate);
                }
            }
            finally { credentialLock.Release(); }
        }
        return (await sharePointCredential.GetTokenAsync(new TokenRequestContext([scope]),
            token)).Token;
    }

    public async Task<JObject> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken token)
    {
        var uri = new Uri(new Uri("https://graph.microsoft.com/v1.0/"), path.TrimStart('/'));
        if (uri.Host != "graph.microsoft.com" || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Graph URL must use graph.microsoft.com", nameof(path));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await TokenAsync("https://graph.microsoft.com/.default", token));
        if (body is not null)
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8,
                "application/json");
        using var response = await http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Graph {method} {path} returned {(int)response.StatusCode}: {text}",
                null, response.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    public Task<JObject> GetAsync(string path, CancellationToken token) =>
        SendAsync(HttpMethod.Get, path, null, token);

    public Task<JObject> PostAsync(string path, object body, CancellationToken token) =>
        SendAsync(HttpMethod.Post, path, body, token);

    public Task<JObject> PatchAsync(string path, object body, CancellationToken token) =>
        SendAsync(HttpMethod.Patch, path, body, token);

    public Task<JObject> DeleteAsync(string path, CancellationToken token) =>
        SendAsync(HttpMethod.Delete, path, null, token);

    public async Task<JObject?> FindUserByEmailAsync(string email, CancellationToken token)
    {
        var literal = email.Replace("'", "''", StringComparison.Ordinal);
        var result = await GetAsync($"users?$filter={Uri.EscapeDataString($"mail eq '{literal}'")}", token);
        return result["value"]?.FirstOrDefault() as JObject;
    }

    public async Task<string> SharePointSiteIdAsync(string host, string accountId,
        CancellationToken token)
    {
        var site = await GetAsync(
            $"sites/{host}:/sites/{Uri.EscapeDataString(accountId)}?$select=sharepointIds", token);
        return site["sharepointIds"]?["siteId"]?.Value<string>() ??
            throw new InvalidDataException("Graph site returned no SharePoint site ID");
    }
}

public sealed class SharePointClient(HttpClient http, IGraphClient graph) : ISharePointClient
{
    private string Host => Settings.Required("SHAREPOINT_HOST");
    public string SiteUrl(string accountId) => $"https://{Host}/sites/{Uri.EscapeDataString(accountId)}";

    public async Task<JObject> SendAsync(HttpMethod method, string baseUrl, string path,
        object? body, CancellationToken token, bool merge = false, bool verbose = false)
    {
        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SharePoint URL must use sharepoint.com", nameof(baseUrl));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await graph.SharePointTokenAsync($"https://{uri.Host}/.default", token));
        request.Headers.Accept.ParseAdd("application/json;odata=verbose");
        if (merge)
        {
            request.Headers.TryAddWithoutValidation("X-HTTP-Method", "MERGE");
            request.Headers.TryAddWithoutValidation("If-Match", "*");
        }
        if (body is not null)
        {
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8,
                "application/json");
            if (verbose)
                request.Content.Headers.ContentType!.Parameters.Add(
                    new NameValueHeaderValue("odata", "verbose"));
        }
        using var response = await http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SharePoint {method} {uri.AbsolutePath} returned " +
                $"{(int)response.StatusCode}: {text}", null, response.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    public Task<JObject> SiteAsync(HttpMethod method, string accountId, string path,
        object? body, CancellationToken token, bool merge = false, bool verbose = false) =>
        SendAsync(method, SiteUrl(accountId), path, body, token, merge, verbose);

    public Task<JObject> TenantAsync(HttpMethod method, string path, object? body,
        CancellationToken token, bool merge = false, bool verbose = false) =>
        SendAsync(method, $"https://{Host.Replace(".sharepoint.com", "-admin.sharepoint.com",
            StringComparison.OrdinalIgnoreCase)}", path, body, token, merge, verbose);

    public async Task<int> EnsureUserAsync(string accountId, string principal,
        CancellationToken token)
    {
        var response = await SiteAsync(HttpMethod.Post, accountId, "_api/web/ensureuser",
            new { logonName = $"i:0#.f|membership|{principal}" }, token);
        return response["d"]?["Id"]?.Value<int>() ??
            throw new InvalidDataException("SharePoint ensureuser returned no user ID");
    }

    public async Task SetSiteAdminAsync(string accountId, int userId, bool isAdmin,
        CancellationToken token) =>
        await SiteAsync(HttpMethod.Post, accountId, $"_api/web/siteusers/getbyid({userId})",
            new { __metadata = new { type = "SP.User" }, IsSiteAdmin = isAdmin }, token,
            merge: true, verbose: true);
}

public sealed class FunctionClient(HttpClient http, ILogger<FunctionClient> logger) : IFunctionClient
{
    private readonly HttpOrchestrationClient orchestration = new(http, logger,
        new HttpOrchestrationOptions { Timeout = TimeSpan.FromHours(2) });

    public async Task<System.Text.Json.JsonElement> CallAsync(string prefix, HttpMethod method,
        object? body, bool wait, CancellationToken token)
    {
        var endpoint = new Uri(Settings.Required(prefix + "_FUNCTION_URL"));
        var key = Settings.Get(prefix + "_FUNCTION_API_KEY");
        IReadOnlyDictionary<string, string>? headers = string.IsNullOrWhiteSpace(key) ? null :
            new Dictionary<string, string> { ["x-functions-key"] = key };
        var response = wait
            ? await orchestration.SendAndWaitForCompletionAsync(method, endpoint, body, prefix,
                headers, token, retryCount: 3, retryDelay: TimeSpan.FromSeconds(2))
            : await orchestration.SendAsync(method, endpoint, body, prefix,
                headers, token, retryCount: 3, retryDelay: TimeSpan.FromSeconds(2));
        return response ?? throw new InvalidDataException($"{prefix} Function returned no response");
    }

    public async Task NotifyAsync(object body, CancellationToken token)
    {
        var endpoint = new Uri(Settings.Required("NOTIFICATION_SERVICE_URL"));
        // NOTIFICATION_SERVICE_URL points to the internal AKS notification service, which is
        // exposed only over cluster-local HTTP.
        try
        {
            await orchestration.SendAsync(HttpMethod.Post, endpoint, body, "NOTIFICATION",
                null, token, retryCount: 3, retryDelay: TimeSpan.FromSeconds(2));
        }
        catch (HttpRequestException error)
        {
            throw new HttpRequestException($"Notification POST {endpoint} failed: {error.Message}",
                error, error.StatusCode);
        }
    }
}
