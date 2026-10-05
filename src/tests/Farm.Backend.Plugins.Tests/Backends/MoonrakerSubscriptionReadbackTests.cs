using Farm.Backend.Plugin.Moonraker;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerSubscriptionReadbackTests
{
    [Theory]
    [InlineData("complete", "printing", "old.gcode", "new.gcode", 100d, null)]
    [InlineData("printing", "printing", "same.gcode", "same.gcode", 100d, 200d)]
    [InlineData("ready", "paused", "same.gcode", "same.gcode", null, null)]
    public void ResetLayerReadbacksForNewJob_JobChanges_ClearsPreviousCounters(
        string previousState,
        string incomingState,
        string previousJobName,
        string incomingJobName,
        double? previousStartTime,
        double? incomingStartTime)
    {
        var state = new PrinterState
        {
            State = previousState,
            JobName = previousJobName,
            ThumbnailJobStartTime = previousStartTime,
            CurrentLayer = 41,
            TotalLayers = 180,
        };

        MoonrakerSubscriptionService.ResetLayerReadbacksForNewJob(
            state,
            incomingState,
            incomingJobName,
            incomingStartTime);

        Assert.Null(state.CurrentLayer);
        Assert.Null(state.TotalLayers);
    }

    [Fact]
    public void ResetLayerReadbacksForNewJob_SameActiveJob_PreservesCounters()
    {
        var state = new PrinterState
        {
            State = "printing",
            JobName = "same.gcode",
            ThumbnailJobStartTime = 100,
            CurrentLayer = 41,
            TotalLayers = 180,
        };

        MoonrakerSubscriptionService.ResetLayerReadbacksForNewJob(
            state,
            "printing",
            "same.gcode",
            100);

        Assert.Equal(41, state.CurrentLayer);
        Assert.Equal(180, state.TotalLayers);
    }
}
