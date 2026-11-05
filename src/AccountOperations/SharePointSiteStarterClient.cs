using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AccountOperations;

public sealed class SharePointSiteStarterClient(HttpClient http, ILogger<SharePointSiteStarterClient> logger)
    : ISharePointSiteStarterClient
{
    public async Task<Guid> StartAsync(SharePointSiteStarterRequest request, CancellationToken token)
    {
        var baseUrl = Settings.Required("SHAREPOINT_SITE_STARTER_URL");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) ||
            (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(serviceUri.UserInfo))
            throw new InvalidOperationException("SHAREPOINT_SITE_STARTER_URL must be an HTTP(S) URL without credentials.");

        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/start");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.TryAddWithoutValidation("x-functions-key", Settings.Required("SHAREPOINT_SITE_STARTER_API_KEY"));
        message.Content = JsonContent.Create(request);
        logger.LogInformation("Calling SharePoint Site Starter for account {AccountId}", request.AccountId);

        using var response = await http.SendAsync(message, token);
        var responseBody = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            var operationId = ReadOperationId(responseBody);
            logger.LogError(
                "SharePoint Site Starter returned HTTP {StatusCode} for account {AccountId}, operation {OperationId}",
                (int)response.StatusCode, request.AccountId, operationId);
            throw new HttpRequestException(
                $"SharePoint Site Starter failed for account {request.AccountId:D}, operation {operationId?.ToString("D") ?? "unknown"}, with HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<SharePointSiteStarterResult>(responseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (result is null || result.OperationId == Guid.Empty || result.AccountId != request.AccountId)
            throw new InvalidDataException("SharePoint Site Starter returned an invalid operation response.");

        logger.LogInformation("SharePoint Site Starter completed for account {AccountId}, operation {OperationId}",
            request.AccountId, result.OperationId);
        return result.OperationId;
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

    private sealed record SharePointSiteStarterResult(Guid OperationId, Guid AccountId,
        Uri SharePointSiteUri, string Status);
}
