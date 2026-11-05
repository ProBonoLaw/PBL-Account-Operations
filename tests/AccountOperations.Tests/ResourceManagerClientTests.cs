using System.Net;
using System.Text.Json;
using AccountOperations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AccountOperations.Tests;

public sealed class ResourceManagerClientTests
{
    private const string ApiKey = "a-resource-manager-api-key-with-32-characters";

    [Fact]
    public async Task PostsAccountIdToInternalApiAndReturnsSearchInstance()
    {
        var priorUrl = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_URL");
        var priorKey = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_API_KEY");
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_URL", "http://localhost:8090");
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_API_KEY", ApiKey);
        try
        {
            var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            var client = new ResourceManagerClient(http, NullLogger<ResourceManagerClient>.Instance);
            var accountId = Guid.Parse("22222222-3333-4444-8555-666666666666");
            var operationId = Guid.Parse("33333333-4444-4555-8666-777777777777");
            handler.ResponseBody = JsonSerializer.Serialize(new
            {
                operationId,
                accountId,
                searchEndpoint = "https://search-test.search.windows.net"
            });

            var result = await client.GetSearchInstanceAsync(accountId, CancellationToken.None);

            Assert.Equal(operationId, result.OperationId);
            Assert.Equal(new Uri("https://search-test.search.windows.net"), result.SearchEndpoint);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("/api/getSearchInstance", handler.Path);
            Assert.Equal(ApiKey, handler.ApiKey);
            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(accountId, body.RootElement.GetProperty("accountId").GetGuid());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESOURCE_MANAGER_URL", priorUrl);
            Environment.SetEnvironmentVariable("RESOURCE_MANAGER_API_KEY", priorKey);
        }
    }

    [Fact]
    public async Task PropagatesSupportOperationIdWithoutExposingFailureDetails()
    {
        var priorUrl = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_URL");
        var priorKey = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_API_KEY");
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_URL", "http://localhost:8090");
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_API_KEY", ApiKey);
        try
        {
            var operationId = Guid.Parse("33333333-4444-4555-8666-777777777777");
            var handler = new CapturingHandler
            {
                StatusCode = HttpStatusCode.InternalServerError,
                ResponseBody = JsonSerializer.Serialize(new
                {
                    title = "Search instance provisioning failed.",
                    detail = "private deployment details",
                    operationId
                })
            };
            using var http = new HttpClient(handler);
            var client = new ResourceManagerClient(http, NullLogger<ResourceManagerClient>.Instance);

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetSearchInstanceAsync(Guid.NewGuid(), CancellationToken.None));

            Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
            Assert.Contains(operationId.ToString("D"), exception.Message);
            Assert.DoesNotContain("private deployment details", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESOURCE_MANAGER_URL", priorUrl);
            Environment.SetEnvironmentVariable("RESOURCE_MANAGER_API_KEY", priorKey);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? ApiKey { get; private set; }
        public string? RequestBody { get; private set; }
        public string ResponseBody { get; set; } = "";
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            ApiKey = request.Headers.GetValues("x-functions-key").Single();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseBody)
            };
        }
    }
}
