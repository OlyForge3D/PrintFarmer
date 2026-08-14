namespace Farm.Moonraker.Emulator.Domain;

/// <summary>One configured webcam fixture, mirroring Moonraker's <c>server/webcams/list</c> entry shape.</summary>
public sealed class WebcamFixture
{
    public required string Name { get; init; }

    /// <summary>
    /// Identifier reported as this webcam's "uid" field. Random by default (an ad-hoc
    /// webcam nobody seeded deterministically has no natural stable id to fall back to),
    /// but every seeded fixture (see <c>PrinterRegistry.SeedFixtures</c>) sets this
    /// explicitly to a fixed value so tests and the real client's
    /// <c>server/webcams/test?uid=...</c> lookups get a reproducible identifier across
    /// process restarts instead of a fresh GUID each time.
    /// </summary>
    public string Uid { get; init; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    public string Service { get; init; } = "mjpegstreamer";

    public string Location { get; init; } = "printer";

    public string Icon { get; init; } = "mdiWebcam";

    public int TargetFps { get; init; } = 15;

    public int TargetFpsIdle { get; init; } = 5;

    /// <summary>Path-relative (printer-scoped) stream URL served locally by this emulator instance.</summary>
    public string StreamPath { get; init; } = "stream";

    /// <summary>Path-relative (printer-scoped) snapshot URL served locally by this emulator instance.</summary>
    public string SnapshotPath { get; init; } = "snapshot";
}
