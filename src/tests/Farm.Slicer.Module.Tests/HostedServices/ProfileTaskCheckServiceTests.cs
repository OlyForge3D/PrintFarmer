using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Tasks;
using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Api.Repositories;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Slicer.Module.Tests.HostedServices;

/// <summary>
/// Unit tests for <see cref="ProfileTaskCheckService"/>.
/// Tests the hosted service logic that detects printers missing slicer profiles
/// and creates user tasks to prompt profile import.
/// </summary>
public sealed class ProfileTaskCheckServiceTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IServiceScope> _scope = new();
    private readonly Mock<IServiceProvider> _scopeProvider = new();
    private readonly Mock<ILogger<ProfileTaskCheckService>> _logger = new();
    private readonly Mock<ISlicersService> _slicersService = new();
    private readonly Mock<IPrinterProfileCheckRepository> _printersService = new();
    private readonly Mock<IMachineModelProfileRepository> _machineModelProfileRepo = new();
    private readonly Mock<IMachineProfileRepository> _machineProfileRepo = new();
    private readonly Mock<IUserTaskService> _taskService = new();
    private readonly Mock<ICatalogService> _catalogService = new();

    public ProfileTaskCheckServiceTests()
    {
        _machineModelProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _machineProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _catalogService.Setup(c => c.GetProfileTaskModelsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // Wire up scope factory → scope → provider chain
        _scopeFactory.Setup(f => f.CreateScope()).Returns(_scope.Object);
        _scope.Setup(s => s.ServiceProvider).Returns(_scopeProvider.Object);

        _scopeProvider.Setup(p => p.GetService(typeof(ISlicersService)))
            .Returns(_slicersService.Object);
        _scopeProvider.Setup(p => p.GetService(typeof(IPrinterProfileCheckRepository)))
            .Returns(_printersService.Object);
        _scopeProvider.Setup(p => p.GetService(typeof(IMachineModelProfileRepository)))
            .Returns(_machineModelProfileRepo.Object);
        _scopeProvider.Setup(p => p.GetService(typeof(IMachineProfileRepository)))
            .Returns(_machineProfileRepo.Object);
        _scopeProvider.Setup(p => p.GetService(typeof(IUserTaskService)))
            .Returns(_taskService.Object);
        _scopeProvider.Setup(p => p.GetService(typeof(ICatalogService)))
            .Returns(_catalogService.Object);
    }

    private static IConfiguration BuildConfig(bool enabled = true, bool periodicCheck = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProfileTaskCheck:Enabled"] = enabled.ToString(),
                ["ProfileTaskCheck:EnablePeriodicCheck"] = periodicCheck.ToString(),
            })
            .Build();
    }

    private ProfileTaskCheckService CreateService(IConfiguration? config = null)
    {
        return new ProfileTaskCheckService(
            _scopeFactory.Object,
            _logger.Object,
            config ?? BuildConfig());
    }

    // --- Constructor tests ---

    [Fact]
    public void Constructor_WithNullScopeFactory_ThrowsArgumentNullException()
    {
        Action act = () => new ProfileTaskCheckService(null!, _logger.Object, BuildConfig());

        act.Should().Throw<ArgumentNullException>().WithParameterName("scopeFactory");
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Action act = () => new ProfileTaskCheckService(_scopeFactory.Object, null!, BuildConfig());

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // --- Disabled mode ---

    [Fact]
    public async Task CheckPrinters_WhenDisabled_LogsAndReturnsWithoutCheckingScope()
    {
        ProfileTaskCheckService service = CreateService(BuildConfig(enabled: false));

        // ExecuteAsync exits early when disabled — no scope is created
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        // StartAsync triggers ExecuteAsync in background; with enabled=false it exits immediately
        await service.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None); // let background task settle
        await service.StopAsync(CancellationToken.None);

        _scopeFactory.Verify(f => f.CreateScope(), Times.Never);
    }

    // --- No slicer workers ---

    [Fact]
    public async Task CheckPrinters_WhenNoSlicerModule_SkipsTaskCreation()
    {
        // ISlicersService not registered → GetService returns null
        _scopeProvider.Setup(p => p.GetService(typeof(ISlicersService)))
            .Returns((ISlicersService?)null);

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _printersService.Verify(p => p.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckPrinters_WhenNoWorkers_SkipsTaskCreation()
    {
        _slicersService.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SlicerService>());

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _printersService.Verify(p => p.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- No printers ---

    [Fact]
    public async Task CheckPrinters_WhenNoPrinters_ReturnsEarly()
    {
        SetupWorkers(1);
        _printersService.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Printer>());

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _machineModelProfileRepo.Verify(
            r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Existing profiles → skip ---

    [Fact]
    public async Task CheckPrinters_WhenModelHasProfileAlready_SkipsTaskCreation()
    {
        Guid modelId = Guid.NewGuid();
        SetupWorkers(1);
        SetupPrinters(new Printer { Id = Guid.NewGuid(), ModelId = modelId });

        _machineModelProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([modelId]);

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _taskService.Verify(
            t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskBatchDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckPrinters_WhenHasMachineProfiles_SkipsTaskCreation()
    {
        Guid modelId = Guid.NewGuid();
        SetupWorkers(1);
        SetupPrinters(new Printer { Id = Guid.NewGuid(), ModelId = modelId });

        _machineProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([modelId]);

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _taskService.Verify(
            t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskBatchDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Unknown model → skip ---

    [Fact]
    public async Task CheckPrinters_WhenModelNameIsUnknown_SkipsTaskCreation()
    {
        Guid modelId = Guid.NewGuid();
        SetupWorkers(1);
        SetupPrinters(new Printer { Id = Guid.NewGuid(), ModelId = modelId });
        _catalogService.Setup(c => c.GetProfileTaskModelsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProfileTaskModelDto(modelId, "Unknown", "Unknown")]);

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _taskService.Verify(
            t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskBatchDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Creates tasks ---

    [Fact]
    public async Task CheckPrinters_WhenMissingProfiles_CreatesTask()
    {
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        SetupWorkers(1);
        SetupPrinters(new Printer { Id = printerId, ModelId = modelId });
        _catalogService.Setup(c => c.GetProfileTaskModelsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProfileTaskModelDto(modelId, "CORE One", "Prusa")]);

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _taskService.Verify(
            t => t.CreateOrUpdateProfileImportTaskAsync(
                It.Is<CreateProfileImportTaskBatchDto>(dto =>
                    dto.PrinterModelId == modelId &&
                    dto.PrinterIds.SequenceEqual(new[] { printerId }) &&
                    dto.PrinterModelName == "CORE One" &&
                    dto.ManufacturerName == "Prusa"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Error handling ---

    [Fact]
    public async Task CheckPrinters_WhenExceptionThrown_BubblesUp()
    {
        SetupWorkers(1);
        _printersService.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB down"));

        ProfileTaskCheckService service = CreateService();

        Func<Task> act = () => service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DB down");
    }

    // --- Printers with empty ModelId are excluded ---

    [Fact]
    public async Task CheckPrinters_WhenPrinterHasEmptyModelId_SkipsIt()
    {
        SetupWorkers(1);
        SetupPrinters(new Printer { Id = Guid.NewGuid(), ModelId = Guid.Empty });

        ProfileTaskCheckService service = CreateService();

        await service.CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _machineModelProfileRepo.Verify(
            r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Helper methods ---

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(1000, 100)]
    public async Task CheckPrintersForMissingProfilesAsync_ManyPrinters_UpsertsCompleteGroupsOnce(int printerCount, int modelCount)
    {
        Guid[] models = Enumerable.Range(0, modelCount).Select(_ => Guid.NewGuid()).ToArray();
        Printer[] printers = Enumerable.Range(0, printerCount)
            .Select(i => new Printer { Id = Guid.NewGuid(), ModelId = models[i % modelCount] }).ToArray();
        SetupWorkers(1);
        SetupPrinters(printers);
        _catalogService.Setup(c => c.GetProfileTaskModelsAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(models)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(models.Select(id => new ProfileTaskModelDto(id, "MK4", "Prusa")).ToArray());
        List<CreateProfileImportTaskBatchDto> requests = [];
        _taskService.Setup(t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskBatchDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProfileImportTaskBatchDto, CancellationToken>((dto, _) => requests.Add(dto));

        await CreateService().CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        Assert.Equal(modelCount, requests.Count);
        foreach (CreateProfileImportTaskBatchDto request in requests)
        {
            Assert.True(request.PrinterIds.ToHashSet().SetEquals(printers.Where(p => p.ModelId == request.PrinterModelId).Select(p => p.Id)));
        }

        _machineModelProfileRepo.Verify(r => r.GetPrinterModelIdsWithProfilesAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(models)), It.IsAny<CancellationToken>()), Times.Once);
        _machineProfileRepo.Verify(r => r.GetPrinterModelIdsWithProfilesAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(models)), It.IsAny<CancellationToken>()), Times.Once);
        _catalogService.Verify(c => c.GetProfileTaskModelsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _catalogService.Verify(c => c.GetModelByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _taskService.Verify(t => t.HasPendingProfileImportTaskAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _taskService.Verify(t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckPrintersForMissingProfilesAsync_MixedCoverage_ResolvesOnlyMissingAndSkipsAbsentCatalogModels()
    {
        Guid[] models = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        SetupWorkers(1);
        SetupPrinters(models.Select(id => new Printer { Id = Guid.NewGuid(), ModelId = id }).ToArray());
        _machineModelProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([models[0]]);
        _machineProfileRepo.Setup(r => r.GetPrinterModelIdsWithProfilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([models[1]]);
        _catalogService.Setup(c => c.GetProfileTaskModelsAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(models.Skip(2))), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new(models[2], "MK4", "Unknown"), new(models[3], "uNkNoWn Model", "Unknown")]);

        await CreateService().CheckPrintersForMissingProfilesAsync(CancellationToken.None);

        _taskService.Verify(t => t.CreateOrUpdateProfileImportTaskAsync(
            It.Is<CreateProfileImportTaskBatchDto>(dto => dto.PrinterModelId == models[2] && dto.ManufacturerName == "Unknown"),
            It.IsAny<CancellationToken>()), Times.Once);
        _taskService.Verify(t => t.CreateOrUpdateProfileImportTaskAsync(It.IsAny<CreateProfileImportTaskBatchDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetupWorkers(int count)
    {
        var workers = Enumerable.Range(0, count)
            .Select(_ => new SlicerService { Id = Guid.NewGuid() })
            .ToList();
        _slicersService.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workers);
    }

    private void SetupPrinters(params Printer[] printers)
    {
        _printersService.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers.ToList());
    }

}
