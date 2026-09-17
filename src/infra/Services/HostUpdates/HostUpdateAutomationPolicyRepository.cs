#pragma warning disable CA1849 // The policy file must synchronously flush to durable storage before replacement.
#pragma warning disable S3218 // Persisted record property names are part of the on-disk JSON contract.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services.HostUpdates;

public sealed record HostUpdateAutomationPolicy(
    bool Enabled = false,
    bool KillSwitch = false,
    string Channel = UpdateChannelSettings.StableChannel,
    bool InsiderAcknowledged = false,
    int PollIntervalSeconds = 3600,
    int? InsiderPollIntervalSeconds = null,
    int MaintenanceWindowStartHour = 0,
    int MaintenanceWindowEndHour = 24,
    long Revision = 0,
    string Fingerprint = "");

public sealed record HostUpdatePolicyReadResult(bool Available, HostUpdateAutomationPolicy Policy, string? Error);

public interface IHostUpdateAutomationPolicyProvisioner
{
    Task ProvisionAsync(CancellationToken ct);
}

public interface IHostUpdateAutomationPolicyRepository
{
    HostUpdatePolicyReadResult Read();

    Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct);
}

/// <summary>Single-record durable CAS repository for standing automatic-update authorization.</summary>
public sealed class FileHostUpdateAutomationPolicyRepository : IHostUpdateAutomationPolicyRepository, IHostUpdateAutomationPolicyProvisioner, IDisposable
{
    private const int Version = 1;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly HostStatePath _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileHostUpdateAutomationPolicyRepository(HostStatePath paths)
    {
        _paths = paths;
        _path = paths.Resolve("update-automation-policy.json");
        _lockPath = paths.Resolve("update-automation-policy.lock");
    }

    public HostUpdatePolicyReadResult Read() => Read(requireExisting: true);

    private HostUpdatePolicyReadResult Read(bool requireExisting)
    {
        if (!File.Exists(_path))
        {
            return requireExisting
                ? new(false, new HostUpdateAutomationPolicy(), "host_update_policy_not_provisioned")
                : new(true, new HostUpdateAutomationPolicy(), null);
        }

        try
        {
            PolicyFile? file = JsonSerializer.Deserialize<PolicyFile>(File.ReadAllText(_path));
            if (file is null || file.Version != Version || file.Policy is null || !IsValid(file.Policy) || file.Checksum != Checksum(file.Policy))
            {
                return new(false, new(), "host_update_policy_corrupt");
            }

            return new(true, file.Policy, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, new(), "host_update_policy_unavailable");
        }
    }

    public async Task ProvisionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using FileStream processLock = AcquireProcessLock(ct);
            HostUpdatePolicyReadResult current = Read(requireExisting: false);
            if (!current.Available)
            {
                throw new InvalidDataException(current.Error ?? "host_update_policy_unavailable");
            }

            if (File.Exists(_path))
            {
                return;
            }

            HostUpdateAutomationPolicy initial = new() { Fingerprint = Fingerprint(new HostUpdateAutomationPolicy()) };
            string json = JsonSerializer.Serialize(new PolicyFile(Version, initial, Checksum(initial)));
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(true);
                }

                HostStateFileSecurity.RejectReparseTarget(temp);
                HostStateFileSecurity.RejectReparseTarget(_path);
                File.Move(temp, _path, true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsValid(policy) || expectedRevision < 0)
        {
            return new(false, new(), "host_update_policy_invalid");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using FileStream processLock = AcquireProcessLock(ct);
            HostUpdatePolicyReadResult current = Read();
            if (!current.Available)
            {
                return current;
            }

            if (current.Policy.Revision != expectedRevision)
            {
                return new(false, current.Policy, "host_update_policy_revision_conflict");
            }

            HostUpdateAutomationPolicy next = policy with { Revision = checked(expectedRevision + 1) };
            next = next with { Fingerprint = Fingerprint(next) };
            string json = JsonSerializer.Serialize(new PolicyFile(Version, next, Checksum(next)));
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(true);
                }

                HostStateFileSecurity.RejectReparseTarget(temp);
                HostStateFileSecurity.RejectReparseTarget(_path);
                File.Move(temp, _path, true);
                return new(true, next, null);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private FileStream AcquireProcessLock(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                HostStateFileSecurity.RejectReparseTarget(_lockPath);
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static bool IsValid(HostUpdateAutomationPolicy p) =>
        (p.Channel == UpdateChannelSettings.StableChannel || p.Channel == UpdateChannelSettings.InsiderChannel) &&
        p.PollIntervalSeconds is >= 60 and <= 86400 && (!p.InsiderPollIntervalSeconds.HasValue || p.InsiderPollIntervalSeconds.Value is >= 60 and <= 86400) &&
        p.MaintenanceWindowStartHour is >= 0 and <= 23 && p.MaintenanceWindowEndHour is >= 1 and <= 24 && p.Revision >= 0;

    public static string Fingerprint(HostUpdateAutomationPolicy p) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { p.Enabled, p.KillSwitch, p.Channel, p.InsiderAcknowledged, p.PollIntervalSeconds, p.InsiderPollIntervalSeconds, p.MaintenanceWindowStartHour, p.MaintenanceWindowEndHour, p.Revision }))));

    private static string Checksum(HostUpdateAutomationPolicy p) => Fingerprint(p);

    private sealed record PolicyFile(int Version, HostUpdateAutomationPolicy Policy, string Checksum);

    public void Dispose() => _gate.Dispose();
}

public sealed class HostStateHostUpdateSchedulerSettings(IHostUpdateAutomationPolicyRepository repository) : IHostUpdateSchedulerSettings
{
    public HostUpdateSchedulerSettings Current
    {
        get
        {
            HostUpdatePolicyReadResult result = repository.Read();
            HostUpdateAutomationPolicy p = result.Policy;
            return new(p.Enabled, p.KillSwitch, p.Channel, p.InsiderAcknowledged, p.Revision, p.PollIntervalSeconds, p.InsiderPollIntervalSeconds, p.MaintenanceWindowStartHour, p.MaintenanceWindowEndHour);
        }
    }
}

public sealed class StaticHostUpdateSchedulerSettings(HostUpdateSchedulerSettings current) : IHostUpdateSchedulerSettings
{
    public HostUpdateSchedulerSettings Current { get; } = current;
}
