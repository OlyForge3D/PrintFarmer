using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Farm.Infrastructure.Discovery;

namespace Farm.Infrastructure.Services.Discovery;

/// <summary>
/// Tracks discovery-session ownership and keeps network targets server-side.
/// </summary>
public interface IDiscoverySessionRegistry
{
    /// <summary>Registers an authenticated user as the owner of a discovery session.</summary>
    void RegisterSession(string sessionId, Guid ownerUserId);

    /// <summary>Determines whether a live discovery session belongs to a user.</summary>
    bool IsSessionOwner(string sessionId, Guid userId);

    /// <summary>Determines whether a live discovery session exists.</summary>
    bool SessionExists(string sessionId);

    /// <summary>Stores a discovered network target and returns its redacted event contract.</summary>
    DiscoveryPrinterFoundDto? StorePrinter(InternalDiscoveryPrinterFoundDto found);

    /// <summary>Resolves a server-side discovered target for an authorized registration request.</summary>
    bool TryGetPrinter(
        string sessionId,
        Guid discoveryId,
        Guid userId,
        bool allowAdministratorBypass,
        out DiscoveredPrinterDto? printer);

    /// <summary>Removes a target after it has been registered successfully.</summary>
    void RemovePrinter(string sessionId, Guid discoveryId);
}

/// <summary>
/// In-memory discovery registry with short-lived, owner-bound entries.
/// </summary>
public sealed class DiscoverySessionRegistry : IDiscoverySessionRegistry
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void RegisterSession(string sessionId, Guid ownerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("A discovery session requires an authenticated owner.", nameof(ownerUserId));
        }

        RemoveExpiredSessions();
        _sessions[sessionId] = new SessionEntry(ownerUserId, DateTimeOffset.UtcNow.Add(SessionLifetime));
    }

    /// <inheritdoc />
    public bool IsSessionOwner(string sessionId, Guid userId)
    {
        if (!TryGetLiveSession(sessionId, out SessionEntry? session))
        {
            return false;
        }

        return session.OwnerUserId == userId;
    }

    /// <inheritdoc />
    public bool SessionExists(string sessionId) => TryGetLiveSession(sessionId, out _);

    /// <inheritdoc />
    public DiscoveryPrinterFoundDto? StorePrinter(InternalDiscoveryPrinterFoundDto found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (!TryGetLiveSession(found.SessionId, out SessionEntry? session))
        {
            return null;
        }

        var printer = new DiscoveredPrinterDto
        {
            Name = found.Name,
            ServerUrl = found.ServerUrl,
            OriginalServerUrl = found.OriginalServerUrl,
            IpAddress = found.IpAddress,
            Backend = found.Backend,
            BackendPort = found.BackendPort,
            FrontendPort = found.FrontendPort,
            CameraStreamUrl = found.CameraStreamUrl,
            CameraSnapshotUrl = found.CameraSnapshotUrl,
            Manufacturer = found.Manufacturer,
            Model = found.Model,
            Notes = found.Notes,
            DiscoveredAt = found.DiscoveredAt,
            IsReachable = found.IsReachable,
        };

        Guid discoveryId;
        do
        {
            discoveryId = new Guid(RandomNumberGenerator.GetBytes(16));
        }
        while (!session.Printers.TryAdd(discoveryId, printer));

        return new DiscoveryPrinterFoundDto(
            found.SessionId,
            new DiscoveredPrinterSummaryDto(
                discoveryId,
                found.Name,
                found.Backend,
                found.Manufacturer,
                found.Model,
                found.DiscoveredAt,
                found.IsReachable));
    }

    /// <inheritdoc />
    public bool TryGetPrinter(
        string sessionId,
        Guid discoveryId,
        Guid userId,
        bool allowAdministratorBypass,
        out DiscoveredPrinterDto? printer)
    {
        printer = null;
        if (!TryGetLiveSession(sessionId, out SessionEntry? session) ||
            (session.OwnerUserId != userId && !allowAdministratorBypass))
        {
            return false;
        }

        return session.Printers.TryGetValue(discoveryId, out printer);
    }

    /// <inheritdoc />
    public void RemovePrinter(string sessionId, Guid discoveryId)
    {
        if (TryGetLiveSession(sessionId, out SessionEntry? session))
        {
            _ = session.Printers.TryRemove(discoveryId, out _);
        }
    }

    private bool TryGetLiveSession(
        string sessionId,
        [NotNullWhen(true)] out SessionEntry? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out SessionEntry? candidate))
        {
            return false;
        }

        if (candidate.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _ = _sessions.TryRemove(sessionId, out _);
            return false;
        }

        session = candidate;
        return true;
    }

    private void RemoveExpiredSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string sessionId, SessionEntry session) in _sessions)
        {
            if (session.ExpiresAt <= now)
            {
                _ = _sessions.TryRemove(sessionId, out _);
            }
        }
    }

    private sealed record SessionEntry(Guid OwnerUserId, DateTimeOffset ExpiresAt)
    {
        public ConcurrentDictionary<Guid, DiscoveredPrinterDto> Printers { get; } = new();
    }
}
