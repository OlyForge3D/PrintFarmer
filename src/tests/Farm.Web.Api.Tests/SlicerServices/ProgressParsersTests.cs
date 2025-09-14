using Farm.Web.Api.Services.SlicerServices.Progress;

namespace Farm.Web.Api.Tests.SlicerServices;

public class ProgressParsersTests
{
    [Fact]
    public void PrusaParser_ParsesPercentLines()
    {
        var parser = new PrusaProgressParser();
        var upd = parser.Parse("Progress: 45%");
        upd.Should().NotBeNull();
        upd!.Percentage.Should().Be(45);
        upd.State.Should().Be(SlicerProgressState.InProgress);
    }

    [Fact]
    public void PrusaParser_ParsesLayerLines()
    {
        var parser = new PrusaProgressParser();
        var upd = parser.Parse("Layer 10/100");
        upd.Should().NotBeNull();
        upd!.Percentage.Should().BeApproximately(10.0, 0.01);
    }

    [Fact]
    public void PrusaParser_DetectsCompletionAndError()
    {
        var parser = new PrusaProgressParser();
        var done = parser.Parse("Exported gcode to /tmp/foo.gcode");
        done.Should().NotBeNull();
        done!.State.Should().Be(SlicerProgressState.Completed);

        var err = parser.Parse("ERROR: Failed to export");
        err.Should().NotBeNull();
        err!.State.Should().Be(SlicerProgressState.Failed);
    }

    [Fact]
    public void OrcaParser_ParsesPercentAndExporting()
    {
        var parser = new OrcaProgressParser();
        var upd = parser.Parse("[info] Exporting: 30%");
        upd.Should().NotBeNull();
        upd!.Percentage.Should().Be(30);
        upd.State.Should().Be(SlicerProgressState.InProgress);

        var indeterminate = parser.Parse("Saving G-code...");
        indeterminate.Should().NotBeNull();
        indeterminate!.Percentage.Should().Be(0);
        indeterminate.State.Should().Be(SlicerProgressState.InProgress);
    }

    [Fact]
    public void OrcaParser_DetectsError()
    {
        var parser = new OrcaProgressParser();
        var err = parser.Parse("failed: write permission denied");
        err.Should().NotBeNull();
        err!.State.Should().Be(SlicerProgressState.Failed);
    }
}
