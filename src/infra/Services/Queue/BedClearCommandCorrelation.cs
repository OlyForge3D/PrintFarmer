using System.Security.Cryptography;
using System.Text;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Builds non-secret correlation evidence for exact bed-clear idempotency keys.</summary>
internal static class BedClearCommandCorrelation
{
    public static string HashIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return Convert.ToHexStringLower(hash);
    }
}
