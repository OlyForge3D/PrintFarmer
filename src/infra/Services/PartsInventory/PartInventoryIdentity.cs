namespace Farm.Infrastructure.Services.PartsInventory;

/// <summary>Canonical identity normalization for printed-part SKUs and bin barcodes.</summary>
public static class PartInventoryIdentity
{
    /// <summary>Normalizes a printed-part SKU for storage and lookup.</summary>
    public static string NormalizeSku(string value)
        => Normalize(value);

    /// <summary>Normalizes a printed-part bin barcode for storage and lookup.</summary>
    public static string NormalizeBinCode(string value)
        => Normalize(value);

    /// <summary>Normalizes an optional idempotency key.</summary>
    public static string? NormalizeOperationKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToUpperInvariant();
    }
}
