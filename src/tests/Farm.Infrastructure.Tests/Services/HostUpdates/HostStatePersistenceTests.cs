using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostStatePersistenceTests
{
    [Fact]
    public async Task ReplayAnchor_RequiresProvisioning_AndRejectsRollback()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            HostStatePath paths = new(Options.Create(new HostStateOptions { RootPath = root }));
            using FileHostUpdateReplayAnchor anchor = new(paths);
            await Assert.ThrowsAsync<InvalidDataException>(() => anchor.ReadEpochAsync(CancellationToken.None));
            await anchor.ProvisionAsync(CancellationToken.None);
            Assert.Equal(0, await anchor.ReadEpochAsync(CancellationToken.None));
            await anchor.AdvanceEpochAsync(1, CancellationToken.None);
            Assert.Equal(1, await anchor.ReadEpochAsync(CancellationToken.None));
            File.WriteAllText(Path.Combine(root, "replay-anchor.json"), "{}");
            await Assert.ThrowsAsync<InvalidDataException>(() => anchor.ReadEpochAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PolicyRepository_DefaultsOff_AndUsesRevisionCas()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            HostStatePath paths = new(Options.Create(new HostStateOptions { RootPath = root }));
            using FileHostUpdateAutomationPolicyRepository repository = new(paths);
            Assert.False(repository.Read().Policy.Enabled);
            HostUpdatePolicyReadResult applied = await repository.ReplaceAsync(new HostUpdateAutomationPolicy(Enabled: true), 0, CancellationToken.None);
            Assert.True(applied.Available);
            Assert.Equal(1, applied.Policy.Revision);
            HostUpdatePolicyReadResult stale = await repository.ReplaceAsync(new HostUpdateAutomationPolicy(Enabled: false), 0, CancellationToken.None);
            Assert.Equal("host_update_policy_revision_conflict", stale.Error);
            Assert.True(repository.Read().Policy.Enabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
