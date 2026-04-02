#pragma warning disable CS1998 // Async methods that don't use await — intentional for emulator stubs

using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Emulated backend client that simulates printer behavior without real hardware.
/// Implements the 6 MVP capability interfaces for Playwright E2E testing.
/// </summary>
public class TestEmulatorClient(TestEmulatorStateManager stateManager)
    : ITestEmulatorClient,
      ISupportsConnectionTest,
      ISupportsStatus,
      ISupportsCompositeStatus,
      ISupportsStartPrint,
      ISupportsControlOperations,
      ISupportsCamera
{
    private readonly TestEmulatorStateManager _stateManager = stateManager;

    // ISupportsConnectionTest
    public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken ct = default) => true;

    public async Task<bool> TestConnectionAsync(Uri baseUrl, CancellationToken ct = default) => true;

    // ISupportsStatus
    public async Task<PrinterStatus> GetStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        // Extract printer ID from the baseUrl convention: http://testemulator-{guid}
        Guid printerId = ExtractPrinterId(baseUrl);
        EmulatedPrinterState? state = _stateManager.GetState(printerId);
        if (state is null)
        {
            return new PrinterStatus(false, null);
        }

        bool isOnline = state.State != EmulatorPrinterState.Offline;
        string? stateStr = state.State switch
        {
            EmulatorPrinterState.Idle => "idle",
            EmulatorPrinterState.Printing => "printing",
            EmulatorPrinterState.Paused => "paused",
            EmulatorPrinterState.Complete => "complete",
            EmulatorPrinterState.Error => "error",
            EmulatorPrinterState.Offline => null,
            _ => null
        };

        return new PrinterStatus(isOnline, stateStr);
    }

    // ISupportsCompositeStatus
    public async Task<PrinterCompositeStatus> GetCompositeStatusAsync(string baseUrl, CancellationToken ct = default)
    {
        Guid printerId = ExtractPrinterId(baseUrl);
        EmulatedPrinterState? state = _stateManager.GetState(printerId);
        if (state is null)
        {
            return new PrinterCompositeStatus(false, null, null, null, null, null, null);
        }

        bool isOnline = state.State != EmulatorPrinterState.Offline;
        string? stateStr = state.State switch
        {
            EmulatorPrinterState.Idle => "idle",
            EmulatorPrinterState.Printing => "printing",
            EmulatorPrinterState.Paused => "paused",
            EmulatorPrinterState.Complete => "complete",
            EmulatorPrinterState.Error => "error",
            EmulatorPrinterState.Offline => null,
            _ => null
        };

        double? progress = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? state.Progress
            : null;

        string? jobName = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? state.JobName
            : null;

        double hotendTemp = state.GetHotendTemp();
        double bedTemp = state.GetBedTemp();

        double? hotendTarget = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? EmulatedPrinterState.TargetHotendTemp
            : null;
        double? bedTarget = state.State is EmulatorPrinterState.Printing or EmulatorPrinterState.Paused
            ? EmulatedPrinterState.TargetBedTemp
            : null;

        return new PrinterCompositeStatus(
            IsOnline: isOnline,
            State: stateStr,
            Progress: progress,
            JobName: jobName,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            HotendTemp: Math.Round(hotendTemp, 1),
            BedTemp: Math.Round(bedTemp, 1),
            HotendTarget: hotendTarget,
            BedTarget: bedTarget);
    }

    // ISupportsStartPrint
    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        Guid printerId = ExtractPrinterId(baseUrl);
        EmulatedPrinterState? state = _stateManager.GetState(printerId);
        if (state is null || state.State != EmulatorPrinterState.Idle)
        {
            return false;
        }

        _stateManager.StartPrint(printerId, state.PrintDurationSeconds);
        return true;
    }

    // ISupportsControlOperations
    public async Task<bool> PauseAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        Guid printerId = ExtractPrinterId(baseUrl);
        EmulatedPrinterState? state = _stateManager.GetState(printerId);
        if (state is null || state.State != EmulatorPrinterState.Printing)
        {
            return false;
        }

        _stateManager.Pause(printerId);
        return true;
    }

    public async Task<bool> ResumeAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        Guid printerId = ExtractPrinterId(baseUrl);
        EmulatedPrinterState? state = _stateManager.GetState(printerId);
        if (state is null || state.State != EmulatorPrinterState.Paused)
        {
            return false;
        }

        _stateManager.Resume(printerId);
        return true;
    }

    public async Task<bool> CancelAsync(string baseUrl, PrinterCredential? credential = null, CancellationToken ct = default)
    {
        Guid printerId = ExtractPrinterId(baseUrl);
        _stateManager.Cancel(printerId);
        return true;
    }

    // ISupportsCamera
    public async Task<string?> GetCameraStreamUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default) =>
        "https://placehold.co/640x480/1a1a2e/e0e0e0?text=Test+Camera";

    public async Task<string?> GetCameraSnapshotUrlAsync(string baseUrl, int? frontendPort = null, PrinterCredential? credential = null, CancellationToken ct = default) =>
        "https://placehold.co/640x480/1a1a2e/e0e0e0?text=Test+Snapshot";

    /// <summary>
    /// Extracts a printer GUID from the synthetic server URL format: http://testemulator-{guid}
    /// </summary>
    internal static Guid ExtractPrinterId(string baseUrl)
    {
        const string prefix = "http://testemulator-";
        if (baseUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string guidPart = baseUrl[prefix.Length..].TrimEnd('/');
            if (Guid.TryParse(guidPart, out Guid id))
            {
                return id;
            }
        }

        return Guid.Empty;
    }
}

#pragma warning restore CS1998
