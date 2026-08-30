extern alias SlicerHost;

using System.Text.Json;
using Farm.Slicer.Module.Dtos;
using Farm.Slicers.OrcaSlicer.v2_4_0;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlicerHostProgram = SlicerHost::Program;

namespace Farm.Slicer.Module.Tests.Contracts;

/// <summary>
/// Wire-contract parity guard for issue #2238: proves the standalone slicer host's
/// (<c>Farm.Slicer.Host/Program.cs</c>) independently-configured MVC and SignalR
/// <see cref="JsonSerializerOptions"/> instances are what's actually registered in that host's
/// DI container (never a locally-reimplemented copy), and serializes a representative payload
/// through each of those two REAL registered option objects to prove — from the resulting JSON
/// bytes, never from inspecting the CLR options object's properties — that this host shares the
/// main API's camelCase-naming + string-enum convention.
/// </summary>
public sealed class SlicerHostSerializerParityTests : IClassFixture<SlicerHostSerializerParityTests.Factory>
{
    private readonly Factory _factory;

    public SlicerHostSerializerParityTests(Factory factory) => _factory = factory;

    /// <summary>
    /// The slicer host's registered MVC <see cref="JsonSerializerOptions"/> (via
    /// <c>AddJsonOptions</c>) uses camelCase property names and the exact enum string token —
    /// matching the main API's controller convention.
    /// </summary>
    [Fact]
    public void MvcJsonSerializerOptions_SerializesCamelCaseWithStringEnumToken()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        string json = JsonSerializer.Serialize(SampleDto(), options);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "status", "Slicing");
        _ = JsonContractAssertions.AssertProperty(document.RootElement, "jobId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(document.RootElement, "progress", JsonValueKind.Number);
    }

    /// <summary>
    /// The slicer host's registered SignalR payload <see cref="JsonSerializerOptions"/> (via
    /// <c>AddJsonProtocol</c>) uses the identical camelCase + string-enum convention as its MVC
    /// options — both are configured by the same shared <c>configureJson</c> delegate in
    /// <c>Farm.Slicer.Host/Program.cs</c>, so this proves that sharing hasn't drifted.
    /// </summary>
    [Fact]
    public void SignalRJsonSerializerOptions_SerializesCamelCaseWithStringEnumToken()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        string json = JsonSerializer.Serialize(SampleDto(), options);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "status", "Slicing");
        _ = JsonContractAssertions.AssertProperty(document.RootElement, "jobId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(document.RootElement, "progress", JsonValueKind.Number);
    }

    /// <summary>
    /// Fixed by issue #2248: the slicer host's shared <c>configureJson</c> delegate in
    /// <c>Farm.Slicer.Host/Program.cs</c> now sets
    /// <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> on its MVC options
    /// object, matching the main API's <c>ControllerStartup.cs</c>/<c>SignalRStartup.cs</c>
    /// convention. This test pins the REAL, current wire behaviour (a null field's key is
    /// omitted entirely, not serialized as an explicit JSON <c>null</c>), so any future
    /// regression that reintroduces the divergence shows up here as a reviewed test failure
    /// rather than a silent break discovered downstream by React/mobile.
    /// </summary>
    [Fact]
    public void MvcJsonSerializerOptions_NullField_IsOmitted_MatchesMainApi()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        var dto = SampleDto();
        dto.Message = null;
        string json = JsonSerializer.Serialize(dto, options);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertMissingKey(document.RootElement, "message");
    }

    /// <summary>Proves the MVC and SignalR options within this host agree with each other (both derive from the same shared delegate).</summary>
    [Fact]
    public void MvcAndSignalRJsonSerializerOptions_ShareIdenticalNullHandling_WithinThisHost()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions mvcOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        JsonSerializerOptions signalROptions = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        var dto = SampleDto();
        dto.Message = null;
        string mvcJson = JsonSerializer.Serialize(dto, mvcOptions);
        string signalRJson = JsonSerializer.Serialize(dto, signalROptions);

        using JsonDocument mvcDocument = JsonDocument.Parse(mvcJson);
        using JsonDocument signalRDocument = JsonDocument.Parse(signalRJson);
        IReadOnlyList<string> differences = JsonContractAssertions.CompareStructurally(
            mvcDocument.RootElement,
            signalRDocument.RootElement);

        Assert.Empty(differences);
    }

    private static SlicingJobDto SampleDto() => new()
    {
        JobId = Guid.NewGuid().ToString(),
        UserId = Guid.NewGuid(),
        Status = SlicingJobStatus.Slicing,
        Progress = 42,
        Message = "Generating toolpaths",
        SlicerEngine = "OrcaSlicer",
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Hosts the real production standalone slicer entry point (<c>Farm.Slicer.Host</c>) purely
    /// to resolve its actual registered JSON options from DI — no HTTP requests are made against
    /// it. Mirrors the minimal settings used by <c>StandaloneSlicerHostModelDownloadSecurityTests</c>.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<SlicerHostProgram>
    {
        private const string JwtKey = "PrintFarmerSlicerHostSerializerParityTestsSigningKey-1234567890";
        private readonly string _testRoot;
        private readonly string _databasePath;

        public Factory()
        {
            _testRoot = Path.Join(Path.GetTempPath(), $"slicer-host-parity-{Guid.NewGuid():N}");
            _databasePath = Path.Join(_testRoot, "slicer-host.db");
            Directory.CreateDirectory(_testRoot);

            // Forces the OrcaSlicer plugin assembly to load so the slicer host's "zero registered
            // slicer libraries" startup sanity check (#578) doesn't reject the host.
            _ = typeof(OrcaSlicerLibrary_v2_4_0).Assembly.GetName();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.UseEnvironment("Testing");
            _ = builder.UseSetting("Jwt:Key", JwtKey);
            _ = builder.UseSetting("Jwt:Issuer", "PrintFarmer");
            _ = builder.UseSetting("Jwt:Audience", "PrintFarmer");
            _ = builder.UseSetting("DB_PROVIDER", "sqlite");
            _ = builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath};Pooling=False");
            _ = builder.UseSetting("STORAGE_PATHS:UPLOADS", Path.Join(_testRoot, "uploads"));
            _ = builder.UseSetting("STORAGE_PATHS:GCODE", Path.Join(_testRoot, "gcode"));
            _ = builder.UseSetting("WorkerAuth:SharedKey", "slicer-host-parity-test-worker-key");
            _ = builder.UseSetting("ArtifactStorage:RootPath", Path.Join(_testRoot, "artifacts"));
            _ = builder.UseSetting("ArtifactStorage:EnableStorageAlerts", "false");
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
    }
}
