// <copyright file="DispatchTestDoubles.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Shared test doubles for the production queue/dispatch call chain.
///
/// These deliberately produce REALISTIC telemetry (fresh, online, idle) so tests exercise
/// the production guards rather than bypassing them. Tests that want to prove a guard fires
/// construct the degenerate reader explicitly.
/// </summary>
internal static class DispatchTestDoubles
{
    /// <summary>
    /// A snapshot reader that reports a fresh, online, idle printer — the only state in
    /// which the production guards permit a dispatch.
    /// </summary>
    /// <param name="printerId">Printer the snapshot belongs to.</param>
    /// <param name="state">Reported backend state (defaults to <c>idle</c>).</param>
    /// <param name="ageSeconds">Age of the observation in seconds.</param>
    /// <returns>A snapshot reader for the given printer.</returns>
    public static IPrinterStatusSnapshotReader OnlineIdleReader(
        Guid printerId,
        string state = "idle",
        int ageSeconds = 5) =>
        new StubSnapshotReader(printerId, new PrinterStatusSnapshot(
            new PrinterStatusDto(Id: printerId, IsOnline: true, State: state),
            DateTime.UtcNow.AddSeconds(-ageSeconds),
            DateTime.UtcNow.AddSeconds(-ageSeconds),
            "test"));

    /// <summary>A snapshot reader that never returns telemetry (simulates a silent backend).</summary>
    /// <returns>A reader that always returns <see langword="null"/>.</returns>
    public static IPrinterStatusSnapshotReader NoTelemetryReader() =>
        new StubSnapshotReader(Guid.Empty, null);

    /// <summary>A snapshot reader whose observation is older than the freshness limit.</summary>
    /// <param name="printerId">Printer the snapshot belongs to.</param>
    /// <returns>A reader reporting stale telemetry.</returns>
    public static IPrinterStatusSnapshotReader StaleReader(Guid printerId) =>
        new StubSnapshotReader(printerId, new PrinterStatusSnapshot(
            new PrinterStatusDto(Id: printerId, IsOnline: true, State: "idle"),
            DateTime.UtcNow.AddHours(-2),
            DateTime.UtcNow.AddHours(-2),
            "test"));

    public static IStoredGcodeIntegrityVerifier ValidByteIntegrityVerifier() =>
        new ValidIntegrityVerifier();

    private sealed class StubSnapshotReader(Guid printerId, PrinterStatusSnapshot? snapshot)
        : IPrinterStatusSnapshotReader
    {
        public PrinterStatusSnapshot? GetStatusSnapshot(Guid id) =>
            printerId == Guid.Empty || id == printerId ? snapshot : null;
    }

    private sealed class ValidIntegrityVerifier : IStoredGcodeIntegrityVerifier
    {
        public Task<StoredGcodeIntegrityResult> VerifyAsync(
            GcodeFile file,
            string expectedSha256,
            long? expectedSizeBytes,
            CancellationToken ct = default) =>
            Task.FromResult(StoredGcodeIntegrityResult.Valid());
    }
}
