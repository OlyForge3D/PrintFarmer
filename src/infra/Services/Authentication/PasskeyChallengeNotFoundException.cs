namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Thrown when a WebAuthn challenge is not found in the cache — either it has expired (TTL exceeded)
/// or the same challenge was already consumed (replay prevention).
/// </summary>
public sealed class PasskeyChallengeNotFoundException : Exception
{
    public PasskeyChallengeNotFoundException()
    {
    }

    public PasskeyChallengeNotFoundException(string message)
        : base(message)
    {
    }

    public PasskeyChallengeNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
