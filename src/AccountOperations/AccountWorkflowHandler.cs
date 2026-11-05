using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AccountOperations;

public sealed class AccountPublishers : IAccountEventPublisher
{
    public EventPublisher Account { get; } = new(Settings.Required("ACCOUNT_EVENTHUB_NAME"));
    public EventPublisher Communications { get; } = new(Settings.Required("EMAIL_EVENTHUB_NAME"));

    public async ValueTask DisposeAsync()
    {
        await Account.DisposeAsync();
        await Communications.DisposeAsync();
    }

    public Task PublishAccountAsync(object content, CancellationToken token) =>
        Account.PublishAsync(content, token);

    public Task PublishCommunicationAsync(object content, CancellationToken token) =>
        Communications.PublishAsync(content, token);
}

public sealed partial class AccountWorkflowHandler(IAccountStore accounts, IGraphClient graph,
    ISharePointClient sharePoint, IFunctionClient functions, IResourceManagerClient resourceManager,
    ISharePointSiteStarterClient siteStarter,
    IAccountEventPublisher events,
    ILogger<AccountWorkflowHandler> logger) :
    IAccountEventHandler
{
    private const string RootAccountId = "00000000-0000-0000-0000-000000000000";
    // Microsoft 365 licensing service plan reference: Office 365 E1 (no Teams).
    private const string DefaultEmailLicenseSkuId = "f8ced641-8e17-4dc5-b014-f5a2d53f6ac8";
    private static string EmailLicenseSkuId =>
        string.IsNullOrWhiteSpace(Settings.Get("EMAIL_LICENSE_SKU_ID"))
            ? DefaultEmailLicenseSkuId : Settings.Required("EMAIL_LICENSE_SKU_ID");
    private readonly ILogger<AccountWorkflowHandler> log = logger;

    public Task HandleAsync(JsonElement content, CancellationToken token)
    {
        var request = JObject.Parse(content.GetRawText());
        var kind = Text(request, "eventKind") ?? throw new InvalidEventException("Event is missing eventKind");
        var role = Settings.Required("ROLE");
        return (role, kind) switch
        {
            ("account", "AccountInitialization") => AssignAccountIdentityAsync(request, token),
            ("account", "CaseAccountUserLink") => LinkUserAsync(request, token),
            ("account", "AccountCleanup") => CleanupAccountAsync(request, token),
            ("account", "AccountInformationRefinement") => UpsertAccountInformationAsync(request, token),
            ("sharepoint", "SharePointAccessGrant") => GrantSharePointAccessAsync(request, token),
            ("sharepoint", "SharePointAccessRevoke") => RevokeSharePointAccessAsync(request, token),
            ("sharepoint", "SharePointSiteProcurement") => CreateSharePointSiteAsync(request, token),
            ("sharepoint", "WorkflowProcurement") => ProcureIngestionWorkflowsAsync(request, token),
            ("email", "EmailAccountProcurement") => ProcureEmailAccountAsync(request, token),
            _ => Task.CompletedTask
        };
    }

    private static string? Text(JToken? value, string path) => value?.SelectToken(path)?.Type == JTokenType.String
        ? value.SelectToken(path)?.Value<string>() : null;

    private static string Required(JToken? value, string path) =>
        !string.IsNullOrWhiteSpace(Text(value, path)) ? Text(value, path)! :
            throw new InvalidEventException($"Event is missing {path}");

    private static bool Flag(JToken? value, string path) =>
        value?.SelectToken(path)?.Type == JTokenType.Boolean &&
        value.SelectToken(path)!.Value<bool>();

    private static JObject Object(JToken? value, string path) =>
        value?.SelectToken(path) as JObject ?? new JObject();

    private static JArray Array(JToken? value, string path) =>
        value?.SelectToken(path) as JArray ?? new JArray();

    private static JObject Copy(JObject value) => (JObject)value.DeepClone();

    private static void SetIfPresent(JObject destination, string name, JToken? source)
    {
        if (source is not null && source.Type != JTokenType.Null)
            destination[name] = source.DeepClone();
    }

    private static void FillMissingStrings(JObject destination, params string[] names)
    {
        foreach (var name in names) destination[name] ??= "";
    }

    private static string OppositionRole(string? filingRole) => filingRole?.ToLowerInvariant() switch
    {
        "plaintiff" => "Defendent",
        "defendent" => "Plaintiff",
        "petitioner" => "Respondent",
        _ => "Petitioner"
    };

    private async Task<List<string>> CaseOidsAsync(string accountId, CancellationToken token)
    {
        var roots = await accounts.QueryAsync("accounts",
            new QueryDefinition("SELECT * FROM c WHERE c.accountId = @root")
                .WithParameter("@root", RootAccountId), RootAccountId, token);
        return roots.Where(root => Array(root, "casesAccounts").OfType<JObject>()
                .Any(item => Text(item, "accountId") == accountId))
            .SelectMany(root => Array(root, "oids").Select(item => item.Value<string>()))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task NotifyCaseAsync(string accountId, string notificationType,
        string status, string? error, IEnumerable<string> oids, CancellationToken token)
    {
        var data = new Dictionary<string, string>
        {
            ["accountId"] = accountId,
            ["status"] = status
        };
        if (error is not null) data["errorMessage"] = error;
        foreach (var oid in oids)
        {
            await functions.NotifyAsync(new
            {
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
                data,
                notificationType,
                oid
            }, token);
        }
    }

    private async Task AssignAccountIdentityAsync(JObject request, CancellationToken token)
    {
        var attorneyEmail = Text(request, "attorney.emailAddress");
        var clientEmail = Text(request, "client.emailAddress");
        if (string.IsNullOrWhiteSpace(attorneyEmail) && string.IsNullOrWhiteSpace(clientEmail))
            throw new InvalidEventException("Account initialization needs an attorney or client email");
        var caseNumber = Text(request, "case.caseId") ??
            $"TBD:{DateTimeOffset.UtcNow:O}";
        log.LogInformation("AccountInitialization received for case {CaseNumber} with attorney {AttorneyEmail} and client {ClientEmail}",
            caseNumber, attorneyEmail ?? "(none)", clientEmail ?? "(none)");
        var roots = await accounts.QueryAsync("accounts",
            new QueryDefinition("SELECT * FROM c WHERE c.accountId = @root AND " +
                "(ARRAY_CONTAINS(c.emailAddresses, @attorney) OR " +
                "ARRAY_CONTAINS(c.emailAddresses, @client))")
                .WithParameter("@root", RootAccountId)
                .WithParameter("@attorney", attorneyEmail)
                .WithParameter("@client", clientEmail), RootAccountId, token);
        var root = roots.FirstOrDefault();
        var priorCase = root is null ? null : Array(root, "casesAccounts").OfType<JObject>()
            .FirstOrDefault(item => Text(item, "caseNumber") == caseNumber);
        var caseIndex = priorCase is null ? Array(root, "casesAccounts").Count
            : Array(root, "casesAccounts").IndexOf(priorCase);
        var accountId = Text(priorCase, "accountId") ?? Guid.NewGuid().ToString();
        var oids = new[] { Text(request, "attorney.id"), Text(request, "client.id") }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var created = priorCase is null;
        try
        {
            if (root is null)
            {
                var entry = new JObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["accountId"] = RootAccountId,
                    ["agreedToTerms"] = false,
                    ["isActive"] = true,
                    ["emailAddresses"] = new JArray((attorneyEmail ?? clientEmail)!),
                    ["oids"] = new JArray(oids),
                    ["casesAccounts"] = new JArray(new JObject
                    {
                        ["accountId"] = accountId,
                        ["caseNumber"] = caseNumber,
                        ["billingCode"] = Text(request, "billingCode")
                    })
                };
                await accounts.CreateAsync("accounts", entry, RootAccountId, token);
                root = entry;
                log.LogInformation("AccountInitialization created new root account entry for case {CaseNumber} and provisional account {AccountId}",
                    caseNumber, accountId);
            }
            else if (created)
            {
                var caseEntry = new
                {
                    accountId,
                    caseNumber,
                    billingCode = Text(request, "billingCode")
                };
                await accounts.PatchAsync("accounts", Required(root, "id"), RootAccountId,
                    [PatchOperation.Add("/casesAccounts/-", caseEntry)], token);
                oids.AddRange(Array(root, "oids").Select(value => value.Value<string>()!)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            else
            {
                oids.AddRange(Array(root!, "oids").Select(value => value.Value<string>()!)
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            oids = oids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            log.LogInformation("AccountInitialization prepared notifications for account {AccountId} with oids {Oids}",
                accountId, string.Join(',', oids));
            await NotifyCaseAsync(accountId, "CASE_CREATED", "STARTED", null, oids, token);
            var payload = Copy(request);
            payload["accountId"] = accountId;
            if (!Flag(priorCase, "sharePointSiteProcurementPublished"))
            {
                var siteRequest = Copy(payload);
                siteRequest["eventKind"] = "SharePointSiteProcurement";
                await events.PublishAccountAsync(siteRequest, token);
                await accounts.PatchAsync("accounts", Required(root, "id"), RootAccountId,
                    [PatchOperation.Add($"/casesAccounts/{caseIndex}/sharePointSiteProcurementPublished", true)], token);
                log.LogInformation("AccountInitialization published SharePointSiteProcurement for account {AccountId}", accountId);
            }
            payload["eventKind"] = "AccountInformationRefinement";
            await events.PublishAccountAsync(payload, token);
            log.LogInformation("AccountInitialization published AccountInformationRefinement for account {AccountId}", accountId);
        }
        catch
        {
            log.LogError("AccountInitialization failed for case {CaseNumber}", caseNumber);
            await NotifyCaseAsync(accountId, "CASE_CREATED", "FAILED",
                "an internal service error has occurred.", oids, token);
            throw;
        }
    }

    private async Task GrantSharePointAccessAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("SharePointAccessGrant received for account {AccountId} with access level {AccessLevel}",
            accountId, Text(request, "accessLevel") ?? "USER");
        await SetSiteSharingAsync(accountId, token);
        var email = Text(request, "recipient.emailAddress");
        if (string.IsNullOrWhiteSpace(email))
        {
            log.LogWarning("SharePointAccessGrant missing recipient email for account {AccountId}", accountId);
            return;
        }
        var user = await graph.FindUserByEmailAsync(email, token);
        if (user is null)
        {
            log.LogWarning("SharePointAccessGrant recipient not found for account {AccountId} and email {Email}", accountId, email);
            return;
        }
        var oid = Text(user, "id");
        var principal = Required(user, "userPrincipalName");
        log.LogInformation("SharePointAccessGrant resolved Graph user for account {AccountId} with oid {Oid}",
            accountId, oid ?? "(unknown)");
        var userId = await sharePoint.EnsureUserAsync(accountId, principal, token);
        await RemoveRoleGroupsAsync(accountId, userId, token);
        await sharePoint.SetSiteAdminAsync(accountId, userId, false, token);
        if (Text(request, "accessLevel") == "ADMIN")
        {
            await sharePoint.SetSiteAdminAsync(accountId, userId, true, token);
            log.LogInformation("SharePointAccessGrant assigned site admin for account {AccountId} with oid {Oid} and SharePointUserId {SharePointUserId}",
                accountId, oid ?? "(unknown)", userId);
            return;
        }
        var groupSuffix = Text(request, "accessLevel") == "VIEW-ONLY" ? "Visitors" : "Members";
        var groups = await sharePoint.SiteAsync(HttpMethod.Get, accountId,
            "_api/web/sitegroups?$select=Id,Title", null, token);
        var group = (groups["d"]?["results"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(item => Text(item, "Title")?.EndsWith(groupSuffix,
                StringComparison.Ordinal) == true);
        if (group is null) throw new InvalidDataException($"SharePoint {groupSuffix} group was not found");
        var groupId = group["Id"]?.Value<int>() ?? throw new InvalidDataException("Group ID is missing");
        await sharePoint.SiteAsync(HttpMethod.Post, accountId,
            $"_api/web/sitegroups({groupId})/users",
            new { __metadata = new { type = "SP.User" },
                LoginName = $"i:0#.f|membership|{principal}" }, token, verbose: true);
        log.LogInformation("SharePointAccessGrant added user to {GroupSuffix} for account {AccountId} with oid {Oid} and SharePointUserId {SharePointUserId}",
            groupSuffix, accountId, oid ?? "(unknown)", userId);
    }

    private async Task RevokeSharePointAccessAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("SharePointAccessRevoke received for account {AccountId}", accountId);
        await SetSiteSharingAsync(accountId, token);
        var email = Text(request, "recipient.emailAddress");
        if (string.IsNullOrWhiteSpace(email))
        {
            log.LogWarning("SharePointAccessRevoke missing recipient email for account {AccountId}", accountId);
            return;
        }
        var user = await graph.FindUserByEmailAsync(email, token);
        if (user is null)
        {
            log.LogWarning("SharePointAccessRevoke recipient not found for account {AccountId} and email {Email}", accountId, email);
            return;
        }
        var oid = Text(user, "id");
        var userPrincipal = Required(user, "userPrincipalName");
        log.LogInformation("SharePointAccessRevoke resolved Graph user for account {AccountId} with oid {Oid}",
            accountId, oid ?? "(unknown)");
        var userId = await sharePoint.EnsureUserAsync(accountId, userPrincipal, token);
        await RemoveRoleGroupsAsync(accountId, userId, token);
        await sharePoint.SetSiteAdminAsync(accountId, userId, false, token);
        log.LogInformation("SharePointAccessRevoke completed for account {AccountId} with oid {Oid} and SharePointUserId {SharePointUserId}",
            accountId, oid ?? "(unknown)", userId);
    }

    private async Task LinkUserAsync(JObject request, CancellationToken token)
    {
        var requesterOid = Text(request, "requesterOid");
        if (string.IsNullOrWhiteSpace(requesterOid)) return;
        var accountId = Required(request, "caseId");
        var recipientEmail = Required(request, "recipient.emailAddress");
        log.LogInformation("CaseAccountUserLink received for account {AccountId} from requester {RequesterOid} to recipient {RecipientEmail}",
            accountId, requesterOid, recipientEmail);
        var roots = await accounts.QueryAsync("accounts",
            new QueryDefinition("SELECT * FROM c WHERE c.accountId = @root")
                .WithParameter("@root", RootAccountId), RootAccountId, token);
        var requesterAllowed = roots.Any(root =>
            Array(root, "oids").Any(oid => oid.Value<string>() == requesterOid) &&
            Array(root, "casesAccounts").OfType<JObject>().Any(caseEntry =>
                Text(caseEntry, "accountId") == accountId && !Flag(caseEntry, "isGuest")));
        if (!requesterAllowed)
        {
            log.LogWarning("CaseAccountUserLink requester {RequesterOid} is not authorized for account {AccountId}",
                requesterOid, accountId);
            return;
        }
        var recipient = roots.FirstOrDefault(root =>
            Array(root, "emailAddresses").Any(address =>
                string.Equals(address.Value<string>(), recipientEmail,
                    StringComparison.OrdinalIgnoreCase)) && Text(root, "type") != "SHARED_RESOURCE");
        var guestCase = new JObject
        {
            ["accountId"] = accountId,
            ["caseNumber"] = Text(request, "caseNumber"),
            ["agreedToCaseTerms"] = Flag(request, "agreedToCaseTerms"),
            ["documentAccessRole"] = Text(request, "documentAccessRole"),
            ["requestDate"] = Text(request, "requestDate"),
            ["isGuest"] = true,
            ["isActive"] = true
        };
        foreach (var field in new[] { "canAccessDocuments", "canGenerateDeclaration", "canUseQA" })
            SetIfPresent(guestCase, field, request[field]);
        bool shouldGrantAccess;
        if (recipient is null)
        {
            var graphUser = await graph.FindUserByEmailAsync(recipientEmail, token);
            var created = new JObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["accountId"] = RootAccountId,
                ["agreedToTerms"] = false,
                ["isActive"] = true,
                ["casesAccounts"] = new JArray(guestCase),
                ["emailAddresses"] = new JArray(recipientEmail),
                ["oids"] = graphUser is null ? new JArray() :
                    new JArray(Required(graphUser, "id"))
            };
            await accounts.CreateAsync("accounts", created, RootAccountId, token);
            shouldGrantAccess = graphUser is not null;
            log.LogInformation("CaseAccountUserLink created guest record for account {AccountId} with new oid {Oid}",
                accountId, graphUser is null ? "(unknown)" : Required(graphUser, "id"));
        }
        else
        {
            var cases = Array(recipient, "casesAccounts");
            var existingCase = cases.OfType<JObject>()
                .FirstOrDefault(item => Text(item, "accountId") == accountId);
            if (existingCase is not null && !Flag(existingCase, "isGuest")) return;
            if (existingCase is not null && Flag(existingCase, "isActive") &&
                Flag(recipient, "isActive")) return;
            var updated = Copy(recipient);
            var updatedCases = Array(updated, "casesAccounts");
            if (existingCase is null)
                updatedCases.Add(guestCase);
            else
                updatedCases.OfType<JObject>()
                    .First(item => Text(item, "accountId") == accountId)["isActive"] = true;
            updated["casesAccounts"] = updatedCases;
            await accounts.UpsertAsync("accounts", updated, RootAccountId, token);
            shouldGrantAccess = Array(updated, "oids").Count > 0;
            log.LogInformation("CaseAccountUserLink updated existing guest record for account {AccountId} with oids {Oids}",
                accountId, string.Join(',', Array(updated, "oids").Select(o => o.Value<string>()).Where(v => !string.IsNullOrWhiteSpace(v))));
        }
        if (shouldGrantAccess)
        {
            await events.PublishAccountAsync(new
            {
                eventKind = "SharePointAccessGrant",
                accessLevel = "USER",
                accountId,
                recipient = new { emailAddress = recipientEmail }
            }, token);
            log.LogInformation("CaseAccountUserLink published SharePointAccessGrant for account {AccountId} to recipient {RecipientEmail}",
                accountId, recipientEmail);
        }
        await events.PublishCommunicationAsync(new
        {
            eventKind = "Communication",
            status = "ShareRequest",
            accountId,
            emailAddress = recipientEmail
        }, token);
        log.LogInformation("CaseAccountUserLink published Communication for account {AccountId} to recipient {RecipientEmail}",
            accountId, recipientEmail);
    }

    private async Task CleanupAccountAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("AccountCleanup received for account {AccountId}", accountId);
        await RemoveEmailAccountAsync(request, token);
        await RemoveSharePointSiteAsync(request, token);
        await RemoveAccountAsync(request, token);
        log.LogInformation("AccountCleanup completed for account {AccountId}", accountId);
    }

    private async Task RemoveEmailAccountAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        if (!Flag(request, "deactivate.emailIngestion"))
        {
            log.LogInformation("AccountCleanup skipping email ingestion removal for account {AccountId}", accountId);
            return;
        }
        log.LogInformation("AccountCleanup removing email ingestion account for account {AccountId}", accountId);
        var entries = await accounts.QueryAsync("accounts",
            "SELECT * FROM c WHERE c.type = 'ACCOUNT_DETAILS'", accountId, token);
        var entry = entries.SingleOrDefault();
        if (entry is null)
        {
            log.LogWarning("AccountCleanup found no account details while removing email account for {AccountId}", accountId);
            return;
        }
        var email = Text(entry, "ingestionServiceAccount");
        if (string.IsNullOrWhiteSpace(email))
        {
            log.LogInformation("AccountCleanup no ingestion email present for account {AccountId}", accountId);
            return;
        }
        var encodedEmail = Uri.EscapeDataString(email);
        try
        {
            var user = await graph.GetAsync($"users/{encodedEmail}?$select=id,assignedLicenses", token);
            log.LogInformation("AccountCleanup fetched ingestion user {Email} with oid {Oid} for account {AccountId}",
                email, Text(user, "id") ?? "(unknown)", accountId);
            if ((user["assignedLicenses"] as JArray)?.Any(item =>
                    Text(item, "skuId") == EmailLicenseSkuId) == true)
                await graph.PostAsync($"users/{encodedEmail}/assignLicense", new
                {
                    addLicenses = System.Array.Empty<object>(),
                    removeLicenses = new[] { EmailLicenseSkuId }
                }, token);
            await graph.DeleteAsync($"users/{encodedEmail}", token);
            log.LogInformation("AccountCleanup deleted ingestion user {Email} for account {AccountId}", email, accountId);
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        var secretName = Text(entry, "secretname");
        if (!string.IsNullOrWhiteSpace(secretName))
        {
            var vault = new SecretClient(new Uri(Settings.Required("ONBOARDING_KEYVAULT_URI")),
                Settings.Credential);
            try { await vault.StartDeleteSecretAsync(secretName, token); }
            catch (RequestFailedException error) when (error.Status == 404) { }
            log.LogInformation("AccountCleanup deleted Key Vault secret {SecretName} for account {AccountId}", secretName, accountId);
        }
        var operations = new List<PatchOperation>();
        if (entry["ingestionServiceAccount"] is not null)
            operations.Add(PatchOperation.Remove("/ingestionServiceAccount"));
        if (entry["secretname"] is not null)
            operations.Add(PatchOperation.Remove("/secretname"));
        if (operations.Count > 0)
            await accounts.PatchAsync("accounts", Required(entry, "id"), accountId,
                operations, token);
        log.LogInformation("AccountCleanup cleared email ingestion metadata for account {AccountId}", accountId);
    }

    private async Task RemoveAccountAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        if (!Flag(request, "deactivate.sharePoint"))
        {
            log.LogInformation("AccountCleanup skipping SharePoint/account removal for account {AccountId}", accountId);
            return;
        }
        var oids = await CaseOidsAsync(accountId, token);
        try
        {
            log.LogInformation("AccountCleanup removing account {AccountId} affecting oids {Oids}",
                accountId, string.Join(',', oids));
            await NotifyCaseAsync(accountId, "CASE_DELETED", "STARTED", null, oids, token);
            var entries = await accounts.QueryAsync("accounts",
                "SELECT * FROM c WHERE c.type = 'ACCOUNT_DETAILS'", accountId, token);
            foreach (var entry in entries)
            {
                var dataSourceId = Text(entry, "dataSourceId");
                var searchEndpoint = Text(entry, "searchEndpoint");
                if (string.IsNullOrWhiteSpace(dataSourceId) && string.IsNullOrWhiteSpace(searchEndpoint))
                {
                    log.LogInformation("AccountCleanup found no search index to purge for account {AccountId}", accountId);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(dataSourceId) || string.IsNullOrWhiteSpace(searchEndpoint))
                        throw new InvalidDataException($"Incomplete search index metadata for account {accountId}");
                    await functions.CallAsync("DOCUMENT_INGRESS", HttpMethod.Delete, new
                    {
                        accountId,
                        documentName = "not important",
                        isCurrentOrder = false,
                        sourceId = "PURGE_ACCOUNT"
                    }, wait: true, token);
                }
                var operations = new List<PatchOperation>();
                if (entry["dataSourceId"] is not null)
                    operations.Add(PatchOperation.Remove("/dataSourceId"));
                if (entry["searchEndpoint"] is not null)
                    operations.Add(PatchOperation.Remove("/searchEndpoint"));
                operations.Add(PatchOperation.Set("/isActive", false));
                await accounts.PatchAsync("accounts", Required(entry, "id"), accountId,
                    operations, token);
            }
            var roots = await accounts.QueryAsync("accounts",
                new QueryDefinition("SELECT * FROM c WHERE c.accountId = @root")
                    .WithParameter("@root", RootAccountId), RootAccountId, token);
            foreach (var root in roots)
            {
                var cases = Array(root, "casesAccounts");
                for (var index = cases.Count - 1; index >= 0; index--)
                {
                    if (Text(cases[index], "accountId") != accountId) continue;
                    await accounts.PatchAsync("accounts", Required(root, "id"), RootAccountId,
                        [PatchOperation.Remove($"/casesAccounts/{index}")], token);
                }
            }
            await NotifyCaseAsync(accountId, "CASE_DELETED", "SUCCESS", null, oids, token);
            log.LogInformation("AccountCleanup removed account {AccountId}", accountId);
        }
        catch
        {
            log.LogError("AccountCleanup failed for account {AccountId}", accountId);
            await NotifyCaseAsync(accountId, "CASE_DELETED", "FAILED",
                "there were issues processing the owner's request.", oids, token);
            throw;
        }
    }

    private async Task CreateSharePointSiteAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("SharePointSiteProcurement received for account {AccountId}", accountId);
        var siteUrl = sharePoint.SiteUrl(accountId);
        var status = await SiteStatusAsync(siteUrl, token);
        var siteId = Text(status, "d.Status.SiteId");
        if (string.IsNullOrWhiteSpace(siteId))
        {
            var site = await sharePoint.SendAsync(HttpMethod.Post,
                $"https://{Settings.Required("SHAREPOINT_HOST")}", "_api/SPSiteManager/create",
                new
                {
                    request = new
                    {
                        Title = $"{Text(request, "client.preferredName")} - {Text(request, "case.caseId")}",
                        Url = siteUrl,
                        Description = Text(request, "case.description"),
                        Owner = Settings.Required("SHAREPOINT_SITE_OWNER_UPN"),
                        Lcid = 1033,
                        WebTemplate = "SITEPAGEPUBLISHING#0",
                        SiteDesignId = Guid.NewGuid().ToString(),
                        ShareByEmailEnabled = true
                    }
                }, token);
            siteId = Text(site, "d.Create.SiteId") ??
                throw new InvalidDataException("SharePoint site creation returned no site ID");
            log.LogInformation("SharePointSiteProcurement created site {SiteId} for account {AccountId}",
                siteId, accountId);
        }
        else
        {
            log.LogInformation("SharePointSiteProcurement reusing existing site {SiteId} for account {AccountId}",
                siteId, accountId);
        }
        await SetSiteSharingByIdAsync(siteId, token);
        foreach (var emailPath in new[] { "attorney.emailAddress", "client.emailAddress" })
        {
            var email = Text(request, emailPath);
            if (string.IsNullOrWhiteSpace(email)) continue;
            var user = await graph.FindUserByEmailAsync(email, token);
            if (user is null) continue;
            var oid = Text(user, "id");
            var userId = await sharePoint.EnsureUserAsync(accountId,
                Required(user, "userPrincipalName"), token);
            await sharePoint.SetSiteAdminAsync(accountId, userId, true, token);
            log.LogInformation("SharePointSiteProcurement set site admin for account {AccountId} with oid {Oid} and SharePointUserId {SharePointUserId}",
                accountId, oid ?? "(unknown)", userId);
        }
        await events.PublishAccountAsync(new
        {
            eventKind = "WorkflowProcurement",
            siteId = accountId,
            caseNumber = Text(request, "case.caseId"),
            firstName = Text(request, "client.firstName"),
            lastName = Text(request, "client.lastName"),
            preferredName = Text(request, "client.preferredName"),
            sharePointSiteUri = siteUrl,
            useDedicatedEmailAddress = Flag(request, "useDedicatedEmailAddress")
        }, token);
        log.LogInformation("SharePointSiteProcurement published WorkflowProcurement for account {AccountId}", accountId);
    }

    private async Task RemoveSharePointSiteAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        if (!Flag(request, "deactivate.sharePoint"))
        {
            log.LogInformation("AccountCleanup skipping SharePoint site removal for account {AccountId}", accountId);
            return;
        }
        log.LogInformation("AccountCleanup removing SharePoint site for account {AccountId}", accountId);
        var siteUrl = sharePoint.SiteUrl(accountId);
        var status = await SiteStatusAsync(siteUrl, token);
        var siteId = Text(status, "d.Status.SiteId");
        if (string.IsNullOrWhiteSpace(siteId))
        {
            log.LogInformation("AccountCleanup found no SharePoint site for account {AccountId}", accountId);
            return;
        }
        await sharePoint.SendAsync(HttpMethod.Post,
            $"https://{Settings.Required("SHAREPOINT_HOST")}", "_api/SPSiteManager/delete",
            new { siteId }, token);
        log.LogInformation("AccountCleanup requested SharePoint site deletion for account {AccountId}", accountId);
    }

    private Task<JObject> SiteStatusAsync(string siteUrl, CancellationToken token) =>
        sharePoint.SendAsync(HttpMethod.Get,
            $"https://{Settings.Required("SHAREPOINT_HOST")}",
            $"_api/SPSiteManager/status?url='{Uri.EscapeDataString(siteUrl)}'", null, token);

    private async Task SetSiteSharingAsync(string accountId, CancellationToken token)
    {
        var siteId = await graph.SharePointSiteIdAsync(Settings.Required("SHAREPOINT_HOST"),
            accountId, token);
        await SetSiteSharingByIdAsync(siteId, token);
    }

    private Task<JObject> SetSiteSharingByIdAsync(string siteId, CancellationToken token) =>
        sharePoint.TenantAsync(HttpMethod.Patch, $"_api/SPO.Tenant/sites('{siteId}')",
            new
            {
                SharingCapability = 2,
                DefaultLinkPermission = 0,
                SharingDomainRestrictionMode = 0,
                SharingAllowedDomainList = "",
                SharingBlockedDomainList = ""
            }, token);

    private async Task RemoveRoleGroupsAsync(string accountId, int userId,
        CancellationToken token)
    {
        var groups = await sharePoint.SiteAsync(HttpMethod.Get, accountId,
            $"_api/web/siteusers/getbyid({userId})/Groups?$select=Id,Title", null, token);
        foreach (var group in (groups["d"]?["results"] as JArray)?.OfType<JObject>() ?? [])
        {
            var title = Text(group, "Title");
            if (title is null || !(title.EndsWith("Owners", StringComparison.Ordinal) ||
                title.EndsWith("Members", StringComparison.Ordinal) ||
                title.EndsWith("Visitors", StringComparison.Ordinal))) continue;
            var groupId = group["Id"]?.Value<int>() ??
                throw new InvalidDataException("SharePoint role group ID is missing");
            await sharePoint.SiteAsync(HttpMethod.Post, accountId,
                $"_api/web/sitegroups({groupId})/users/removebyid({userId})", null, token);
        }
    }

    private async Task ProcureIngestionWorkflowsAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "siteId");
        if (!Guid.TryParse(accountId, out var parsedAccountId))
            throw new InvalidEventException("Event field siteId must be a GUID");
        var oids = await CaseOidsAsync(accountId, token);
        try
        {
            log.LogInformation("WorkflowProcurement received for account {AccountId} with oids {Oids}",
                accountId, string.Join(',', oids));
            var entries = await accounts.QueryAsync("accounts",
                "SELECT * FROM c WHERE c.type = 'ACCOUNT_DETAILS'", accountId, token);
            var entry = entries.SingleOrDefault() ??
                throw new InvalidDataException($"Account details are missing for {accountId}");
            if (string.IsNullOrWhiteSpace(Text(entry, "dataSourceId")))
            {
                var searchInstance = await resourceManager.GetSearchInstanceAsync(parsedAccountId, token);
                var handle = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var searchEndpoint = searchInstance.SearchEndpoint.ToString();
                await accounts.PatchAsync("accounts", Required(entry, "id"), accountId,
                    [PatchOperation.Add("/dataSourceId", handle),
                        PatchOperation.Add("/searchEndpoint", searchEndpoint)], token);
                entry["dataSourceId"] = handle;
                entry["searchEndpoint"] = searchEndpoint;
                log.LogInformation(
                    "WorkflowProcurement set search endpoint for account {AccountId}, Resource Manager operation {OperationId}",
                    accountId, searchInstance.OperationId);
            }

            if (!Flag(entry, "sharePointSiteStarterCompleted"))
            {
                var firstName = Text(request, "firstName") ?? Required(entry, "personInformation.firstName");
                var lastName = Text(request, "lastName") ?? Required(entry, "personInformation.lastName");
                var requestBody = new SharePointSiteStarterRequest(
                    parsedAccountId, Text(request, "caseNumber") ?? Required(entry, "caseNumber"),
                    firstName, lastName, Text(request, "preferredName"),
                    Text(entry, "personInformation.emailAddress"),
                    new Uri(Required(request, "sharePointSiteUri")));
                var starterOperationId = await siteStarter.StartAsync(requestBody, token);
                await SetAccountMarkerAsync(accountId, entry, "sharePointSiteStarterCompleted", token);
                log.LogInformation("WorkflowProcurement completed SharePoint site preparation for account {AccountId}, operation {OperationId}",
                    accountId, starterOperationId);
            }

            if (!Flag(entry, "onboardingCompletePublished"))
            {
                await events.PublishCommunicationAsync(new
                {
                    eventKind = "Communication",
                    status = "OnboardingComplete",
                    accountId,
                    url = Text(request, "sharePointSiteUri")
                }, token);
                await SetAccountMarkerAsync(accountId, entry, "onboardingCompletePublished", token);
                log.LogInformation("WorkflowProcurement published OnboardingComplete communication for account {AccountId}", accountId);
            }

            if (Flag(request, "useDedicatedEmailAddress") &&
                !Flag(entry, "emailAccountProcurementRequested"))
            {
                await events.PublishAccountAsync(new { eventKind = "EmailAccountProcurement", accountId }, token);
                await SetAccountMarkerAsync(accountId, entry, "emailAccountProcurementRequested", token);
                log.LogInformation("WorkflowProcurement requested EmailAccountProcurement for account {AccountId}", accountId);
            }
            await NotifyCaseAsync(accountId, "CASE_CREATED", "SUCCESS", null, oids, token);
            log.LogInformation("WorkflowProcurement completed for account {AccountId}", accountId);
        }
        catch
        {
            log.LogError("WorkflowProcurement failed for account {AccountId}", accountId);
            await NotifyCaseAsync(accountId, "CASE_CREATED", "FAILED",
                "preparations failed to complete while provisioning for this case", oids, token);
            throw;
        }
    }

    private async Task SetAccountMarkerAsync(string accountId, JObject entry, string marker,
        CancellationToken token)
    {
        var operation = entry[marker] is null
            ? PatchOperation.Add($"/{marker}", true)
            : PatchOperation.Set($"/{marker}", true);
        await accounts.PatchAsync("accounts", Required(entry, "id"), accountId, [operation], token);
        entry[marker] = true;
    }

    private async Task UpsertAccountInformationAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("AccountInformationRefinement received for account {AccountId}", accountId);
        var existing = await accounts.QueryAsync("accounts",
            "SELECT * FROM c WHERE c.type='ACCOUNT_DETAILS'", accountId, token);
        var item = existing.Count > 0 ? Copy(existing[0]) : new JObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["accountId"] = accountId,
            ["type"] = "ACCOUNT_DETAILS"
        };
        var isNew = existing.Count == 0;
        if (isNew || item["caseNumber"] is null)
            SetIfPresent(item, "caseNumber", request.SelectToken("case.caseId"));
        foreach (var (field, path) in new[]
        {
            ("county", "case.county"), ("description", "case.description"),
            ("filingRole", "case.filingRole"), ("state", "case.state")
        }) SetIfPresent(item, field, request.SelectToken(path));

        var person = Object(item, "personInformation");
        foreach (var (field, path) in new[]
        {
            ("emailAddress", "client.emailAddress"), ("firstName", "client.firstName"),
            ("lastName", "client.lastName"), ("organization", "client.organization"),
            ("phoneNumber", "client.phoneNumber"), ("middleInitial", "client.middleName")
        }) SetIfPresent(person, field, request.SelectToken(path));
        item["personInformation"] = person;

        var priorAttorney = Array(item, "attorneyInformation").LastOrDefault() as JObject;
        var attorney = priorAttorney is null ? new JObject() : Copy(priorAttorney);
        foreach (var (field, path) in new[]
        {
            ("emailAddress", "attorney.emailAddress"), ("firmName", "attorney.firmName"),
            ("firstName", "attorney.firstName"), ("lastName", "attorney.lastName"),
            ("phoneNumber", "attorney.phoneNumber")
        }) SetIfPresent(attorney, field, request.SelectToken(path));
        attorney["isCurrent"] = true;
        item["attorneyInformation"] = new JArray(attorney);

        var opposition = Object(item, "opposition");
        foreach (var (field, path) in new[]
        {
            ("emailAddress", "opposition.client.emailAddress"),
            ("firstName", "opposition.client.firstName"),
            ("lastName", "opposition.client.lastName"),
            ("organization", "opposition.client.organization"),
            ("phoneNumber", "opposition.client.phoneNumber")
        }) SetIfPresent(opposition, field, request.SelectToken(path));
        opposition["filingRole"] = OppositionRole(Text(request, "case.filingRole"));
        var oppositionAttorney = Array(opposition, "attorneyInformation").LastOrDefault() as JObject;
        var currentOppositionAttorney = oppositionAttorney is null ? new JObject() :
            Copy(oppositionAttorney);
        foreach (var (field, path) in new[]
        {
            ("emailAddress", "opposition.attorney.emailAddress"),
            ("firmName", "opposition.attorney.firmName"),
            ("firstName", "opposition.attorney.firstName"),
            ("lastName", "opposition.attorney.lastName"),
            ("phoneNumber", "opposition.attorney.phoneNumber")
        }) SetIfPresent(currentOppositionAttorney, field, request.SelectToken(path));
        currentOppositionAttorney["isCurrent"] = true;
        opposition["attorneyInformation"] = new JArray(currentOppositionAttorney);
        item["opposition"] = opposition;
        if (isNew)
        {
            FillMissingStrings(item, "caseNumber", "county", "description", "filingRole", "state");
            FillMissingStrings(person, "emailAddress", "firstName", "lastName",
                "middleInitial", "organization", "phoneNumber");
            FillMissingStrings(attorney, "emailAddress", "firmName", "firstName",
                "lastName", "phoneNumber");
            FillMissingStrings(opposition, "emailAddress", "firstName", "lastName",
                "organization", "phoneNumber");
            FillMissingStrings(currentOppositionAttorney, "emailAddress", "firmName",
                "firstName", "lastName", "phoneNumber");
        }
        if (Flag(request, "addGoldFeatures"))
            item["extendedControls"] ??= new JObject
            {
                ["verbosity"] = true,
                ["swapRoles"] = true
            };

        if (isNew) await accounts.CreateAsync("accounts", item, accountId, token);
        else await accounts.UpsertAsync("accounts", item, accountId, token);
        log.LogInformation("AccountInformationRefinement saved details for account {AccountId} (isNew: {IsNew})",
            accountId, isNew);
    }

    private async Task ProcureEmailAccountAsync(JObject request, CancellationToken token)
    {
        var accountId = Required(request, "accountId");
        log.LogInformation("EmailAccountProcurement received for account {AccountId}", accountId);
        var entries = await accounts.QueryAsync("accounts",
            "SELECT * FROM c WHERE c.type = 'ACCOUNT_DETAILS'", accountId, token);
        var entry = entries.SingleOrDefault() ??
            throw new InvalidDataException($"Account details are missing for {accountId}");
        if (!string.IsNullOrWhiteSpace(Text(entry, "ingestionServiceAccount"))) return;
        var localPart = Text(request, "customEmailAddressName") ??
            accountId.Replace("-", "", StringComparison.Ordinal);
        if (!Regex.IsMatch(localPart, "^[A-Za-z0-9._+-]+$"))
            throw new InvalidEventException("Invalid custom email address name");
        var email = $"{localPart}@{Settings.Required("ACCOUNT_EMAIL_DOMAIN")}";
        var secretName = $"account-operations-{accountId}";
        var vault = new SecretClient(new Uri(Settings.Required("ONBOARDING_KEYVAULT_URI")),
            Settings.Credential);
        string password;
        try { password = (await vault.GetSecretAsync(secretName, cancellationToken: token)).Value.Value; }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "Aa1!";
            await vault.SetSecretAsync(secretName, password, token);
            log.LogInformation("EmailAccountProcurement generated new password secret {SecretName} for account {AccountId}",
                secretName, accountId);
        }
        JObject? user;
        try { user = await graph.GetAsync(
            $"users/{Uri.EscapeDataString(email)}?$select=id,displayName,assignedLicenses", token); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        { user = null; }
        if (user is null)
        {
            user = await graph.PostAsync("users", new
            {
                accountEnabled = true,
                displayName = $"Service Account for {accountId}",
                mailNickname = $"ACCOUNT_{accountId}",
                passwordProfile = new
                {
                    forceChangePasswordNextSignIn = false,
                    password
                },
                usageLocation = "US",
                userPrincipalName = email
            }, token);
            log.LogInformation("EmailAccountProcurement created ingestion user {Email} with oid {Oid} for account {AccountId}",
                email, Required(user, "id"), accountId);
        }
        else if (Text(user, "displayName") != $"Service Account for {accountId}")
            throw new InvalidDataException($"Email address {email} belongs to another account");
        var userId = Required(user, "id");
        if ((user["assignedLicenses"] as JArray)?.Any(item =>
                Text(item, "skuId") == EmailLicenseSkuId) != true)
            await graph.PostAsync($"users/{Uri.EscapeDataString(userId)}/assignLicense", new
            {
                addLicenses = new[] { new { skuId = EmailLicenseSkuId } },
                removeLicenses = System.Array.Empty<string>()
            }, token);
        log.LogInformation("EmailAccountProcurement ensured license for user {Email} (oid {Oid}) on account {AccountId}",
            email, userId, accountId);
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        await accounts.PatchAsync("accounts", Required(entry, "id"), accountId,
            [PatchOperation.Set("/ingestionServiceAccount", email),
                PatchOperation.Set("/secretname", secretName)], token);
        log.LogInformation("EmailAccountProcurement saved ingestion account {Email} for account {AccountId}",
            email, accountId);
        await events.PublishCommunicationAsync(new
        {
            accountId,
            eventKind = "Communication",
            status = "EmailIngestionComplete"
        }, token);
        log.LogInformation("EmailAccountProcurement published EmailIngestionComplete for account {AccountId}", accountId);
    }
}
