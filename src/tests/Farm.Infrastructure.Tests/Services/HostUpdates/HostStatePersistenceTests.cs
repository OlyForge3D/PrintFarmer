using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostStatePersistenceTests
{
    private static HostStateOptions OptionsFor(string root) => new()
    {
        Enabled = true,
        RootPath = root,
        WindowsSecurityAttested = OperatingSystem.IsWindows(),
    };
    [Fact]
    public async Task ReplayAnchor_RequiresProvisioning_AndRejectsRollback()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
            using FileHostUpdateReplayAnchor anchor = new(paths);
            await Assert.ThrowsAsync<InvalidDataException>(() => anchor.ReadEpochAsync(CancellationToken.None));
            await anchor.ProvisionAsync(CancellationToken.None);
            Assert.Equal(0, await anchor.ReadEpochAsync(CancellationToken.None));
            await anchor.AdvanceEpochAsync(1, FileHash(Path.Combine(root, "host-update-replay.json")), CancellationToken.None);
            Assert.Equal(1, await anchor.ReadEpochAsync(CancellationToken.None));
            File.WriteAllText(Path.Combine(root, "replay-anchor.json"), "{}");
            Assert.Equal(1, await anchor.ReadEpochAsync(CancellationToken.None));
            File.AppendAllText(Path.Combine(root, "replay-anchor.journal"), "truncated");
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
    public async Task PolicyRepository_RequiresProvisioning_DefaultsOff_AndUsesRevisionCas()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
            using FileHostUpdateAutomationPolicyRepository repository = new(paths);
            Assert.Equal("host_update_policy_not_provisioned", repository.Read().Error);
            Assert.Equal("host_update_policy_not_provisioned", (await repository.ReplaceAsync(new HostUpdateAutomationPolicy(Enabled: true), 0, CancellationToken.None)).Error);

            await repository.ProvisionAsync(CancellationToken.None);
            await repository.ProvisionAsync(CancellationToken.None);
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
    public async Task PolicyProvisioner_RefusesCorruptExistingState()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
            File.WriteAllText(paths.Resolve("update-automation-policy.json"), "{\"version\":1,\"policy\":{\"enabled\":true}}");
            using FileHostUpdateAutomationPolicyRepository repository = new(paths);

            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => repository.ProvisionAsync(CancellationToken.None));

            Assert.Equal("host_update_policy_corrupt", ex.Message);
            Assert.Equal("host_update_policy_corrupt", repository.Read().Error);
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
    public void HostStateOptionsValidator_DefaultDisabledAllowsEmptyRoot_AndEnabledMissingRootFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            ValidateOptionsResult disabled = new HostStateOptionsValidator().Validate(null, new HostStateOptions());
            Assert.True(disabled.Succeeded);

            ValidateOptionsResult enabled = new HostStateOptionsValidator().Validate(null, OptionsFor(root));
            Assert.False(enabled.Succeeded);
            Assert.False(Directory.Exists(root));
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
            ValidateOptionsResult result = new HostStateOptionsValidator().Validate(null, OptionsFor(file));

            Assert.False(result.Succeeded);
            Assert.Contains("security validation failed", result.FailureMessage);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("provision-replay-staged")]
    [InlineData("provision-anchor-journal-committed")]
    [InlineData("provision-anchor-snapshot-replaced")]
    [InlineData("provision-replay-committed")]
    public async Task ReplayProvisioner_recovers_interruption_at_every_boundary(string boundary)
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
            using (FileHostUpdateReplayAnchor interrupted = new(paths, reached =>
            {
                if (reached == boundary)
                {
                    throw new InvalidOperationException();
                }
            }))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => interrupted.ProvisionAsync(default));
            }

            using FileHostUpdateReplayAnchor restarted = new(paths);
            await restarted.ProvisionAsync(default);
            Assert.Equal(0, await restarted.ReadEpochAsync(default));
            Assert.True(File.Exists(Path.Combine(root, "host-update-replay.json")));
            using FileHostUpdateReplayStore store = new(root, restarted);
            Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default)).Disposition);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Theory]
    [InlineData("stage-durable")]
    [InlineData("anchor-committed")]
    [InlineData("snapshot-replaced")]
    public async Task Replay_commit_recovers_forward_after_each_interruption_boundary(string boundary)
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
            using FileHostUpdateReplayAnchor anchor = new(paths);
            await anchor.ProvisionAsync(default);
            using (FileHostUpdateReplayStore interrupted = new(root, anchor, reached =>
            {
                if (reached == boundary)
                {
                    throw new InvalidOperationException();
                }
            }))
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => interrupted.DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default));
            }

            using FileHostUpdateReplayStore restarted = new(root, anchor);
            HostUpdateReplayDecision decision = await restarted.DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default);
            Assert.Equal(HostUpdateReplayDisposition.Accepted, decision.Disposition);
            Assert.Equal(await anchor.ReadEpochAsync(default), ReadReplayEpoch(root));
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
    public void HostStateOptionsValidator_rejects_reparse_component()
    {
        string parent = Path.Combine(Path.GetTempPath(), "printfarmer-host-parent-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(Path.GetTempPath(), "printfarmer-host-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(target);
        string link = Path.Combine(parent, "linked");
        try
        {
            try
            { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            ValidateOptionsResult result = new HostStateOptionsValidator().Validate(null, OptionsFor(Path.Combine(link, "state")));
            Assert.False(result.Succeeded);
            Assert.Contains("reparse", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
    }

    [Fact]
    public void HostStateOptionsValidator_rejects_insecure_unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
            ValidateOptionsResult result = new HostStateOptionsValidator().Validate(null, OptionsFor(root));
            Assert.False(result.Succeeded);
            Assert.Contains("permissions", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HostStateOwnerValidation_UsesArchitectureSpecificStatxSyscallNumbers()
    {
        Assert.Equal(332, HostStateFileSecurity.NativeMethods.StatxSyscallNumberForArchitecture(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal(291, HostStateFileSecurity.NativeMethods.StatxSyscallNumberForArchitecture(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() => HostStateFileSecurity.NativeMethods.StatxSyscallNumberForArchitecture(System.Runtime.InteropServices.Architecture.X86));
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64)]
    public void HostStateOwnerValidation_LinuxStatxLayoutNeverReadsGroupIdAsUserId(System.Runtime.InteropServices.Architecture architecture)
    {
        // Selecting the architecture-specific syscall number (covering the arm64 branch explicitly)
        // must not affect the managed struct layout: UserId and GroupId are distinct named fields,
        // not overlapping offsets into a raw/fragile stat buffer, so swapping owner/group can never
        // happen regardless of which architecture selected the syscall.
        long syscallNumber = HostStateFileSecurity.NativeMethods.StatxSyscallNumberForArchitecture(architecture);
        Assert.True(syscallNumber is 332 or 291);

        HostStateFileSecurity.NativeMethods.LinuxStatx stat = default;
        stat.Mask = 0x7ff;
        stat.UserId = 1001;
        stat.GroupId = 2002;

        Assert.Equal(1001u, stat.UserId);
        Assert.NotEqual(stat.GroupId, stat.UserId);
    }

    private static VerifiedHostUpdateCandidate Candidate() => new("release-1", "commit-1", 1, "sha256:manifest", "stable", true, true, true, true, true, true,
        new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), "sha256:" + new string('d', 64), "sha256:" + new string('e', 64), "sha256:" + new string('f', 64)));

    private static string FileHash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static long ReadReplayEpoch(string root)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "host-update-replay.json")));
        return document.RootElement.GetProperty("Epoch").GetInt64();
    }

    [Fact]
    public void PolicyRepository_ReportsCorruptionInsteadOfUsingStoredPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            HostStatePath paths = new(Options.Create(OptionsFor(root)));
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
