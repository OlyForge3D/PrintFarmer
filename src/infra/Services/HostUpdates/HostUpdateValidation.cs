using System.Diagnostics.CodeAnalysis;

namespace Farm.Infrastructure.Services.HostUpdates;

internal static class HostUpdateValidation
{
    public static bool IsCanonicalDigest(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('.', 3, StringSplitOptions.None);
        return parts.Length == 3 && parts.All(IsSemanticVersionPart);
    }

    public static bool TryCompareSemanticVersions(string? left, string? right, out int result)
    {
        result = 0;
        if (!TryParseSemanticVersion(left, out int[]? leftParts) || !TryParseSemanticVersion(right, out int[]? rightParts))
        {
            return false;
        }

        int[] parsedLeft = leftParts;
        int[] parsedRight = rightParts;
        for (int i = 0; i < parsedLeft.Length; i++)
        {
            result = parsedLeft[i].CompareTo(parsedRight[i]);
            if (result != 0)
            {
                return true;
            }
        }

        return true;
    }

    private static bool TryParseSemanticVersion(string? value, [NotNullWhen(true)] out int[]? parts)
    {
        parts = null;
        if (value is null || !IsSemanticVersion(value))
        {
            return false;
        }

        string[] valueParts = value.Split('.', 3, StringSplitOptions.None);
        parts = valueParts.Select(int.Parse).ToArray();
        return true;
    }

    private static bool IsSemanticVersionPart(string part) =>
        int.TryParse(part, out int parsed) && parsed >= 0 && part == parsed.ToString();
}
