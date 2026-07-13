using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Integration tests for <see cref="IFilamentFallbackGroupService"/> — issue #711 (F6).
/// Covers ownership, min-2 members, unique members, physical-only, unique names, and
/// the fallback resolver used for auto-switch severity downgrade evidence.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FilamentFallbackGroupServiceTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private IFilamentFallbackGroupService _service = null!;
    private AppDbContext _db = null!;

    public FilamentFallbackGroupServiceTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _service = _scope.ServiceProvider.GetRequiredService<IFilamentFallbackGroupService>();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private async Task<(Printer Printer, Toolhead T0, Toolhead T1, Toolhead Mmu)> SeedPrinterWithToolheadsAsync(string name = "Fallback Printer")
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Mfg-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model-{suffix}" };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{name}-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = $"http://10.0.0.{(Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 240) + 2}",
            IsEnabled = true,
        };
        Toolhead t0 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, Name = "T0", ToolheadType = ToolheadType.Physical };
        Toolhead t1 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 1, Name = "T1", ToolheadType = ToolheadType.Physical };
        Toolhead mmu = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 2, Name = "MMU-1", ToolheadType = ToolheadType.MmuGate };

        _db.Manufacturers.Add(mfg);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        _db.Toolheads.AddRange(t0, t1, mmu);
        await _db.SaveChangesAsync();

        return (printer, t0, t1, mmu);
    }

    [Fact]
    public async Task CreateAsync_WithValidMembers_PersistsAndReturnsGroup()
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();

        FilamentFallbackGroupDto dto = await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        dto.Name.Should().Be("PLA Chain");
        dto.MaterialType.Should().Be("PLA");
        dto.Members.Should().HaveCount(2);
        dto.Members[0].ToolheadId.Should().Be(t0.Id);
        dto.Members[0].Position.Should().Be(0);
        dto.Members[1].ToolheadId.Should().Be(t1.Id);
        dto.Members[1].Position.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithMmuGateMember_Throws()
    {
        (Printer p, Toolhead t0, _, Toolhead mmu) = await SeedPrinterWithToolheadsAsync();

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("Bad", "PLA", null, [t0.Id, mmu.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*not a physical toolhead*");
    }

    [Fact]
    public async Task CreateAsync_WithSingleMember_Throws()
    {
        (Printer p, Toolhead t0, _, _) = await SeedPrinterWithToolheadsAsync();

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("Solo", "PLA", null, [t0.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*at least two*");
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateMembers_Throws()
    {
        (Printer p, Toolhead t0, _, _) = await SeedPrinterWithToolheadsAsync();

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("Dup", "PLA", null, [t0.Id, t0.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*at most once*");
    }

    [Fact]
    public async Task CreateAsync_ForeignToolhead_Throws()
    {
        (Printer p, _, _, _) = await SeedPrinterWithToolheadsAsync("A");
        (_, Toolhead foreignA, Toolhead foreignB, _) = await SeedPrinterWithToolheadsAsync("B");

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("Cross", "PLA", null, [foreignA.Id, foreignB.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*does not belong to printer*");
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_CaseInsensitive_Throws()
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();
        await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("pla chain", "PLA", null, [t1.Id, t0.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task FindAvailableFallbackAsync_ReturnsMemberWithMatchingLoadedMaterial()
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();

        await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        AvailableFallbackMember? result = await _service.FindAvailableFallbackAsync(
            p.Id,
            sourceToolheadId: t0.Id,
            materialType: "PLA",
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolheadId.Should().Be(t1.Id);
        result.LoadedMaterial.Should().Be("PLA");
        result.LoadedSpoolId.Should().Be(42);
    }

    [Fact]
    public async Task FindAvailableFallbackAsync_ReturnsNull_WhenNoMemberLoaded()
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();
        // Neither t0 nor t1 has anything loaded.
        await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        AvailableFallbackMember? result = await _service.FindAvailableFallbackAsync(
            p.Id,
            sourceToolheadId: t0.Id,
            materialType: "PLA",
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindAvailableFallbackAsync_ExcludesSourceToolhead()
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();
        // The source is loaded — but that shouldn't count as its own fallback.
        t0.CurrentMaterial = "PLA";
        await _db.SaveChangesAsync();

        await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        AvailableFallbackMember? result = await _service.FindAvailableFallbackAsync(
            p.Id,
            sourceToolheadId: t0.Id,
            materialType: "PLA",
            CancellationToken.None);

        result.Should().BeNull();
    }
}
