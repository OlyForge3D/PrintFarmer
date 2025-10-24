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

        private static long s_storageBytes;
        private static long s_warningThreshold;
        private static long s_criticalThreshold;
        private static int s_currentState;

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

        private static Measurement<long> ObserveStorageBytes() => new Measurement<long>(Interlocked.Read(ref s_storageBytes));
        private static Measurement<int> ObserveThresholdState() => new Measurement<int>(Interlocked.CompareExchange(ref s_currentState, 0, 0));

        public ArtifactsMetrics()
        {
            EnsureInitialized();
            ResetForTests();
        }

        public void SetThresholds(long warningBytes, long criticalBytes)
        {
            Interlocked.Exchange(ref s_warningThreshold, warningBytes);
            Interlocked.Exchange(ref s_criticalThreshold, criticalBytes);
        }

        public void RecordUpload(long sizeBytes)
        {
            UploadedCount.Add(1);
            UploadBytes.Record(sizeBytes);
            var newTotal = Interlocked.Add(ref s_storageBytes, sizeBytes);
            CheckThresholds(newTotal);
        }

        private void CheckThresholds(long currentBytes)
        {
            var warning = Interlocked.Read(ref s_warningThreshold);
            var critical = Interlocked.Read(ref s_criticalThreshold);

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

            var oldState = Interlocked.Exchange(ref s_currentState, newState);
            if (newState > oldState && newState > 0)
            {
                ThresholdExceeded?.Invoke(this, new StorageThresholdEventArgs(level, currentBytes, warning, critical));
            }
        }

        public static void ResetForTests()
        {
            // Only reset mutable metric state for tests. Do NOT clear the
            // registration guard or dispose the Meter here because observable
            // callback registration must remain idempotent per-process to avoid
            // duplicate callbacks when multiple test-hosts run in the same process.
            Interlocked.Exchange(ref s_storageBytes, 0);
            Interlocked.Exchange(ref s_warningThreshold, 0);
            Interlocked.Exchange(ref s_criticalThreshold, 0);
            Interlocked.Exchange(ref s_currentState, 0);
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
