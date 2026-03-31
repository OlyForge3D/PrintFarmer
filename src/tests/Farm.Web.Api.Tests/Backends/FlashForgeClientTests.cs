using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.FlashForge;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

/// <summary>
/// Tests for FlashForge TCP client response parsing and URL handling.
/// Tests the internal static parsing methods (ParseHostPort, ParseMachineStatus,
/// ParseTemperatures, ParseProgress, ParseDeviceInfo) that extract structured data
/// from FlashForge's proprietary ~Mxxx command responses.
/// </summary>
public sealed class FlashForgeClientTests
{
    #region ParseHostPort

    [Fact]
    public void ParseHostPort_HostAndPort_ReturnsCorrectly()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("192.168.1.100:8899");

        host.Should().Be("192.168.1.100");
        port.Should().Be(8899);
    }

    [Fact]
    public void ParseHostPort_HostOnly_ReturnsDefaultPort()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("192.168.1.100");

        host.Should().Be("192.168.1.100");
        port.Should().Be(IFlashForgeClient.DefaultPort);
    }

    [Fact]
    public void ParseHostPort_HttpScheme_StripsScheme()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("http://192.168.1.100:8080");

        host.Should().Be("192.168.1.100");
        port.Should().Be(8080);
    }

    [Fact]
    public void ParseHostPort_HttpsScheme_StripsScheme()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("https://printer.local:8899");

        host.Should().Be("printer.local");
        port.Should().Be(8899);
    }

    [Fact]
    public void ParseHostPort_HttpSchemeNoPort_ReturnsDefault()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("http://printer.local");

        host.Should().Be("printer.local");
        port.Should().Be(IFlashForgeClient.DefaultPort);
    }

    [Fact]
    public void ParseHostPort_WithTrailingPath_StripsPath()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("http://192.168.1.100:8899/some/path");

        host.Should().Be("192.168.1.100");
        port.Should().Be(8899);
    }

    [Fact]
    public void ParseHostPort_AD5XPort_ParsesCorrectly()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("10.0.0.50:8080");

        host.Should().Be("10.0.0.50");
        port.Should().Be(8080);
    }

    [Fact]
    public void ParseHostPort_NullOrWhitespace_Throws()
    {
        Action act = () => FlashForgeClient.ParseHostPort("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseHostPort_Hostname_ParsesCorrectly()
    {
        var (host, port) = FlashForgeClient.ParseHostPort("my-flashforge.local:8899");

        host.Should().Be("my-flashforge.local");
        port.Should().Be(8899);
    }

    #endregion

    #region ParseMachineStatus

    [Theory]
    [InlineData("READY", "Idle")]
    [InlineData("BUILDING_FROM_SD", "Printing")]
    [InlineData("BUILDING", "Printing")]
    [InlineData("PAUSED", "Paused")]
    [InlineData("BUILDING_COMPLETED", "Complete")]
    public void ParseMachineStatus_KnownStates_MapsCorrectly(string rawStatus, string expected)
    {
        string response = $"CMD M119 Received.\nEndstop: X-max:0 Y-max:0 Z-max:0\nMachineStatus: {rawStatus}\nMoveMode: READY\nok\n";

        string? result = FlashForgeClient.ParseMachineStatus(response);

        result.Should().Be(expected);
    }

    [Fact]
    public void ParseMachineStatus_UnknownState_ReturnsRawValue()
    {
        string response = "CMD M119 Received.\nMachineStatus: CALIBRATING\nok\n";

        string? result = FlashForgeClient.ParseMachineStatus(response);

        result.Should().Be("CALIBRATING");
    }

    [Fact]
    public void ParseMachineStatus_NoStatusField_ReturnsNull()
    {
        string response = "CMD M119 Received.\nEndstop: X-max:0\nok\n";

        string? result = FlashForgeClient.ParseMachineStatus(response);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseMachineStatus_EmptyResponse_ReturnsNull()
    {
        string? result = FlashForgeClient.ParseMachineStatus("");

        result.Should().BeNull();
    }

    [Fact]
    public void ParseMachineStatus_FullM119Response_ParsesCorrectly()
    {
        // Realistic M119 response from an Adventurer printer
        string response =
            "CMD M119 Received.\n" +
            "Endstop: X-max:0 Y-max:0 Z-max:0\n" +
            "MachineStatus: BUILDING_FROM_SD\n" +
            "MoveMode: MOVING\n" +
            "Status: S:0 L:0 J:0 F:0\n" +
            "ok\n";

        string? result = FlashForgeClient.ParseMachineStatus(response);

        result.Should().Be("Printing");
    }

    #endregion

    #region ParseTemperatures

    [Fact]
    public void ParseTemperatures_StandardResponse_ParsesAllValues()
    {
        string response = "CMD M105 Received.\nT0:205 /210 B:60 /65\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(205);
        hotendTarget.Should().Be(210);
        bedTemp.Should().Be(60);
        bedTarget.Should().Be(65);
    }

    [Fact]
    public void ParseTemperatures_DecimalValues_ParsesCorrectly()
    {
        string response = "CMD M105 Received.\nT0:205.5 /210.0 B:59.8 /65.0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(205.5);
        hotendTarget.Should().Be(210.0);
        bedTemp.Should().Be(59.8);
        bedTarget.Should().Be(65.0);
    }

    [Fact]
    public void ParseTemperatures_ColdPrinter_ReturnsZeroValues()
    {
        string response = "CMD M105 Received.\nT0:22 /0 B:21 /0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(22);
        hotendTarget.Should().Be(0);
        bedTemp.Should().Be(21);
        bedTarget.Should().Be(0);
    }

    [Fact]
    public void ParseTemperatures_HotendOnly_ReturnsBedNull()
    {
        string response = "CMD M105 Received.\nT0:205 /210\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(205);
        hotendTarget.Should().Be(210);
        bedTemp.Should().BeNull();
        bedTarget.Should().BeNull();
    }

    [Fact]
    public void ParseTemperatures_NoMatch_ReturnsAllNull()
    {
        string response = "CMD M105 Received.\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().BeNull();
        hotendTarget.Should().BeNull();
        bedTemp.Should().BeNull();
        bedTarget.Should().BeNull();
    }

    [Fact]
    public void ParseTemperatures_EmptyResponse_ReturnsAllNull()
    {
        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures("");

        hotendTemp.Should().BeNull();
        hotendTarget.Should().BeNull();
        bedTemp.Should().BeNull();
        bedTarget.Should().BeNull();
    }

    #endregion

    #region ParseProgress

    [Fact]
    public void ParseProgress_ActivePrint_ReturnsProgressPercentage()
    {
        string response = "CMD M27 Received.\nSD printing byte 1234/5678\nok\n";

        var (progress, jobName) = FlashForgeClient.ParseProgress(response);

        progress.Should().BeApproximately(21.7, 0.1); // 1234/5678 * 100
        jobName.Should().BeNull();
    }

    [Fact]
    public void ParseProgress_Complete_Returns100Percent()
    {
        string response = "CMD M27 Received.\nSD printing byte 5678/5678\nok\n";

        var (progress, jobName) = FlashForgeClient.ParseProgress(response);

        progress.Should().Be(100.0);
        jobName.Should().BeNull();
    }

    [Fact]
    public void ParseProgress_JustStarted_ReturnsZero()
    {
        string response = "CMD M27 Received.\nSD printing byte 0/5678\nok\n";

        var (progress, jobName) = FlashForgeClient.ParseProgress(response);

        progress.Should().Be(0.0);
        jobName.Should().BeNull();
    }

    [Fact]
    public void ParseProgress_LargeFile_ParsesCorrectly()
    {
        string response = "CMD M27 Received.\nSD printing byte 50000000/100000000\nok\n";

        var (progress, _) = FlashForgeClient.ParseProgress(response);

        progress.Should().Be(50.0);
    }

    [Fact]
    public void ParseProgress_NotPrinting_ReturnsNull()
    {
        string response = "CMD M27 Received.\nNot SD printing\nok\n";

        var (progress, jobName) = FlashForgeClient.ParseProgress(response);

        progress.Should().BeNull();
        jobName.Should().BeNull();
    }

    [Fact]
    public void ParseProgress_EmptyResponse_ReturnsNull()
    {
        var (progress, jobName) = FlashForgeClient.ParseProgress("");

        progress.Should().BeNull();
        jobName.Should().BeNull();
    }

    [Fact]
    public void ParseProgress_ZeroTotal_ReturnsNull()
    {
        // Guard against divide by zero
        string response = "CMD M27 Received.\nSD printing byte 0/0\nok\n";

        var (progress, jobName) = FlashForgeClient.ParseProgress(response);

        progress.Should().BeNull();
        jobName.Should().BeNull();
    }

    #endregion

    #region ParseDeviceInfo

    [Fact]
    public void ParseDeviceInfo_FullResponse_ParsesAllFields()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 5X\n" +
            "Machine Name: AD5X\n" +
            "Firmware: v2.7.9\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Model.Should().Be("Adventurer 5X");
        info.Name.Should().Be("AD5X");
        info.Firmware.Should().Be("v2.7.9");
    }

    [Fact]
    public void ParseDeviceInfo_ModelOnly_FallsBackToDefaults()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Dreamer NX\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Model.Should().Be("Dreamer NX");
        info.Name.Should().Be("FlashForge"); // Default
        info.Firmware.Should().Be("Unknown"); // Default
    }

    [Fact]
    public void ParseDeviceInfo_EmptyResponse_ReturnsDefaults()
    {
        var info = FlashForgeClient.ParseDeviceInfo("");

        info.Model.Should().Be("Unknown");
        info.Name.Should().Be("FlashForge");
        info.Firmware.Should().Be("Unknown");
    }

    [Fact]
    public void ParseDeviceInfo_AdventurerResponse_ParsesCorrectly()
    {
        // Realistic response from an Adventurer 3
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Flashforge Adventurer 3\n" +
            "Machine Name: MyPrinter\n" +
            "Firmware: v1.2.4\n" +
            "SN: SN12345678\n" +
            "X: 150 Y: 150 Z: 150\n" +
            "Tool Count: 1\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Model.Should().Be("Flashforge Adventurer 3");
        info.Name.Should().Be("MyPrinter");
        info.Firmware.Should().Be("v1.2.4");
    }

    [Fact]
    public void ParseDeviceInfo_NameOnly_ReturnsNameWithDefaults()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Name: Lab Printer\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Name.Should().Be("Lab Printer");
        info.Model.Should().Be("Unknown");
        info.Firmware.Should().Be("Unknown");
    }

    #endregion

    #region Multi-Extruder Edge Cases & Regression Tests

    [Fact]
    public void ParseExtruderTemperatures_MalformedResponse_ReturnsEmptyExtruders()
    {
        string response = "CMD M105 Received.\ngarbage data here\nok\n";

        var (extruders, bedTemp, bedTarget) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().BeEmpty();
        bedTemp.Should().BeNull();
        bedTarget.Should().BeNull();
    }

    [Fact]
    public void ParseExtruderTemperatures_T1ActiveWhileT0Idle_ParsesBothCorrectly()
    {
        // Edge case: second extruder heating while first is cold (IDEX independent mode)
        string response = "CMD M105 Received.\nT0:0.0 /0.0 T1:200.0 /200.0 B:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().HaveCount(2);
        extruders[0].Current.Should().Be(0.0);
        extruders[0].Target.Should().Be(0.0);
        extruders[1].Current.Should().Be(200.0);
        extruders[1].Target.Should().Be(200.0);
    }

    [Fact]
    public void ParseExtruderTemperatures_HighTemperatures_ParsesCorrectly()
    {
        // Boundary: temperatures near max for all-metal hotends
        string response = "CMD M105 Received.\nT0:499.9 /500.0 B:120.0 /120.0\nok\n";

        var (extruders, bedTemp, bedTarget) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().HaveCount(1);
        extruders[0].Current.Should().Be(499.9);
        extruders[0].Target.Should().Be(500.0);
        bedTemp.Should().Be(120.0);
        bedTarget.Should().Be(120.0);
    }

    [Fact]
    public void ParseExtruderTemperatures_BedOnly_ReturnsEmptyExtrudersWithBed()
    {
        // Only bed temp reported — no extruder entries
        string response = "CMD M105 Received.\nB:60.0 /60.0\nok\n";

        var (extruders, bedTemp, bedTarget) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().BeEmpty();
        bedTemp.Should().Be(60.0);
        bedTarget.Should().Be(60.0);
    }

    [Fact]
    public void ParseExtruderTemperatures_DictionaryKeysMatchExtruderIndices()
    {
        string response = "CMD M105 Received.\nT0:200.0 /200.0 T1:180.0 /180.0 T2:160.0 /160.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Keys.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public void ParseExtruderTemperatures_DoesNotIncludeBedAsExtruder()
    {
        // "B:" must not be matched by the Tn regex
        string response = "CMD M105 Received.\nT0:200.0 /200.0 B:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().HaveCount(1);
        extruders.Keys.Should().OnlyContain(k => k >= 0);
    }

    [Fact]
    public void ParseExtruderTemperatures_PartiallyMalformed_ParsesValidEntries()
    {
        // T0 valid, T1 missing target value — T0 should still parse
        string response = "CMD M105 Received.\nT0:200.0 /200.0 T1:garbage B:60.0 /60.0\nok\n";

        var (extruders, bedTemp, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().ContainKey(0);
        extruders[0].Current.Should().Be(200.0);
        bedTemp.Should().Be(60.0);
    }

    [Fact]
    public void ParseExtruderTemperatures_ZeroTemperatures_ParsedNotSkipped()
    {
        // Zero temps are valid — they mean the extruder exists but is cold
        string response = "CMD M105 Received.\nT0:0.0 /0.0 T1:0.0 /0.0 B:0.0 /0.0\nok\n";

        var (extruders, bedTemp, bedTarget) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Should().HaveCount(2);
        extruders[0].Current.Should().Be(0.0);
        extruders[1].Current.Should().Be(0.0);
        bedTemp.Should().Be(0.0);
        bedTarget.Should().Be(0.0);
    }

    #endregion

    #region Extruder Count Detection

    [Fact]
    public void ParseExtruderTemperatures_SingleExtruder_CountIsOne()
    {
        string response = "CMD M105 Received.\nT0:200.0 /200.0 B:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Count.Should().Be(1);
    }

    [Fact]
    public void ParseExtruderTemperatures_DualExtruder_CountIsTwo()
    {
        string response = "CMD M105 Received.\nT0:219.6 /220.0 T1:0.0 /0.0 B:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Count.Should().Be(2, "ADX5 M105 reports T0 and T1");
    }

    [Fact]
    public void ParseExtruderTemperatures_QuadExtruder_CountIsFour()
    {
        string response = "CMD M105 Received.\nT0:200.0 /200.0 T1:180.0 /180.0 T2:0.0 /0.0 T3:0.0 /0.0 B:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Count.Should().Be(4);
    }

    [Fact]
    public void ParseExtruderTemperatures_NoExtruders_CountIsZero()
    {
        string response = "CMD M105 Received.\nB:60.0 /60.0\nok\n";

        var (extruders, _, _) = FlashForgeClient.ParseExtruderTemperatures(response);

        extruders.Count.Should().Be(0);
    }

    #endregion

    #region Backward Compatibility — T0 still populates HotendTemp/HotendTarget

    [Fact]
    public void ParseTemperatures_DualExtruderResponse_StillReturnsPrimaryHotend()
    {
        // ADX5 dual-extruder response — existing ParseTemperatures must still return T0
        string response = "CMD M105 Received.\nT0:219.6 /220.0 T1:0.0 /0.0 B:60.0 /60.0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(219.6);
        hotendTarget.Should().Be(220.0);
        bedTemp.Should().Be(60.0);
        bedTarget.Should().Be(60.0);
    }

    [Fact]
    public void ParseTemperatures_QuadExtruderResponse_StillReturnsPrimaryHotend()
    {
        string response = "CMD M105 Received.\nT0:200.0 /200.0 T1:180.0 /180.0 T2:0.0 /0.0 T3:0.0 /0.0 B:60.0 /60.0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(200.0);
        hotendTarget.Should().Be(200.0);
        bedTemp.Should().Be(60.0);
        bedTarget.Should().Be(60.0);
    }

    [Fact]
    public void ParseTemperatures_NoSpacesAroundSlash_StillParsesT0()
    {
        string response = "CMD M105 Received.\nT0:205.5/210.0 B:59.8/65.0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().Be(205.5);
        hotendTarget.Should().Be(210.0);
        bedTemp.Should().Be(59.8);
        bedTarget.Should().Be(65.0);
    }

    [Fact]
    public void ParseTemperatures_T0Absent_ReturnsHotendNull()
    {
        // Only T1 present — ParseTemperatures should return null for HotendTemp since T0 missing
        string response = "CMD M105 Received.\nT1:200.0 /200.0 B:60.0 /60.0\nok\n";

        var (hotendTemp, hotendTarget, bedTemp, bedTarget) = FlashForgeClient.ParseTemperatures(response);

        hotendTemp.Should().BeNull("T0 not present in response");
        hotendTarget.Should().BeNull("T0 not present in response");
        bedTemp.Should().Be(60.0);
        bedTarget.Should().Be(60.0);
    }

    #endregion

    #region BackendPlugin Metadata

    [Fact]
    public void BackendPlugin_HasCorrectMetadata()
    {
        var plugin = new FlashForgeBackendPlugin();

        plugin.BackendType.Should().Be("flashforge");
        plugin.DisplayName.Should().Be("FlashForge");
        plugin.Version.Should().Be(new Version(1, 0, 0));
        plugin.ClientType.Should().Be(typeof(FlashForgeClient));
        plugin.ClientInterfaceType.Should().Be(typeof(IFlashForgeClient));
    }

    [Fact]
    public void BackendPlugin_DeclaresExpectedCapabilities()
    {
        // Cast required: default interface methods dispatch only via interface reference.
        IBackendClientPlugin plugin = new FlashForgeBackendPlugin();

        IEnumerable<Type> capabilities = plugin.GetCapabilities();

        capabilities.Should().Contain(typeof(ISupportsFileUpload));
        capabilities.Should().Contain(typeof(ISupportsStartPrint));
        capabilities.Should().Contain(typeof(ISupportsUploadAndPrint));
        capabilities.Should().Contain(typeof(ISupportsControlOperations));
        capabilities.Should().Contain(typeof(ISupportsStatus));
        capabilities.Should().Contain(typeof(ISupportsCompositeStatus));
        capabilities.Should().Contain(typeof(ISupportsPrinterInformation));
        capabilities.Should().Contain(typeof(ISupportsTemperatureControl));
    }

    [Fact]
    public void BackendPlugin_HasConfigurationSections()
    {
        var plugin = new FlashForgeBackendPlugin();

        plugin.GetConfigurationSections().Should().Contain("FlashForge");
    }

    #endregion

    #region DefaultPort

    [Fact]
    public void DefaultPort_Is8899()
    {
        IFlashForgeClient.DefaultPort.Should().Be(8899);
    }

    #endregion

    #region DiscoveryProbe

    [Fact]
    public void DiscoveryProbe_HasCorrectMetadata()
    {
        var probe = new FlashForgeDiscoveryProbe(NullLogger<FlashForgeDiscoveryProbe>.Instance);

        probe.DisplayName.Should().Be("FlashForge");
        probe.Backend.Should().Be(PrinterBackend.FlashForge);
    }

    [Fact]
    public async Task DiscoveryProbe_ReturnsNullForUnreachableHost()
    {
        var probe = new FlashForgeDiscoveryProbe(NullLogger<FlashForgeDiscoveryProbe>.Instance);

        // RFC 5737 TEST-NET address — guaranteed non-routable
        var result = await probe.ProbeAsync("192.0.2.1", timeoutMs: 200, cancellationToken: default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DiscoveryProbe_ReturnsNullOnCancellation()
    {
        var probe = new FlashForgeDiscoveryProbe(NullLogger<FlashForgeDiscoveryProbe>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await probe.ProbeAsync("192.168.1.1", timeoutMs: 5000, cancellationToken: cts.Token);

        result.Should().BeNull();
    }

    #endregion

    #region Phase 2 — ToolCount Parsing from M115

    [Fact]
    public void ParseDeviceInfo_ToolCountOne_ParsesCorrectly()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 3\n" +
            "Machine Name: A3\n" +
            "Firmware: v1.2.4\n" +
            "Tool Count: 1\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.ToolCount.Should().Be(1);
    }

    [Fact]
    public void ParseDeviceInfo_ToolCountTwo_ParsesCorrectly()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 5X\n" +
            "Machine Name: ADX5\n" +
            "Firmware: v2.7.9\n" +
            "Tool Count: 2\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.ToolCount.Should().Be(2);
    }

    [Fact]
    public void ParseDeviceInfo_ToolCountFour_ParsesCorrectly()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Creator 4\n" +
            "Tool Count: 4\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.ToolCount.Should().Be(4);
    }

    [Fact]
    public void ParseDeviceInfo_NoToolCountField_ReturnsNull()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 3\n" +
            "Machine Name: A3\n" +
            "Firmware: v1.2.4\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.ToolCount.Should().BeNull();
    }

    [Fact]
    public void ParseDeviceInfo_MalformedToolCount_ReturnsNull()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 3\n" +
            "Tool Count: abc\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.ToolCount.Should().BeNull("malformed Tool Count should not parse");
    }

    [Fact]
    public void ParseDeviceInfo_FullResponseWithToolCount_AllFieldsPopulated()
    {
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 5X\n" +
            "Machine Name: ADX5\n" +
            "Firmware: v2.7.9\n" +
            "SN: SN12345678\n" +
            "X: 220 Y: 220 Z: 220\n" +
            "Tool Count: 2\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Model.Should().Be("Adventurer 5X");
        info.Name.Should().Be("ADX5");
        info.Firmware.Should().Be("v2.7.9");
        info.ToolCount.Should().Be(2);
    }

    [Fact]
    public void ParseDeviceInfo_BackwardCompat_ExistingFieldsStillWork()
    {
        // The existing Adventurer response test data includes "Tool Count: 1" —
        // verify ToolCount is populated without breaking existing assertions.
        string response =
            "CMD M115 Received.\n" +
            "Machine Type: Flashforge Adventurer 3\n" +
            "Machine Name: MyPrinter\n" +
            "Firmware: v1.2.4\n" +
            "SN: SN12345678\n" +
            "X: 150 Y: 150 Z: 150\n" +
            "Tool Count: 1\n" +
            "ok\n";

        var info = FlashForgeClient.ParseDeviceInfo(response);

        info.Model.Should().Be("Flashforge Adventurer 3");
        info.Name.Should().Be("MyPrinter");
        info.Firmware.Should().Be("v1.2.4");
        info.ToolCount.Should().Be(1);
    }

    #endregion

    #region Phase 2 — DetectExtruderCount

    [Fact]
    public void DetectExtruderCount_M115SaysOne_M105HasTwoExtruders_ReturnsTwo()
    {
        string m115 = "CMD M115 Received.\nMachine Type: ADX5\nTool Count: 1\nok\n";
        string m105 = "CMD M105 Received.\nT0:219.6 /220.0 T1:0.0 /0.0 B:60.0 /60.0\nok\n";

        int count = FlashForgeClient.DetectExtruderCount(m115, m105);

        count.Should().Be(2, "M105 detected 2 extruders which is higher than M115's Tool Count of 1");
    }

    [Fact]
    public void DetectExtruderCount_BothAgreeOnTwo_ReturnsTwo()
    {
        string m115 = "CMD M115 Received.\nTool Count: 2\nok\n";
        string m105 = "CMD M105 Received.\nT0:200.0 /200.0 T1:180.0 /180.0 B:60.0 /60.0\nok\n";

        int count = FlashForgeClient.DetectExtruderCount(m115, m105);

        count.Should().Be(2);
    }

    [Fact]
    public void DetectExtruderCount_M115SaysTwo_M105HasOnlyT0_ReturnsTwoFromM115()
    {
        string m115 = "CMD M115 Received.\nTool Count: 2\nok\n";
        string m105 = "CMD M105 Received.\nT0:200.0 /200.0 B:60.0 /60.0\nok\n";

        int count = FlashForgeClient.DetectExtruderCount(m115, m105);

        count.Should().Be(2, "M115 Tool Count wins when M105 reports fewer extruders");
    }

    [Fact]
    public void DetectExtruderCount_M115Missing_M105HasThreeExtruders_ReturnsThree()
    {
        string m115 = "CMD M115 Received.\nMachine Type: Custom\nok\n"; // no Tool Count
        string m105 = "CMD M105 Received.\nT0:200.0 /200.0 T1:180.0 /180.0 T2:160.0 /160.0 B:60.0 /60.0\nok\n";

        int count = FlashForgeClient.DetectExtruderCount(m115, m105);

        count.Should().Be(3, "M105 is the only source when M115 has no Tool Count");
    }

    [Fact]
    public void DetectExtruderCount_BothEmpty_ReturnsSafeDefault()
    {
        int count = FlashForgeClient.DetectExtruderCount("", "");

        count.Should().Be(1, "safe default when neither M115 nor M105 provides data");
    }

    [Fact]
    public void DetectExtruderCount_ADX5Realistic_ReturnsTwo()
    {
        // Realistic ADX5 scenario: firmware reports Tool Count: 1 but M105 shows T0+T1
        string m115 =
            "CMD M115 Received.\n" +
            "Machine Type: Adventurer 5X\n" +
            "Machine Name: ADX5\n" +
            "Firmware: v2.7.9\n" +
            "Tool Count: 1\n" +
            "ok\n";
        string m105 = "CMD M105 Received.\nT0:219.6 /220.0 T1:0.0 /0.0 B:60.0 /60.0\nok\n";

        int count = FlashForgeClient.DetectExtruderCount(m115, m105);

        count.Should().Be(2, "ADX5 reports Tool Count: 1 but actually has T0+T1");
    }

    [Fact]
    public void DetectExtruderCount_NullResponses_ReturnsSafeDefault()
    {
        int count = FlashForgeClient.DetectExtruderCount(null!, null!);

        count.Should().Be(1, "null inputs should degrade gracefully to safe default");
    }

    #endregion

    #region Phase 5 — ISupportsMultiExtruderTemperatureControl

    [Fact]
    public void FlashForgeClient_ImplementsMultiExtruderTemperatureControl()
    {
        typeof(FlashForgeClient).Should().Implement<ISupportsMultiExtruderTemperatureControl>(
            "FlashForge ADX5 supports per-extruder temperature addressing via T-index");
    }

    [Fact]
    public void IFlashForgeClient_InheritsMultiExtruderTemperatureControl()
    {
        typeof(IFlashForgeClient).GetInterfaces()
            .Should().Contain(typeof(ISupportsMultiExtruderTemperatureControl),
                "IFlashForgeClient must declare multi-extruder temp capability for plugin discovery");
    }

    [Fact]
    public void BackendPlugin_DeclaresMultiExtruderTemperatureCapability()
    {
        IBackendClientPlugin plugin = new FlashForgeBackendPlugin();

        IEnumerable<Type> capabilities = plugin.GetCapabilities();

        capabilities.Should().Contain(typeof(ISupportsMultiExtruderTemperatureControl),
            "plugin capability discovery should find ISupportsMultiExtruderTemperatureControl via reflection");
    }

    #endregion
}
