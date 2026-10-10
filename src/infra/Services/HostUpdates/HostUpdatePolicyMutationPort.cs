namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>A single settings-section mutation guarded by an expected-revision compare-and-set.
/// <see cref="ExpectedRevision"/> must match the currently persisted revision for
/// <paramref name="Key"/> or the mutation is refused; a successful mutation always increments the
/// persisted revision.</summary>
public sealed record HostUpdatePolicyMutationRequest(string Key, long ExpectedRevision, string Value);

public enum HostUpdatePolicyMutationResult
{
    Applied,
    RevisionConflict,
    Failed
}

public sealed record HostUpdatePolicyMutationResponse(HostUpdatePolicyMutationResult Result, long NewRevision);

/// <summary>Atomic, expected-revision compare-and-set policy mutation port.
/// <para>
/// This is intentionally narrow: it is not a general settings-mutation API. It exists so that any
/// future enablement endpoint for the host-update scheduler can increment
/// <see cref="Farm.Infrastructure.Settings.HostUpdateAutomationSettings.PolicyRevision"/>
/// atomically, guaranteeing every relevant settings mutation bumps the revision the scheduler
/// fences against (see <see cref="IHostUpdatePolicyFence"/>).
/// </para>
/// <para>
/// <see cref="Farm.Infrastructure.Repositories.Settings.IAppSettingsRepository"/> has no
/// compare-and-set primitive today (<c>SetAsync</c> is unconditional last-writer-wins). Rather
/// than register an implementation that silently bypasses the revision fence this port exists to
/// guarantee, only a fail-closed implementation is provided until a real CAS-capable
/// implementation is added. <b>Do not wire an enablement endpoint against
/// <see cref="FailClosedHostUpdatePolicyMutationPort"/>; wiring remains blocked pending that
/// work.</b>
/// </para>
/// </summary>
public interface IHostUpdatePolicyMutationPort
{
    Task<HostUpdatePolicyMutationResponse> ApplyAsync(HostUpdatePolicyMutationRequest request, CancellationToken ct);
}

/// <summary>Fails closed on every call; see <see cref="IHostUpdatePolicyMutationPort"/> remarks.
/// Not registered in production dependency injection.</summary>
public sealed class FailClosedHostUpdatePolicyMutationPort : IHostUpdatePolicyMutationPort
{
    public Task<HostUpdatePolicyMutationResponse> ApplyAsync(HostUpdatePolicyMutationRequest request, CancellationToken ct) =>
        throw new NotSupportedException("host_update_policy_mutation_not_available");
}
