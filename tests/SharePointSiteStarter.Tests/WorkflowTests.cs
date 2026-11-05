using Microsoft.Extensions.Logging;
using SharePointSiteStarter.Domain;
using SharePointSiteStarter.Services;
using Xunit;

namespace SharePointSiteStarter.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task PassesRequestThroughProvisioningAndLogsOperationAndAccountIds()
    {
        var operationId = Guid.NewGuid();
        var request = ValidRequest();
        var provisioner = new FakeProvisioner();
        var logger = new CapturingLogger<SharePointSiteStarterWorkflow>();
        var workflow = new SharePointSiteStarterWorkflow(provisioner, logger);

        await workflow.StartAsync(operationId, request, CancellationToken.None);

        Assert.Same(request, provisioner.Request);
        Assert.Contains(logger.Messages, message => message.Contains(operationId.ToString("D")) &&
                                                    message.Contains(request.AccountId.ToString("D")) &&
                                                    message.Contains("completed"));
    }

    [Fact]
    public async Task LogsAndPropagatesProvisioningFailures()
    {
        var exception = new InvalidOperationException("SharePoint operation failed");
        var logger = new CapturingLogger<SharePointSiteStarterWorkflow>();
        var workflow = new SharePointSiteStarterWorkflow(new FakeProvisioner { Exception = exception }, logger);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.StartAsync(Guid.NewGuid(), ValidRequest(), CancellationToken.None));

        Assert.Same(exception, actual);
        Assert.Contains(logger.Exceptions, item => ReferenceEquals(item, exception));
    }

    [Fact]
    public async Task SerializesConcurrentSiteProvisioning()
    {
        var provisioner = new FakeProvisioner { BlockFirst = true };
        var workflow = new SharePointSiteStarterWorkflow(provisioner,
            new CapturingLogger<SharePointSiteStarterWorkflow>());

        var first = workflow.StartAsync(Guid.NewGuid(), ValidRequest(), CancellationToken.None);
        await provisioner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = workflow.StartAsync(Guid.NewGuid(), ValidRequest() with { AccountId = Guid.NewGuid() },
            CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, provisioner.Calls);
        provisioner.ReleaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, provisioner.Calls);
    }

    private static StartSiteRequest ValidRequest() => new()
    {
        AccountId = Guid.Parse("22222222-3333-4444-8555-666666666666"),
        CaseNumber = "CASE-123",
        FirstName = "Taylor",
        LastName = "Smith",
        SharePointSiteUri = new Uri("https://tenant.example.sharepoint.com/sites/22222222-3333-4444-8555-666666666666")
    };

    private sealed class FakeProvisioner : ISharePointSiteProvisioner
    {
        private int calls;
        public StartSiteRequest? Request { get; private set; }
        public Exception? Exception { get; init; }
        public bool BlockFirst { get; init; }
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProvisionAsync(StartSiteRequest request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            Request = request;
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                if (BlockFirst)
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            if (Exception is not null)
                throw Exception;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
