using System.Net;
using System.Text;
using AccountOperations;
using Xunit;

namespace AccountOperations.Tests;

public sealed class HttpOrchestrationClientTests
{
    private static readonly Uri Start = new("https://example.test/start");
    private static readonly Uri Status = new("https://example.test/status?code=example");

    [Fact]
    public async Task PollsSameOriginOperationUntilCompleted()
    {
        using var handler = new ScriptedHandler(
            Json(HttpStatusCode.Accepted, $$"""{"statusQueryGetUri":"{{Status}}"}"""),
            Json(HttpStatusCode.Accepted, """{"runtimeStatus":"Running"}"""),
            Json(HttpStatusCode.OK, """{"runtimeStatus":"Completed","output":{"ok":true}}"""));
        using var http = new HttpClient(handler);
        var client = new HttpOrchestrationClient(http, options: new()
        {
            PollingInterval = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(5)
        });

        var result = await client.SendAndWaitForCompletionAsync(HttpMethod.Post, Start,
            new { name = "test" }, "example", null, CancellationToken.None);

        Assert.True(result!.Value.GetProperty("output").GetProperty("ok").GetBoolean());
        Assert.Equal([HttpMethod.Post, HttpMethod.Get, HttpMethod.Get], handler.Methods);
        Assert.Equal([Start, Status, Status], handler.Uris);
    }

    [Fact]
    public async Task ForwardsFunctionKeyHeaderWhenStatusUrlHasNoEmbeddedKey()
    {
        var statusWithoutKey = new Uri("https://example.test/status");
        using var handler = new ScriptedHandler(
            Json(HttpStatusCode.Accepted,
                $$"""{"statusQueryGetUri":"{{statusWithoutKey}}"}"""),
            Json(HttpStatusCode.OK, """{"runtimeStatus":"Completed"}"""));
        using var http = new HttpClient(handler);
        var client = new HttpOrchestrationClient(http, options: new()
        {
            PollingInterval = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(5)
        });

        await client.SendAndWaitForCompletionAsync(HttpMethod.Post, Start, new { },
            "example", new Dictionary<string, string> { ["x-functions-key"] = "test-key" },
            CancellationToken.None);

        Assert.Equal(["test-key", "test-key"], handler.FunctionKeys);
        Assert.Equal(statusWithoutKey, handler.Uris[1]);
    }

    [Fact]
    public async Task RejectsCrossOriginStatusUrl()
    {
        using var handler = new ScriptedHandler(Json(HttpStatusCode.Accepted,
            """{"statusQueryGetUri":"https://other.test/status"}"""));
        using var http = new HttpClient(handler);
        var client = new HttpOrchestrationClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendAndWaitForCompletionAsync(HttpMethod.Post, Start, new { },
                "example", new Dictionary<string, string> { ["x-functions-key"] = "test-key" },
                CancellationToken.None));
        Assert.Single(handler.Uris);
        Assert.Equal(["test-key"], handler.FunctionKeys);
    }

    [Fact]
    public async Task RetriesTransientResponse()
    {
        using var handler = new ScriptedHandler(
            Json(HttpStatusCode.ServiceUnavailable, ""),
            Json(HttpStatusCode.OK, """{"ok":true}"""));
        using var http = new HttpClient(handler);
        var client = new HttpOrchestrationClient(http);

        var result = await client.SendAsync(HttpMethod.Get, Start, null, "example", null,
            CancellationToken.None, retryCount: 1, retryDelay: TimeSpan.Zero);

        Assert.True(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(2, handler.Uris.Count);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        public List<HttpMethod> Methods { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<string?> FunctionKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Uris.Add(request.RequestUri!);
            FunctionKeys.Add(request.Headers.TryGetValues("x-functions-key", out var keys)
                ? keys.Single() : null);
            return Task.FromResult(queue.Dequeue());
        }
    }
}
