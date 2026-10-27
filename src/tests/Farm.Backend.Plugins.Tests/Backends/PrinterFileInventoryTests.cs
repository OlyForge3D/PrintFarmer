using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.FlashForge;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterFileInventoryTests
{
    [Theory]
    [InlineData("moonraker", """{"result":[]}""", "gcodes")]
    [InlineData("octoprint", """{"files":[]}""", "local")]
    [InlineData("prusalink", """{"storage_list":[{"path":"/usb/","available":true}]}""", "usb")]
    public async Task GetFileInventoryAsync_ExplicitEmptyListing_ProvesCompleteEmptyRoot(
        string backend, string json, string storageRoot)
    {
        using InventoryHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/v1/files/usb"
            ? Reply("""{"type":"FOLDER","children":[]}""") : Reply(json));
        using HttpClient http = new(handler);
        ISupportsFileInventory client = CreateClient(backend, http);

        PrinterFileInventoryResult result = await client.GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.None, result.Reason);
        Assert.Empty(result.Entries);
        PrinterFileInventoryCoverage coverage = Assert.Single(result.Coverage);
        Assert.Equal(storageRoot, coverage.StorageRoot);
        Assert.Equal(string.Empty, coverage.Subtree);
        Assert.True(coverage.IsComplete);
        Assert.NotEqual(PrinterFileInventoryEvidence.None, coverage.Evidence);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData("moonraker")]
    [InlineData("octoprint")]
    [InlineData("prusalink")]
    public async Task GetFileInventoryAsync_AuthenticationFailure_IsNotAnEmptySuccess(string backend)
    {
        using InventoryHandler handler = new(_ => Reply("secret body", HttpStatusCode.Forbidden));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http)
            .GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.AuthenticationRequired, result.Reason);
        Assert.Empty(result.Coverage);
        Assert.Empty(result.Entries);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("moonraker", "{}")]
    [InlineData("moonraker", """{"result":null}""")]
    [InlineData("moonraker", """{"result":[],"error":{"message":"failed"}}""")]
    [InlineData("octoprint", "{}")]
    [InlineData("octoprint", """{"files":[{"name":"folder","type":"folder"}]}""")]
    [InlineData("prusalink", "{}")]
    [InlineData("prusalink", """{"storage_list":null}""")]
    [InlineData("moonraker", """{"result":[""")]
    public async Task GetFileInventoryAsync_MalformedResponse_HasNoCompleteCoverage(string backend, string json)
    {
        using InventoryHandler handler = new(_ => Reply(json));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http)
            .GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.MalformedResponse, result.Reason);
        Assert.DoesNotContain(result.Coverage, coverage => coverage.IsComplete);
    }

    [Theory]
    [InlineData("moonraker", """{"result":[],"truncated":true}""")]
    [InlineData("octoprint", """{"files":[],"next":"/next"}""")]
    [InlineData("prusalink", """{"storage_list":[],"has_more":true}""")]
    public async Task GetFileInventoryAsync_TruncatedListing_IsPartialNotComplete(string backend, string json)
    {
        using InventoryHandler handler = new(_ => Reply(json));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http)
            .GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.IncompleteResponse, result.Reason);
        Assert.DoesNotContain(result.Coverage, coverage => coverage.IsComplete);
    }

    [Theory]
    [InlineData("moonraker")]
    [InlineData("octoprint")]
    [InlineData("prusalink")]
    public async Task GetFileInventoryAsync_TransportFailure_IsUnavailable(string backend)
    {
        using InventoryHandler handler = new(_ => throw new HttpRequestException("sensitive diagnostic"));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http)
            .GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.TransportFailure, result.Reason);
        Assert.Empty(result.Coverage);
    }

    [Theory]
    [InlineData("moonraker")]
    [InlineData("octoprint")]
    [InlineData("prusalink")]
    public async Task GetFileInventoryAsync_MissingNativeEndpoint_IsExplicitlyUnsupported(string backend)
    {
        using InventoryHandler handler = new(_ => Reply("{}", HttpStatusCode.NotFound));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Unsupported, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.Unsupported, result.Reason);
        Assert.Empty(result.Coverage);
    }

    [Theory]
    [InlineData("moonraker", false)]
    [InlineData("moonraker", true)]
    [InlineData("octoprint", false)]
    [InlineData("octoprint", true)]
    [InlineData("prusalink", false)]
    [InlineData("prusalink", true)]
    public async Task GetFileInventoryAsync_CancelOrDeadline_PreservesDistinctFailure(string backend, bool callerCancel)
    {
        using InventoryHandler handler = new(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply("{}");
        });
        using HttpClient http = new(handler);
        using CancellationTokenSource cancellation = new();
        if (callerCancel)
        {
            cancellation.Cancel();
        }

        PrinterFileInventoryResult result = await CreateClient(backend, http).GetFileInventoryAsync(
            "http://printer/", options: new() { Deadline = TimeSpan.FromMilliseconds(30) }, ct: cancellation.Token);

        Assert.Equal(callerCancel ? PrinterFileInventoryOutcome.Cancelled : PrinterFileInventoryOutcome.Unavailable,
            result.Outcome);
        Assert.Equal(callerCancel ? PrinterFileInventoryReason.Cancelled : PrinterFileInventoryReason.Timeout,
            result.Reason);
        Assert.Empty(result.Coverage);
        Assert.Equal(callerCancel ? 0 : 1, handler.Paths.Count);
    }

    [Fact]
    public async Task GetFileInventoryAsync_MoonrakerListing_PreservesNestedCaseAndUnknownMetadataWithoutFanout()
    {
        using InventoryHandler handler = new(_ =>
            Reply("""{"result":[{"path":"Folder/Part.gcode"},{"path":"folder/Part.gcode","size":0,"modified":1700000000}]}"""));
        using HttpClient http = new(handler);
        PrinterCredential credential = new() { ApiKey = "test-key" };

        PrinterFileInventoryResult result = await CreateClient("moonraker", http)
            .GetFileInventoryAsync("http://printer/", credential);

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal(["Folder/Part.gcode", "folder/Part.gcode"], result.Entries.Select(entry => entry.RelativePath));
        Assert.Null(result.Entries[0].SizeBytes);
        Assert.Null(result.Entries[0].ModifiedAt);
        Assert.Null(result.Entries[0].ModifiedValue);
        Assert.Equal(0, result.Entries[1].SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.Entries[1].ModifiedAt);
        Assert.Equal("1700000000", result.Entries[1].ModifiedValue);
        Assert.Equal(["/server/files/list?root=gcodes"], handler.Paths);
        Assert.Equal(["test-key"], handler.ApiKeys);
    }

    [Fact]
    public async Task GetFileInventoryAsync_OctoPrintRecursiveListing_RetainsPathsAndProvidedHashWithoutFanout()
    {
        using InventoryHandler handler = new(_ => Reply("""
            {"files":[{"name":"Dir","path":"Dir","type":"folder","children":[
              {"name":"Part.gcode","path":"Dir/Part.gcode","type":"machinecode","size":123,"date":1700000000,"hash":"provider-hash"},
              {"name":"shape.stl","type":"model"}]}]}
            """));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("octoprint", http)
            .GetFileInventoryAsync("http://printer/", new() { ApiKey = "test-key" });

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        PrinterFileInventoryEntry file = Assert.Single(result.Entries);
        Assert.Equal("local", file.StorageRoot);
        Assert.Equal("Dir/Part.gcode", file.RelativePath);
        Assert.Equal(123, file.SizeBytes);
        Assert.Equal("provider-hash", file.ProviderHash);
        Assert.Equal(["/api/files/local?recursive=true"], handler.Paths);
        Assert.Equal(["test-key"], handler.ApiKeys);
    }

    [Fact]
    public async Task GetFileInventoryAsync_PrusaDeclaredRoots_TraversesFoldersWithoutGuessingUsbOrDownloadingFiles()
    {
        using InventoryHandler handler = new(request => Reply(request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/storage" => """{"storage_list":[{"path":"/Custom","available":true},{"path":"/Other","available":true}]}""",
            "/api/v1/files/Custom" => """{"type":"FOLDER","children":[{"name":"Sub","type":"FOLDER"}]}""",
            "/api/v1/files/Custom/Sub" => """{"type":"FOLDER","children":[{"name":"Part.bgcode","type":"PRINT_FILE","size":77}]}""",
            "/api/v1/files/Other" => """{"type":"FOLDER","children":[{"name":"Part.bgcode","type":"PRINT_FILE","m_timestamp":1700000000}]}""",
            _ => throw new InvalidOperationException("Unexpected metadata fanout or guessed storage")
        }));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal(["Custom", "Other"], result.Entries.Select(entry => entry.StorageRoot));
        Assert.Equal(["Sub/Part.bgcode", "Part.bgcode"], result.Entries.Select(entry => entry.RelativePath));
        Assert.Equal(77, result.Entries[0].SizeBytes);
        Assert.Null(result.Entries[1].SizeBytes);
        Assert.Null(result.Entries[0].ModifiedAt);
        Assert.All(result.Coverage, coverage => Assert.True(coverage.IsComplete));
        Assert.Equal(4, handler.Paths.Count);
    }

    [Fact]
    public async Task GetFileInventoryAsync_PrusaDepthBudget_NeverClaimsRootComplete()
    {
        using InventoryHandler handler = new(request => Reply(request.RequestUri!.AbsolutePath == "/api/v1/storage"
            ? """{"storage_list":[{"path":"/custom","available":true}]}"""
            : """{"type":"FOLDER","children":[{"name":"Nested","type":"FOLDER"}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", options: new() { MaxDepth = 0 });

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.DepthLimit, result.Reason);
        Assert.DoesNotContain(result.Coverage, coverage => coverage.IsComplete);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task GetFileInventoryAsync_EntryBudget_PreservesObservedFilesWithoutCompleteCoverage()
    {
        using InventoryHandler handler = new(_ =>
            Reply("""{"result":[{"path":"first.gcode"},{"path":"second.gcode"}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http)
            .GetFileInventoryAsync("http://printer/", options: new() { MaxEntries = 1 });

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.EntryLimit, result.Reason);
        Assert.Equal("first.gcode", Assert.Single(result.Entries).RelativePath);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task GetFileInventoryAsync_FractionalProviderTimestamp_RetainsRawPrecision()
    {
        using InventoryHandler handler = new(_ =>
            Reply("""{"result":[{"path":"part.gcode","modified":1700000000.123456}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal("1700000000.123456", Assert.Single(result.Entries).ModifiedValue);
    }

    [Fact]
    public async Task GetFileInventoryAsync_InvalidOptionalMetadata_PreservesUnknownInsteadOfZero()
    {
        using InventoryHandler handler = new(_ =>
            Reply("""{"result":[{"path":"part.gcode","size":"unknown","modified":1e1000}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        PrinterFileInventoryEntry file = Assert.Single(result.Entries);
        Assert.Null(file.SizeBytes);
        Assert.Null(file.ModifiedAt);
        Assert.Null(file.ModifiedValue);
        Assert.Null(file.ProviderHash);
        Assert.Null(file.ProviderVersion);
    }

    [Theory]
    [InlineData("moonraker", """{"result":[{"path":"dir/file.gcode"}]}""")]
    [InlineData("octoprint", """{"files":[{"name":"dir","type":"folder","children":[]}]}""")]
    public async Task GetFileInventoryAsync_DepthBudget_HasNoCompleteCoverage(string backend, string json)
    {
        using InventoryHandler handler = new(_ => Reply(json));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient(backend, http)
            .GetFileInventoryAsync("http://printer/", options: new() { MaxDepth = 0 });

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.DepthLimit, result.Reason);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task GetFileInventoryAsync_UnavailableDeclaredStorage_IsNotCompleteEmptyStorage()
    {
        using InventoryHandler handler = new(_ =>
            Reply("""{"storage_list":[{"path":"/usb","available":false}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.StorageUnavailable, result.Reason);
        Assert.False(Assert.Single(result.Coverage).IsComplete);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task GetFileInventoryAsync_ResponseSizeBudget_DoesNotReadUnboundedMetadata()
    {
        using InventoryHandler handler = new(_ =>
        {
            HttpResponseMessage response = Reply("""{"result":[]}""");
            response.Content.Headers.ContentLength = 9 * 1024 * 1024;
            return response;
        });
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http).GetFileInventoryAsync("http://printer/");

        Assert.NotEqual(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.ResponseLimit, result.Reason);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task GetFileInventoryAsync_PrusaDetails_MoreThan100RequestsCompletesWithoutFetchingBodies()
    {
        string listing = JsonSerializer.Serialize(new
        {
            type = "FOLDER",
            children = Enumerable.Range(0, 105).Select(index => new { name = $"PART{index}.BGC", type = "PRINT_FILE" })
        });
        using InventoryHandler handler = new(request => Reply(request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/storage" => """{"storage_list":[{"path":"/usb","available":true}]}""",
            "/api/v1/files/usb" => listing,
            var path when path.StartsWith("/api/v1/files/usb/PART", StringComparison.Ordinal) =>
                JsonSerializer.Serialize(new { type = "PRINT_FILE", name = path.Split('/')[^1], size = 123, meta = new { } }),
            _ => throw new InvalidOperationException("Unexpected file body or thumbnail request")
        }));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", new() { ApiKey = "test-key" }, new() { IncludeFileDetails = true });

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Equal(105, result.Entries.Length);
        Assert.Equal(107, handler.Paths.Count);
        Assert.All(result.Entries, entry =>
        {
            Assert.Equal(123, entry.SizeBytes);
            Assert.NotNull(entry.DetailsFetchedAtUtc);
            Assert.Null(entry.Metadata);
        });
        Assert.All(handler.ApiKeys, key => Assert.Equal("test-key", key));
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task GetFileInventoryAsync_PrusaDigestChallenge_AuthenticatesAndCompletesTraversal()
    {
        using InventoryHandler handler = new(request =>
        {
            if (request.Headers.Authorization?.Scheme == "Digest")
            {
                return Reply(request.RequestUri!.AbsolutePath == "/api/v1/storage"
                    ? """{"storage_list":[{"path":"/usb","available":true}]}"""
                    : """{"type":"FOLDER","children":[]}""");
            }

            HttpResponseMessage challenge = Reply("{}", HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.ParseAdd("""Digest realm="printer", nonce="test-nonce", qop="auth" """);
            return challenge;
        });
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", new() { Username = "maker", Password = "test-password" });

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        // One challenge; the transport reuses the negotiated digest for later traversal requests.
        Assert.Equal(["/api/v1/storage", "/api/v1/storage", "/api/v1/files/usb"], handler.Paths);
        Assert.True(Assert.Single(result.Coverage).IsComplete);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.0)]
    public async Task GetFileInventoryAsync_PrusaDetails_MapsEveryNativeMetadataFieldWithoutChangingIdentity(double layerHeight)
    {
        using InventoryHandler handler = new(request => Reply(request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/storage" => """{"storage_list":[{"path":"/usb","available":true}]}""",
            "/api/v1/files/usb" => """{"type":"FOLDER","children":[{"name":"PART~1.BGC","type":"PRINT_FILE","m_timestamp":1700000000}]}""",
            "/api/v1/files/usb/PART~1.BGC" => $$$"""
                {"name":"PART~1.BGC","type":"PRINT_FILE","display_name":"Long printable part.bgcode","size":629505,"m_timestamp":1700000000,
                 "refs":{"download":"/usb/PART~1.BGC","thumbnail":"/thumb/l/usb/PART~1.BGC"},
                 "meta":{"bed_temperature":60,"bed_temperature per tool":[60,0],"temperature":215,"temperature per tool":[215,0],
                 "brim_width":0,"estimated printing time (normal mode)":"1h 2m","estimated_print_time":3720,"faded_layers":3,
                 "filament cost":1.5,"filament cost per tool":[1.5,0],"filament used [cm3]":12,"filament used [cm3] per tool":[12,0],
                 "filament used [g]":15,"filament used [g] per tool":[15,0],"filament used [mm]":100,"filament used [mm] per tool":[100,0],
                 "filament_type":"PLA","filament_type per tool":["PLA","PETG"],"fill_density":"15%","initial_exposure_time":20,
                 "layer_height":{{{JsonSerializer.Serialize(layerHeight)}}},"material_name":"Resin","exposure_time":2,"max_exposure_time":3,"max_initial_exposure_time":25,
                 "min_exposure_time":1,"min_initial_exposure_time":15,"nozzle_diameter":0.4,"nozzle_diameter per tool":[0.4,0.6],
                 "normal_percent_present":true,"normal_left_present":false,"quiet_percent_present":false,"quiet_left_present":true,
                 "layer_info_present":false,"max_layer_z":42,"print_time":3700,"printer_model":"Future printer","support_material":"0",
                 "ironing":0,"required_resin_ml":25,"profile":"Quality"}}
                """,
            _ => throw new InvalidOperationException("Unexpected body request")
        }));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", options: new() { IncludeFileDetails = true });

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        PrinterFileInventoryEntry entry = Assert.Single(result.Entries);
        Assert.Equal("PART~1.BGC", entry.RelativePath);
        Assert.Equal("Long printable part.bgcode", entry.DisplayName);
        Assert.Equal(629505, entry.SizeBytes);
        Assert.NotNull(entry.DetailsFetchedAtUtc);
        PrinterPrintMetadata expected = new()
        {
            BedTemperatureC = 60,
            BedTemperaturePerToolC = [60, 0],
            NozzleTemperatureC = 215,
            NozzleTemperaturePerToolC = [215, 0],
            BrimWidthMm = 0,
            EstimatedPrintTimeNormalMode = "1h 2m",
            EstimatedPrintTimeSeconds = 3720,
            FadedLayers = 3,
            FilamentCost = 1.5,
            FilamentCostPerTool = [1.5, 0],
            FilamentUsedCm3 = 12,
            FilamentUsedPerToolCm3 = [12, 0],
            FilamentUsedG = 15,
            FilamentUsedPerToolG = [15, 0],
            FilamentUsedMm = 100,
            FilamentUsedPerToolMm = [100, 0],
            FilamentType = "PLA",
            FilamentTypePerTool = ["PLA", "PETG"],
            FillDensity = "15%",
            InitialExposureTimeSeconds = 20,
            LayerHeightMm = layerHeight,
            MaterialName = "Resin",
            ExposureTimeSeconds = 2,
            MaxExposureTimeSeconds = 3,
            MaxInitialExposureTimeSeconds = 25,
            MinExposureTimeSeconds = 1,
            MinInitialExposureTimeSeconds = 15,
            NozzleDiameterMm = 0.4,
            NozzleDiameterPerToolMm = [0.4, 0.6],
            NormalPercentPresent = true,
            NormalLeftPresent = false,
            QuietPercentPresent = false,
            QuietLeftPresent = true,
            LayerInfoPresent = false,
            MaxLayerZMm = 42,
            PrintTimeSeconds = 3700,
            PrinterModel = "Future printer",
            SupportMaterial = "0",
            Ironing = 0,
            RequiredResinMl = 25,
            Profile = "Quality"
        };
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(entry.Metadata));
        Assert.True(entry.Metadata!.IsValid());
        Assert.Equal(3, handler.Paths.Count);
    }

    [Theory]
    [InlineData("1700000000", null, true, true)]
    [InlineData("1700000000", 123L, true, true)]
    [InlineData("1700000000", 124L, true, false)]
    [InlineData("1700000001", null, true, false)]
    [InlineData("null", null, true, false)]
    [InlineData("1700000000", null, false, false)]
    public async Task GetFileInventoryAsync_PrusaDetails_ReusesOnlyFetchedDetailsWithMatchingListingEvidence(
        string timestamp, long? size, bool fetched, bool reuse)
    {
        PrinterPrintMetadata metadata = new() { BrimWidthMm = 0, QuietLeftPresent = false };
        DateTime fetchedAt = DateTime.UtcNow.AddDays(-30);
        PrinterFileInventoryEntry cached = new("usb", "PART.BGC", 123,
            ModifiedValue: "1700000000", DisplayName: "Cached part.bgcode", Metadata: metadata,
            DetailsFetchedAtUtc: fetched ? fetchedAt : null);
        using InventoryHandler handler = new(request => Reply(request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/storage" => """{"storage_list":[{"path":"/usb","available":true}]}""",
            "/api/v1/files/usb" => $$$"""{"type":"FOLDER","children":[{"type":"PRINT_FILE","name":"PART.BGC","size":{{{JsonSerializer.Serialize(size)}}},"m_timestamp":{{{timestamp}}}}]}""",
            "/api/v1/files/usb/PART.BGC" => $$$"""{"type":"PRINT_FILE","name":"PART.BGC","size":{{{size ?? 123}}},"m_timestamp":{{{timestamp}}},"meta":{}}""",
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", options: new()
            {
                IncludeFileDetails = true,
                CachedEntries = new Dictionary<string, PrinterFileInventoryEntry>
                {
                    [PrinterInventoryPath.ComputeIdentity("usb", "PART.BGC")] = cached
                }
            });

        Assert.Equal(PrinterFileInventoryOutcome.Complete, result.Outcome);
        PrinterFileInventoryEntry entry = Assert.Single(result.Entries);
        Assert.Equal(reuse ? 2 : 3, handler.Paths.Count);
        Assert.NotNull(entry.DetailsFetchedAtUtc);
        if (reuse)
        {
            Assert.Equal(fetchedAt, entry.DetailsFetchedAtUtc);
            Assert.Same(metadata, entry.Metadata);
            Assert.Equal(123, entry.SizeBytes);
            Assert.Equal("Cached part.bgcode", entry.DisplayName);
        }
        else
        {
            Assert.Null(entry.Metadata);
            Assert.NotEqual(fetchedAt, entry.DetailsFetchedAtUtc);
        }
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.OK, PrinterFileInventoryReason.IncompleteResponse)]
    [InlineData("""{"type":"PRINT_FILE","name":"OTHER.BGC"}""", HttpStatusCode.OK, PrinterFileInventoryReason.IncompleteResponse)]
    [InlineData("""{"type":"PRINT_FILE","name":"PART.BGC","m_timestamp":1700000001}""", HttpStatusCode.OK, PrinterFileInventoryReason.IncompleteResponse)]
    [InlineData("broken", HttpStatusCode.OK, PrinterFileInventoryReason.MalformedResponse)]
    [InlineData("{}", HttpStatusCode.Forbidden, PrinterFileInventoryReason.AuthenticationRequired)]
    public async Task GetFileInventoryAsync_PrusaDetailFailure_RetainsListingButDoesNotClaimCompleteEnrichment(
        string details, HttpStatusCode status, PrinterFileInventoryReason reason)
    {
        using InventoryHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/storage" => Reply("""{"storage_list":[{"path":"/usb","available":true}]}"""),
            "/api/v1/files/usb" => Reply("""{"type":"FOLDER","children":[{"type":"PRINT_FILE","name":"PART.BGC","m_timestamp":1700000000}]}"""),
            _ => Reply(details, status)
        });
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", options: new() { IncludeFileDetails = true });

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.True(Assert.Single(result.Coverage).IsComplete);
        Assert.Null(Assert.Single(result.Entries).DetailsFetchedAtUtc);
        Assert.Null(result.Entries[0].Metadata);
    }

    [Fact]
    public async Task GetFileInventoryAsync_PrusaDetailDeadline_StopsRemainingDetailRequests()
    {
        using InventoryHandler handler = new(async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v1/storage":
                    return Reply("""{"storage_list":[{"path":"/usb","available":true}]}""");
                case "/api/v1/files/usb":
                    return Reply("""{"type":"FOLDER","children":[{"type":"PRINT_FILE","name":"FIRST.BGC"},{"type":"PRINT_FILE","name":"SECOND.BGC"}]}""");
                default:
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Reply("{}");
            }
        });
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http).GetFileInventoryAsync(
            "http://printer/", options: new() { IncludeFileDetails = true, Deadline = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.Timeout, result.Reason);
        Assert.Equal(2, result.Entries.Length);
        Assert.Equal(["/api/v1/storage", "/api/v1/files/usb", "/api/v1/files/usb/FIRST.BGC"], handler.Paths);
    }

    [Fact]
    public async Task GetFileInventoryAsync_DuplicateProviderIdentity_IsPartialWithNoAbsenceAuthority()
    {
        using InventoryHandler handler = new(_ => Reply("""{"result":[{"path":"part.gcode"},{"path":"part.gcode"}]}"""));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryOutcome.Partial, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.InvalidPath, result.Reason);
        Assert.Single(result.Entries);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task GetFileInventoryAsync_CallerCancelsDuringTraversal_StopsFurtherMetadataRequests()
    {
        using CancellationTokenSource cancellation = new();
        using InventoryHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/storage")
            {
                return Reply("""{"storage_list":[{"path":"/custom","available":true}]}""");
            }

            cancellation.Cancel();
            return Reply("""{"type":"FOLDER","children":[{"name":"Nested","type":"FOLDER"}]}""");
        });
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("prusalink", http)
            .GetFileInventoryAsync("http://printer/", ct: cancellation.Token);

        Assert.Equal(PrinterFileInventoryOutcome.Cancelled, result.Outcome);
        Assert.Equal(PrinterFileInventoryReason.Cancelled, result.Reason);
        Assert.Equal(2, handler.Paths.Count);
        Assert.Empty(result.Coverage);
    }

    [Theory]
    [InlineData("../part.gcode")]
    [InlineData("/part.gcode")]
    [InlineData("dir\\part.gcode")]
    [InlineData("dir//part.gcode")]
    [InlineData("dir/./part.gcode")]
    public async Task GetFileInventoryAsync_UnsafeProviderPath_HasNoCompleteCoverage(string path)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { result = new[] { new { path } } });
        using InventoryHandler handler = new(_ => Reply(json));
        using HttpClient http = new(handler);

        PrinterFileInventoryResult result = await CreateClient("moonraker", http).GetFileInventoryAsync("http://printer/");

        Assert.Equal(PrinterFileInventoryReason.InvalidPath, result.Reason);
        Assert.NotEqual(PrinterFileInventoryOutcome.Complete, result.Outcome);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public void ComputeIdentity_RootPathAndCaseVariations_UsesOrdinalNulDelimitedSha256()
    {
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Root\0Folder/Part.gcode")));

        Assert.Equal(expected, PrinterInventoryPath.ComputeIdentity("/Root/", "Folder/Part.gcode"));
        Assert.NotEqual(expected, PrinterInventoryPath.ComputeIdentity("root", "Folder/Part.gcode"));
        Assert.NotEqual(expected, PrinterInventoryPath.ComputeIdentity("Root", "folder/Part.gcode"));
        Assert.NotEqual(PrinterInventoryPath.ComputeIdentity("a", "b/c"),
            PrinterInventoryPath.ComputeIdentity("a/b", "c"));
    }

    [Fact]
    public void TryGetFileInventoryClientTyped_ActualCapabilityOnly_UnknownAndLegacyFailClosed()
    {
        Mock<IBackendClientFactory> clients = new();
        Mock<IBackendClient> inventory = new();
        inventory.As<ISupportsFileInventory>();
        Mock<IBackendClient> legacy = new();
        legacy.As<ISupportsFileList>();
        clients.Setup(factory => factory.GetClient(PrinterBackend.Moonraker)).Returns(inventory.Object);
        clients.Setup(factory => factory.GetClient(PrinterBackend.SDCP)).Returns(legacy.Object);
        BackendCapabilityFactory factory = new(clients.Object, NullLogger<BackendCapabilityFactory>.Instance);

        Assert.True(factory.TryGetFileInventoryClientTyped(PrinterBackend.Moonraker, out ISupportsFileInventory? native));
        Assert.Same(inventory.Object, native);
        Assert.False(factory.TryGetFileInventoryClientTyped(PrinterBackend.SDCP, out _));
        Assert.False(factory.TryGetFileInventoryClientTyped(PrinterBackend.Unknown, out _));
        Assert.False(typeof(ISupportsFileInventory).IsAssignableFrom(typeof(SdcpClient)));
        Assert.False(typeof(ISupportsFileInventory).IsAssignableFrom(typeof(FlashForgeClient)));
    }

    private static ISupportsFileInventory CreateClient(string backend, HttpClient http) => backend switch
    {
        "moonraker" => new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings()),
        "octoprint" => new OctoPrintClient(http),
        "prusalink" => new PrusaLinkClient(http),
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class InventoryHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public InventoryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public InventoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<string> Paths { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        public List<string?> ApiKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method);
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? keys.Single() : null);
            return _respond(request, cancellationToken);
        }
    }
}
