using System.Text.RegularExpressions;

namespace ResourceManager.Domain;

public static partial class SearchServiceName
{
    public static string ForOperation(string prefix, Guid operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!PrefixPattern().IsMatch(prefix))
            throw new ArgumentException("The service name prefix must be a valid name prefix.", nameof(prefix));
        var suffix = new string(operationId.ToString("N").Where(char.IsAsciiLetterOrDigit).ToArray())[..12];
        var name = $"{prefix}-{suffix}".ToLowerInvariant();
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException("The service name prefix must produce a valid Azure AI Search service name.", nameof(prefix));
        return name;
    }

    public static bool IsValid(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^(?=.{2,60}$)(?!.*--)[a-z0-9]{2}(?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,58}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
