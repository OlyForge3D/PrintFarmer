namespace Farm.Infrastructure.Domain.Notifications;

/// <summary>Canonical bounds and wire representations for native-push registrations.</summary>
public static class NativePushRegistrationContract
{
    /// <summary>Maximum installation identifier length.</summary>
    public const int InstallationIdMaxLength = 128;

    /// <summary>Maximum lowercase-hex APNs token length.</summary>
    public const int TokenMaxLength = 256;

    /// <summary>Minimum encoded APNs token length (32 bytes).</summary>
    public const int TokenMinLength = 64;

    /// <summary>Maximum platform token length.</summary>
    public const int PlatformMaxLength = 16;

    /// <summary>Maximum APNs environment token length.</summary>
    public const int EnvironmentMaxLength = 16;

    /// <summary>Maximum application bundle identifier length.</summary>
    public const int AppBundleIdMaxLength = 256;

    /// <summary>Data-annotation pattern for an opaque ASCII installation identifier.</summary>
    public const string InstallationIdPattern = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$";

    /// <summary>Data-annotation pattern for a canonical, byte-aligned lowercase APNs token.</summary>
    public const string ApnsTokenPattern = "^(?:[0-9a-f]{2}){32,128}$";

    /// <summary>Data-annotation pattern for the only currently supported platform.</summary>
    public const string PlatformPattern = "^ios$";

    /// <summary>Data-annotation pattern for supported APNs environments.</summary>
    public const string EnvironmentPattern = "^(?:development|production)$";

    /// <summary>Data-annotation pattern for a canonical lowercase bundle identifier.</summary>
    public const string AppBundleIdPattern = "^[a-z0-9]+(?:[.-][a-z0-9]+)*$";

    /// <summary>Returns whether an installation identifier is in canonical wire form.</summary>
    public static bool IsCanonicalInstallationId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > InstallationIdMaxLength
            || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
    }

    /// <summary>Returns whether a token is byte-aligned lowercase hexadecimal within bounds.</summary>
    public static bool IsCanonicalApnsToken(string? value)
    {
        return value is not null
            && value.Length is >= TokenMinLength and <= TokenMaxLength
            && value.Length % 2 == 0
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>Returns whether a platform is currently supported and canonical.</summary>
    public static bool IsCanonicalPlatform(string? value) => string.Equals(value, "ios", StringComparison.Ordinal);

    /// <summary>Returns whether an APNs environment is supported and canonical.</summary>
    public static bool IsCanonicalEnvironment(string? value)
        => string.Equals(value, "development", StringComparison.Ordinal)
            || string.Equals(value, "production", StringComparison.Ordinal);

    /// <summary>Returns whether an optional bundle identifier is canonical.</summary>
    public static bool IsCanonicalAppBundleId(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length == 0 || value.Length > AppBundleIdMaxLength)
        {
            return false;
        }

        string[] segments = value.Split(['.', '-'], StringSplitOptions.None);
        return segments.All(segment =>
            segment.Length > 0
            && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'));
    }

    private static bool IsAsciiLetterOrDigit(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';
}
