using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharePointSiteStarter.Configuration;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Services;

namespace SharePointSiteStarter.Api;

public static class SharePointSiteStarterEndpoints
{
    public static IEndpointRouteBuilder MapSharePointSiteStarterEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/start", StartAsync);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        StartSiteRequest? request,
        HttpRequest httpRequest,
        IOptions<SharePointSiteStarterOptions> options,
        StartSiteRequestValidator requestValidator,
        ISharePointSiteStarterWorkflow workflow,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var logger = loggerFactory.CreateLogger("SharePointSiteStarter.Api");
        httpRequest.HttpContext.Response.Headers["X-Operation-Id"] = operationId.ToString("D");
        using var operationScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["OperationId"] = operationId,
            ["AccountId"] = request?.AccountId ?? Guid.Empty
        });
        using var activity = SharePointSiteStarterTelemetry.ActivitySource.StartActivity("sharepoint.site_starter");
        activity?.SetTag("resource.operation_id", operationId);
        if (request is not null)
            activity?.SetTag("account.id", request.AccountId);
        logger.LogInformation("SharePoint site starter request received for operation {OperationId}, account {AccountId}",
            operationId, request?.AccountId);

        if (!HasValidAdminKey(httpRequest, options.Value.AdminApiKey))
        {
            logger.LogWarning("SharePoint site starter request rejected for missing or invalid admin key; operation {OperationId}, request {RequestTraceId}",
                operationId, httpRequest.HttpContext.TraceIdentifier);
            return Results.Unauthorized();
        }

        var startedAt = timeProvider.GetTimestamp();
        SharePointSiteStarterTelemetry.OperationsStarted.Add(1);

        try
        {
            var errors = requestValidator.Validate(request);
            if (errors.Count > 0)
            {
                logger.LogWarning("SharePoint site starter request validation failed for operation {OperationId}, account {AccountId}: {ValidationErrors}",
                    operationId, request?.AccountId, string.Join("; ", errors.SelectMany(error =>
                        error.Value.Select(message => $"{error.Key}: {message}"))));
                SharePointSiteStarterTelemetry.OperationsFailed.Add(1);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Invalid request");
                return Results.ValidationProblem(errors.ToDictionary(error => error.Key, error => error.Value));
            }

            await workflow.StartAsync(operationId, request!, cancellationToken);
            SharePointSiteStarterTelemetry.OperationsSucceeded.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            logger.LogInformation("SharePoint site starter request succeeded for operation {OperationId}, account {AccountId}",
                operationId, request!.AccountId);
            return Results.Ok(new StartSiteResponse(operationId, request.AccountId,
                request.SharePointSiteUri!, "Completed"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SharePointSiteStarterTelemetry.OperationsFailed.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Request canceled");
            logger.LogWarning("SharePoint site starter request canceled for operation {OperationId}, account {AccountId}",
                operationId, request!.AccountId);
            throw;
        }
        catch (Exception exception)
        {
            SharePointSiteStarterTelemetry.OperationsFailed.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            logger.LogError(exception, "SharePoint site starter request failed for operation {OperationId}, account {AccountId}",
                operationId, request!.AccountId);
            return Results.Problem(
                title: "SharePoint site preparation failed.",
                detail: $"Contact support with operation ID {operationId:D} and account ID {request.AccountId:D}.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["operationId"] = operationId });
        }
        finally
        {
            SharePointSiteStarterTelemetry.OperationDuration.Record(
                timeProvider.GetElapsedTime(startedAt).TotalSeconds);
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
