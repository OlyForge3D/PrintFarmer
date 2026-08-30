extern alias SlicerHost;

using System.Text.Json;
using Farm.Slicers.OrcaSlicer.v2_4_0;
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
/// Wire-contract parity guard for issue #2248: proves the standalone slicer host's
/// (<c>Farm.Slicer.Host/Program.cs</c>) shared <c>configureJson</c> delegate applies
/// <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> to BOTH its MVC (via
/// <c>IOptions&lt;JsonOptions&gt;</c>) and SignalR (via
/// <c>IOptions&lt;JsonHubProtocolOptions&gt;</c>) options objects, matching the main API's
/// <c>ControllerStartup.cs</c>/<c>SignalRStartup.cs</c> null-handling policy. Resolves the REAL
/// registered options from the slicer host's DI container (never a locally-reimplemented copy)
/// and inspects the serialized JSON bytes directly, never the CLR options object's properties.
/// </summary>
public sealed class SlicerHostSerializerParityTests : IClassFixture<SlicerHostSerializerParityTests.Factory>
{
    private readonly Factory _factory;

    public SlicerHostSerializerParityTests(Factory factory) => _factory = factory;

    /// <summary>
    /// A null field serializes as a MISSING key, not an explicit JSON <c>null</c> — matching the
    /// main API's null-handling policy (issue #2248's fix). Previously this test pinned the
    /// opposite (explicit-null) behavior as a known, intentional divergence; the assertion below
    /// was updated in lockstep with the <c>configureJson</c> production fix.
    /// </summary>
    [Fact]
    public void MvcJsonSerializerOptions_NullField_IsMissingKey_MatchesMainApi()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        string json = JsonSerializer.Serialize(SampleDto(message: null), options);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.False(
            document.RootElement.TryGetProperty("message", out _),
            "Expected 'message' to be omitted from the payload, matching the main API's " +
            "DefaultIgnoreCondition = WhenWritingNull policy, but the key was present.");
    }

    /// <summary>
    /// The slicer host's registered SignalR payload <see cref="JsonSerializerOptions"/> agrees
    /// with its MVC options: both are configured by the same shared <c>configureJson</c> delegate
    /// in <c>Farm.Slicer.Host/Program.cs</c>, so a null field is omitted from both, not just one.
    /// </summary>
    [Fact]
    public void MvcAndSignalRJsonSerializerOptions_ShareIdenticalNullHandling_WithinThisHost()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        JsonSerializerOptions mvcOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        JsonSerializerOptions signalROptions = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        SampleSlicingStatus dto = SampleDto(message: null);
        string mvcJson = JsonSerializer.Serialize(dto, mvcOptions);
        string signalRJson = JsonSerializer.Serialize(dto, signalROptions);

        using JsonDocument mvcDocument = JsonDocument.Parse(mvcJson);
        using JsonDocument signalRDocument = JsonDocument.Parse(signalRJson);

        bool mvcHasMessage = mvcDocument.RootElement.TryGetProperty("message", out _);
        bool signalRHasMessage = signalRDocument.RootElement.TryGetProperty("message", out _);

        Assert.False(mvcHasMessage, "MVC options should omit a null 'message' field.");
        Assert.False(signalRHasMessage, "SignalR options should omit a null 'message' field.");
    }

    private static SampleSlicingStatus SampleDto(string? message) => new()
    {
        JobId = Guid.NewGuid().ToString(),
        Status = "Slicing",
        Progress = 42,
        Message = message,
    };

    /// <summary>Minimal, self-contained DTO shape used only to exercise null-handling — deliberately not one of the production slicer DTOs, to keep this regression guard independent of unrelated schema changes.</summary>
    private sealed class SampleSlicingStatus
    {
        public string JobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int Progress { get; set; }

        public string? Message { get; set; }
    }

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
