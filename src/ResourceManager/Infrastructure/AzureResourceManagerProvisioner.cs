using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Options;
using ResourceManager.Configuration;
using ResourceManager.Domain;

namespace ResourceManager.Infrastructure;

public sealed class AzureResourceManagerProvisioner(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<ResourceManagerOptions> options,
    ILogger<AzureResourceManagerProvisioner> logger) : IResourceManagerProvisioner
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string SearchScope = "https://search.azure.com/.default";
    private const string ArmApiVersion = "2025-05-01";
    private const string NetworkApiVersion = "2025-01-01";
    private const string DeploymentApiVersion = "2025-04-01";
    private const string SearchStatsApiVersion = "2025-09-01";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ResourceManagerOptions settings = options.Value;

    public async Task<IReadOnlyList<SearchServiceCapacity>> ListSearchServicesAsync(
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Listing managed Azure AI Search services in resource group {DeploymentResourceGroup}",
            settings.DeploymentResourceGroup);
        var subscription = Uri.EscapeDataString(settings.SubscriptionId.ToString());
        var resourceGroup = Uri.EscapeDataString(settings.DeploymentResourceGroup);
        var next = new Uri($"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/Microsoft.Search/searchServices?api-version={ArmApiVersion}");
        var services = new List<SearchServiceCapacity>();
        var pageCount = 0;

        while (next is not null)
        {
            pageCount++;
            var response = await SendArmAsync<ArmListResponse<SearchServiceResource>>(HttpMethod.Get, next,
                null, cancellationToken);
            foreach (var service in response.Value)
            {
                if (string.IsNullOrWhiteSpace(service.Id) || string.IsNullOrWhiteSpace(service.Name))
                    continue;
                if (!service.Name.StartsWith($"{settings.SearchServiceNamePrefix}-",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var serviceResourceGroup = ParseResourceGroup(service.Id);
                var endpoint = new Uri($"https://{service.Properties?.HostName ?? $"{service.Name}.search.windows.net"}");
                var statsUri = new Uri(endpoint,
                    $"servicestats?api-version={SearchStatsApiVersion}");
                logger.LogInformation("Reading Azure AI Search capacity for service {SearchServiceName}",
                    service.Name);
                var stats = await SendSearchAsync<SearchServiceStats>(statsUri, cancellationToken);
                var counters = stats.Counters ?? new SearchServiceCounters(null, null, null);
                var storage = counters.StorageSize ?? new SearchCounter(0, 0);
                var vector = counters.VectorIndexSize ?? new SearchCounter(0, 0);
                var indexes = counters.IndexesCount ?? new SearchCounter(0, 0);
                services.Add(new SearchServiceCapacity(
                    settings.SubscriptionId, serviceResourceGroup, service.Name, endpoint, service.Sku?.Name,
                    storage.Usage, storage.Quota, vector.Usage, vector.Quota, indexes.Usage, indexes.Quota));
            }

            next = string.IsNullOrWhiteSpace(response.NextLink) ? null : new Uri(response.NextLink);
        }

        logger.LogInformation(
            "Completed managed Azure AI Search service listing in resource group {DeploymentResourceGroup}: {SearchServiceCount} services across {ArmPageCount} ARM pages",
            settings.DeploymentResourceGroup, services.Count, pageCount);
        return services;
    }

    public async Task<string> AllocatePrivateEndpointSubnetAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Reading virtual network and existing subnets to allocate a private endpoint subnet for VNet {VirtualNetworkName}",
            settings.ExistingVnetName);
        var group = Uri.EscapeDataString(settings.ExistingVnetResourceGroup);
        var name = Uri.EscapeDataString(settings.ExistingVnetName);
        var vnetUri = new Uri(
            $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(settings.SubscriptionId.ToString())}/resourceGroups/{group}/providers/Microsoft.Network/virtualNetworks/{name}?api-version={NetworkApiVersion}");
        var vnet = await SendArmAsync<VirtualNetworkResource>(HttpMethod.Get, vnetUri, null,
            cancellationToken);

        var subnetUri = new Uri(
            $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(settings.SubscriptionId.ToString())}/resourceGroups/{group}/providers/Microsoft.Network/virtualNetworks/{name}/subnets?api-version={NetworkApiVersion}");
        var existing = new List<string>();
        while (subnetUri is not null)
        {
            var response = await SendArmAsync<ArmListResponse<SubnetResource>>(HttpMethod.Get, subnetUri,
                null, cancellationToken);
            foreach (var subnet in response.Value)
            {
                if (!string.IsNullOrWhiteSpace(subnet.Properties?.AddressPrefix))
                    existing.Add(subnet.Properties.AddressPrefix);
                if (subnet.Properties?.AddressPrefixes is not null)
                    existing.AddRange(subnet.Properties.AddressPrefixes);
            }

            subnetUri = string.IsNullOrWhiteSpace(response.NextLink) ? null : new Uri(response.NextLink);
        }

        var subnetPrefix = Ipv4CidrAllocator.FindFirstAvailableSubnet(
            vnet.Properties?.AddressSpace?.AddressPrefixes ?? [], existing, settings.SubnetPrefixLength);
        logger.LogInformation(
            "Allocated private endpoint subnet {SubnetPrefix} from VNet {VirtualNetworkName}; inspected {ExistingSubnetCount} existing prefixes",
            subnetPrefix, settings.ExistingVnetName, existing.Count);
        return subnetPrefix;
    }

    public async Task StartDeploymentAsync(SearchDeploymentRequest request, CancellationToken cancellationToken)
    {
        using var templateDocument = JsonDocument.Parse(await ReadEmbeddedArmTemplateAsync(cancellationToken));
        var deployment = new ArmDeployment(
            new ArmDeploymentProperties(
                "Incremental",
                templateDocument.RootElement,
                BuildDeploymentParameters(request)));
        var uri = DeploymentUri(request.DeploymentName);

        logger.LogInformation("Validating ARM deployment for Search service {SearchServiceName}",
            request.DeploymentName);
        await SendArmAsync<JsonElement>(HttpMethod.Post, DeploymentUri(request.DeploymentName, "validate"), deployment,
            cancellationToken, allowEmptyResponse: true);
        logger.LogInformation("ARM deployment validation completed for Search service {SearchServiceName}",
            request.DeploymentName);
        logger.LogInformation("Submitting ARM deployment for Search service {SearchServiceName}", request.DeploymentName);
        await SendArmAsync<JsonElement>(HttpMethod.Put, uri, deployment, cancellationToken,
            allowEmptyResponse: true);
        logger.LogInformation("ARM deployment submission completed for Search service {SearchServiceName}",
            request.DeploymentName);
    }

    public async Task<ArmDeploymentStatus> GetDeploymentStatusAsync(string deploymentName,
        CancellationToken cancellationToken)
    {
        var uri = DeploymentUri(deploymentName);
        var response = await SendArmAsync<ArmDeploymentResource>(HttpMethod.Get, uri, null,
            cancellationToken);
        return new ArmDeploymentStatus(response.Properties?.ProvisioningState ?? "Unknown",
            FormatArmError(response.Properties?.Error));
    }

    private Uri DeploymentUri(string deploymentName, string? action = null) => new(
        $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(settings.SubscriptionId.ToString())}/resourceGroups/{Uri.EscapeDataString(settings.DeploymentResourceGroup)}/providers/Microsoft.Resources/deployments/{Uri.EscapeDataString(deploymentName)}{(action is null ? "" : $"/{action}")}?api-version={DeploymentApiVersion}");

    private Dictionary<string, ArmDeploymentParameter> BuildDeploymentParameters(SearchDeploymentRequest request) =>
        new(StringComparer.Ordinal)
        {
            ["location"] = new(settings.Location),
            ["searchServiceName"] = new(request.DeploymentName),
            ["newLaSubnetPrefix"] = new(request.SubnetPrefix),
            ["indexDataReaderPrincipalIds"] = new(request.IndexDataReaderPrincipalIds),
            ["indexDataReaderWriterPrincipalIds"] = new(request.IndexDataReaderWriterPrincipalIds),
            ["indexAdminPrincipalIds"] = new(request.IndexAdminPrincipalIds),
            ["principalType"] = new(settings.PrincipalType.ToString()),
            ["laLocation"] = new(settings.PrivateEndpointLocation),
            ["privateDnsZoneLaResourceGroupName"] = new(settings.PrivateDnsZoneResourceGroup),
            ["existingLaVnetResourceGroupName"] = new(settings.ExistingVnetResourceGroup),
            ["existingLaVnetName"] = new(settings.ExistingVnetName)
        };

    private async Task<T> SendArmAsync<T>(HttpMethod method, Uri uri, object? content,
        CancellationToken cancellationToken, bool allowEmptyResponse = false)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (content is not null)
            request.Content = JsonContent.Create(content, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure Resource Manager request {HttpMethod} {RequestPath} failed with {HttpStatusCode}; Azure request ID {AzureRequestId}",
                method, uri.AbsolutePath, (int)response.StatusCode, GetAzureRequestId(response));
            throw CreateArmException(response.StatusCode, method, uri, body);
        }
        if (allowEmptyResponse && string.IsNullOrWhiteSpace(body))
            return default!;
        if (typeof(T) == typeof(JsonElement) && string.IsNullOrWhiteSpace(body))
            return (T)(object)JsonDocument.Parse("{}").RootElement.Clone();
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
               throw new InvalidDataException($"Azure Resource Manager returned an empty response for {method} {uri.AbsolutePath}.");
    }

    private async Task<T> SendSearchAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([SearchScope]), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Azure AI Search stats request for host {SearchHost} failed with {HttpStatusCode}; Azure request ID {AzureRequestId}",
                uri.Host, (int)response.StatusCode, GetAzureRequestId(response));
            throw new HttpRequestException(
                $"Search service stats request failed with {(int)response.StatusCode} ({response.StatusCode}) for {uri.Host}: {body}",
                null, response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
               throw new InvalidDataException("Azure AI Search returned an empty service-stats response.");
    }

    private static string? GetAzureRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ms-correlation-request-id", out var correlationIds))
            return correlationIds.FirstOrDefault();
        return response.Headers.TryGetValues("x-ms-request-id", out var requestIds)
            ? requestIds.FirstOrDefault()
            : null;
    }

    private static HttpRequestException CreateArmException(HttpStatusCode statusCode, HttpMethod method,
        Uri uri, string body) => new(
        $"Azure Resource Manager request {method} {uri.AbsolutePath} failed with {(int)statusCode} ({statusCode}): {body}",
        null, statusCode);

    private static string ParseResourceGroup(string resourceId)
    {
        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return segments[index + 1];
        }

        throw new InvalidDataException($"Unable to find a resource group in Azure resource id '{resourceId}'.");
    }

    private static string? FormatArmError(ArmError? error)
    {
        if (error is null)
            return null;
        var details = error.Details?.Select(detail => detail.Message).Where(message => !string.IsNullOrWhiteSpace(message));
        var text = string.Join("; ", new[] { error.Code, error.Message }.Concat(details ?? []));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static async Task<string> ReadEmbeddedArmTemplateAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(AzureResourceManagerProvisioner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Infrastructure.arm-main.json", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName) ??
                                 throw new InvalidOperationException("Embedded ARM template resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private sealed record ArmListResponse<T>(
        [property: JsonPropertyName("value")] List<T> Value,
        [property: JsonPropertyName("nextLink")] string? NextLink);

    private sealed record SearchServiceResource(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sku")] SearchSku? Sku,
        [property: JsonPropertyName("properties")] SearchServiceProperties? Properties);

    private sealed record SearchSku([property: JsonPropertyName("name")] string? Name);

    private sealed record SearchServiceProperties([property: JsonPropertyName("hostName")] string? HostName);

    private sealed record SearchServiceStats([property: JsonPropertyName("counters")] SearchServiceCounters? Counters);

    private sealed record SearchServiceCounters(
        [property: JsonPropertyName("storageSize")] SearchCounter? StorageSize,
        [property: JsonPropertyName("vectorIndexSize")] SearchCounter? VectorIndexSize,
        [property: JsonPropertyName("indexesCount")] SearchCounter? IndexesCount);

    private sealed record SearchCounter(
        [property: JsonPropertyName("usage")] long Usage,
        [property: JsonPropertyName("quota")] long Quota);

    private sealed record VirtualNetworkResource(
        [property: JsonPropertyName("properties")] VirtualNetworkProperties? Properties);

    private sealed record VirtualNetworkProperties(
        [property: JsonPropertyName("addressSpace")] AddressSpace? AddressSpace);

    private sealed record AddressSpace(
        [property: JsonPropertyName("addressPrefixes")] string[]? AddressPrefixes);

    private sealed record SubnetResource(
        [property: JsonPropertyName("properties")] SubnetProperties? Properties);

    private sealed record SubnetProperties(
        [property: JsonPropertyName("addressPrefix")] string? AddressPrefix,
        [property: JsonPropertyName("addressPrefixes")] string[]? AddressPrefixes);

    private sealed record ArmDeployment(
        [property: JsonPropertyName("properties")] ArmDeploymentProperties Properties);

    private sealed record ArmDeploymentProperties(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("template")] JsonElement Template,
        [property: JsonPropertyName("parameters")] Dictionary<string, ArmDeploymentParameter> Parameters);

    private sealed record ArmDeploymentParameter([property: JsonPropertyName("value")] object Value);

    private sealed record ArmDeploymentResource(
        [property: JsonPropertyName("properties")] ArmDeploymentResultProperties? Properties);

    private sealed record ArmDeploymentResultProperties(
        [property: JsonPropertyName("provisioningState")] string? ProvisioningState,
        [property: JsonPropertyName("error")] ArmError? Error);

    private sealed record ArmError(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("details")] List<ArmError>? Details);
}
