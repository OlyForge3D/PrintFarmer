using Farm.Web.Api.Services.SlicerServices.Progress;

namespace Farm.Web.Api.Tests.SlicerServices;

public class ProgressParsersTests
{
    [Fact]
    public void PrusaParser_ParsesPercentLines()
    {
        PrusaProgressParser parser = new PrusaProgressParser();
        ProgressUpdate? upd = parser.Parse("Progress: 45%");
        _ = upd.Should().NotBeNull();
        _ = upd!.Percentage.Should().Be(45);
        _ = upd.State.Should().Be(SlicerProgressState.InProgress);
    }

    [Fact]
    public void PrusaParser_ParsesLayerLines()
    {
        PrusaProgressParser parser = new PrusaProgressParser();
        ProgressUpdate? upd = parser.Parse("Layer 10/100");
        _ = upd.Should().NotBeNull();
        _ = upd!.Percentage.Should().BeApproximately(10.0, 0.01);
    }

    [Fact]
    public void PrusaParser_DetectsCompletionAndError()
    {
        PrusaProgressParser parser = new PrusaProgressParser();
        ProgressUpdate? done = parser.Parse("Exported gcode to /tmp/foo.gcode");
        _ = done.Should().NotBeNull();
        _ = done!.State.Should().Be(SlicerProgressState.Completed);

        ProgressUpdate? err = parser.Parse("ERROR: Failed to export");
        _ = err.Should().NotBeNull();
        _ = err!.State.Should().Be(SlicerProgressState.Failed);
    }

    [Fact]
    public void OrcaParser_ParsesPercentAndExporting()
    {
        OrcaProgressParser parser = new OrcaProgressParser();
        ProgressUpdate? upd = parser.Parse("[info] Exporting: 30%");
        _ = upd.Should().NotBeNull();
        _ = upd!.Percentage.Should().Be(30);
        _ = upd.State.Should().Be(SlicerProgressState.InProgress);

        ProgressUpdate? indeterminate = parser.Parse("Saving G-code...");
        _ = indeterminate.Should().NotBeNull();
        _ = indeterminate!.Percentage.Should().Be(0);
        _ = indeterminate.State.Should().Be(SlicerProgressState.InProgress);
    }

    [Fact]
    public void OrcaParser_DetectsError()
    {
        OrcaProgressParser parser = new OrcaProgressParser();
        ProgressUpdate? err = parser.Parse("failed: write permission denied");
        _ = err.Should().NotBeNull();
        _ = err!.State.Should().Be(SlicerProgressState.Failed);
    }
}
