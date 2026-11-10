using System.Globalization;

namespace Farm.Infrastructure.Normalization;

/// <summary>
/// Validates scanned codes that may be remembered as exact-match filament aliases.
/// </summary>
public static class ScanCodeAliasValidator
{
    public const int MaxLength = 128;

    /// <summary>
    /// Returns the trimmed code when it can be stored as an alias, otherwise <c>null</c> with
    /// an error message. A value made only of digits, spaces and dashes that is a valid
    /// GTIN-8/12/13/14 is rejected, because it belongs in the GTIN mapping. Other values are
    /// kept verbatim, so <c>X0022Z40I1</c> is never reduced to its digits.
    /// </summary>
    public static string? Validate(string? code, out string? error)
    {
        string trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "Code is required.";
            return null;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"Code must be at most {MaxLength} characters.";
            return null;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                error = "Code contains control or formatting characters.";
                return null;
            }
        }

        if (IsGtinShaped(trimmed))
        {
            error = "Code is a valid GTIN. Save it with the barcode mapping instead.";
            return null;
        }

        error = null;
        return trimmed;
    }

    /// <summary>
    /// Matches the mobile client's product-barcode rule: ASCII digits, spaces and dashes only,
    /// and a valid GTIN-8/12/13/14 after removing the separators.
    /// </summary>
    public static bool IsGtinShaped(string code)
        => code.All(c => c is (>= '0' and <= '9') or ' ' or '-')
            && GtinNormalizer.Normalize(code) is not null;
}
