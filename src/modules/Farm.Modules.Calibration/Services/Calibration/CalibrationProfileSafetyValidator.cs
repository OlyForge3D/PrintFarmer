using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Farm.Modules.Calibration.Services.Calibration;

internal sealed record CalibrationProfileSafetyResult(
    JsonElement? Json,
    string? Code,
    string? Field,
    string? Message)
{
    public bool IsSafe => Json.HasValue && Code is null;
}

internal static class CalibrationProfileSafetyValidator
{
    private static readonly string[] UnsafeCommandMarkers =
    [
        "RUN_SHELL_COMMAND",
        "/bin/sh",
        "cmd.exe",
        "powershell ",
        "curl ",
        "wget ",
    ];

    public static CalibrationProfileSafetyResult Validate(string? rawJson, string field)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new(
                null,
                "profile_json_missing",
                field,
                "The selected profile has no exact JSON payload.");
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new(
                null,
                "profile_json_invalid",
                field,
                "The selected profile JSON is invalid.");
        }

        return FindUnsafeValue(root, field) ??
            new CalibrationProfileSafetyResult(root, null, null, null);
    }

    private static CalibrationProfileSafetyResult? FindUnsafeValue(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string propertyPath = $"{path}.{property.Name}";
                string normalizedName = NormalizeName(property.Name);
                if (IsSensitiveName(normalizedName) && HasNonEmptyValue(property.Value))
                {
                    return Unsafe(
                        "profile_contains_credential",
                        propertyPath,
                        "Profile JSON contains a credential-bearing field.");
                }

                if (normalizedName.Contains("postprocess", StringComparison.Ordinal) &&
                    HasNonEmptyValue(property.Value))
                {
                    return Unsafe(
                        "profile_contains_unsafe_command",
                        propertyPath,
                        "Profile JSON contains an unsafe post-processing command.");
                }

                CalibrationProfileSafetyResult? nested =
                    FindUnsafeValue(property.Value, propertyPath);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                CalibrationProfileSafetyResult? nested =
                    FindUnsafeValue(item, $"{path}[{index}]");
                if (nested is not null)
                {
                    return nested;
                }

                index++;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string value = element.GetString() ?? string.Empty;
            if (ContainsPrivateUri(value))
            {
                return Unsafe(
                    "profile_contains_private_url",
                    path,
                    "Profile JSON contains a private or internal URL.");
            }

            if (ContainsAbsolutePath(value))
            {
                return Unsafe(
                    "profile_contains_filesystem_path",
                    path,
                    "Profile JSON contains an absolute filesystem path.");
            }

            if (UnsafeCommandMarkers.Any(marker =>
                value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return Unsafe(
                    "profile_contains_unsafe_command",
                    path,
                    "Profile JSON contains an unsafe host command.");
            }
        }

        return null;
    }

    private static CalibrationProfileSafetyResult Unsafe(
        string code,
        string field,
        string message) =>
        new(null, code, field, message);

    private static string NormalizeName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value.Where(character => char.IsLetterOrDigit(character)))
        {
            _ = builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool IsSensitiveName(string normalizedName) =>
        normalizedName.Contains("apikey", StringComparison.Ordinal) ||
        normalizedName.Contains("password", StringComparison.Ordinal) ||
        normalizedName.Contains("token", StringComparison.Ordinal) ||
        normalizedName.Contains("secret", StringComparison.Ordinal) ||
        normalizedName.Contains("authorization", StringComparison.Ordinal) ||
        normalizedName.Contains("header", StringComparison.Ordinal) ||
        normalizedName.Contains("clientsecret", StringComparison.Ordinal) ||
        normalizedName.Contains("cookie", StringComparison.Ordinal) ||
        normalizedName == "username";

    private static bool HasNonEmptyValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true,
        };

    private static bool ContainsPrivateUri(string value)
    {
        foreach (string token in value.Split(
            [' ', '\r', '\n', '\t', '"', '\'', '(', ')', '[', ']', '<', '>'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = token.TrimEnd(',', ';');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(uri.Host, out IPAddress? address) &&
                IsPrivateAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static bool ContainsAbsolutePath(string value)
    {
        string trimmed = value.TrimStart();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return true;
        }

        return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            (trimmed.Length >= 3 &&
                char.IsLetter(trimmed[0]) &&
                trimmed[1] == ':' &&
                (trimmed[2] == '\\' || trimmed[2] == '/')) ||
            trimmed.StartsWith('/');
    }
}
