using System.Security.Cryptography;
using System.Text;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Provider identities are ordinal, case-preserving '/' separated names, not host filesystem paths.
/// No Unicode normalization, URL decoding or case folding is applied.
/// </summary>
public static class PrinterInventoryPath
{
    /// <summary>Canonicalizes a declared root, removing only its optional boundary slashes.</summary>
    public static bool TryCanonicalizeRoot(string? value, out string canonical) =>
        TryCanonicalizeRelativePath(value?.Trim('/'), out canonical);

    /// <summary>Accepts only safe nonempty relative paths; ambiguous or traversal paths fail closed.</summary>
    public static bool TryCanonicalizeRelativePath(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            value.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#') ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        canonical = value;
        return true;
    }

    /// <summary>Returns uppercase SHA256 hex of canonical root, NUL, canonical path (UTF8).</summary>
    public static string ComputeIdentity(string storageRoot, string relativePath)
    {
        if (!TryCanonicalizeRoot(storageRoot, out string root) ||
            !TryCanonicalizeRelativePath(relativePath, out string path))
        {
            throw new ArgumentException("Invalid printer inventory identity.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + '\0' + path)));
    }
}
