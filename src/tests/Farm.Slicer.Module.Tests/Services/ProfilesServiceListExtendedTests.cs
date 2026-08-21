using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos;
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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Regression tests for #1779: <see cref="ProfilesService.ListExtendedAsync"/> must surface
/// <c>NozzleDiameter</c> and <c>PrinterVariant</c> for each machine profile, recovered from the
/// profile's already-stored settings JSON (no schema migration required).
/// </summary>
public class ProfilesServiceListExtendedTests
{
    /// <summary>
    /// This is the actual contract Calibration Setup depends on (#1779): a single
    /// <see cref="ProfilesService.ListExtendedAsync"/> call must return BOTH the standard and HF
    /// variants of the same printer model side by side, each carrying its own distinct
    /// <c>printerVariant</c>/<c>nozzleDiameter</c>. Neither the seeding-parity test nor the
    /// isolated-extraction tests above prove this co-existence — they only prove HF groups can be
    /// imported at all, and that extraction works on a single profile in isolation.
    /// </summary>
    [Fact]
    public async Task ListExtendedAsync_StandardAndHfVariantsOfSameModel_BothReturnedWithDistinctFields()
    {
        // Arrange: two machine profiles for the same underlying printer model (same PrinterModelId),
        // one standard and one HF, each with its own nozzle diameter / variant.
        Guid sharedPrinterModelId = Guid.NewGuid();
        MachineProfile standard = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = sharedPrinterModelId,
            SettingsJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": null}",
            Hash = "hash-standard"
        };
        MachineProfile hf = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One HF",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = sharedPrinterModelId,
            SettingsJson = "{\"NozzleDiameter\": 0.6, \"PrinterVariant\": \"HF\"}",
            Hash = "hash-hf"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { standard, hf });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert: both variants are present simultaneously, each with its own distinct fields.
        Assert.Equal(2, result.MachineProfiles.Count);

        MachineProfileListItemDto standardDto = Assert.Single(result.MachineProfiles, p => p.Name == "Prusa CORE One");
        Assert.Equal(0.4, standardDto.NozzleDiameter);
        Assert.Null(standardDto.PrinterVariant);

        MachineProfileListItemDto hfDto = Assert.Single(result.MachineProfiles, p => p.Name == "Prusa CORE One HF");
        Assert.Equal(0.6, hfDto.NozzleDiameter);
        Assert.Equal("HF", hfDto.PrinterVariant);

        // Sanity: the two variants are distinguishable from each other, not accidentally identical.
        Assert.NotEqual(standardDto.NozzleDiameter, hfDto.NozzleDiameter);
        Assert.NotEqual(standardDto.PrinterVariant, hfDto.PrinterVariant);
    }

    /// <summary>
    /// Regression test for #1779: real OrcaSlicer HF (high-flow) and standard machine profiles for
    /// the same printer model commonly share the SAME nozzle diameter (e.g. both "Prusa CORE One
    /// 0.4 nozzle" and "Prusa CORE One HF 0.4 nozzle" are 0.4mm) — the discriminator is the profile
    /// <em>name</em>/hierarchy group, not <c>NozzleDiameter</c>. The other co-existence test above
    /// uses distinct nozzle diameters (0.4 vs 0.6) for the two variants; this test specifically
    /// covers the same-nozzle-diameter case called out in the issue's acceptance criteria, asserting
    /// both the returned count and the presence of an HF-named entry.
    /// </summary>
    [Fact]
    public async Task ListExtendedAsync_SameNozzleDiameterHfAndStandardVariants_BothReturnedDistinctly()
    {
        // Arrange: standard and HF variants of the same model, both at the 0.4mm nozzle.
        Guid sharedPrinterModelId = Guid.NewGuid();
        MachineProfile standard = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One 0.4 nozzle",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = sharedPrinterModelId,
            SettingsJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": \"0.4\"}",
            Hash = "hash-standard-0.4"
        };
        MachineProfile hf = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One HF 0.4 nozzle",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = sharedPrinterModelId,
            SettingsJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": \"0.4\"}",
            Hash = "hash-hf-0.4"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { standard, hf });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert: both same-nozzle-diameter variants are present, and the HF one is identifiable by name.
        Assert.Equal(2, result.MachineProfiles.Count);
        Assert.Contains(result.MachineProfiles, p => Regex.IsMatch(p.Name, @"\bHF\b"));
        Assert.All(result.MachineProfiles, p => Assert.Equal(0.4, p.NozzleDiameter));
    }

    /// <summary>
    /// End-to-end regression test for #1779: seeds machine profiles through
    /// <see cref="ProfilesService.SeedSystemProfilesFromWorkerAsync"/> — where the HF variant forms
    /// its own worker-side hierarchy group reachable only via a configured OrcaSlicer alias, and the
    /// HF/standard variants share the same nozzle diameter — then calls
    /// <see cref="ProfilesService.ListExtendedAsync"/> against the resulting persisted profiles. This
    /// proves the full seed→list round trip surfaces both variants: neither the isolated seeding test
    /// (<c>ProfilesServiceSeedBatchingTests</c>) nor the mocked-repository tests above, on their own,
    /// prove that profiles imported by seeding are actually what <c>/api/slicer/profiles/extended</c>
    /// serves back to a caller.
    /// </summary>
    [Fact]
    public async Task ListExtendedAsync_AfterSeedingHfAliasHierarchyGroup_ReturnsBothSameNozzleVariants()
    {
        const string ManufacturerName = "Prusa Research";
        const string BaseModelName = "Prusa CORE One";
        const string HfModelName = "Prusa CORE One HF";
        Guid modelId = Guid.NewGuid();

        List<MachineProfileDto> baseMachineProfiles = new()
        {
            new MachineProfileDto
            {
                Name = "Prusa CORE One 0.4 nozzle",
                Manufacturer = ManufacturerName,
                PrinterModel = BaseModelName,
                NozzleDiameter = 0.4,
                PrinterVariant = "0.4"
            }
        };
        List<MachineProfileDto> hfMachineProfiles = new()
        {
            new MachineProfileDto
            {
                Name = "Prusa CORE One HF 0.4 nozzle",
                Manufacturer = ManufacturerName,
                PrinterModel = HfModelName,
                NozzleDiameter = 0.4,
                PrinterVariant = "0.4"
            }
        };

        List<MachineProfile> persisted = new();
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo
            .Setup(r => r.GetExistingSystemHashesAsync(It.IsAny<IEnumerable<string>>(), SlicerType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.Ordinal));
        _ = machineRepo
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<MachineProfile>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MachineProfile>, CancellationToken>((profiles, _) => persisted.AddRange(profiles))
            .ReturnsAsync((IEnumerable<MachineProfile> profiles, CancellationToken _) => new List<MachineProfile>(profiles).Count);
        _ = machineRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
        _ = filamentRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FilamentProfile>());

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Loose);
        _ = processRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessProfile>());

        // The catalog configures "Prusa CORE One HF" as a configured OrcaSlicer alias of the
        // "Prusa CORE One" catalog model, mirroring printer-models.yaml's real seed data.
        List<SlicerModelAliasDto> aliases = new()
        {
            new SlicerModelAliasDto(Guid.NewGuid(), modelId, HfModelName, "OrcaSlicer")
        };

        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        _ = catalogService
            .Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ManufacturerDto> { new(Guid.NewGuid(), ManufacturerName) }, (string?)null));
        _ = catalogService
            .Setup(c => c.GetModelsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrinterModelDto> { new(modelId, BaseModelName, Guid.NewGuid()) }, (string?)null));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aliases);

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    Version = CalibrationContractConstants.SlicerVersion,
                    CapabilitiesJson = $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            });

        // Preserve the serialized profile JSON verbatim as SettingsJson so NozzleDiameter/PrinterVariant
        // survive the seed pipeline into the fields ListExtendedAsync's extraction reads. (Unlike
        // ProfilesServiceSeedBatchingTests' default parsing-service mock, which discards them by
        // always returning "{}" — fine there since that suite only asserts on profile Name/count.)
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        _ = parsingService
            .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
            .Returns((string json) => (json, json, "hash-" + json.GetHashCode(StringComparison.Ordinal).ToString("x", CultureInfo.InvariantCulture)));

        Mock<IProfilesRepository> profilesRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);

        ProfilesService svc = new(
            profilesRepo.Object,
            NullLogger<ProfilesService>.Instance,
            processRepo.Object,
            machineRepo.Object,
            filamentRepo.Object,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);

        using HttpClient httpClient = CreateWorkerHttpClient(
            BuildWorkerProfilesResponseJson(
                ManufacturerName,
                new Dictionary<string, IReadOnlyList<MachineProfileDto>>
                {
                    [BaseModelName] = baseMachineProfiles,
                    [HfModelName] = hfMachineProfiles
                }));

        // Act: seed from the (mocked) worker, then read back through the extended endpoint exactly
        // as /api/slicer/profiles/extended would.
        _ = await svc.SeedSystemProfilesFromWorkerAsync(httpClient, CancellationToken.None);
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert: both same-nozzle-diameter variants made it through seeding and are surfaced by
        // ListExtendedAsync, with the HF one identifiable by name.
        Assert.Equal(2, result.MachineProfiles.Count);
        Assert.Contains(result.MachineProfiles, p => Regex.IsMatch(p.Name, @"\bHF\b"));
        Assert.All(result.MachineProfiles, p => Assert.Equal(0.4, p.NozzleDiameter));
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

    /// <summary>
    /// Builds a worker <c>/api/profiles</c> response with multiple <c>printer_model</c> hierarchy
    /// groups under the same manufacturer, keyed by the supplied model group names (e.g. a base
    /// catalog name plus an HF-alias name) — mirroring the real worker's grouping behavior that
    /// causes HF variants to form their own hierarchy group (#1779).
    /// </summary>
    private static string BuildWorkerProfilesResponseJson(
        string manufacturerName,
        IReadOnlyDictionary<string, IReadOnlyList<MachineProfileDto>> machineProfilesByModelGroupName)
    {
        Dictionary<string, PrinterModelProfilesDto> models = new();
        int index = 0;
        foreach (KeyValuePair<string, IReadOnlyList<MachineProfileDto>> group in machineProfilesByModelGroupName)
        {
            string modelKey = "model" + (++index).ToString(CultureInfo.InvariantCulture);
            models[modelKey] = new PrinterModelProfilesDto
            {
                Name = group.Key,
                ModelId = modelKey,
                MachineProfiles = [.. group.Value],
                FilamentProfiles = [],
                ProcessProfiles = []
            };
        }

        AllProfilesResponseDto response = new()
        {
            ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>
            {
                [manufacturerName] = new ManufacturerProfilesDto
                {
                    Name = manufacturerName,
                    Models = models
                }
            }
        };

        return JsonSerializer.Serialize(response);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send(request));
        }
    }

    [Fact]
    public async Task ListExtendedAsync_SettingsJsonContainsNozzleDiameterAndPrinterVariant_PopulatesDto()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One HF",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{\"NozzleDiameter\": 0.6, \"PrinterVariant\": \"HF\"}",
            Hash = "hash-1"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Equal(0.6, dto.NozzleDiameter);
        Assert.Equal("HF", dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_SettingsJsonMissing_FallsBackToRawJson()
    {
        // Arrange: SettingsJson absent, but RawJson still carries the original values.
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = null,
            RawJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": null}",
            Hash = "hash-2"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Equal(0.4, dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_NoNozzleDiameterOrPrinterVariantInJson_YieldsNulls()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Generic Machine",
            Manufacturer = "Generic Mfg",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{\"SomeOtherField\": 123}",
            Hash = "hash-3"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Null(dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_MalformedSettingsJson_DoesNotThrowAndYieldsNulls()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Broken Machine",
            Manufacturer = "Generic Mfg",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{not valid json",
            Hash = "hash-4"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Null(dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    private static ProfilesService CreateService(IReadOnlyList<MachineProfile> machineProfiles)
    {
        Mock<IProfilesRepository> profilesRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);

        Mock<IProcessProfileRepository> processProfileRepo = new(MockBehavior.Loose);
        _ = processProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessProfile>());

        Mock<IFilamentProfileRepository> filamentProfileRepo = new(MockBehavior.Loose);
        _ = filamentProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FilamentProfile>());

        Mock<IMachineProfileRepository> machineProfileRepo = new(MockBehavior.Loose);
        _ = machineProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineProfiles);

        return new ProfilesService(
            profilesRepo.Object,
            NullLogger<ProfilesService>.Instance,
            processProfileRepo.Object,
            machineProfileRepo.Object,
            filamentProfileRepo.Object,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }
}
