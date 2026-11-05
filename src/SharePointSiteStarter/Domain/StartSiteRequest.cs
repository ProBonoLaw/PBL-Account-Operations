using System.ComponentModel.DataAnnotations;

namespace SharePointSiteStarter.Domain;

public sealed record StartSiteRequest
{
    public Guid AccountId { get; init; }

    [Required]
    public string CaseNumber { get; init; } = "";

    [Required]
    public string FirstName { get; init; } = "";

    [Required]
    public string LastName { get; init; } = "";

    public string? PreferredName { get; init; }

    [EmailAddress]
    public string? Email { get; init; }

    [Required]
    public Uri? SharePointSiteUri { get; init; }
}

public sealed record StartSiteResponse(Guid OperationId, Guid AccountId, Uri SharePointSiteUri,
    string Status);
