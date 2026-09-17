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
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void HostStateOptionsValidator_CreatesMissingRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            ValidateOptionsResult result = new HostStateOptionsValidator().Validate(null, new HostStateOptions { RootPath = root });

            Assert.True(result.Succeeded);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void HostStateOptionsValidator_RejectsRootThatIsAFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            ValidateOptionsResult result = new HostStateOptionsValidator().Validate(null, new HostStateOptions { RootPath = file });

            Assert.False(result.Succeeded);
            Assert.Contains("unavailable or unwritable", result.FailureMessage);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void PolicyRepository_ReportsCorruptionInsteadOfUsingStoredPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            HostStatePath paths = new(Options.Create(new HostStateOptions { RootPath = root }));
            using FileHostUpdateAutomationPolicyRepository repository = new(paths);
            File.WriteAllText(paths.Resolve("update-automation-policy.json"), "{\"version\":1,\"policy\":{\"enabled\":true}}");

            HostUpdatePolicyReadResult result = repository.Read();

            Assert.False(result.Available);
            Assert.Equal("host_update_policy_corrupt", result.Error);
            Assert.False(result.Policy.Enabled);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
