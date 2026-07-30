using Farm.Infrastructure.Normalization;

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

        // NFKC folds Unicode compatibility/width variants (e.g. fullwidth ＡＢＣ → ABC, the ﬁ
        // ligature → fi) to their canonical equivalents BEFORE upper-casing, so app-side identity
        // matches SQL Server's width-insensitive default collation on the PartInventory.Sku and
        // bin-code columns (issue #715, Hicks r4 blocker 1). Without it a fullwidth SKU would mint
        // a distinct idempotency record yet resolve to the SAME physical row at the store,
        // double-applying a stock delta under a single Idempotency-Key. NFKC is a no-op for the
        // ASCII SKUs that make up the vast majority of traffic, so existing behavior is unchanged.
        return UnicodeCompatibilityNormalizer
            .ToCompatibilityForm(value.Trim())
            .ToUpperInvariant();
    }
}
