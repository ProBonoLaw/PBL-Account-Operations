using Microsoft.Extensions.Options;
using SharePointSiteStarter.Configuration;

namespace SharePointSiteStarter.Domain;

public sealed class StartSiteRequestValidator(IOptions<SharePointSiteStarterOptions> options)
{
    public IReadOnlyDictionary<string, string[]> Validate(StartSiteRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["request"] = ["A request body is required."];
            return errors;
        }

        if (request.AccountId == Guid.Empty)
            errors["accountId"] = ["AccountId must be a non-empty GUID."];
        if (string.IsNullOrWhiteSpace(request.CaseNumber))
            errors["caseNumber"] = ["CaseNumber is required."];
        if (string.IsNullOrWhiteSpace(request.FirstName))
            errors["firstName"] = ["FirstName is required."];
        if (string.IsNullOrWhiteSpace(request.LastName))
            errors["lastName"] = ["LastName is required."];
        if (!string.IsNullOrWhiteSpace(request.Email) &&
            !System.Net.Mail.MailAddress.TryCreate(request.Email, out _))
            errors["email"] = ["Email must be a valid email address."];

        var siteUri = request.SharePointSiteUri;
        if (siteUri is null || !siteUri.IsAbsoluteUri || siteUri.Scheme != Uri.UriSchemeHttps ||
            !siteUri.IsDefaultPort || !string.IsNullOrEmpty(siteUri.UserInfo) ||
            !siteUri.Host.Equals(options.Value.SharePointHost, StringComparison.OrdinalIgnoreCase) ||
            !siteUri.AbsolutePath.Equals($"/sites/{request.AccountId:D}", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(siteUri.Query) || !string.IsNullOrEmpty(siteUri.Fragment))
            errors["sharePointSiteUri"] = ["SharePointSiteUri must identify this account's site on the configured tenant."];

        return errors;
    }
}
