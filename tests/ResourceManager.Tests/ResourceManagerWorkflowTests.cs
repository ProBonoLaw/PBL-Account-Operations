using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.Configuration;
using ResourceManager.Domain;
using ResourceManager.Infrastructure;
using ResourceManager.Services;
using Xunit;

namespace ResourceManager.Tests;

public sealed class ResourceManagerWorkflowTests
{
    private static readonly Guid ReaderId = Guid.Parse("f1d33e91-1111-4b4b-bbbb-123456789abc");
    private static readonly Guid AdminId = Guid.Parse("f1d33e91-2222-4b4b-bbbb-123456789abc");
    private static readonly Guid WriterId = Guid.Parse("f1d33e91-3333-4b4b-bbbb-123456789abc");

    [Fact]
    public async Task ReturnsAvailableSearchEndpointWithoutProvisioning()
    {
        var provisioner = new FakeProvisioner
        {
            Services = [Capacity("https://existing.search.windows.net", 1_000_000_000)]
        };
        var workflow = CreateWorkflow(provisioner);

        var endpoint = await workflow.GetSearchEndpointAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new Uri("https://existing.search.windows.net"), endpoint);
        Assert.Equal(1, provisioner.ListCalls);
        Assert.Equal(0, provisioner.SubnetCalls);
        Assert.Null(provisioner.DeploymentRequest);
        Assert.Empty(provisioner.StatusesRead);
    }

    [Fact]
    public async Task SerializesConcurrentCapacityChecksAndDeployments()
    {
        var firstOperationId = Guid.Parse("22222222-3333-4444-8555-666666666666");
        var deployedEndpoint = new Uri("https://probono-search-222222223333.search.windows.net");
        var provisioner = new FakeProvisioner
        {
            BlockFirstList = true,
            CapacityAfterDeployment = Capacity(deployedEndpoint.ToString(), 1_000_000_000),
            Statuses = new Queue<ArmDeploymentStatus>([new("Succeeded", null)])
        };
        var workflow = CreateWorkflow(provisioner);

        var firstRequest = workflow.GetSearchEndpointAsync(firstOperationId, CancellationToken.None);
        await provisioner.FirstListStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = Task.Run(async () =>
        {
            secondRequestStarted.TrySetResult();
            return await workflow.GetSearchEndpointAsync(Guid.NewGuid(), CancellationToken.None);
        });
        await secondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.False(provisioner.SecondListStarted.Task.IsCompleted);
        Assert.Equal(1, provisioner.ListCalls);

        provisioner.ReleaseFirstList.TrySetResult();
        var endpoints = await Task.WhenAll(firstRequest, secondRequest);

        Assert.All(endpoints, endpoint => Assert.Equal(deployedEndpoint, endpoint));
        Assert.Equal(2, provisioner.ListCalls);
        Assert.Equal(1, provisioner.SubnetCalls);
        Assert.Single(provisioner.StatusesRead);
    }

    [Fact]
    public async Task AllocatesAndWaitsForDeploymentWhenNoServiceHasCapacity()
    {
        var operationId = Guid.Parse("22222222-3333-4444-8555-666666666666");
        var provisioner = new FakeProvisioner
        {
            Statuses = new Queue<ArmDeploymentStatus>([
                new("Creating", null), new("Succeeded", null)])
        };
        var workflow = CreateWorkflow(provisioner);

        var endpoint = await workflow.GetSearchEndpointAsync(operationId, CancellationToken.None);

        Assert.Equal(new Uri("https://probono-search-222222223333.search.windows.net"), endpoint);
        Assert.Equal(1, provisioner.SubnetCalls);
        Assert.Equal("10.9.8.0/24", provisioner.DeploymentRequest?.SubnetPrefix);
        Assert.Equal("probono-search-222222223333", provisioner.DeploymentRequest?.DeploymentName);
        var deploymentRequest = Assert.IsType<SearchDeploymentRequest>(provisioner.DeploymentRequest);
        Assert.Equal([ReaderId], deploymentRequest.IndexDataReaderPrincipalIds);
        Assert.Equal([WriterId], deploymentRequest.IndexDataReaderWriterPrincipalIds);
        Assert.Equal([AdminId], deploymentRequest.IndexAdminPrincipalIds);
        Assert.Equal(2, provisioner.StatusesRead.Count);
    }

    [Fact]
    public async Task LogsCorrelatedProvisioningStagesForSupport()
    {
        var operationId = Guid.Parse("22222222-3333-4444-8555-666666666666");
        var provisioner = new FakeProvisioner
        {
            Statuses = new Queue<ArmDeploymentStatus>([
                new("Creating", null), new("Succeeded", null)])
        };
        var logger = new CapturingLogger<ResourceManagerWorkflow>();
        var workflow = CreateWorkflow(provisioner, logger: logger);

        await workflow.GetSearchEndpointAsync(operationId, CancellationToken.None);

        var messages = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains($"queued for operation {operationId:D}", messages);
        Assert.Contains($"capacity inventory started for operation {operationId:D}", messages);
        Assert.Contains($"Private endpoint subnet allocated for operation {operationId:D}", messages);
        Assert.Contains($"deployment submission completed for operation {operationId:D}", messages);
        Assert.Contains($"poll 1: Creating", messages);
        Assert.Contains($"poll 2: Succeeded", messages);
        Assert.Contains($"workflow completed for operation {operationId:D}", messages);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public async Task ThrowsDeploymentDetailsForTerminalFailures(string state)
    {
        var provisioner = new FakeProvisioner
        {
            Statuses = new Queue<ArmDeploymentStatus>([new(state, "subnet allocation failed")])
        };
        var workflow = CreateWorkflow(provisioner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.GetSearchEndpointAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Contains(state, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subnet allocation failed", exception.Message);
    }

    [Fact]
    public async Task HonorsCancellationWhileWaitingForDeployment()
    {
        var provisioner = new FakeProvisioner
        {
            Statuses = new Queue<ArmDeploymentStatus>([new("Running", null)])
        };
        var workflow = CreateWorkflow(provisioner, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.GetSearchEndpointAsync(Guid.NewGuid(), cancellation.Token));
    }

    private static ResourceManagerWorkflow CreateWorkflow(FakeProvisioner provisioner,
        TimeSpan? pollInterval = null, ILogger<ResourceManagerWorkflow>? logger = null)
    {
        var options = Options.Create(new ResourceManagerOptions
        {
            SearchServiceNamePrefix = "probono-search",
            MinimumCapacityHeadroomBytes = 1_000_000_000,
            DeploymentPollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1),
            IndexDataReaderPrincipalIds = [ReaderId],
            IndexDataReaderWriterPrincipalIds = [WriterId],
            IndexAdminPrincipalIds = [AdminId]
        });
        return new ResourceManagerWorkflow(provisioner, options, TimeProvider.System,
            logger ?? NullLogger<ResourceManagerWorkflow>.Instance);
    }

    private static SearchServiceCapacity Capacity(string endpoint, long remainingBytes) => new(
        Guid.NewGuid(), "rg", "existing-search", new Uri(endpoint), "basic", 0, remainingBytes,
        0, remainingBytes, 1, 10);

    private sealed class FakeProvisioner : IResourceManagerProvisioner
    {
        private int listCalls;

        public IReadOnlyList<SearchServiceCapacity> Services { get; set; } = [];
        public Queue<ArmDeploymentStatus> Statuses { get; init; } = new();
        public List<ArmDeploymentStatus> StatusesRead { get; } = [];
        public int ListCalls => Volatile.Read(ref listCalls);
        public int SubnetCalls { get; private set; }
        public SearchDeploymentRequest? DeploymentRequest { get; private set; }
        public bool BlockFirstList { get; init; }
        public SearchServiceCapacity? CapacityAfterDeployment { get; init; }
        public TaskCompletionSource FirstListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstList { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SearchServiceCapacity>> ListSearchServicesAsync(CancellationToken token)
        {
            var call = Interlocked.Increment(ref listCalls);
            if (call == 1)
            {
                FirstListStarted.TrySetResult();
                if (BlockFirstList)
                    await ReleaseFirstList.Task.WaitAsync(token);
            }
            else if (call == 2)
            {
                SecondListStarted.TrySetResult();
            }

            return Services;
        }

        public Task<string> AllocatePrivateEndpointSubnetAsync(CancellationToken token)
        {
            SubnetCalls++;
            return Task.FromResult("10.9.8.0/24");
        }

        public Task StartDeploymentAsync(SearchDeploymentRequest request, CancellationToken token)
        {
            DeploymentRequest = request;
            if (CapacityAfterDeployment is not null)
                Services = [CapacityAfterDeployment];
            return Task.CompletedTask;
        }

        public Task<ArmDeploymentStatus> GetDeploymentStatusAsync(string deploymentName, CancellationToken token)
        {
            var status = Statuses.Dequeue();
            StatusesRead.Add(status);
            return Task.FromResult(status);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
