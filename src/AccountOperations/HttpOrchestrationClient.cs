using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AccountOperations;

internal sealed class HttpOrchestrationOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed class HttpOrchestrationClient(
    HttpClient client, ILogger? logger = null, HttpOrchestrationOptions? options = null)
{
    private readonly HttpOrchestrationOptions settings = options ?? new();

    public async Task<JsonElement?> SendAsync(HttpMethod method, Uri endpoint, object? body,
        string? operation, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken,
        int retryCount = 0, TimeSpan? retryDelay = null)
    {
        ValidateEndpoint(endpoint);
        using var response = await SendResponseAsync(method, endpoint, body, headers,
            cancellationToken, retryCount, retryDelay ?? TimeSpan.FromSeconds(2));
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken);
    }

    public async Task<JsonElement?> SendAndWaitForCompletionAsync(HttpMethod method, Uri endpoint,
        object? body, string? operation, IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken, int retryCount = 0, TimeSpan? retryDelay = null)
    {
        ValidateEndpoint(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var token = timeout.Token;
        using var initial = await SendResponseAsync(method, endpoint, body, headers,
            token, retryCount, retryDelay ?? TimeSpan.FromSeconds(2));
        initial.EnsureSuccessStatusCode();
        var initialJson = await ReadJsonAsync(initial, token);
        if (initial.StatusCode != HttpStatusCode.Accepted)
            return CheckCompletion(initialJson, operation);

        var statusUrl = StatusUrl(initial, initialJson);
        if (statusUrl is null)
            throw new InvalidDataException("HTTP 202 without a statusQueryGetUri or Location header.");
        if (statusUrl.Scheme != endpoint.Scheme || statusUrl.Authority != endpoint.Authority ||
            statusUrl.UserInfo.Length != 0)
            throw new InvalidDataException("The operation returned an invalid status URL.");

        var delay = RetryAfter(initial) ?? settings.PollingInterval;
        while (true)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            using var status = await SendResponseAsync(HttpMethod.Get, statusUrl, null, headers,
                token, retryCount, retryDelay ?? TimeSpan.FromSeconds(2));
            status.EnsureSuccessStatusCode();
            var result = await ReadJsonAsync(status, token);
            if (result is { } document && document.ValueKind == JsonValueKind.Object &&
                document.TryGetProperty("runtimeStatus", out var state))
            {
                var value = state.GetString();
                if (value is "Failed" or "Terminated" or "Canceled")
                    throw new InvalidDataException($"{operation ?? "Operation"} ended with status {value}.");
                if (value == "Completed") return result;
                if (value is "Running" or "Pending" or "ContinuedAsNew")
                {
                    delay = RetryAfter(status) ?? settings.PollingInterval;
                    continue;
                }
            }
            if (status.StatusCode == HttpStatusCode.Accepted)
            {
                delay = RetryAfter(status) ?? settings.PollingInterval;
                continue;
            }
            return result;
        }
    }

    private async Task<HttpResponseMessage> SendResponseAsync(HttpMethod method, Uri endpoint,
        object? body, IReadOnlyDictionary<string, string>? headers, CancellationToken token,
        int retryCount, TimeSpan retryDelay)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, endpoint);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (headers is not null)
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            try
            {
                var response = await client.SendAsync(request, token);
                if (attempt >= retryCount || !IsTransient(response.StatusCode)) return response;
                var delay = RetryAfter(response) ?? retryDelay;
                response.Dispose();
                logger?.LogWarning("Retrying HTTP {Method} after transient response", method);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
            catch (HttpRequestException) when (attempt < retryCount)
            {
                if (retryDelay > TimeSpan.Zero) await Task.Delay(retryDelay, token);
            }
        }
    }

    private static JsonElement? CheckCompletion(JsonElement? result, string? operation)
    {
        if (result is { } document && document.ValueKind == JsonValueKind.Object &&
            document.TryGetProperty("runtimeStatus", out var state) &&
            state.GetString() is "Failed" or "Terminated" or "Canceled")
            throw new InvalidDataException($"{operation ?? "Operation"} ended with status {state.GetString()}.");
        return result;
    }

    private static Uri? StatusUrl(HttpResponseMessage response, JsonElement? body)
    {
        if (body is { } document && document.ValueKind == JsonValueKind.Object &&
            document.TryGetProperty("statusQueryGetUri", out var property) &&
            Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri)) return uri;
        return response.Headers.Location;
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response,
        CancellationToken token)
    {
        var content = await response.Content.ReadAsStringAsync(token);
        if (string.IsNullOrWhiteSpace(content)) return null;
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ??
        (response.Headers.RetryAfter?.Date is { } date
            ? TimeSpan.FromTicks(Math.Max(0, (date - DateTimeOffset.UtcNow).Ticks)) : null);

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0)
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URL without credentials.",
                nameof(endpoint));
    }
}
