using System.Text;

namespace Farm.Infrastructure.Normalization;

/// <summary>
/// Applies Unicode NFKC (compatibility decomposition followed by canonical composition,
/// <see cref="NormalizationForm.FormKC"/>) so that width- and compatibility-variants of a string
/// collapse onto a single canonical form (issue #715, Hicks r4 blockers 1 &amp; 2).
///
/// <para>
/// SQL Server's default database collation on the printed-part identity columns
/// (<c>PartInventory.Sku</c>, bin codes) is <b>width-insensitive</b>, so the fullwidth text
/// <c>ＡＢＣ</c> (U+FF21..U+FF23) and ASCII <c>ABC</c> compare <b>equal</b> at the store yet are
/// distinct .NET strings in memory. Any identity we derive app-side — an idempotency route key or
/// a reserved-namespace prefix check — must fold those compatibility variants together too, or a
/// width-variant request mints a <b>distinct</b> app-side identity that nonetheless resolves to the
/// <b>same</b> physical row, re-applying a mutation under one <c>Idempotency-Key</c> (blocker 1) or
/// slipping a fullwidth <c>ｉｄｅｍ:</c> past an ordinal reserved-prefix guard (blocker 2). NFKC is the
/// canonicalization that aligns app-side identity with the store's width-insensitive comparison.
/// </para>
/// </summary>
public static class UnicodeCompatibilityNormalizer
{
    /// <summary>
    /// Returns the Unicode NFKC (<see cref="NormalizationForm.FormKC"/>) form of
    /// <paramref name="value"/>. Pure-ASCII input (the overwhelming majority of SKUs and keys) is
    /// already in NFKC, so the <see cref="string.IsNormalized(NormalizationForm)"/> fast-path
    /// returns it unchanged without allocating.
    ///
    /// <para>
    /// Malformed UTF-16 (for example an unpaired surrogate) cannot be normalized and would throw
    /// <see cref="ArgumentException"/>; such input can never equal an ASCII SKU or the ASCII
    /// reserved prefix, so we fall back to the original value rather than surfacing the
    /// normalization failure as an unhandled exception. This keeps the primitive total for any
    /// caller and preserves the pre-existing robustness of the ordinal comparisons it replaces.
    /// </para>
    /// </summary>
    /// <param name="value">The string to canonicalize. Must not be <see langword="null"/>.</param>
    /// <returns>The NFKC form of <paramref name="value"/>, or the original value when it cannot be normalized.</returns>
    public static string ToCompatibilityForm(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            return value.IsNormalized(NormalizationForm.FormKC)
                ? value
                : value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }
}
