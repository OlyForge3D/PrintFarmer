using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// End-to-end wire-shape coverage for the per-tool attribution surface added to
/// <c>GET /api/printers/{id}/details</c> (issue #711, F6 backend). Confirms the
/// controller (a) projects <c>SupportsPerToolAttribution</c> and per-toolhead
/// <c>CumulativePrintHours</c> onto the JSON envelope, (b) gates them by both the
/// <c>MultiSlotFallback</c> operator feature and the printer's persisted domain flag,
/// and (c) preserves the deterministic non-omitting shape #719 UI consumers depend on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PrintersControllerPerToolAttributionDetailsTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task GetDetails_MoonrakerWithPerToolCapabilityAndHours_EmitsFlagTrueAndNumericHours()
    {
        await using CustomWebApplicationFactory factory = new();
        using HttpClient client = await factory.CreateAdminClientAsync();

        Guid printerId = await SeedPrinterAsync(
            factory,
            backend: PrinterBackend.Moonraker,
            supportsPerToolAttribution: true,
            toolheadHours: [4.5, 1.25]);

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/details");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();

        // Wire-shape assertions first — the deterministic JSON envelope is what #719 reads.
        JsonNode root = JsonNode.Parse(body)!;
        root["supportsPerToolAttribution"]!.GetValue<bool>().Should().BeTrue();

        JsonArray toolheads = root["toolheads"]!.AsArray();
        toolheads.Should().HaveCount(2);
        foreach (JsonNode? tool in toolheads)
        {
            tool.Should().NotBeNull();
            // Every toolhead entry must carry an explicit cumulativePrintHours key,
            // regardless of value.
            tool!.AsObject().ContainsKey("cumulativePrintHours").Should().BeTrue();
        }

        double t0 = toolheads[0]!["cumulativePrintHours"]!.GetValue<double>();
        double t1 = toolheads[1]!["cumulativePrintHours"]!.GetValue<double>();
        t0.Should().Be(4.5);
        t1.Should().Be(1.25);

        // Deserialized round-trip confirms the DTO carries the same values a typed
        // consumer would observe.
        PrinterDetailsDto details = JsonSerializer.Deserialize<PrinterDetailsDto>(body, ReadOptions)!;
        details.SupportsPerToolAttribution.Should().BeTrue();
        details.Toolheads.Should().NotBeNull();
        details.Toolheads!.Select(th => th.CumulativePrintHours)
            .Should().BeEquivalentTo([4.5, 1.25]);
    }

    [Fact]
    public async Task GetDetails_PrusaLinkSingleToolhead_EmitsFlagFalseAndNullHours()
    {
        await using CustomWebApplicationFactory factory = new();
        using HttpClient client = await factory.CreateAdminClientAsync();

        // PrusaLink single-toolhead printer — the derived capability is false so
        // the projection collapses even when the operator feature is on.
        Guid printerId = await SeedPrinterAsync(
            factory,
            backend: PrinterBackend.PrusaLink,
            supportsPerToolAttribution: false,
            toolheadHours: [7.0]);

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/details");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonNode root = JsonNode.Parse(body)!;

        root["supportsPerToolAttribution"]!.GetValue<bool>().Should().BeFalse();

        JsonArray toolheads = root["toolheads"]!.AsArray();
        toolheads.Should().HaveCount(1);
        // The odometer key is present but explicitly null so consumers see
        // "not applicable" rather than a missing key.
        toolheads[0]!.AsObject().ContainsKey("cumulativePrintHours").Should().BeTrue();
        toolheads[0]!["cumulativePrintHours"].Should().BeNull(
            "the printer does not support per-tool attribution so the persisted 7.0 hours must not leak onto the wire");

        PrinterDetailsDto details = JsonSerializer.Deserialize<PrinterDetailsDto>(body, ReadOptions)!;
        details.SupportsPerToolAttribution.Should().BeFalse();
        details.Toolheads.Should().NotBeNull();
        details.Toolheads![0].CumulativePrintHours.Should().BeNull();
    }

    [Fact]
    public async Task GetDetails_FeatureGloballyDisabled_ForcesFlagFalseAndNullHoursEvenForSupportedPrinter()
    {
        // The operator feature is hard-disabled via configuration (mirrors an
        // OperatorFeatures__multiSlotFallbackEnabled=false emergency rollback).
        // Even a Moonraker printer whose persisted SupportsPerToolAttribution flag is
        // true must project the wire fields to their unset shape.
        await using CustomWebApplicationFactory factory = new(new Dictionary<string, string?>
        {
            ["OperatorFeatures:multiSlotFallbackEnabled"] = "false",
        });
        using HttpClient client = await factory.CreateAdminClientAsync();

        Guid printerId = await SeedPrinterAsync(
            factory,
            backend: PrinterBackend.Moonraker,
            supportsPerToolAttribution: true,
            toolheadHours: [10.0, 20.0]);

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/details");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonNode root = JsonNode.Parse(body)!;

        root["supportsPerToolAttribution"]!.GetValue<bool>().Should().BeFalse(
            "the operator feature is disabled, so the capability must collapse to false");

        JsonArray toolheads = root["toolheads"]!.AsArray();
        toolheads.Should().HaveCount(2);
        foreach (JsonNode? tool in toolheads)
        {
            tool!.AsObject().ContainsKey("cumulativePrintHours").Should().BeTrue();
            tool["cumulativePrintHours"].Should().BeNull();
        }
    }

    [Fact]
    public async Task GetDetails_SupportedPrinterWithZeroHours_EmitsExplicitNumericZero()
    {
        // Regression guard: a supported printer that has accrued no per-tool hours yet
        // must render 0.0 as a numeric zero, not null. The UI uses this to distinguish
        // "supported and fresh baseline" from "not applicable".
        await using CustomWebApplicationFactory factory = new();
        using HttpClient client = await factory.CreateAdminClientAsync();

        Guid printerId = await SeedPrinterAsync(
            factory,
            backend: PrinterBackend.Moonraker,
            supportsPerToolAttribution: true,
            toolheadHours: [0.0, 0.0]);

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/details");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonNode root = JsonNode.Parse(body)!;

        root["supportsPerToolAttribution"]!.GetValue<bool>().Should().BeTrue();

        JsonArray toolheads = root["toolheads"]!.AsArray();
        toolheads.Should().HaveCount(2);
        foreach (JsonNode? tool in toolheads)
        {
            JsonNode? hours = tool!["cumulativePrintHours"];
            hours.Should().NotBeNull("zero hours must serialize as numeric 0, not omitted or null");
            hours!.GetValue<double>().Should().Be(0.0);
        }
    }

    private static async Task<Guid> SeedPrinterAsync(
        CustomWebApplicationFactory factory,
        PrinterBackend backend,
        bool supportsPerToolAttribution,
        double[] toolheadHours)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string suffix = Guid.NewGuid().ToString("N")[..8];

        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"PerToolDetails-Mfr-{suffix}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"PerToolDetails-Model-{suffix}",
            ManufacturerId = manufacturer.Id,
        };

        List<Toolhead> toolheads = new(toolheadHours.Length);
        for (int i = 0; i < toolheadHours.Length; i++)
        {
            toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                Name = $"T{i}",
                Index = i,
                IsPrimary = i == 0,
                ToolheadType = ToolheadType.Physical,
                CumulativePrintHours = toolheadHours[i],
            });
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"per-tool-details-{suffix}",
            ServerUrl = $"http://per-tool-details-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            Backend = (int)backend,
            IsEnabled = false,
            SupportsPerToolAttribution = supportsPerToolAttribution,
            Toolheads = toolheads,
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer.Id;
    }
}
