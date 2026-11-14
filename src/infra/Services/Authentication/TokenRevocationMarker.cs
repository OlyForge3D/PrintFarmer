using System.Security.Cryptography;

namespace Farm.Infrastructure.Services.Authentication;

internal static class TokenRevocationMarker
{
    private const string Prefix = "ALL_TOKENS_";
    private const int UniqueSuffixByteLength = 10;

    public static string CreateTokenHash(Guid userId)
    {
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(UniqueSuffixByteLength));
        return $"{Prefix}{userId:N}_{suffix}";
    }
}
