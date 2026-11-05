namespace ResourceManager.Domain;

public sealed record SearchServiceCapacity(
    Guid SubscriptionId,
    string ResourceGroup,
    string ServiceName,
    Uri Endpoint,
    string? Sku,
    long StorageUsedBytes,
    long StorageQuotaBytes,
    long VectorUsedBytes,
    long VectorQuotaBytes,
    long IndexCount,
    long IndexQuota)
{
    public long StorageRemainingBytes => Math.Max(0, StorageQuotaBytes - StorageUsedBytes);

    public long VectorRemainingBytes => Math.Max(0, VectorQuotaBytes - VectorUsedBytes);

    public bool HasCapacity(long minimumHeadroomBytes) =>
        StorageRemainingBytes >= minimumHeadroomBytes &&
        VectorRemainingBytes >= minimumHeadroomBytes &&
        IndexCount < IndexQuota;
}
