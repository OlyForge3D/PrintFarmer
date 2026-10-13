using Farm.Infrastructure.Domain;
using FluentAssertions;

namespace Farm.Infrastructure.Tests.Domain;

public class PrinterCameraSelectorTests
{
    [Fact]
    public void OrderCamerasForPrinter_UsesTheRequestedPrintersLink()
    {
        Guid printerA = Guid.NewGuid();
        Guid printerB = Guid.NewGuid();
        Camera shared = NewCamera("Shared");
        Camera onlyA = NewCamera("Only A");
        LinkTo(shared, printerA, isPrimary: false, sortOrder: 1);
        LinkTo(shared, printerB, isPrimary: true, sortOrder: 0);
        LinkTo(onlyA, printerA, isPrimary: true, sortOrder: 0);

        PrinterCameraSelector.OrderCamerasForPrinter([shared, onlyA], printerA)
            .Should().Equal(onlyA, shared);
        PrinterCameraSelector.OrderCamerasForPrinter([shared, onlyA], printerB)
            .Should().Equal(shared);
    }

    [Fact]
    public void OrderCameras_OrdersByPrimaryThenLinkSortThenCameraSortThenName()
    {
        Camera b = NewCamera("B");
        Camera a = NewCamera("A");
        Camera lowCameraSort = NewCamera("Z");
        lowCameraSort.SortOrder = -1;
        Camera primary = NewCamera("Primary");
        var links = new[]
        {
            new CameraPrinter { Camera = b, CameraId = b.Id, SortOrder = 2 },
            new CameraPrinter { Camera = a, CameraId = a.Id, SortOrder = 2 },
            new CameraPrinter { Camera = lowCameraSort, CameraId = lowCameraSort.Id, SortOrder = 2 },
            new CameraPrinter { Camera = primary, CameraId = primary.Id, SortOrder = 9, IsPrimary = true },
        };

        PrinterCameraSelector.OrderCameras(links).Should().Equal(primary, lowCameraSort, a, b);
    }

    [Fact]
    public void SelectSnapshotCamera_SkipsDisabledAndMissingSnapshotCameras()
    {
        Camera disabled = NewCamera("Disabled");
        disabled.IsEnabled = false;
        Camera noSnapshot = NewCamera("No snapshot");
        noSnapshot.SnapshotUrl = " ";
        Camera usable = NewCamera("Usable");

        PrinterCameraSelection selection = PrinterCameraSelector.SelectSnapshotCamera([disabled, noSnapshot, usable]);

        selection.Should().Be(new PrinterCameraSelection(usable, PrinterCameraAvailability.Available));
    }

    [Fact]
    public void SelectSnapshotCamera_AllOffline_ReturnsFirstOfflineCandidate()
    {
        Camera first = NewCamera("First");
        first.HealthStatus = CameraHealthStatus.Unhealthy;
        Camera second = NewCamera("Second");
        second.HealthStatus = CameraHealthStatus.Unhealthy;

        PrinterCameraSelection selection = PrinterCameraSelector.SelectSnapshotCamera([first, second]);

        selection.Should().Be(new PrinterCameraSelection(first, PrinterCameraAvailability.CamerasOffline));
    }

    [Fact]
    public void SelectSnapshotCamera_DistinguishesNoCameraDisabledAndNoSnapshot()
    {
        Camera disabled = NewCamera("Disabled");
        disabled.IsEnabled = false;
        Camera rtspOnly = NewCamera("RTSP");
        rtspOnly.SnapshotUrl = null;

        PrinterCameraSelector.SelectSnapshotCamera([]).Availability
            .Should().Be(PrinterCameraAvailability.NoCameraLinked);
        PrinterCameraSelector.SelectSnapshotCamera([disabled]).Availability
            .Should().Be(PrinterCameraAvailability.AllCamerasDisabled);
        PrinterCameraSelector.SelectSnapshotCamera([disabled, rtspOnly]).Availability
            .Should().Be(PrinterCameraAvailability.NoSnapshotUrl);
    }

    private static Camera NewCamera(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SnapshotUrl = $"http://{name.Replace(' ', '-')}/snapshot.jpg",
        IsEnabled = true,
    };

    private static void LinkTo(Camera camera, Guid printerId, bool isPrimary, int sortOrder)
    {
        camera.PrinterLinks.Add(new CameraPrinter
        {
            CameraId = camera.Id,
            Camera = camera,
            PrinterId = printerId,
            IsPrimary = isPrimary,
            SortOrder = sortOrder,
        });
    }
}
