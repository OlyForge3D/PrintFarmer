using Farm.Infrastructure.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Domain;

public sealed class CameraLinkExtensionsTests
{
    private static Printer NewPrinter(Location? location = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "printer",
        ServerUrl = $"http://{Guid.NewGuid():N}.local",
        LocationId = location?.Id,
        Location = location,
    };

    private static Location NewLocation(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static void Link(Camera camera, Printer printer, bool isPrimary = false, int sortOrder = 0)
        => camera.LinkToPrinter(printer.Id, isPrimary, sortOrder).Printer = printer;

    [Fact]
    public void EffectiveLocation_NoLinks_UsesCameraAssignedLocation()
    {
        Location room = NewLocation("Room");
        var camera = new Camera { Name = "c", LocationId = room.Id, AssignedLocation = room };

        camera.GetEffectiveLocationId().Should().Be(room.Id);
        camera.GetEffectiveLocation().Should().BeSameAs(room);
        camera.IsLocationDerivedFromPrinter().Should().BeFalse();
    }

    [Fact]
    public void EffectiveLocation_SingleLink_UsesPrinterLocationOverCameraLocation()
    {
        Location printerRoom = NewLocation("Printer room");
        Location cameraRoom = NewLocation("Camera room");
        var camera = new Camera { Name = "c", LocationId = cameraRoom.Id, AssignedLocation = cameraRoom };
        Link(camera, NewPrinter(printerRoom), isPrimary: true);

        camera.GetEffectiveLocationId().Should().Be(printerRoom.Id);
        camera.GetEffectiveLocation().Should().BeSameAs(printerRoom);
        camera.IsLocationDerivedFromPrinter().Should().BeTrue();
    }

    [Fact]
    public void EffectiveLocation_SingleLinkWithoutPrinterLocation_IsNull()
    {
        Location cameraRoom = NewLocation("Camera room");
        var camera = new Camera { Name = "c", LocationId = cameraRoom.Id, AssignedLocation = cameraRoom };
        Link(camera, NewPrinter());

        camera.GetEffectiveLocationId().Should().BeNull();
    }

    [Fact]
    public void EffectiveLocation_MultipleLinks_UsesCameraAssignedLocation()
    {
        Location cameraRoom = NewLocation("Camera room");
        var camera = new Camera { Name = "c", LocationId = cameraRoom.Id, AssignedLocation = cameraRoom };
        Link(camera, NewPrinter(NewLocation("A")));
        Link(camera, NewPrinter(NewLocation("B")));

        camera.GetEffectiveLocationId().Should().Be(cameraRoom.Id);
        camera.GetEffectiveLocation().Should().BeSameAs(cameraRoom);
        camera.IsLocationDerivedFromPrinter().Should().BeFalse();
    }

    [Fact]
    public void PrimaryPrinter_PrefersPrimaryLinkThenSortOrder()
    {
        var camera = new Camera { Name = "c" };
        Printer first = NewPrinter();
        Printer primary = NewPrinter();
        Link(camera, first, sortOrder: 0);
        Link(camera, primary, isPrimary: true, sortOrder: 5);

        camera.GetPrimaryPrinterId().Should().Be(primary.Id);
        camera.GetPrimaryPrinter().Should().BeSameAs(primary);
        camera.OrderedPrinterLinks().Select(l => l.PrinterId).Should().Equal(primary.Id, first.Id);
    }

    [Fact]
    public void PrimaryPrinter_NoLinks_IsNull()
    {
        var camera = new Camera { Name = "c" };

        camera.GetPrimaryPrinterId().Should().BeNull();
        camera.GetPrimaryPrinter().Should().BeNull();
    }

    [Fact]
    public void LinkToPrinter_IsIdempotent()
    {
        var camera = new Camera { Name = "c" };
        Guid printerId = Guid.NewGuid();

        CameraPrinter first = camera.LinkToPrinter(printerId, isPrimary: true);
        CameraPrinter second = camera.LinkToPrinter(printerId);

        second.Should().BeSameAs(first);
        camera.PrinterLinks.Should().ContainSingle();
        camera.IsLinkedTo(printerId).Should().BeTrue();
        camera.IsLinkedTo(Guid.NewGuid()).Should().BeFalse();
    }
}
