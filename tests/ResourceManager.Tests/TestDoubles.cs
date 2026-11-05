using System.Net;
using System.Net.Sockets;
using ResourceManager.Api;
using ResourceManager.Services;

namespace ResourceManager.Tests;

internal sealed class FakeResourceManagerWorkflow : IResourceManagerWorkflow
{
    public Uri Endpoint { get; set; } = new("https://search-test.search.windows.net");
    public Exception? Exception { get; set; }
    public int Calls { get; private set; }
    public Guid? OperationId { get; private set; }

    public Task<Uri> GetSearchEndpointAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Calls++;
        OperationId = operationId;
        if (Exception is not null)
            return Task.FromException<Uri>(Exception);
        return Task.FromResult(Endpoint);
    }
}

internal sealed class FakeHostAddressResolver : IHostAddressResolver
{
    public IReadOnlyList<IPAddress> Addresses { get; set; } = [];
    public bool ThrowSocketException { get; set; }
    public string? Host { get; private set; }
    public int Calls { get; private set; }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
    {
        Calls++;
        Host = host;
        if (ThrowSocketException)
            throw new SocketException((int)SocketError.HostNotFound);
        return Task.FromResult(Addresses);
    }
}
