using System.Net;
using System.Text.Json;
using AccountOperations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AccountOperations.Tests;

public sealed class SharePointSiteStarterClientTests
{
    private const string ApiKey = "a-sharepoint-site-starter-key-with-32-characters";

    [Fact]
    public async Task PostsTypedRequestToInternalApiAndReturnsOperationId()
    {
        var priorUrl = Environment.GetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL");
        var priorKey = Environment.GetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY");
        Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL", "http://localhost:8090");
        Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY", ApiKey);
        try
        {
            var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            var client = new SharePointSiteStarterClient(http,
                NullLogger<SharePointSiteStarterClient>.Instance);
            var accountId = Guid.Parse("22222222-3333-4444-8555-666666666666");
            var operationId = Guid.Parse("33333333-4444-4555-8666-777777777777");
            handler.ResponseBody = JsonSerializer.Serialize(new
            {
                operationId,
                accountId,
                sharePointSiteUri = $"https://tenant.example.sharepoint.com/sites/{accountId:D}",
                status = "Completed"
            });
            var request = new SharePointSiteStarterRequest(accountId, "CASE-123", "Taylor", "Smith",
                "Tay", "taylor@example.com",
                new Uri($"https://tenant.example.sharepoint.com/sites/{accountId:D}"));

            var result = await client.StartAsync(request, CancellationToken.None);

            Assert.Equal(operationId, result);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("/api/start", handler.Path);
            Assert.Equal(ApiKey, handler.ApiKey);
            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(accountId, body.RootElement.GetProperty("accountId").GetGuid());
            Assert.Equal("taylor@example.com", body.RootElement.GetProperty("email").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL", priorUrl);
            Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY", priorKey);
        }
    }

    [Fact]
    public async Task PropagatesSupportOperationIdWithoutLoggingProblemBody()
    {
        var priorUrl = Environment.GetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL");
        var priorKey = Environment.GetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY");
        Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL", "http://localhost:8090");
        Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY", ApiKey);
        try
        {
            var operationId = Guid.Parse("33333333-4444-4555-8666-777777777777");
            var handler = new CapturingHandler
            {
                StatusCode = HttpStatusCode.InternalServerError,
                ResponseBody = JsonSerializer.Serialize(new
                {
                    title = "SharePoint site preparation failed.",
                    detail = "private failure details",
                    operationId
                })
            };
            using var http = new HttpClient(handler);
            var client = new SharePointSiteStarterClient(http,
                NullLogger<SharePointSiteStarterClient>.Instance);

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.StartAsync(
                new SharePointSiteStarterRequest(Guid.NewGuid(), "CASE-123", "Taylor", "Smith", null,
                    null, new Uri("https://tenant.example.sharepoint.com/sites/test")),
                CancellationToken.None));

            Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
            Assert.Contains(operationId.ToString("D"), exception.Message);
            Assert.DoesNotContain("private failure details", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_URL", priorUrl);
            Environment.SetEnvironmentVariable("SHAREPOINT_SITE_STARTER_API_KEY", priorKey);
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
