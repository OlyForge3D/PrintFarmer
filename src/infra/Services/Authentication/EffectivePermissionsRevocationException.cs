namespace Farm.Infrastructure.Services.Authentication;

public sealed class EffectivePermissionsRevocationException : Exception
{
    public EffectivePermissionsRevocationException()
        : this(0, new Dictionary<Guid, Exception>())
    {
    }

    public EffectivePermissionsRevocationException(string message)
        : base(message)
    {
    }

    public EffectivePermissionsRevocationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EffectivePermissionsRevocationException(
        int successfullyRevokedUserCount,
        IReadOnlyDictionary<Guid, Exception> failedUsers)
        : base(
            $"Token revocation succeeded for {successfullyRevokedUserCount} affected users and failed for {failedUsers.Count}.",
            new AggregateException(failedUsers.Values))
    {
        SuccessfullyRevokedUserCount = successfullyRevokedUserCount;
        FailedUsers = new Dictionary<Guid, Exception>(failedUsers);
    }

    public int SuccessfullyRevokedUserCount { get; }

    public IReadOnlyDictionary<Guid, Exception> FailedUsers { get; } = new Dictionary<Guid, Exception>();
}
