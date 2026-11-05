using System.Net.Http;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace AccountOperations;

public interface IAccountStore
{
    Task<List<JObject>> QueryAsync(string containerName, string query,
        string? partitionKey, CancellationToken token);

    Task<List<JObject>> QueryAsync(string containerName, QueryDefinition query,
        string? partitionKey, CancellationToken token);

    Task CreateAsync(string containerName, JObject item, string partitionKey,
        CancellationToken token);

    Task UpsertAsync(string containerName, JObject item, string partitionKey,
        CancellationToken token);

    Task PatchAsync(string containerName, string id, string partitionKey,
        IReadOnlyList<PatchOperation> operations, CancellationToken token);
}

public interface IGraphClient
{
    Task<JObject?> FindUserByEmailAsync(string email, CancellationToken token);

    Task<string> SharePointTokenAsync(string scope, CancellationToken token);

    Task<string> SharePointSiteIdAsync(string host, string accountId, CancellationToken token);

    Task<JObject> GetAsync(string path, CancellationToken token);

    Task<JObject> PostAsync(string path, object body, CancellationToken token);

    Task<JObject> PatchAsync(string path, object body, CancellationToken token);

    Task<JObject> DeleteAsync(string path, CancellationToken token);
}

public interface ISharePointClient
{
    string SiteUrl(string accountId);

    Task<int> EnsureUserAsync(string accountId, string userPrincipalName, CancellationToken token);

    Task SetSiteAdminAsync(string accountId, int userId, bool isAdmin, CancellationToken token);

    Task<JObject> SiteAsync(HttpMethod method, string accountId, string path, object? body,
        CancellationToken token, bool merge = false, bool verbose = false);

    Task<JObject> TenantAsync(HttpMethod method, string path, object? body,
        CancellationToken token, bool merge = false, bool verbose = false);

    Task<JObject> SendAsync(HttpMethod method, string baseUrl, string path, object? body,
        CancellationToken token, bool merge = false, bool verbose = false);
}

public interface IFunctionClient
{
    Task<JsonElement> CallAsync(string prefix, HttpMethod method, object? body, bool wait,
        CancellationToken token);

    Task NotifyAsync(object body, CancellationToken token);
}

public interface IResourceManagerClient
{
    Task<ResourceManagerSearchInstance> GetSearchInstanceAsync(Guid accountId, CancellationToken token);
}

public sealed record ResourceManagerSearchInstance(Guid OperationId, Uri SearchEndpoint);

public interface ISharePointSiteStarterClient
{
    Task<Guid> StartAsync(SharePointSiteStarterRequest request, CancellationToken token);
}

public sealed record SharePointSiteStarterRequest(
    Guid AccountId,
    string CaseNumber,
    string FirstName,
    string LastName,
    string? PreferredName,
    string? Email,
    Uri SharePointSiteUri);

public interface IAccountEventPublisher : IAsyncDisposable
{
    Task PublishAccountAsync(object content, CancellationToken token);

    Task PublishCommunicationAsync(object content, CancellationToken token);
}
