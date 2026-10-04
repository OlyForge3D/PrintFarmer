using System.Diagnostics.CodeAnalysis;

namespace Farm.Infrastructure.Services.HostUpdates;

internal static class HostUpdateValidation
{
    public static bool IsCanonicalDigest(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSemanticVersion(string? value) =>
        TryParseSemanticVersion(value, out _);

    public static bool TryCompareSemanticVersions(string? left, string? right, out int result)
    {
        result = 0;
        if (!TryParseSemanticVersion(left, out SemanticVersion? leftVersion) ||
            !TryParseSemanticVersion(right, out SemanticVersion? rightVersion))
        {
            return false;
        }

        int[] parsedLeft = leftVersion.Release;
        int[] parsedRight = rightVersion.Release;
        for (int i = 0; i < parsedLeft.Length; i++)
        {
            result = parsedLeft[i].CompareTo(parsedRight[i]);
            if (result != 0)
            {
                return true;
            }
        }

        result = ComparePrerelease(leftVersion.Prerelease, rightVersion.Prerelease);
        return true;
    }

    private static bool TryParseSemanticVersion(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] prereleaseSplit = value.Split('-', 2, StringSplitOptions.None);
        if (prereleaseSplit.Length > 2)
        {
            return false;
        }

        string[] releaseParts = prereleaseSplit[0].Split('.', 3, StringSplitOptions.None);
        if (releaseParts.Length != 3 || !releaseParts.All(IsSemanticVersionPart))
        {
            return false;
        }

        string[]? prerelease = null;
        if (prereleaseSplit.Length == 2)
        {
            prerelease = prereleaseSplit[1].Split('.', StringSplitOptions.None);
            if (prerelease.Length == 0 || prerelease.Any(part => part.Length == 0))
            {
                return false;
            }

            foreach (string part in prerelease)
            {
                bool numeric = int.TryParse(part, out int parsed);
                if (numeric && (parsed < 0 || part != parsed.ToString()))
                {
                    return false;
                }

                if (!numeric && part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                {
                    return false;
                }
            }
        }

        version = new(releaseParts.Select(int.Parse).ToArray(), prerelease);
        return true;
    }

    private static bool IsSemanticVersionPart(string part) =>
        int.TryParse(part, out int parsed) && parsed >= 0 && part == parsed.ToString();

    private static int ComparePrerelease(string[]? left, string[]? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        int max = Math.Max(left.Length, right.Length);
        for (int i = 0; i < max; i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            bool leftNumeric = int.TryParse(left[i], out int leftNumber);
            bool rightNumeric = int.TryParse(right[i], out int rightNumber);
            int comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private sealed record SemanticVersion(int[] Release, string[]? Prerelease);
}
