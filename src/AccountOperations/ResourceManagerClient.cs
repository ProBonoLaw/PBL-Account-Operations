using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AccountOperations;

public sealed class ResourceManagerClient(HttpClient http, ILogger<ResourceManagerClient> logger)
    : IResourceManagerClient
{
    public async Task<ResourceManagerSearchInstance> GetSearchInstanceAsync(Guid accountId,
        CancellationToken token)
    {
        var baseUrl = Settings.Required("RESOURCE_MANAGER_URL");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) ||
            (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(serviceUri.UserInfo) ||
            !string.IsNullOrEmpty(serviceUri.Query) || !string.IsNullOrEmpty(serviceUri.Fragment))
            throw new InvalidOperationException("RESOURCE_MANAGER_URL must be an HTTP(S) service URL without credentials, query, or fragment.");

        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/getSearchInstance");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.TryAddWithoutValidation("x-functions-key", Settings.Required("RESOURCE_MANAGER_API_KEY"));
        message.Content = JsonContent.Create(new GetSearchInstanceRequest(accountId));
        logger.LogInformation("Calling Resource Manager for account {AccountId}", accountId);

        using var response = await http.SendAsync(message, token);
        var responseBody = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            var operationId = ReadOperationId(responseBody);
            logger.LogError(
                "Resource Manager returned HTTP {StatusCode} for account {AccountId}, operation {OperationId}",
                (int)response.StatusCode, accountId, operationId);
            throw new HttpRequestException(
                $"Resource Manager failed for account {accountId:D}, operation {operationId?.ToString("D") ?? "unknown"}, with HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<GetSearchInstanceResponse>(responseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (result is null || result.OperationId == Guid.Empty || result.AccountId != accountId ||
            result.SearchEndpoint is null || !result.SearchEndpoint.IsAbsoluteUri ||
            result.SearchEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Resource Manager returned an invalid search instance response.");

        logger.LogInformation("Resource Manager completed for account {AccountId}, operation {OperationId}, endpoint {SearchEndpoint}",
            accountId, result.OperationId, result.SearchEndpoint);
        return new ResourceManagerSearchInstance(result.OperationId, result.SearchEndpoint);
    }

    private static Guid? ReadOperationId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("operationId", out var value) &&
                   value.TryGetGuid(out var operationId) ? operationId : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GetSearchInstanceRequest(Guid AccountId);

    private sealed record GetSearchInstanceResponse(Guid OperationId, Guid AccountId, Uri? SearchEndpoint);
}
