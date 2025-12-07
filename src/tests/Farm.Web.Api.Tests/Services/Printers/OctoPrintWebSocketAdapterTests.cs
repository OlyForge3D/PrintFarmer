using System.Reflection;
using Farm.Web.Api.Services;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

public class OctoPrintWebSocketAdapterTests
{
    [Fact]
    public void ParsePrinterState_ExtractsTempsAndState_WhenPrinting()
    {
        const string json = """
        {
          "state": {
            "flags": {
              "operational": true,
              "printing": true,
              "paused": false
            }
          },
          "currentZ": 12.3,
          "temperature": {
            "tool0": { "actual": 210.5, "target": 215.0 },
            "bed": { "actual": 60.1, "target": 65.0 }
          }
        }
        """;

        var result = InvokeParsePrinterState(json);

        GetProperty<bool>(result, "IsOnline").Should().BeTrue();
        GetProperty<bool>(result, "Operational").Should().BeTrue();
        GetProperty<string?>(result, "State").Should().Be("Printing");
        GetProperty<double?>(result, "Z").Should().Be(12.3);
        GetProperty<double?>(result, "HotendTemp").Should().Be(210.5);
        GetProperty<double?>(result, "HotendTarget").Should().Be(215.0);
        GetProperty<double?>(result, "BedTemp").Should().Be(60.1);
        GetProperty<double?>(result, "BedTarget").Should().Be(65.0);
    }

    [Fact]
    public void ParsePrinterState_ReturnsOffline_WhenNotOperational()
    {
        const string json = """
        {
          "state": {
            "flags": {
              "operational": false,
              "printing": false,
              "paused": false
            }
          }
        }
        """;

        var result = InvokeParsePrinterState(json);

        GetProperty<bool>(result, "IsOnline").Should().BeFalse();
        GetProperty<bool>(result, "Operational").Should().BeFalse();
        GetProperty<string?>(result, "State").Should().Be("Offline");
        GetProperty<double?>(result, "Z").Should().BeNull();
        GetProperty<double?>(result, "HotendTemp").Should().BeNull();
        GetProperty<double?>(result, "BedTemp").Should().BeNull();
    }

    [Fact]
    public void ParsePrinterState_InvalidJson_ThrowsInvalidOperation()
    {
        Action act = () => InvokeParsePrinterState("not json");

      var inner = act.Should()
        .Throw<TargetInvocationException>()
        .WithInnerExceptionExactly<InvalidOperationException>()
        .Which;

      inner.Message.Should().Be("Failed to parse OctoPrint printer state");
    }

    private static T? GetProperty<T>(object instance, string propertyName)
    {
        var value = instance.GetType().GetProperty(propertyName)!.GetValue(instance);
        return (T?)value;
    }

    private static object InvokeParsePrinterState(string json)
    {
        var method = typeof(OctoPrintWebSocketAdapter).GetMethod(
            "ParsePrinterState",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("ParsePrinterState should exist");

        return method!.Invoke(null, new object[] { json })!;
    }
}
