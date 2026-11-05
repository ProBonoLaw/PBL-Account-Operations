using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ResourceManager.Configuration;
using ResourceManager.Domain;
using ResourceManager.Services;

namespace ResourceManager.Api;

public static class ResourceManagerEndpoints
{
    public static IEndpointRouteBuilder MapResourceManagerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/getSearchInstance", StartSearchInstanceAsync);
        endpoints.MapPost("/api/getServiceIp", GetServiceIpAsync);
        return endpoints;
    }

    private static async Task<IResult> StartSearchInstanceAsync(
        GetSearchInstanceRequest? searchRequest,
        HttpRequest httpRequest,
        IOptions<ResourceManagerOptions> options,
        IResourceManagerWorkflow workflow,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ResourceManager.SearchInstance");
        var operationId = Guid.NewGuid();
        var accountId = searchRequest?.AccountId ?? Guid.Empty;
        httpRequest.HttpContext.Response.Headers["X-Operation-Id"] = operationId.ToString("D");
        using var operationScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OperationId"] = operationId,
            ["AccountId"] = accountId
        });
        using var activity = ResourceManagerTelemetry.ActivitySource.StartActivity("resource.search_instance");
        activity?.SetTag("resource.operation_id", operationId);
        if (searchRequest is not null)
            activity?.SetTag("account.id", searchRequest.AccountId);
        if (!HasValidAdminKey(httpRequest, options.Value.AdminApiKey))
        {
            logger.LogWarning("Search instance request rejected because the admin key was missing or invalid; operation {OperationId}, request {RequestTraceId}",
                operationId, httpRequest.HttpContext.TraceIdentifier);
            return Results.Unauthorized();
        }

        if (searchRequest is null || searchRequest.AccountId == Guid.Empty)
        {
            logger.LogWarning("Search instance request rejected for invalid account ID; operation {OperationId}",
                operationId);
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["accountId"] = ["Provide a non-empty account ID."]
            });
        }

        var startedAt = timeProvider.GetTimestamp();
        ResourceManagerTelemetry.OperationsStarted.Add(1);
        logger.LogInformation("Search instance request started for account {AccountId}, operation {OperationId}",
            searchRequest.AccountId, operationId);

        try
        {
            var endpoint = await workflow.GetSearchEndpointAsync(operationId, cancellationToken);
            ResourceManagerTelemetry.OperationsSucceeded.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            logger.LogInformation("Search instance request completed for account {AccountId}, operation {OperationId} with endpoint {SearchEndpoint}",
                searchRequest.AccountId, operationId, endpoint);
            return Results.Ok(new SearchInstanceResponse(operationId, searchRequest.AccountId, endpoint));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResourceManagerTelemetry.OperationsFailed.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Request canceled");
            logger.LogWarning("Search instance request canceled for account {AccountId}, operation {OperationId}",
                searchRequest.AccountId, operationId);
            throw;
        }
        catch (Exception exception)
        {
            ResourceManagerTelemetry.OperationsFailed.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            logger.LogError(exception, "Search instance request failed for account {AccountId}, operation {OperationId}",
                searchRequest.AccountId, operationId);
            return Results.Problem(
                title: "Search instance provisioning failed.",
                detail: $"Contact support with operation ID {operationId:D} and account ID {searchRequest.AccountId:D}.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["operationId"] = operationId });
        }
        finally
        {
            ResourceManagerTelemetry.OperationDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalSeconds);
            logger.LogInformation("Search instance request finished for operation {OperationId} after {DurationMilliseconds} ms",
                operationId, timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private static async Task<IResult> GetServiceIpAsync(
        GetServiceIpRequest request,
        HttpRequest httpRequest,
        IOptions<ResourceManagerOptions> options,
        IHostAddressResolver resolver,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var logger = loggerFactory.CreateLogger("ResourceManager.SearchServiceIp");
        var startedAt = timeProvider.GetTimestamp();
        httpRequest.HttpContext.Response.Headers["X-Operation-Id"] = operationId.ToString("D");
        using var operationScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OperationId"] = operationId
        });
        using var activity = ResourceManagerTelemetry.ActivitySource.StartActivity("resource.search_service_ip");
        activity?.SetTag("resource.operation_id", operationId);
        logger.LogInformation("Search service IP lookup started for operation {OperationId}", operationId);
        if (!HasValidAdminKey(httpRequest, options.Value.AdminApiKey))
        {
            logger.LogWarning(
                "Search service IP lookup rejected because the admin key was missing or invalid for operation {OperationId}; request {RequestTraceId}",
                operationId, httpRequest.HttpContext.TraceIdentifier);
            logger.LogInformation("Search service IP lookup finished for operation {OperationId} after {DurationMilliseconds} ms",
                operationId, timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return Results.Unauthorized();
        }
        var serviceName = request?.ServiceName;
        if (!SearchServiceName.IsValid(serviceName ?? ""))
        {
            logger.LogWarning("Search service IP lookup rejected invalid service name {SearchServiceName} for operation {OperationId}",
                serviceName, operationId);
            logger.LogInformation("Search service IP lookup finished for operation {OperationId} after {DurationMilliseconds} ms",
                operationId, timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["serviceName"] = ["Provide a valid Azure AI Search service name."]
            });
        }

        var host = $"{serviceName}.search.windows.net";
        activity?.SetTag("server.address", host);
        try
        {
            logger.LogInformation("Resolving Search service host {SearchHost} for operation {OperationId}", host,
                operationId);
            var addresses = await resolver.ResolveAsync(host, cancellationToken);
            var lookup = addresses.Select(address => new ServiceIpAddress(address.ToString(),
                IpAddressClassifier.IsPrivate(address.ToString()))).ToArray();
            logger.LogInformation(
                "Search service IP lookup completed for operation {OperationId}, host {SearchHost}, resolved {AddressCount} addresses: {ResolvedAddresses}",
                operationId, host, lookup.Length,
                string.Join(", ", lookup.Select(address => $"{address.Address} (private={address.Private})")));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return Results.Ok(new ServiceIpResponse(host, lookup));
        }
        catch (SocketException exception)
        {
            logger.LogWarning(exception, "Search service host resolution failed for operation {OperationId}, host {SearchHost}",
                operationId, host);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            return Results.Problem("The Search service host could not be resolved.", statusCode: 502);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Search service IP lookup canceled for operation {OperationId}, host {SearchHost}",
                operationId, host);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Request canceled");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Search service IP lookup failed for operation {OperationId}, host {SearchHost}",
                operationId, host);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
        finally
        {
            logger.LogInformation("Search service IP lookup finished for operation {OperationId} after {DurationMilliseconds} ms",
                operationId, timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private static bool HasValidAdminKey(HttpRequest request, string configuredKey)
    {
        if (!request.Headers.TryGetValue("x-functions-key", out var provided) || provided.Count != 1)
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        var providedBytes = Encoding.UTF8.GetBytes(provided[0] ?? "");
        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

public sealed record GetServiceIpRequest(string? ServiceName);

public sealed record GetSearchInstanceRequest(Guid AccountId);

public sealed record SearchInstanceResponse(Guid OperationId, Guid AccountId, Uri SearchEndpoint);

public sealed record ServiceIpResponse(string Host, IReadOnlyList<ServiceIpAddress> Lookup);

public sealed record ServiceIpAddress(string Address, bool Private);

public interface IHostAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class HostAddressResolver : IHostAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}
