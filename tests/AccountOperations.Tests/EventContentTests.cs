using System.Text.Json;
using AccountOperations;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AccountOperations.Tests;

public sealed class EventContentTests
{
    [Fact]
    public void NewtonsoftPayloadRemainsAnObject()
    {
        var payload = JObject.Parse("""{"accountId":"case-1","eventKind":"WorkflowProcurement"}""");
        using var document = JsonDocument.Parse(EventContent.Envelope(payload));

        var content = EventContent.Unwrap(document.RootElement);
        Assert.Equal(JsonValueKind.Object, content.ValueKind);
        Assert.Equal("case-1", EventContent.Required(content, "accountId"));
        Assert.Equal("WorkflowProcurement", EventContent.Required(content, "eventKind"));
    }

    [Fact]
    public void StringEnvelopeFromLogicAppsIsUnwrapped()
    {
        using var document = JsonDocument.Parse(
            """{"contentData":"{\"eventKind\":\"AccountCleanup\",\"accountId\":\"case-2\"}"}""");

        var content = EventContent.Unwrap(document.RootElement);
        Assert.Equal("AccountCleanup", EventContent.Required(content, "eventKind"));
        Assert.Equal("case-2", EventContent.Required(content, "accountId"));
    }

    [Theory]
    [InlineData("accountId")]
    [InlineData("siteId")]
    [InlineData("caseId")]
    public void AccountEventsUseStablePartitionKey(string property)
    {
        var payload = new JObject { [property] = "case-3", ["eventKind"] = "test" };
        Assert.Equal("case-3", EventContent.PartitionKey(payload));
    }

    [Fact]
    public void EventWithoutAccountHasNoPartitionKey()
    {
        Assert.Null(EventContent.PartitionKey(new { eventKind = "test" }));
    }
}
