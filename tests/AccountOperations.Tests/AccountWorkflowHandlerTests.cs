using System.Net;
using System.Net.Http;
using System.Text.Json;
using AccountOperations;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AccountOperations.Tests;

public sealed class AccountWorkflowHandlerTests
{
    private const string RootAccountId = "00000000-0000-0000-0000-000000000000";

    public AccountWorkflowHandlerTests()
    {
        Environment.SetEnvironmentVariable("ROLE", "account");
        Environment.SetEnvironmentVariable("EMAIL_LICENSE_SKU_ID", "99999999-aaaa-4bbb-8ccc-dddddddddddd");
    }

    [Fact]
    public async Task SharePointClient_UsesVerboseODataContentTypeForMetadataPayloads()
    {
        var priorHost = Environment.GetEnvironmentVariable("SHAREPOINT_HOST");
        Environment.SetEnvironmentVariable("SHAREPOINT_HOST", "tenant.example.sharepoint.com");
        try
        {
            var httpHandler = new CapturingHttpHandler();
            using var http = new HttpClient(httpHandler);
            var client = new SharePointClient(http, new FakeGraphClient());

            await client.SetSiteAdminAsync("account-123", 1073, false, CancellationToken.None);

            Assert.Contains("odata=verbose", httpHandler.ContentType, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"__metadata\":{\"type\":\"SP.User\"}", httpHandler.Body);
            Assert.Equal("MERGE", httpHandler.MethodOverride);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREPOINT_HOST", priorHost);
        }
    }

    [Fact]
    public async Task SharePointClient_UsesPlainJsonForTenantUpdatesWithoutMetadata()
    {
        var priorHost = Environment.GetEnvironmentVariable("SHAREPOINT_HOST");
        Environment.SetEnvironmentVariable("SHAREPOINT_HOST", "tenant.example.sharepoint.com");
        try
        {
            var httpHandler = new CapturingHttpHandler();
            using var http = new HttpClient(httpHandler);
            var client = new SharePointClient(http, new FakeGraphClient());

            await client.TenantAsync(HttpMethod.Patch, "_api/SPO.Tenant/sites('site-id')",
                new { SharingCapability = 2 }, CancellationToken.None);

            Assert.DoesNotContain("odata=verbose", httpHandler.ContentType,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREPOINT_HOST", priorHost);
        }
    }

    [Fact]
    public async Task LinkUserAsync_ReactivatesExistingRecipient()
    {
        var store = new FakeAccountStore();
        var graph = new FakeGraphClient();
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, graph, new FakeSharePointClient(),
            new FakeFunctionClient(), new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);

        var requesterRoot = new JObject
        {
            ["id"] = "requester-root",
            ["accountId"] = RootAccountId,
            ["oids"] = new JArray("requester-oid"),
            ["casesAccounts"] = new JArray(new JObject
            {
                ["accountId"] = "account-123",
                ["isGuest"] = false
            })
        };
        var recipientRoot = new JObject
        {
            ["id"] = "recipient-root",
            ["accountId"] = RootAccountId,
            ["isActive"] = false,
            ["emailAddresses"] = new JArray("recipient@example.com"),
            ["casesAccounts"] = new JArray(),
            ["oids"] = new JArray("recipient-oid")
        };
        store.QueryResults.Enqueue(new List<JObject> { requesterRoot, recipientRoot });

        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "CaseAccountUserLink",
            requesterOid = "requester-oid",
            caseId = "account-123",
            caseNumber = "CASE-123",
            recipient = new { emailAddress = "recipient@example.com" },
            agreedToCaseTerms = true,
            documentAccessRole = "USER",
            requestDate = "2026-10-03"
        });

        await handler.HandleAsync(request, CancellationToken.None);

        var upsert = Assert.Single(store.Upserts);
        Assert.Equal(RootAccountId, upsert.PartitionKey);
        Assert.False(upsert.Item["isActive"]?.Value<bool>());
        var cases = (JArray)upsert.Item["casesAccounts"]!;
        Assert.Contains(cases.OfType<JObject>(), entry =>
            entry.Value<string>("accountId") == "account-123" && entry.Value<bool>("isGuest") &&
            entry.Value<bool>("isActive"));

        var accountEvent = Assert.Single(events.AccountEvents);
        Assert.Equal("SharePointAccessGrant", accountEvent.Value<string>("eventKind"));
        Assert.Equal("account-123", accountEvent.Value<string>("accountId"));
        Assert.Equal("recipient@example.com",
            accountEvent["recipient"]?["emailAddress"]?.Value<string>());

        var communication = Assert.Single(events.CommunicationEvents);
        Assert.Equal("ShareRequest", communication.Value<string>("status"));
        Assert.Equal("account-123", communication.Value<string>("accountId"));
    }

    [Fact]
    public async Task LinkUserAsync_ReactivatesAlreadyLinkedInactiveCase()
    {
        var store = new FakeAccountStore();
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, new FakeGraphClient(),
            new FakeSharePointClient(), new FakeFunctionClient(), new FakeResourceManagerClient(),
            new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);
        var requesterRoot = new JObject
        {
            ["id"] = "requester-root",
            ["accountId"] = RootAccountId,
            ["oids"] = new JArray("requester-oid"),
            ["casesAccounts"] = new JArray(new JObject
            {
                ["accountId"] = "account-456",
                ["isGuest"] = false
            })
        };
        var recipientRoot = new JObject
        {
            ["id"] = "recipient-root",
            ["accountId"] = RootAccountId,
            ["isActive"] = false,
            ["emailAddresses"] = new JArray("recipient@example.com"),
            ["oids"] = new JArray("recipient-oid"),
            ["casesAccounts"] = new JArray(new JObject
            {
                ["accountId"] = "account-456",
                ["isGuest"] = true,
                ["isActive"] = false,
                ["caseNumber"] = "CASE-456"
            })
        };
        store.QueryResults.Enqueue(new List<JObject> { requesterRoot, recipientRoot });
        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "CaseAccountUserLink",
            requesterOid = "requester-oid",
            caseId = "account-456",
            recipient = new { emailAddress = "recipient@example.com" }
        });

        await handler.HandleAsync(request, CancellationToken.None);

        var upsert = Assert.Single(store.Upserts);
        Assert.False(upsert.Item["isActive"]?.Value<bool>());
        var linkedCase = Assert.Single(((JArray)upsert.Item["casesAccounts"]!).OfType<JObject>());
        Assert.True(linkedCase["isActive"]?.Value<bool>());
        Assert.Equal("CASE-456", linkedCase.Value<string>("caseNumber"));
        Assert.Equal("SharePointAccessGrant",
            Assert.Single(events.AccountEvents).Value<string>("eventKind"));
        Assert.Equal("ShareRequest",
            Assert.Single(events.CommunicationEvents).Value<string>("status"));
    }

    [Fact]
    public async Task LinkUserAsync_CreatesActiveRecipientWhenMissing()
    {
        var store = new FakeAccountStore();
        var graph = new FakeGraphClient();
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, graph, new FakeSharePointClient(),
            new FakeFunctionClient(), new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);

        var requesterRoot = new JObject
        {
            ["id"] = "requester-root",
            ["accountId"] = RootAccountId,
            ["oids"] = new JArray("requester-oid"),
            ["casesAccounts"] = new JArray(new JObject
            {
                ["accountId"] = "account-987",
                ["isGuest"] = false
            })
        };
        store.QueryResults.Enqueue(new List<JObject> { requesterRoot });

        graph.UsersByEmail["recipient@example.com"] = new JObject
        {
            ["id"] = "recipient-oid",
            ["userPrincipalName"] = "recipient@example.com"
        };

        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "CaseAccountUserLink",
            requesterOid = "requester-oid",
            caseId = "account-987",
            caseNumber = "CASE-987",
            recipient = new { emailAddress = "recipient@example.com" },
            agreedToCaseTerms = false,
            documentAccessRole = "USER",
            requestDate = "2026-10-03"
        });

        await handler.HandleAsync(request, CancellationToken.None);

        var created = Assert.Single(store.Created);
        Assert.Equal(RootAccountId, created.PartitionKey);
        Assert.True(created.Item["isActive"]?.Value<bool>());
        var cases = (JArray)created.Item["casesAccounts"]!;
        Assert.Contains(cases.OfType<JObject>(), entry =>
            entry.Value<string>("accountId") == "account-987" && entry.Value<bool>("isGuest"));

        var accountEvent = Assert.Single(events.AccountEvents);
        Assert.Equal("SharePointAccessGrant", accountEvent.Value<string>("eventKind"));
        Assert.Equal("account-987", accountEvent.Value<string>("accountId"));

        var communication = Assert.Single(events.CommunicationEvents);
        Assert.Equal("ShareRequest", communication.Value<string>("status"));
    }

    [Fact]
    public async Task AccountInitialization_OmitsNullErrorMessageFromStartedNotice()
    {
        var store = new FakeAccountStore();
        store.QueryResults.Enqueue([]);
        var functions = new FakeFunctionClient();
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
            functions, new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);
        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "AccountInitialization",
            attorney = new { emailAddress = "attorney@example.com", id = "attorney-oid" },
            @case = new { caseId = "CASE-123" }
        });

        await handler.HandleAsync(request, CancellationToken.None);

        var notice = Assert.Single(functions.Notifications);
        Assert.Equal("CASE_CREATED", notice.GetProperty("notificationType").GetString());
        Assert.Equal("attorney-oid", notice.GetProperty("oid").GetString());
        var data = notice.GetProperty("data");
        Assert.Equal("STARTED", data.GetProperty("status").GetString());
        Assert.Equal(Assert.Single(store.Created).Item.SelectToken("casesAccounts[0].accountId")?.Value<string>(),
            data.GetProperty("accountId").GetString());
        Assert.False(data.TryGetProperty("errorMessage", out _));
        var marker = Assert.Single(store.Patches);
        Assert.Equal(Assert.Single(store.Created).Item.Value<string>("id"), marker.Id);
        Assert.Equal("/casesAccounts/0/sharePointSiteProcurementPublished",
            Assert.Single(marker.Operations).Path);
        Assert.Equal(2, events.AccountEvents.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountInitialization_ReplaysSiteProcurementOnlyWhenUnpublished(bool siteProcurementPublished)
    {
        var accountId = "44444444-5555-4666-8777-888888888888";
        var caseEntry = new JObject
        {
            ["accountId"] = accountId,
            ["caseNumber"] = "CASE-123"
        };
        if (siteProcurementPublished) caseEntry["sharePointSiteProcurementPublished"] = true;
        var store = new FakeAccountStore();
        store.QueryResults.Enqueue([
            new JObject
            {
                ["id"] = "existing-root",
                ["accountId"] = RootAccountId,
                ["emailAddresses"] = new JArray("attorney@example.com"),
                ["oids"] = new JArray("attorney-oid"),
                ["casesAccounts"] = new JArray(caseEntry)
            }
        ]);
        var functions = new FakeFunctionClient();
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
            functions, new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);
        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "AccountInitialization",
            attorney = new { emailAddress = "attorney@example.com", id = "attorney-oid" },
            @case = new { caseId = "CASE-123" }
        });

        await handler.HandleAsync(request, CancellationToken.None);

        Assert.Empty(store.Created);
        if (siteProcurementPublished)
        {
            Assert.Empty(store.Patches);
            var refinement = Assert.Single(events.AccountEvents);
            Assert.Equal("AccountInformationRefinement", refinement.Value<string>("eventKind"));
        }
        else
        {
            var patch = Assert.Single(store.Patches);
            Assert.Equal("existing-root", patch.Id);
            Assert.Equal("/casesAccounts/0/sharePointSiteProcurementPublished",
                Assert.Single(patch.Operations).Path);
            Assert.Collection(events.AccountEvents,
                siteRequest =>
                {
                    Assert.Equal("SharePointSiteProcurement", siteRequest.Value<string>("eventKind"));
                    Assert.Equal(accountId, siteRequest.Value<string>("accountId"));
                },
                refinement =>
                {
                    Assert.Equal("AccountInformationRefinement", refinement.Value<string>("eventKind"));
                    Assert.Equal(accountId, refinement.Value<string>("accountId"));
                });
        }
        Assert.Equal("STARTED", Assert.Single(functions.Notifications)
            .GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AccountInitialization_MarksNewCaseAtItsAppendedIndex()
    {
        var store = new FakeAccountStore();
        store.QueryResults.Enqueue([
            new JObject
            {
                ["id"] = "existing-root",
                ["accountId"] = RootAccountId,
                ["emailAddresses"] = new JArray("attorney@example.com"),
                ["oids"] = new JArray("attorney-oid"),
                ["casesAccounts"] = new JArray(new JObject
                {
                    ["accountId"] = "other-account",
                    ["caseNumber"] = "OTHER-CASE"
                })
            }
        ]);
        var events = new FakeAccountEventPublisher();
        var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
            new FakeFunctionClient(), new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(),
            events, NullLogger<AccountWorkflowHandler>.Instance);
        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "AccountInitialization",
            attorney = new { emailAddress = "attorney@example.com", id = "attorney-oid" },
            @case = new { caseId = "CASE-123" }
        });

        await handler.HandleAsync(request, CancellationToken.None);

        Assert.Empty(store.Created);
        Assert.Collection(store.Patches,
            append => Assert.Equal("/casesAccounts/-", Assert.Single(append.Operations).Path),
            marker => Assert.Equal("/casesAccounts/1/sharePointSiteProcurementPublished",
                Assert.Single(marker.Operations).Path));
        Assert.Equal("SharePointSiteProcurement", events.AccountEvents[0].Value<string>("eventKind"));
    }

    [Fact]
    public async Task AccountInitialization_IncludesErrorMessageInFailedNotice()
    {
        var store = new FakeAccountStore();
        store.QueryResults.Enqueue([]);
        var functions = new FakeFunctionClient();
        var events = new FakeAccountEventPublisher { FailAccountPublish = true };
        var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
            functions, new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(), events,
            NullLogger<AccountWorkflowHandler>.Instance);
        var request = JsonSerializer.SerializeToElement(new
        {
            eventKind = "AccountInitialization",
            attorney = new { emailAddress = "attorney@example.com", id = "attorney-oid" },
            @case = new { caseId = "CASE-123" }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, CancellationToken.None));

        Assert.Collection(functions.Notifications,
            started => Assert.False(started.GetProperty("data").TryGetProperty("errorMessage", out _)),
            failed =>
            {
                var data = failed.GetProperty("data");
                Assert.Equal("FAILED", data.GetProperty("status").GetString());
                Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("errorMessage").GetString()));
            });
        Assert.Empty(store.Patches);
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("index-1", "https://search.example.com", true, false)]
    [InlineData("index-1", null, false, true)]
    [InlineData(null, "https://search.example.com", false, true)]
    public async Task AccountCleanup_PurgesOnlyProvisionedSearchIndexes(
        string? dataSourceId, string? searchEndpoint, bool expectedPurge, bool expectedFailure)
    {
        var priorHost = Environment.GetEnvironmentVariable("SHAREPOINT_HOST");
        Environment.SetEnvironmentVariable("SHAREPOINT_HOST", "tenant.example.sharepoint.com");
        try
        {
            var accountId = "44444444-5555-4666-8777-888888888888";
            var root = new JObject
            {
                ["id"] = "root-id",
                ["accountId"] = RootAccountId,
                ["oids"] = new JArray("owner-oid"),
                ["casesAccounts"] = new JArray(new JObject { ["accountId"] = accountId })
            };
            var details = new JObject
            {
                ["id"] = "details-id",
                ["accountId"] = accountId,
                ["type"] = "ACCOUNT_DETAILS",
                ["isActive"] = true
            };
            if (dataSourceId is not null) details["dataSourceId"] = dataSourceId;
            if (searchEndpoint is not null) details["searchEndpoint"] = searchEndpoint;
            var store = new FakeAccountStore();
            store.QueryResults.Enqueue([root]);
            store.QueryResults.Enqueue([details]);
            store.QueryResults.Enqueue([root]);
            var functions = new FakeFunctionClient
            {
                CallResult = JsonSerializer.SerializeToElement(new { runtimeStatus = "Completed" })
            };
            var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
                functions, new FakeResourceManagerClient(), new FakeSharePointSiteStarterClient(),
                new FakeAccountEventPublisher(), NullLogger<AccountWorkflowHandler>.Instance);
            var request = JsonSerializer.SerializeToElement(new
            {
                eventKind = "AccountCleanup",
                accountId,
                deactivate = new { sharePoint = true, emailIngestion = false }
            });

            if (expectedFailure)
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    handler.HandleAsync(request, CancellationToken.None));
            else
                await handler.HandleAsync(request, CancellationToken.None);

            Assert.Equal(expectedPurge ? ["DOCUMENT_INGRESS"] : [], functions.Calls);
            Assert.Equal(expectedFailure ? ["STARTED", "FAILED"] : ["STARTED", "SUCCESS"],
                functions.Notifications.Select(notice =>
                    notice.GetProperty("data").GetProperty("status").GetString()));
            if (expectedFailure)
            {
                Assert.Empty(store.Patches);
            }
            else
            {
                Assert.Collection(store.Patches,
                    deactivation => Assert.Contains(deactivation.Operations,
                        operation => operation.Path == "/isActive"),
                    unlink => Assert.Equal("/casesAccounts/0",
                        Assert.Single(unlink.Operations).Path));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHAREPOINT_HOST", priorHost);
        }
    }

    [Fact]
    public async Task WorkflowProcurementRejectsMalformedSiteIdAsInvalidEvent()
    {
        var priorRole = Environment.GetEnvironmentVariable("ROLE");
        Environment.SetEnvironmentVariable("ROLE", "sharepoint");
        try
        {
            var store = new FakeAccountStore();
            var resourceManager = new FakeResourceManagerClient();
            var starter = new FakeSharePointSiteStarterClient();
            var events = new FakeAccountEventPublisher();
            var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
                new FakeFunctionClient(), resourceManager, starter, events,
                NullLogger<AccountWorkflowHandler>.Instance);
            var eventRequest = JsonSerializer.SerializeToElement(new
            {
                eventKind = "WorkflowProcurement",
                siteId = "not-a-guid"
            });

            await Assert.ThrowsAsync<InvalidEventException>(() =>
                handler.HandleAsync(eventRequest, CancellationToken.None));

            Assert.Empty(store.QueryTextCalls);
            Assert.Empty(store.QueryDefinitionCalls);
            Assert.Equal(0, resourceManager.Calls);
            Assert.Empty(starter.Requests);
            Assert.Empty(events.CommunicationEvents);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROLE", priorRole);
        }
    }

    [Fact]
    public async Task WorkflowProcurementCallsTypedSiteStarterWithAccountDetails()
    {
        var priorRole = Environment.GetEnvironmentVariable("ROLE");
        Environment.SetEnvironmentVariable("ROLE", "sharepoint");
        try
        {
            var accountId = Guid.Parse("22222222-3333-4444-8555-666666666666");
            var store = new FakeAccountStore();
            store.QueryResults.Enqueue(new List<JObject>
            {
                new()
                {
                    ["accountId"] = RootAccountId,
                    ["oids"] = new JArray(),
                    ["casesAccounts"] = new JArray(new JObject { ["accountId"] = accountId.ToString("D") })
                }
            });
            store.QueryResults.Enqueue(new List<JObject>
            {
                new()
                {
                    ["id"] = "details-id",
                    ["accountId"] = accountId.ToString("D"),
                    ["type"] = "ACCOUNT_DETAILS",
                    ["caseNumber"] = "CASE-123",
                    ["personInformation"] = new JObject
                    {
                        ["firstName"] = "Taylor",
                        ["lastName"] = "Smith",
                        ["emailAddress"] = "taylor@example.com"
                    }
                }
            });
            var functions = new FakeFunctionClient();
            var resourceManager = new FakeResourceManagerClient();
            var starter = new FakeSharePointSiteStarterClient();
            var events = new FakeAccountEventPublisher();
            var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
                functions, resourceManager, starter, events, NullLogger<AccountWorkflowHandler>.Instance);
            var eventRequest = JsonSerializer.SerializeToElement(new
            {
                eventKind = "WorkflowProcurement",
                siteId = accountId.ToString("D"),
                caseNumber = "CASE-123",
                firstName = "Taylor",
                lastName = "Smith",
                preferredName = "Tay",
                sharePointSiteUri = $"https://tenant.example.sharepoint.com/sites/{accountId:D}",
                useDedicatedEmailAddress = false
            });

            await handler.HandleAsync(eventRequest, CancellationToken.None);

            var starterRequest = Assert.Single(starter.Requests);
            Assert.Equal(accountId, starterRequest.AccountId);
            Assert.Equal("CASE-123", starterRequest.CaseNumber);
            Assert.Equal("Taylor", starterRequest.FirstName);
            Assert.Equal("Smith", starterRequest.LastName);
            Assert.Equal("Tay", starterRequest.PreferredName);
            Assert.Equal("taylor@example.com", starterRequest.Email);
            Assert.Equal($"https://tenant.example.sharepoint.com/sites/{accountId:D}",
                starterRequest.SharePointSiteUri.AbsoluteUri.TrimEnd('/'));
            Assert.Equal(accountId, resourceManager.AccountId);
            Assert.Equal(1, resourceManager.Calls);
            Assert.Empty(functions.Calls);
            Assert.Equal("OnboardingComplete",
                Assert.Single(events.CommunicationEvents).Value<string>("status"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROLE", priorRole);
        }
    }

    [Fact]
    public async Task WorkflowProcurementRetriesStarterWhenSearchEndpointWasAlreadySaved()
    {
        var priorRole = Environment.GetEnvironmentVariable("ROLE");
        Environment.SetEnvironmentVariable("ROLE", "sharepoint");
        try
        {
            var accountId = Guid.Parse("22222222-3333-4444-8555-666666666666");
            var store = new FakeAccountStore();
            store.QueryResults.Enqueue(new List<JObject>());
            store.QueryResults.Enqueue(new List<JObject>
            {
                new()
                {
                    ["id"] = "details-id",
                    ["accountId"] = accountId.ToString("D"),
                    ["type"] = "ACCOUNT_DETAILS",
                    ["caseNumber"] = "CASE-123",
                    ["dataSourceId"] = "existing-source",
                    ["onboardingCompletePublished"] = true,
                    ["personInformation"] = new JObject
                    {
                        ["firstName"] = "Taylor",
                        ["lastName"] = "Smith"
                    }
                }
            });
            var starter = new FakeSharePointSiteStarterClient();
            var functions = new FakeFunctionClient();
            var resourceManager = new FakeResourceManagerClient();
            var events = new FakeAccountEventPublisher();
            var handler = new AccountWorkflowHandler(store, new FakeGraphClient(), new FakeSharePointClient(),
                functions, resourceManager, starter, events, NullLogger<AccountWorkflowHandler>.Instance);
            var eventRequest = JsonSerializer.SerializeToElement(new
            {
                eventKind = "WorkflowProcurement",
                siteId = accountId.ToString("D"),
                caseNumber = "CASE-123",
                sharePointSiteUri = $"https://tenant.example.sharepoint.com/sites/{accountId:D}",
                useDedicatedEmailAddress = false
            });

            await handler.HandleAsync(eventRequest, CancellationToken.None);

            Assert.Single(starter.Requests);
            Assert.Empty(functions.Calls);
            Assert.Equal(0, resourceManager.Calls);
            Assert.Contains(store.Patches.SelectMany(patch => patch.Operations), operation =>
                operation.Path == "/sharePointSiteStarterCompleted");
            Assert.Empty(events.CommunicationEvents);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROLE", priorRole);
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public Queue<List<JObject>> QueryResults { get; } = new();
        public List<(string Container, string Query, string? Partition)> QueryTextCalls { get; } = new();
        public List<(string Container, string Query, string? Partition)> QueryDefinitionCalls { get; } = new();
        public List<(string Container, JObject Item, string PartitionKey)> Created { get; } = new();
        public List<(string Container, JObject Item, string PartitionKey)> Upserts { get; } = new();
        public List<(string Container, string Id, string PartitionKey, IReadOnlyList<PatchOperation> Operations)> Patches { get; } = new();

        public Task<List<JObject>> QueryAsync(string containerName, string query,
            string? partitionKey, CancellationToken token)
        {
            QueryTextCalls.Add((containerName, query, partitionKey));
            return Task.FromResult(QueryResults.Dequeue());
        }

        public Task<List<JObject>> QueryAsync(string containerName, QueryDefinition query,
            string? partitionKey, CancellationToken token)
        {
            QueryDefinitionCalls.Add((containerName, query.QueryText, partitionKey));
            return Task.FromResult(QueryResults.Dequeue());
        }

        public Task CreateAsync(string containerName, JObject item, string partitionKey,
            CancellationToken token)
        {
            Created.Add((containerName, (JObject)item.DeepClone(), partitionKey));
            return Task.CompletedTask;
        }

        public Task UpsertAsync(string containerName, JObject item, string partitionKey,
            CancellationToken token)
        {
            Upserts.Add((containerName, (JObject)item.DeepClone(), partitionKey));
            return Task.CompletedTask;
        }

        public Task PatchAsync(string containerName, string id, string partitionKey,
            IReadOnlyList<PatchOperation> operations, CancellationToken token)
        {
            Patches.Add((containerName, id, partitionKey, operations));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGraphClient : IGraphClient
    {
        public Dictionary<string, JObject?> UsersByEmail { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<JObject?> FindUserByEmailAsync(string email, CancellationToken token)
        {
            UsersByEmail.TryGetValue(email, out var user);
            return Task.FromResult(user);
        }

        public Task<string> SharePointTokenAsync(string scope, CancellationToken token) =>
            Task.FromResult("token");

        public Task<string> SharePointSiteIdAsync(string host, string accountId, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<JObject> GetAsync(string path, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<JObject> PostAsync(string path, object body, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<JObject> PatchAsync(string path, object body, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<JObject> DeleteAsync(string path, CancellationToken token) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSharePointClient : ISharePointClient
    {
        public string SiteUrl(string accountId) =>
            $"https://tenant.example.sharepoint.com/sites/{accountId}";

        public Task<int> EnsureUserAsync(string accountId, string userPrincipalName, CancellationToken token) =>
            throw new NotImplementedException();

        public Task SetSiteAdminAsync(string accountId, int userId, bool isAdmin, CancellationToken token) =>
            throw new NotImplementedException();

        public Task<JObject> SiteAsync(HttpMethod method, string accountId, string path, object? body,
            CancellationToken token, bool merge = false, bool verbose = false) =>
            throw new NotImplementedException();

        public Task<JObject> TenantAsync(HttpMethod method, string path, object? body, CancellationToken token,
            bool merge = false, bool verbose = false) => throw new NotImplementedException();

        public Task<JObject> SendAsync(HttpMethod method, string baseUrl, string path, object? body,
            CancellationToken token, bool merge = false, bool verbose = false) =>
            method == HttpMethod.Get && path.StartsWith("_api/SPSiteManager/status", StringComparison.Ordinal)
                ? Task.FromResult(new JObject()) : throw new NotImplementedException();
    }

    private sealed class FakeFunctionClient : IFunctionClient
    {
        public JsonElement? CallResult { get; init; }
        public List<string> Calls { get; } = [];
        public List<JsonElement> Notifications { get; } = [];

        public Task<JsonElement> CallAsync(string prefix, HttpMethod method, object? body, bool wait,
            CancellationToken token)
        {
            Calls.Add(prefix);
            return CallResult is { } result
                ? Task.FromResult(result)
                : throw new NotImplementedException();
        }

        public Task NotifyAsync(object body, CancellationToken token)
        {
            Notifications.Add(JsonSerializer.SerializeToElement(body));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResourceManagerClient : IResourceManagerClient
    {
        public int Calls { get; private set; }
        public Guid? AccountId { get; private set; }

        public Task<ResourceManagerSearchInstance> GetSearchInstanceAsync(Guid accountId,
            CancellationToken token)
        {
            Calls++;
            AccountId = accountId;
            return Task.FromResult(new ResourceManagerSearchInstance(
                Guid.Parse("33333333-4444-4555-8666-777777777777"),
                new Uri("https://search-test.search.windows.net")));
        }
    }

    private sealed class FakeSharePointSiteStarterClient : ISharePointSiteStarterClient
    {
        public List<SharePointSiteStarterRequest> Requests { get; } = [];
        public Task<Guid> StartAsync(SharePointSiteStarterRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(Guid.Parse("33333333-4444-4555-8666-777777777777"));
        }
    }

    private sealed class FakeAccountEventPublisher : IAccountEventPublisher
    {
        public List<JObject> AccountEvents { get; } = new();
        public List<JObject> CommunicationEvents { get; } = new();
        public bool FailAccountPublish { get; init; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task PublishAccountAsync(object content, CancellationToken token)
        {
            if (FailAccountPublish) throw new InvalidOperationException("simulated publish failure");
            AccountEvents.Add(JObject.FromObject(content));
            return Task.CompletedTask;
        }

        public Task PublishCommunicationAsync(object content, CancellationToken token)
        {
            CommunicationEvents.Add(JObject.FromObject(content));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }
        public string? MethodOverride { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            MethodOverride = request.Headers.TryGetValues("X-HTTP-Method", out var values)
                ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public static readonly NullLogger<T> Instance = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
