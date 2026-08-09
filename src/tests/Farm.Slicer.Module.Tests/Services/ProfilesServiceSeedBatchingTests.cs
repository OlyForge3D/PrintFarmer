using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Covers issue #1354: batching per-profile <c>SaveChangesAsync</c> commits in
/// <see cref="ProfilesService.SeedSystemProfilesFromWorkerAsync"/> must preserve the pre-existing
/// per-profile error isolation and idempotency behavior of the old one-commit-per-profile loop.
/// </summary>
public class ProfilesServiceSeedBatchingTests
{
    private const string ManufacturerName = "TestMfg";
    private const string ModelName = "TestModel";
    private const string WorkerHost = "http://worker";

    [Fact]
    public async Task SeedSystemProfilesFromWorkerAsync_SeedingTwice_DoesNotDuplicateProfiles()
    {
        // Arrange: two distinct machine profiles from the worker.
        List<MachineProfileDto> machineProfiles = new()
        {
            new MachineProfileDto { Name = "Machine A", Manufacturer = ManufacturerName },
            new MachineProfileDto { Name = "Machine B", Manufacturer = ManufacturerName }
        };

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Loose);

        // First seed run: nothing exists yet.
        HashSet<string> alreadySeededHashes = new(StringComparer.Ordinal);
        _ = machineRepo
            .Setup(r => r.GetExistingSystemHashesAsync(It.IsAny<IEnumerable<string>>(), SlicerType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HashSet<string>(alreadySeededHashes, StringComparer.Ordinal));

        List<MachineProfile> persisted = new();
        _ = machineRepo
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<MachineProfile>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MachineProfile>, CancellationToken>((profiles, _) =>
            {
                foreach (MachineProfile p in profiles)
                {
                    persisted.Add(p);
                    alreadySeededHashes.Add(p.Hash!);
                }
            })
            .ReturnsAsync((IEnumerable<MachineProfile> profiles, CancellationToken _) => new List<MachineProfile>(profiles).Count);

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            filamentRepoOverride: filamentRepo.Object,
            processRepoOverride: processRepo.Object);

        using HttpClient httpClient = CreateWorkerHttpClient(BuildProfilesResponseJson(machineProfiles, [], []));

        // Act: seed twice.
        dynamic firstRun = await svc.SeedSystemProfilesFromWorkerAsync(httpClient, CancellationToken.None);
        dynamic secondRun = await svc.SeedSystemProfilesFromWorkerAsync(httpClient, CancellationToken.None);

        // Assert: first run imports both, second run imports none (no duplicates persisted).
        Assert.Equal(2, (int)firstRun.imported);
        Assert.Equal(2, persisted.Count);

        Assert.Equal(0, (int)secondRun.imported);
        Assert.Equal(2, (int)secondRun.skipped);
        Assert.Equal(2, persisted.Count); // still only the original two — no duplicates added.

        machineRepo.Verify(
            r => r.AddRangeAsync(It.IsAny<IEnumerable<MachineProfile>>(), It.IsAny<CancellationToken>()),
            Times.Once); // second run's batch is empty after dedup, so AddRangeAsync is never called again.
    }

    [Fact]
    public async Task SeedSystemProfilesFromWorkerAsync_OneMalformedProfileInBatch_DoesNotBlockOtherValidProfiles()
    {
        // Arrange: three filament profiles in the same manufacturer/model batch — the middle one
        // fails during staging (simulating a parse/hash failure for a single malformed profile).
        List<FilamentProfileDto> filamentProfiles = new()
        {
            new FilamentProfileDto { Name = "Good1", Material = "PLA", Manufacturer = ManufacturerName },
            new FilamentProfileDto { Name = "BadProfile", Material = "PLA", Manufacturer = ManufacturerName },
            new FilamentProfileDto { Name = "Good2", Material = "PETG", Manufacturer = ManufacturerName }
        };

        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.Is<string>(json => json.Contains("BadProfile", StringComparison.Ordinal))))
            .Throws<InvalidOperationException>();
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.Is<string>(json => !json.Contains("BadProfile", StringComparison.Ordinal))))
            .Returns((string json) => (json, "{}", ComputeTestHash(json)));

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Loose);

        _ = filamentRepo
            .Setup(r => r.GetExistingSystemHashesAsync(It.IsAny<IEnumerable<string>>(), SlicerType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        List<FilamentProfile> persisted = new();
        _ = filamentRepo
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<FilamentProfile>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<FilamentProfile>, CancellationToken>((profiles, _) => persisted.AddRange(profiles))
            .ReturnsAsync((IEnumerable<FilamentProfile> profiles, CancellationToken _) => new List<FilamentProfile>(profiles).Count);

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            filamentRepoOverride: filamentRepo.Object,
            processRepoOverride: processRepo.Object,
            parsingServiceOverride: parsingService.Object);

        using HttpClient httpClient = CreateWorkerHttpClient(BuildProfilesResponseJson([], filamentProfiles, []));

        // Act
        dynamic result = await svc.SeedSystemProfilesFromWorkerAsync(httpClient, CancellationToken.None);

        // Assert: the two good profiles are still imported despite the malformed one in the same batch.
        Assert.Equal(2, (int)result.imported);
        Assert.Equal(1, (int)result.skipped);
        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, p => p.Name == "Good1");
        Assert.Contains(persisted, p => p.Name == "Good2");
        Assert.DoesNotContain(persisted, p => p.Name == "BadProfile");

        // The batch-level AddRangeAsync must only ever see the two successfully staged profiles.
        filamentRepo.Verify(
            r => r.AddRangeAsync(It.Is<IEnumerable<FilamentProfile>>(list => new List<FilamentProfile>(list).Count == 2), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SeedSystemProfilesFromWorkerAsync_BatchCommitThrowsDbUpdateException_FallsBackToPerRowInsert()
    {
        // Arrange: two valid process profiles. The batched AddRangeAsync throws (simulating a
        // same-hash collision the pre-check missed); the per-row fallback must still persist both.
        List<ProcessProfileDto> processProfiles = new()
        {
            new ProcessProfileDto { Name = "Proc1", Quality = "standard", LayerHeight = 0.2 },
            new ProcessProfileDto { Name = "Proc2", Quality = "draft", LayerHeight = 0.28 }
        };

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Loose);

        _ = processRepo
            .Setup(r => r.GetExistingSystemHashesAsync(It.IsAny<IEnumerable<string>>(), SlicerType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _ = processRepo
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<ProcessProfile>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("simulated batch commit failure"));

        List<ProcessProfile> persisted = new();
        _ = processRepo
            .Setup(r => r.AddAsync(It.IsAny<ProcessProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessProfile, CancellationToken>((p, _) => persisted.Add(p))
            .Returns(Task.CompletedTask);

        ProfilesService svc = CreateService(
            machineRepoOverride: machineRepo.Object,
            filamentRepoOverride: filamentRepo.Object,
            processRepoOverride: processRepo.Object);

        using HttpClient httpClient = CreateWorkerHttpClient(BuildProfilesResponseJson([], [], processProfiles));

        // Act
        dynamic result = await svc.SeedSystemProfilesFromWorkerAsync(httpClient, CancellationToken.None);

        // Assert: both profiles still land via the per-row fallback despite the batch failure.
        Assert.Equal(2, (int)result.imported);
        Assert.Equal(0, (int)result.skipped);
        Assert.Equal(2, persisted.Count);
        processRepo.Verify(r => r.AddAsync(It.IsAny<ProcessProfile>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static string ComputeTestHash(string content)
    {
        return "hash-" + content.GetHashCode(StringComparison.Ordinal).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpClient CreateWorkerHttpClient(string profilesResponseJson)
    {
        return new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profilesResponseJson)
            };
        }))
        {
            BaseAddress = null
        };
    }

    private static string BuildProfilesResponseJson(
        IReadOnlyList<MachineProfileDto> machineProfiles,
        IReadOnlyList<FilamentProfileDto> filamentProfiles,
        IReadOnlyList<ProcessProfileDto> processProfiles)
    {
        AllProfilesResponseDto response = new()
        {
            ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>
            {
                [ManufacturerName] = new ManufacturerProfilesDto
                {
                    Name = ManufacturerName,
                    Models = new Dictionary<string, PrinterModelProfilesDto>
                    {
                        ["model1"] = new PrinterModelProfilesDto
                        {
                            Name = ModelName,
                            ModelId = "model1",
                            MachineProfiles = [.. machineProfiles],
                            FilamentProfiles = [.. filamentProfiles],
                            ProcessProfiles = [.. processProfiles]
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(response);
    }

    private static ProfilesService CreateService(
        IMachineProfileRepository machineRepoOverride,
        IFilamentProfileRepository filamentRepoOverride,
        IProcessProfileRepository processRepoOverride,
        IProfileParsingService? parsingServiceOverride = null)
    {
        Mock<IProfilesRepository> profilesRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> defaultParsingService = new(MockBehavior.Loose);
        _ = defaultParsingService
            .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
            .Returns((string json) => (json, "{}", ComputeTestHash(json)));

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = WorkerHost,
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    Version = CalibrationContractConstants.SlicerVersion,
                    CapabilitiesJson = $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            });

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ManufacturerDto> { new(Guid.NewGuid(), ManufacturerName) }, (string?)null));
        _ = catalogService
            .Setup(c => c.GetModelsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrinterModelDto> { new(Guid.NewGuid(), ModelName, Guid.NewGuid()) }, (string?)null));

        return new ProfilesService(
            profilesRepo.Object,
            NullLogger<ProfilesService>.Instance,
            processRepoOverride,
            machineRepoOverride,
            filamentRepoOverride,
            unitOfWork.Object,
            catalogService.Object,
            parsingServiceOverride ?? defaultParsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send(request));
        }
    }
}
