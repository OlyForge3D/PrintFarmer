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
/// Covers ownership, min-2 members, unique members, MMU/AMS-gate acceptance, unique names,
/// and the fallback resolver used for auto-switch severity downgrade evidence.
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

    private async Task<(Printer Printer, Toolhead T0, Toolhead T1, Toolhead Mmu)> SeedPrinterWithToolheadsAsync(
        string name = "Fallback Printer",
        bool mmuTopology = false)
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
        Toolhead t1 = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 1,
            Name = mmuTopology ? "MMU-1" : "T1",
            ToolheadType = mmuTopology ? ToolheadType.MmuGate : ToolheadType.Physical
        };
        Toolhead mmu = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 2,
            Name = mmuTopology ? "MMU-2" : "T2",
            ToolheadType = mmuTopology ? ToolheadType.MmuGate : ToolheadType.Physical
        };

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
    public async Task CreateAsync_WithMmuGateMember_Succeeds()
    {
        // Issue #711 (FIX D): AMS/MMU multi-slot fallback chains are the primary use case,
        // so MMU/AMS gates ARE eligible fallback-group members (unlike maintenance scope,
        // which remains physical-only). The shared physical hotend is intentionally excluded.
        (Printer p, _, Toolhead gateOne, Toolhead gateTwo) =
            await SeedPrinterWithToolheadsAsync(mmuTopology: true);

        FilamentFallbackGroupDto dto = await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest(
                "AMS Chain",
                "PLA",
                null,
                [gateOne.Id, gateTwo.Id]),
            CancellationToken.None);

        dto.Members.Should().HaveCount(2);
        dto.Members.Select(m => m.ToolheadId).Should().Contain(gateTwo.Id);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_WithOversizedField_ThrowsValidationException(bool oversizedName)
    {
        (Printer p, Toolhead t0, Toolhead t1, _) = await SeedPrinterWithToolheadsAsync();
        string name = oversizedName ? new string('N', 129) : "Valid";
        string material = oversizedName ? "PLA" : new string('M', 65);

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest(name, material, null, [t0.Id, t1.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage(oversizedName ? "*128 characters or fewer*" : "*64 characters or fewer*");
        (await _db.FilamentFallbackGroups.CountAsync()).Should().Be(0);
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

    [Fact]
    public async Task FindAvailableFallbackAsync_ResolvesMmuGateSlot()
    {
        // Issue #711 (FIX D): an MMU/AMS gate loaded with the requested material is a valid
        // fallback slot and must be resolvable so the runout-attention flow can point at it.
        (Printer p, _, Toolhead gateOne, Toolhead gateTwo) =
            await SeedPrinterWithToolheadsAsync(mmuTopology: true);
        gateTwo.CurrentMaterial = "PLA";
        gateTwo.CurrentSpoolId = 7;
        await _db.SaveChangesAsync();

        await _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest(
                "AMS Chain",
                "PLA",
                null,
                [gateOne.Id, gateTwo.Id]),
            CancellationToken.None);

        AvailableFallbackMember? result = await _service.FindAvailableFallbackAsync(
            p.Id,
            sourceToolheadId: gateOne.Id,
            materialType: "PLA",
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolheadId.Should().Be(gateTwo.Id);
        result.LoadedSpoolId.Should().Be(7);
    }

    [Fact]
    public async Task CreateAsync_MmuPrinterSharedPhysicalHotend_Throws()
    {
        (Printer p, Toolhead physical, Toolhead gateOne, _) =
            await SeedPrinterWithToolheadsAsync(mmuTopology: true);

        Func<Task> act = () => _service.CreateAsync(
            p.Id,
            new CreateFilamentFallbackGroupRequest(
                "Invalid shared hotend",
                "PLA",
                null,
                [physical.Id, gateOne.Id]),
            CancellationToken.None);

        await act.Should().ThrowAsync<FilamentFallbackGroupValidationException>()
            .WithMessage("*not a filament source*");
    }

    [Fact]
    public async Task GetAvailableFallbacksAsync_LegacyMmuChain_ExcludesSharedPhysicalHotend()
    {
        (Printer p, Toolhead physical, Toolhead gateOne, Toolhead gateTwo) =
            await SeedPrinterWithToolheadsAsync(mmuTopology: true);
        physical.CurrentMaterial = "PLA";
        physical.CurrentSpoolId = 99;
        gateTwo.CurrentMaterial = "PLA";
        gateTwo.CurrentSpoolId = 42;

        FilamentFallbackGroup group = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = p.Id,
            Name = "Legacy mixed chain",
            NameNormalized = "legacy mixed chain",
            MaterialType = "PLA",
            DisplayOrder = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        group.Members.Add(new FilamentFallbackGroupMember
        {
            Id = Guid.NewGuid(),
            FallbackGroupId = group.Id,
            ToolheadId = gateOne.Id,
            Position = 0,
        });
        group.Members.Add(new FilamentFallbackGroupMember
        {
            Id = Guid.NewGuid(),
            FallbackGroupId = group.Id,
            ToolheadId = physical.Id,
            Position = 1,
        });
        group.Members.Add(new FilamentFallbackGroupMember
        {
            Id = Guid.NewGuid(),
            FallbackGroupId = group.Id,
            ToolheadId = gateTwo.Id,
            Position = 2,
        });
        _db.FilamentFallbackGroups.Add(group);
        await _db.SaveChangesAsync();

        IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution> results =
            await _service.GetAvailableFallbacksAsync([p.Id], CancellationToken.None);

        results[FilamentFallbackLookupKey.Create(p.Id, gateOne.Id, "PLA")]
            .Members.Should().ContainSingle(member => member.ToolheadId == gateTwo.Id);
        results.Should().NotContainKey(
            FilamentFallbackLookupKey.Create(p.Id, physical.Id, "PLA"));
    }

    [Fact]
    public async Task GetAvailableFallbacksAsync_BatchesPrintersAndPreservesForwardChainOrder()
    {
        (Printer first, Toolhead firstT0, Toolhead firstT1, Toolhead firstMmu) =
            await SeedPrinterWithToolheadsAsync("First");
        (Printer second, Toolhead secondT0, Toolhead secondT1, _) =
            await SeedPrinterWithToolheadsAsync("Second");
        firstT1.CurrentMaterial = "PLA";
        firstMmu.CurrentMaterial = "PLA";
        secondT1.CurrentMaterial = "PLA";
        await _db.SaveChangesAsync();

        await _service.CreateAsync(
            first.Id,
            new CreateFilamentFallbackGroupRequest(
                "First chain",
                "PLA",
                null,
                [firstT0.Id, firstT1.Id, firstMmu.Id]),
            CancellationToken.None);
        await _service.CreateAsync(
            second.Id,
            new CreateFilamentFallbackGroupRequest(
                "Second chain",
                "PLA",
                null,
                [secondT0.Id, secondT1.Id]),
            CancellationToken.None);

        IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution> results =
            await _service.GetAvailableFallbacksAsync(
                [first.Id, second.Id],
                CancellationToken.None);

        results[FilamentFallbackLookupKey.Create(first.Id, firstT0.Id, "pla")]
            .Members.Select(member => member.ToolheadId)
            .Should().ContainInOrder(firstT1.Id, firstMmu.Id);
        results[FilamentFallbackLookupKey.Create(first.Id, firstT1.Id, "PLA")]
            .Members.Should().ContainSingle(member => member.ToolheadId == firstMmu.Id);
        results[FilamentFallbackLookupKey.Create(second.Id, secondT0.Id, "PLA")]
            .Members.Should().ContainSingle(member => member.ToolheadId == secondT1.Id);
    }
}
