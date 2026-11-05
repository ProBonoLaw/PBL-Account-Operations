using Microsoft.Extensions.Options;

namespace SharePointSiteStarter.Configuration;

public sealed class SharePointSiteStarterOptionsValidator : IValidateOptions<SharePointSiteStarterOptions>
{
    public ValidateOptionsResult Validate(string? name, SharePointSiteStarterOptions options)
    {
        var errors = new List<string>();
        if (options.TenantId == Guid.Empty)
            errors.Add("TenantId must be a non-empty GUID.");
        if (options.ClientId == Guid.Empty)
            errors.Add("ClientId must be a non-empty GUID.");
        if (!Uri.TryCreate(options.CertificateKeyVaultUri, UriKind.Absolute, out var vaultUri) ||
            vaultUri.Scheme != Uri.UriSchemeHttps ||
            !vaultUri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
            errors.Add("CertificateKeyVaultUri must be an HTTPS Azure Key Vault URI.");
        if (string.IsNullOrWhiteSpace(options.CertificateSecretName))
            errors.Add("CertificateSecretName is required.");
        if (!IsValidSharePointHost(options.SharePointHost))
            errors.Add("SharePointHost must be a SharePoint Online tenant host.");
        if (string.IsNullOrWhiteSpace(options.AdminApiKey) || options.AdminApiKey.Length < 32)
            errors.Add("AdminApiKey must contain at least 32 characters.");
        if (options.DocumentsLibraryFolders.Any(string.IsNullOrWhiteSpace))
            errors.Add("DocumentsLibraryFolders cannot contain empty entries.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static bool IsValidSharePointHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        Uri.TryCreate($"https://{host}", UriKind.Absolute, out var uri) &&
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase);
}
