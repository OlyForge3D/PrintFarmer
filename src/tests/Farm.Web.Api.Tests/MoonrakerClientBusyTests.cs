using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests that MoonrakerClient correctly propagates firmware-level busy responses as
/// <see cref="PrinterBackendBusyException"/>, and that Moonraker 503 (Klippy unavailable)
/// is NOT treated as printer-busy unless the body contains printing-busy keywords (#317, #318).
/// </summary>
public class MoonrakerClientBusyTests
{
    private static (MoonrakerClient client, Mock<HttpMessageHandler> handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responder(req));

#pragma warning disable CA2000
        HttpClient http = new(handler.Object);
#pragma warning restore CA2000

        MoonrakerClient client = new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        return (client, handler);
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns409_ThrowsPrinterBackendBusyException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "G28"));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithPrintingBody_ThrowsPrinterBackendBusyException()
    {
        // A 503 body that explicitly indicates the printer is printing should still be treated as busy.
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"Script response: Unable to send command, printer is currently printing"}""",
                Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "M104 S200"));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithKlippyNotConnectedBody_ReturnsFalse()
    {
        // Moonraker 503 with "Klippy not connected" means backend unavailable, not printer-busy-printing.
        // Should return false (transport error), not throw PrinterBackendBusyException (#318 blocker 2).
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"Klippy is not connected"}""",
                Encoding.UTF8, "application/json")
        });

        bool result = await client.SendGcodeAsync("http://moonraker:7125", "M104 S200");
        result.Should().BeFalse(because: "Klippy-unavailable 503 is a transport error, not a printer-busy signal");
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithEmptyBody_ReturnsFalse()
    {
        // A bare 503 with no body is Klippy unavailable, not printer-busy.
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        bool result = await client.SendGcodeAsync("http://moonraker:7125", "M104 S200");
        result.Should().BeFalse(because: "bare 503 must not be treated as printer-busy (#318 blocker 2)");
    }

    [Fact]
    public async Task SetTemps_WhenFirmwareReturns503WithKlippyBody_ReturnsFalse()
    {
        // SetTempsAsync routes through SendGcodePrivateAsync; same narrowing rule applies.
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"Klippy is not ready"}""",
                Encoding.UTF8, "application/json")
        });

        bool result = await client.SetTempsAsync("http://moonraker:7125", hotend: 200);
        result.Should().BeFalse(because: "Klippy-not-ready 503 must not be treated as printer-busy (#318 blocker 2)");
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithKlippyBusyInitializingBody_ReturnsFalse()
    {
        // "Klippy is busy initializing" contains "busy" as a substring but is NOT printer-job-busy.
        // Phrase-based matching must not match this (#318 r23 blocker).
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"Klippy is busy initializing"}""",
                Encoding.UTF8, "application/json")
        });

        bool result = await client.SendGcodeAsync("http://moonraker:7125", "M104 S200");
        result.Should().BeFalse(because: "'Klippy is busy initializing' is a startup state, not printer-job-busy");
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithSdBusyBody_ThrowsPrinterBackendBusyException()
    {
        // "SD busy" is an unambiguous printer-job-busy signal (SD card locked by active print).
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"SD busy"}""",
                Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "M104 S200"));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns503WithUppercasePrinterIsPrintingBody_ThrowsPrinterBackendBusyException()
    {
        // Detection must be case-insensitive.
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":"WebRequestError","message":"PRINTER IS PRINTING"}""",
                Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<PrinterBackendBusyException>(
            () => client.SendGcodeAsync("http://moonraker:7125", "M104 S200"));
    }

    [Fact]
    public async Task SendGcode_WhenFirmwareReturns200_ReturnsTrueWithoutException()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        bool result = await client.SendGcodeAsync("http://moonraker:7125", "M84");
        result.Should().BeTrue();
    }

    [Fact]
    public void BuildExcludeObjectCommand_WhenNameIsValid_ReturnsUnquotedCommand()
    {
        string command = MoonrakerClient.BuildExcludeObjectCommand("object_1");

        command.Should().Be("EXCLUDE_OBJECT NAME=object_1");
    }

    [Fact]
    public void BuildExcludeObjectCommand_WhenNameContainsSpace_ReturnsQuotedCommand()
    {
        string command = MoonrakerClient.BuildExcludeObjectCommand("left cube");

        command.Should().Be("EXCLUDE_OBJECT NAME=\"left cube\"");
    }

    [Fact]
    public void BuildExcludeObjectCommand_WhenNameHasSurroundingWhitespace_PreservesExactName()
    {
        string command = MoonrakerClient.BuildExcludeObjectCommand(" left cube ");

        command.Should().Be("EXCLUDE_OBJECT NAME=\" left cube \"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("bad;M112")]
    public void BuildExcludeObjectCommand_WhenNameIsInvalid_ThrowsArgumentException(string objectName)
    {
        Action act = () => MoonrakerClient.BuildExcludeObjectCommand(objectName);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ExcludeObjectAsync_WhenNameIsValid_SendsExcludeObjectGcode()
    {
        string? postedJson = null;
        (MoonrakerClient client, _) = CreateClient(req =>
        {
            postedJson = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        bool result = await client.ExcludeObjectAsync("http://moonraker:7125", "object_1");

        result.Should().BeTrue();
        postedJson.Should().NotBeNull();
        using JsonDocument doc = JsonDocument.Parse(postedJson!);
        doc.RootElement.GetProperty("script").GetString().Should().Be("EXCLUDE_OBJECT NAME=object_1");
    }

    [Fact]
    public async Task ExcludeObjectAsync_WhenNameHasSurroundingWhitespace_SendsExactName()
    {
        string? postedJson = null;
        (MoonrakerClient client, _) = CreateClient(req =>
        {
            postedJson = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        bool result = await client.ExcludeObjectAsync("http://moonraker:7125", " object_1 ");

        result.Should().BeTrue();
        postedJson.Should().NotBeNull();
        using JsonDocument doc = JsonDocument.Parse(postedJson!);
        doc.RootElement.GetProperty("script").GetString().Should().Be("EXCLUDE_OBJECT NAME=\" object_1 \"");
    }

    [Fact]
    public async Task GetCurrentJobObjectsAsync_WhenPrintStatsStateIsComplete_ReturnsNoObjects()
    {
        (MoonrakerClient client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "result": {
                    "status": {
                      "print_stats": { "state": "complete", "filename": "plate.gcode" },
                      "exclude_object": {
                        "objects": [{ "name": "cube" }],
                        "excluded_objects": [],
                        "current_object": "cube"
                      }
                    }
                  }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        PrintJobObjectListDto? result = await client.GetCurrentJobObjectsAsync("http://moonraker:7125");

        result.Should().NotBeNull();
        result!.JobName.Should().BeNull();
        result.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentJobObjectsAsync_WhenMetadataObjectHasSurroundingWhitespace_PreservesExactIdentity()
    {
        (MoonrakerClient client, _) = CreateClient(req =>
        {
            string pathAndQuery = req.RequestUri?.PathAndQuery ?? string.Empty;
            if (pathAndQuery.StartsWith("/printer/objects/query", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "result": {
                            "status": {
                              "print_stats": { "state": "printing", "filename": "plate.gcode" },
                              "exclude_object": {
                                "excluded_objects": [" cube "],
                                "current_object": " cube "
                              }
                            }
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "result": {
                        "object_info": [
                          { "name": " cube " }
                        ]
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        PrintJobObjectListDto? result = await client.GetCurrentJobObjectsAsync("http://moonraker:7125");

        result.Should().NotBeNull();
        PrintJobObjectDto objectState = result!.Objects.Single();
        objectState.Name.Should().Be(" cube ");
        objectState.IsExcluded.Should().BeTrue();
        objectState.IsCurrent.Should().BeTrue();
    }
}
