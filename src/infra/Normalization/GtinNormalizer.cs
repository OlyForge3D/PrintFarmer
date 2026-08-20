namespace Farm.Infrastructure.Normalization;

/// <summary>
/// Normalizes scanned retail barcodes (UPC-A/GTIN-12, EAN-13/GTIN-13, GTIN-8, GTIN-14) into a
/// canonical 14-digit GTIN so barcode lookups are format-independent. GTIN formats are
/// zero-pad equivalent: <c>850078714923</c> == <c>0850078714923</c> == <c>00850078714923</c> are
/// the same product, and leading zeros never change the GS1 mod-10 check digit.
/// </summary>
public static class GtinNormalizer
{
    private const int GtinLength = 14;

    // A formatted GTIN-14 with separators (dashes/spaces) between every digit is well under
    // this many characters. Rejecting oversized raw input up front avoids scanning an
    // attacker-controlled arbitrarily long string with the digit filter below before the
    // (always-failing) length check would otherwise catch it.
    private const int MaxRawLength = 64;

    /// <summary>
    /// Normalizes a scanned barcode to a 14-digit GTIN.
    /// </summary>
    /// <param name="barcode">Raw scanned barcode value, in any GTIN-8/12/13/14 form.</param>
    /// <returns>
    /// The barcode left-zero-padded to 14 digits, or <c>null</c> if the input is null/empty,
    /// longer than a plausible formatted barcode, contains no digits, is not 8/12/13/14 digits
    /// long, or fails the GS1 mod-10 check digit.
    /// </returns>
    public static string? Normalize(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        if (barcode.Length > MaxRawLength)
        {
            return null;
        }

        string digits = new(barcode.Where(c => c is >= '0' and <= '9').ToArray());

        if (digits.Length is not (8 or 12 or 13 or 14))
        {
            return null;
        }

        if (!HasValidCheckDigit(digits))
        {
            return null;
        }

        return digits.PadLeft(GtinLength, '0');
    }

    /// <summary>
    /// Validates the GS1 mod-10 check digit (the last digit) of a GTIN-8/12/13/14 string.
    /// </summary>
    private static bool HasValidCheckDigit(string digits)
    {
        int checkDigit = digits[^1] - '0';
        int sum = 0;

        // Weighting alternates 3/1 starting from the digit immediately left of the check
        // digit, per the GS1 general specification.
        for (int i = digits.Length - 2; i >= 0; i--)
        {
            int digit = digits[i] - '0';
            bool isOddPositionFromRight = (digits.Length - 1 - i) % 2 == 1;
            sum += isOddPositionFromRight ? digit * 3 : digit;
        }

        int calculatedCheckDigit = (10 - (sum % 10)) % 10;
        return calculatedCheckDigit == checkDigit;
    }
}
