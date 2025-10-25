using System;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Farm.Web.Api.Services.Artifacts
{
    /// <summary>
    /// Single-file, minimal ArtifactsMetrics with idempotent registration.
    /// </summary>
    public sealed class ArtifactsMetrics : IDisposable
    {
        private static int s_instrumentsRegistered;

        private static Meter? s_meter;
        private static Counter<long>? s_uploadedCount;
        private static Histogram<long>? s_uploadBytes;
        private static ObservableGauge<long>? s_storageTotalBytes;
        private static ObservableGauge<int>? s_storageThresholdState;

        // Registry of live instances so observable gauges can aggregate across them when needed
        private static System.Collections.Concurrent.ConcurrentBag<WeakReference<ArtifactsMetrics>> s_instances = new();

        private static void EnsureInitialized()
        {
            if (Interlocked.CompareExchange(ref s_instrumentsRegistered, 1, 0) != 0)
            {
                return;
            }

            try
            {
                s_meter = new Meter("PrintFarmer.Artifacts", "1.0.0");
                s_uploadedCount = s_meter.CreateCounter<long>("printfarmer.artifacts.uploaded_total", description: "Total number of artifacts uploaded");
                s_uploadBytes = s_meter.CreateHistogram<long>("printfarmer.artifacts.upload_bytes", unit: "bytes", description: "Size of individual artifact uploads");

                s_storageTotalBytes = s_meter.CreateObservableGauge<long>("printfarmer.artifacts.storage_total_bytes", ObserveStorageBytes, unit: "bytes", description: "Approximate total size of stored artifacts (global)");
                s_storageThresholdState = s_meter.CreateObservableGauge<int>("printfarmer.artifacts.storage_threshold_state", ObserveThresholdState, description: "Storage threshold state: 0=normal, 1=warning, 2=critical");

            }
            catch
            {
                Interlocked.Exchange(ref s_instrumentsRegistered, 0);
                throw;
            }
        }

        public Counter<long> UploadedCount { get { EnsureInitialized(); return s_uploadedCount!; } }
        public Histogram<long> UploadBytes { get { EnsureInitialized(); return s_uploadBytes!; } }
        public ObservableGauge<long> StorageTotalBytes { get { EnsureInitialized(); return s_storageTotalBytes!; } }
        public ObservableGauge<int> StorageThresholdState { get { EnsureInitialized(); return s_storageThresholdState!; } }

        public event EventHandler<StorageThresholdEventArgs>? ThresholdExceeded;

        private static Measurement<long> ObserveStorageBytes()
        {
            long sum = 0;
            foreach (var wr in s_instances)
            {
                if (wr.TryGetTarget(out var inst))
                {
                    sum = checked(sum + inst.InstanceStorageBytes);
                }
            }
            return new Measurement<long>(sum);
        }

        private static Measurement<int> ObserveThresholdState()
        {
            int maxState = 0;
            foreach (var wr in s_instances)
            {
                if (wr.TryGetTarget(out var inst))
                {
                    var s = inst.CurrentState;
                    if (s > maxState)
                    {
                        maxState = s;
                    }
                }
            }
            return new Measurement<int>(maxState);
        }

        // Per-instance unique identifier used for diagnostics and mapping
        private readonly Guid _instanceId = Guid.NewGuid();
        // Optional host token used to associate an instance with the test host that created it.
        // Tests/factories register ArtifactsMetrics with a host-specific token so resets
        // can target only instances belonging to the same test host in a shared process.
        private readonly string? _hostToken;

        // Instance-level state (per-metrics instance so tests can be isolated)
        private long _instanceStorageBytes;
        private long _instanceUploadedCount;
        private long _lastUploadSize;
        private long _instanceWarningThreshold;
        private long _instanceCriticalThreshold;
        private int _instanceCurrentState;

        // Expose lightweight accessors used by the static observable aggregators.
        // Expose instance id so tests and diagnostics can correlate which instance
        // receives RecordUpload calls vs which instance a test resolved from DI.
        public Guid InstanceId => _instanceId;

        internal long InstanceStorageBytes => Interlocked.Read(ref _instanceStorageBytes);
        internal int CurrentState => Interlocked.CompareExchange(ref _instanceCurrentState, 0, 0);
        internal long InstanceUploadedCount => Interlocked.Read(ref _instanceUploadedCount);

        // Keep a default parameterless ctor for production and legacy registrations.
        public ArtifactsMetrics(string? hostToken = null)
        {
            EnsureInitialized();
            // store host token for targeted resets
            _hostToken = hostToken;
            // Register instance for observable aggregation
            try
            {
                s_instances.Add(new WeakReference<ArtifactsMetrics>(this));
            }
            catch { }
            // Do NOT reset global/other instances here. ResetForTests() is intentionally
            // an explicit test helper the test factory calls when it needs to clear
            // state across live instances. Calling ResetForTests() from the
            // constructor would inadvertently clear metrics from other concurrently
            // constructed test hosts.
            // Diagnostic: emit instance identity when constructed to help debug test-instance mismatches
            // construction diagnostics removed for normal runs
        }

        /// <summary>
        /// The optional host token provided at registration time (may be null for production instances).
        /// </summary>
        public string? HostToken => _hostToken;

        public void SetThresholds(long warningBytes, long criticalBytes)
        {
            Interlocked.Exchange(ref _instanceWarningThreshold, warningBytes);
            Interlocked.Exchange(ref _instanceCriticalThreshold, criticalBytes);
        }

        public void RecordUpload(long sizeBytes)
        {
            // Emit metrics to the shared Meter instruments
            UploadedCount.Add(1);
            UploadBytes.Record(sizeBytes);

            // Update instance-local state for test isolation
            Interlocked.Add(ref _instanceStorageBytes, sizeBytes);
            Interlocked.Increment(ref _instanceUploadedCount);
            Interlocked.Exchange(ref _lastUploadSize, sizeBytes);

            // runtime diagnostics removed for normal runs

            var newTotal = Interlocked.Read(ref _instanceStorageBytes);
            CheckThresholds(newTotal);
        }

        private void CheckThresholds(long currentBytes)
        {
            var warning = Interlocked.Read(ref _instanceWarningThreshold);
            var critical = Interlocked.Read(ref _instanceCriticalThreshold);

            if (warning <= 0 && critical <= 0)
            {
                return;
            }

            int newState = 0;
            StorageThresholdLevel level = StorageThresholdLevel.Normal;

            if (critical > 0 && currentBytes >= critical)
            {
                newState = 2;
                level = StorageThresholdLevel.Critical;
            }
            else if (warning > 0 && currentBytes >= warning)
            {
                newState = 1;
                level = StorageThresholdLevel.Warning;
            }

            var oldState = Interlocked.Exchange(ref _instanceCurrentState, newState);
            if (newState > oldState && newState > 0)
            {
                ThresholdExceeded?.Invoke(this, new StorageThresholdEventArgs(level, currentBytes, warning, critical));
            }
        }

        public static void ResetForTests()
        {
            // Reset mutable per-instance state for all live instances.
            try
            {
                foreach (var wr in s_instances)
                {
                    if (wr.TryGetTarget(out var inst))
                    {
                        try
                        {
                            inst.ResetInstanceState();
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Return the InstanceId of all currently live ArtifactsMetrics instances.
        /// Tests can use this to determine which instances were created by a specific
        /// host construction and then reset only those instances.
        /// </summary>
        public static Guid[] GetLiveInstanceIds()
        {
            try
            {
                var list = new List<Guid>();
                foreach (var wr in s_instances)
                {
                    if (wr.TryGetTarget(out var inst))
                    {
                        list.Add(inst.InstanceId);
                    }
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<Guid>();
            }
        }

        /// <summary>
        /// Reset all instances that were registered with the specified host token.
        /// Tests should use a per-host token when registering ArtifactsMetrics in order
        /// to safely reset only the instances created by that host.
        /// </summary>
        public static void ResetInstancesForHost(string? hostToken)
        {
            if (string.IsNullOrEmpty(hostToken))
            {
                return;
            }
            try
            {
                foreach (var wr in s_instances)
                {
                    if (wr.TryGetTarget(out var inst))
                    {
                        try
                        {
                            if (string.Equals(inst._hostToken, hostToken, StringComparison.Ordinal))
                            {
                                inst.ResetInstanceState();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Reset only the instances whose InstanceId is present in the provided list.
        /// This is safer for parallel test runs because it avoids clearing unrelated
        /// test-host instances created elsewhere in the same process.
        /// </summary>
        public static void ResetInstances(IEnumerable<Guid> instanceIds)
        {
            if (instanceIds == null)
            {
                return;
            }
            var set = new HashSet<Guid>(instanceIds);
            try
            {
                foreach (var wr in s_instances)
                {
                    if (wr.TryGetTarget(out var inst))
                    {
                        if (set.Contains(inst.InstanceId))
                        {
                            try
                            {
                                inst.ResetInstanceState();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void ResetInstanceState()
        {
            Interlocked.Exchange(ref _instanceStorageBytes, 0);
            Interlocked.Exchange(ref _instanceUploadedCount, 0);
            Interlocked.Exchange(ref _lastUploadSize, 0);
            Interlocked.Exchange(ref _instanceWarningThreshold, 0);
            Interlocked.Exchange(ref _instanceCriticalThreshold, 0);
            Interlocked.Exchange(ref _instanceCurrentState, 0);
        }

        public void Dispose() { }
    }

    public enum StorageThresholdLevel { Normal = 0, Warning = 1, Critical = 2 }

    public sealed class StorageThresholdEventArgs : EventArgs
    {
        public StorageThresholdLevel Level { get; }
        public long CurrentBytes { get; }
        public long WarningThreshold { get; }
        public long CriticalThreshold { get; }

        public StorageThresholdEventArgs(StorageThresholdLevel level, long currentBytes, long warningThreshold, long criticalThreshold)
        {
            Level = level;
            CurrentBytes = currentBytes;
            WarningThreshold = warningThreshold;
            CriticalThreshold = criticalThreshold;
        }
    }
}
