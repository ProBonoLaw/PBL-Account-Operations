using System.ComponentModel.DataAnnotations;

namespace SharePointSiteStarter.Configuration;

public sealed record SharePointSiteStarterOptions
{
    public const string SectionName = "SharePointSiteStarter";

    [Required]
    public string SharePointHost { get; init; } = "";

    [Required]
    public Guid TenantId { get; init; }

    [Required]
    public Guid ClientId { get; init; }

    [Required]
    public string CertificateKeyVaultUri { get; init; } = "";

    [Required]
    public string CertificateSecretName { get; init; } = "";

    [MinLength(32)]
    public string AdminApiKey { get; init; } = "";

    public string[] DocumentsLibraryFolders { get; init; } = [];
}
