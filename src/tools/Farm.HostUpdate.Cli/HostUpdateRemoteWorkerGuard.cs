using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Refuses a network-denied activation or recovery while any slicer worker is registered outside
/// this host's compose topology (issue #3094). Offline activation and recovery only replace the
/// compose services this host runs; a remote or separately pinned worker would keep running a
/// release this host can neither update nor roll back, so it must be restored through its owner.
/// </summary>
internal interface IHostUpdateRemoteWorkerGuard
{
    /// <summary>Returns <see langword="null"/> when every registered worker is compose-managed on this host, otherwise a stable refusal code.</summary>
    Task<string?> ValidateAsync(CancellationToken cancellationToken);
}

internal sealed class SlicerRegistrationRemoteWorkerGuard(
    SlicerDbContext database,
    HostUpdateExecutionOptions options) : IHostUpdateRemoteWorkerGuard
{
    internal const string Unsupported = "remote_worker_unsupported";
    internal const string EvidenceUnavailable = "remote_worker_evidence_unavailable";
    internal const string OwnerDetail = "restore_remote_workers_through_owner";

    private static readonly Type[] RegistrationEntities = [typeof(Farm.Slicer.Module.Domain.SlicerService)];

    public async Task<string?> ValidateAsync(CancellationToken cancellationToken)
    {
        List<string?> hosts;
        try
        {
            // A slicer schema that was provably never migrated has no registrations at all (issue
            // #3126). This is a positive catalog proof; a failed read never counts as absence.
            hosts = await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(database, RegistrationEntities, cancellationToken).ConfigureAwait(false)
                ? []
                : await database.SlicerServices.AsNoTracking().Select(service => service.Host).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Registrations that cannot be read, or whose absence cannot be proven, cannot prove
            // every worker is local.
            return EvidenceUnavailable;
        }

        return Classify(hosts, options);
    }

    /// <summary>
    /// A registration is compose-managed only when its host is an absolute http(s) URL without
    /// credentials whose host name is the compose service name of a service active on this host.
    /// Every other form, including a missing host, an IP address and a loopback name, is treated
    /// as a worker this host does not manage.
    /// </summary>
    internal static string? Classify(IEnumerable<string?> hosts, HostUpdateExecutionOptions options)
    {
        var active = new HashSet<string>(options.ActiveServiceIds, StringComparer.Ordinal);
        var composeNames = new HashSet<string>(
            options.ServiceMappings.Where(mapping => active.Contains(mapping.ServiceId)).Select(mapping => mapping.ComposeServiceName),
            StringComparer.OrdinalIgnoreCase);
        return hosts.All(host => IsComposeManaged(host, composeNames)) ? null : Unsupported;
    }

    private static bool IsComposeManaged(string? host, HashSet<string> composeNames) =>
        Uri.TryCreate(host, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.HostNameType == UriHostNameType.Dns &&
        composeNames.Contains(uri.Host);
}
