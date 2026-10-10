// <copyright file="PrinterPartialMmuGateSpoolBindHttpTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// End-to-end HTTP regression for issue #3292 (built on the #1588 gap-fill): a Qidi Plus 4
/// with a linked QidiBox whose persisted topology is the physical hotend (Index 0) plus only
/// three <see cref="ToolheadType.MmuGate"/> rows (Index 1..3) must accept
/// <c>PUT /api/printers/{id}/toolheads/4/spool</c> through the real guided-swap validator,
/// <see cref="Farm.Infrastructure.Services.Printers.IPrintersService"/>, and EF persistence.
/// Only the external spool source (<see cref="IFilamentCoverageSpoolResolver"/>) is substituted
/// so no live Spoolman or printer is contacted.
/// </summary>
public sealed class PrinterPartialMmuGateSpoolBindHttpTests : IAsyncLifetime, IDisposable
{
    private const int Gate4SpoolId = 4242;
    private const int RackSpoolId = 900;

    private readonly Mock<IFilamentCoverageSpoolResolver> _spoolResolver = new();
    private readonly PartialGateFactory _factory;

    public PrinterPartialMmuGateSpoolBindHttpTests()
    {
        _spoolResolver
            .Setup(r => r.ResolveSpoolAsync(It.IsAny<Printer>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer _, int spoolId, CancellationToken _) => new FilamentCoverageSpoolSnapshot(
                new SpoolmanSpoolDto(spoolId, $"Spool {spoolId}", "PLA", RemainingWeightG: 500, ColorHex: "#112233", InUse: true),
                TracksLiveConsumption: false,
                null));
        _spoolResolver
            .Setup(r => r.ResolveSpoolAsync(It.IsAny<CanonicalSpoolIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(null, TracksLiveConsumption: false, null));
        _spoolResolver
            .Setup(r => r.ResolveAsync(It.IsAny<IReadOnlyList<Printer>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>());

        _factory = new PartialGateFactory(_spoolResolver);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PutToolheadSpool_LiveOnlyFourthGate_OnPartialPersistedGateSet_BindsGate4AndPreservesGates1To3()
    {
        (Guid printerId, Guid submitRoleId) = await SeedQidiPartialGatePrinterAsync();
        using HttpClient client = await CreateSubmitClientAsync(submitRoleId);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IOperatorFeatureGate gate = scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>();
            (await gate.IsEnabledAsync(OperatorFeature.GuidedSwap, CancellationToken.None))
                .Should().BeTrue("this regression must exercise the guided validate-before-bind path");
        }

        Dictionary<int, (Guid Id, int? SpoolId, string? Material, string? Color)> before;
        long revisionBefore;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            before = await db.Toolheads.AsNoTracking()
                .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.MmuGate)
                .ToDictionaryAsync(t => t.Index, t => (t.Id, t.CurrentSpoolId, t.CurrentMaterial, t.CurrentFilamentColor));
            revisionBefore = (await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId)).ConfigurationRevision;
        }

        before.Keys.Should().BeEquivalentTo(new[] { 1, 2, 3 }, "precondition: only gates 1..3 are persisted");

        HttpResponseMessage current = await client.GetAsync($"/api/printers/{printerId}");
        current.StatusCode.Should().Be(HttpStatusCode.OK, await current.Content.ReadAsStringAsync());
        string etagBefore = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Printer GET omitted ETag.");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/printers/{printerId}/toolheads/4/spool")
        {
            Content = JsonContent.Create(new { spoolId = Gate4SpoolId }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etagBefore);

        HttpResponseMessage response = await client.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeInRange(200, 299, body);
        string etagAfter = response.Headers.ETag?.Tag
            ?? throw new InvalidOperationException($"Successful bind omitted ETag. Body: {body}");
        etagAfter.Should().NotBe(etagBefore, "a committed binding must advance the printer revision");

        // Guided path proof: the validator and the commit-time re-resolution both consulted
        // the spool source for gate 4's spool.
        _spoolResolver.Verify(
            r => r.ResolveSpoolAsync(It.Is<Printer>(p => p.Id == printerId), Gate4SpoolId, It.IsAny<CancellationToken>()),
            Times.AtLeast(2));

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<Toolhead> toolheads = await db.Toolheads.AsNoTracking()
                .Where(t => t.PrinterId == printerId)
                .OrderBy(t => t.Index)
                .ToListAsync();

            toolheads.Select(t => t.Index).Should().Equal(0, 1, 2, 3, 4);
            Toolhead rack = toolheads.Single(t => t.Index == 0);
            rack.ToolheadType.Should().Be(ToolheadType.Physical);
            rack.CurrentSpoolId.Should().Be(RackSpoolId, "the physical/rack binding must not move to gate 4");
            rack.CurrentMaterial.Should().Be("ABS-RACK");
            rack.CurrentFilamentColor.Should().Be("#FFFFFF");

            Toolhead gate4 = toolheads.Single(t => t.Index == 4);
            gate4.ToolheadType.Should().Be(ToolheadType.MmuGate);
            gate4.CurrentSpoolId.Should().Be(Gate4SpoolId);
            gate4.CurrentMaterial.Should().Be("PLA");

            foreach ((int index, (Guid id, int? spoolId, string? material, string? color)) in before)
            {
                Toolhead preserved = toolheads.Single(t => t.Index == index);
                preserved.Id.Should().Be(id, $"gate {index} must not be recreated or renumbered");
                preserved.ToolheadType.Should().Be(ToolheadType.MmuGate);
                preserved.CurrentSpoolId.Should().Be(spoolId);
                preserved.CurrentMaterial.Should().Be(material);
                preserved.CurrentFilamentColor.Should().Be(color);
            }

            Printer printer = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId);
            printer.ConfigurationRevision.Should().BeGreaterThan(revisionBefore);
            printer.CurrentSpoolId.Should().BeNull("the legacy T0 scalar must not be touched");
        }

        HttpResponseMessage reread = await client.GetAsync($"/api/printers/{printerId}");
        reread.EnsureSuccessStatusCode();
        reread.Headers.ETag?.Tag.Should().Be(etagAfter, "the returned ETag must match the persisted revision");
    }

    private async Task<HttpClient> CreateSubmitClientAsync(Guid roleId)
    {
        Guid userId = Guid.NewGuid();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"qidi-submit-{userId:N}",
                Email = $"qidi-submit-{userId:N}@example.com",
                PasswordHash = "unused",
                FirstName = "Qidi",
                LastName = "Submit",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = roleId,
                IsActive = true,
                AssignedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "qidi-submit");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", PrintFarmerPermissions.Queue.Write);
        return client;
    }

    private async Task<(Guid PrinterId, Guid SubmitRoleId)> SeedQidiPartialGatePrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Qidi {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Plus 4" };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Qidi group {Guid.NewGuid():N}" };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"qidi-submit-{Guid.NewGuid():N}",
            DisplayName = "Qidi submit role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "qp4-1",
            ServerUrl = $"http://qp4-1-{Guid.NewGuid():N}.invalid",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            HasMmu = true,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = group.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Extruder",
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
            CurrentSpoolId = RackSpoolId,
            CurrentMaterial = "ABS-RACK",
            CurrentFilamentColor = "#FFFFFF",
            UpdatedAt = now,
        });
        for (int index = 1; index <= 3; index++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Index = index,
                Name = $"Gate {index}",
                ToolheadType = ToolheadType.MmuGate,
                CurrentSpoolId = 100 + index,
                CurrentMaterial = $"PETG-{index}",
                CurrentFilamentColor = $"#00000{index}",
                UpdatedAt = now,
            });
        }

        db.AddRange(
            manufacturer,
            model,
            group,
            role,
            printer,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = role.Id,
                AccessLevel = PrinterGroupAccessLevel.Submit,
            },
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return (printer.Id, role.Id);
    }

    private sealed class PartialGateFactory(Mock<IFilamentCoverageSpoolResolver> spoolResolver)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFilamentCoverageSpoolResolver>();
                services.AddSingleton(spoolResolver.Object);
            });
        }
    }
}
