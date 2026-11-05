namespace ResourceManager.Domain;

public static class SearchCapacitySelector
{
    public static SearchServiceCapacity? SelectFirstAvailable(
        IEnumerable<SearchServiceCapacity> services,
        long minimumHeadroomBytes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumHeadroomBytes);

        return services.FirstOrDefault(service => service.HasCapacity(minimumHeadroomBytes));
    }
}
