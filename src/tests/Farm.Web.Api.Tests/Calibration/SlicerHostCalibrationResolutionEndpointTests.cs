using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Contract of the slicer-host calibration resolution endpoint: who may call it, what it accepts,
/// and that ownership scope always comes from the validated token rather than the request.
/// </summary>
public sealed class SlicerHostCalibrationResolutionEndpointTests : IAsyncLifetime, IDisposable
{
    private static readonly Guid PublicMachineId = Guid.NewGuid();
    private static readonly Guid PublicProcessId = Guid.NewGuid();
    private static readonly Guid PublicFilamentId = Guid.NewGuid();
    private static readonly Guid PrivateMachineId = Guid.NewGuid();
    private static readonly Guid PrivateProcessId = Guid.NewGuid();
    private static readonly Guid PrivateFilamentId = Guid.NewGuid();
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private SqliteConnection _keepAlive = null!;
    private string _connectionString = null!;
    private SlicerHostResolutionTestServer _slicerHost = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(
            $"Data Source=file:calibration_resolution_{Guid.NewGuid():N}?mode=memory&cache=shared");
        await _keepAlive.OpenAsync();
        _connectionString = _keepAlive.ConnectionString;

        await SeedProfilesAsync();
        _slicerHost = await SlicerHostResolutionTestServer.StartAsync(
            _connectionString,
            CalibrationTestJwt.Key,
            CalibrationTestJwt.Issuer,
            CalibrationTestJwt.Audience);
    }

    public async Task DisposeAsync()
    {
        await _slicerHost.DisposeAsync();
        await _keepAlive.CloseAsync();
        await _keepAlive.DisposeAsync();
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task ResolveEndpoint_WithoutAuthentication_Returns401()
    {
        using HttpClient client = _slicerHost.CreateClient();

        HttpResponseMessage response = await PostAsync(client, PublicRequestBody());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResolveEndpoint_WithoutCalibrationRead_Returns403()
    {
        using HttpClient client = CreateClient(Guid.NewGuid(), permissions: ["queue:read"]);

        HttpResponseMessage response = await PostAsync(client, PublicRequestBody());
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString().Should().Be("permission_denied");
    }

    [Fact]
    public async Task ResolveEndpoint_WithCalibrationRead_ReturnsPublicProfiles()
    {
        using HttpClient client = CreateReaderClient();

        ResolvedCalibrationProfiles resolved = await ResolveAsync(client, PublicRequestBody());

        _ = resolved.Machine.Should().NotBeNull();
        _ = resolved.Machine!.Name.Should().Be("Public Machine");
        _ = resolved.Process.Should().NotBeNull();
        _ = resolved.Filament.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolveEndpoint_ForNonOwnerOfPrivateProfiles_ReturnsNullProfiles()
    {
        using HttpClient client = CreateReaderClient();

        ResolvedCalibrationProfiles resolved = await ResolveAsync(client, PrivateRequestBody());

        _ = resolved.Machine.Should().BeNull();
        _ = resolved.Process.Should().BeNull();
        _ = resolved.Filament.Should().BeNull();
    }

    [Fact]
    public async Task ResolveEndpoint_ForOwnerOfPrivateProfiles_ReturnsProfiles()
    {
        using HttpClient client = CreateReaderClient(OwnerUserId);

        ResolvedCalibrationProfiles resolved = await ResolveAsync(client, PrivateRequestBody());

        _ = resolved.Machine.Should().NotBeNull();
        _ = resolved.Machine!.Name.Should().Be("Private Machine");
    }

    [Fact]
    public async Task ResolveEndpoint_ForFarmAdmin_UsesTheAuditedOwnershipBypass()
    {
        using HttpClient client = CreateClient(
            Guid.NewGuid(),
            permissions: [],
            roles: [PrintFarmerPermissions.FarmAdminRole]);

        ResolvedCalibrationProfiles resolved = await ResolveAsync(client, PrivateRequestBody());

        _ = resolved.Machine.Should().NotBeNull();
        _ = resolved.Machine!.Name.Should().Be("Private Machine");
    }

    [Fact]
    public async Task ResolveEndpoint_WithCallerSuppliedScope_RefusesAndGrantsNoBypass()
    {
        using HttpClient client = CreateReaderClient();
        string body =
            $$"""
              {"machineProfileId":"{{PrivateMachineId}}","processProfileId":"{{PrivateProcessId}}","filamentProfileId":"{{PrivateFilamentId}}","userId":"{{OwnerUserId}}","bypassOwnership":true}
              """;

        HttpResponseMessage response = await PostAsync(client, body);
        string payload = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be(CalibrationProfileResolutionContract.InvalidRequestCode);
        _ = payload.Should().NotContain("Private Machine");
    }

    [Theory]
    [InlineData("""{"machineProfileId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"machineProfileId":"00000000-0000-0000-0000-000000000000","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]
    [InlineData("""{"machineProfileId":"nope","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]
    [InlineData("""[]""")]
    public async Task ResolveEndpoint_WithInexactRequest_Returns400(string body)
    {
        using HttpClient client = CreateReaderClient();

        HttpResponseMessage response = await PostAsync(client, body);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveEndpoint_WithOversizedBody_IsRefused()
    {
        using HttpClient client = CreateReaderClient();
        string body =
            $$"""
              {"machineProfileId":"{{PublicMachineId}}","processProfileId":"{{PublicProcessId}}","filamentProfileId":"{{PublicFilamentId}}","padding":"{{new string('a', CalibrationProfileResolutionContract.MaxRequestBodyBytes * 4)}}"}
              """;

        HttpResponseMessage response = await PostAsync(client, body);

        _ = response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ResolveEndpoint_ExposesNoListingSurface()
    {
        using HttpClient client = CreateReaderClient();

        HttpResponseMessage response = await client.GetAsync(
            "/" + CalibrationProfileResolutionContract.ResolveRelativeRoute);

        _ = response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task AvailabilityProbe_IsReachableWithoutAnEndUserTokenAndReportsHealthy()
    {
        using HttpClient client = _slicerHost.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/" + CalibrationProfileResolutionContract.HealthRelativeRoute);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = body.Trim().Should().Be("Healthy");

        // The probe proves availability only; it must not carry profile data.
        _ = body.Should().NotContain("Public Machine");
    }

    private static string PublicRequestBody() =>
        $$"""
          {"machineProfileId":"{{PublicMachineId}}","processProfileId":"{{PublicProcessId}}","filamentProfileId":"{{PublicFilamentId}}"}
          """;

    private static string PrivateRequestBody() =>
        $$"""
          {"machineProfileId":"{{PrivateMachineId}}","processProfileId":"{{PrivateProcessId}}","filamentProfileId":"{{PrivateFilamentId}}"}
          """;

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync(
            "/" + CalibrationProfileResolutionContract.ResolveRelativeRoute,
            new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<ResolvedCalibrationProfiles> ResolveAsync(HttpClient client, string body)
    {
        HttpResponseMessage response = await PostAsync(client, body);
        string payload = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        return JsonSerializer.Deserialize<ResolvedCalibrationProfiles>(
                   payload,
                   CalibrationProfileResolutionContract.SerializerOptions)
               ?? throw new InvalidOperationException("Missing resolution payload.");
    }

    private HttpClient CreateReaderClient(Guid? userId = null) =>
        CreateClient(
            userId ?? Guid.NewGuid(),
            permissions: [PrintFarmerPermissions.Calibration.Read]);

    private HttpClient CreateClient(
        Guid userId,
        IEnumerable<string> permissions,
        IEnumerable<string>? roles = null)
    {
        HttpClient client = _slicerHost.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CalibrationTestJwt.Create(userId, permissions, roles));
        return client;
    }

    private async Task SeedProfilesAsync()
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
            .Options;
        await using SlicerDbContext db = new(options);
        _ = await db.Database.EnsureCreatedAsync();

        AddTriple(db, PublicMachineId, PublicProcessId, PublicFilamentId, "Public", isPublic: true, owner: null);
        AddTriple(db, PrivateMachineId, PrivateProcessId, PrivateFilamentId, "Private", isPublic: false, owner: OwnerUserId);
        _ = await db.SaveChangesAsync();
    }

    private static void AddTriple(
        SlicerDbContext db,
        Guid machineId,
        Guid processId,
        Guid filamentId,
        string prefix,
        bool isPublic,
        Guid? owner)
    {
        DateTime nowUtc = DateTime.UtcNow;
        string machineJson =
            $$"""{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"printer_variant":"{{prefix}}"}""";
        string processJson =
            $$"""{"layer_height":0.2,"infill_density":20,"process_variant":"{{prefix}}"}""";
        string filamentJson =
            $$"""{"filament_max_volumetric_speed":12,"filament_variant":"{{prefix}}"}""";

        _ = db.MachineProfiles.Add(new MachineProfile
        {
            Id = machineId,
            Name = $"{prefix} Machine",
            Manufacturer = "Test",
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            SlicerVersion = CalibrationContractConstants.SlicerVersion,
            ProfileFormat = CalibrationContractConstants.ProfileFormat,
            RawJson = machineJson,
            Hash = Sha256(machineJson),
            IsPublic = isPublic,
            CreatedByUserId = owner,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
        _ = db.ProcessProfiles.Add(new ProcessProfile
        {
            Id = processId,
            Name = $"{prefix} Process",
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            SlicerVersion = CalibrationContractConstants.SlicerVersion,
            ProfileFormat = CalibrationContractConstants.ProfileFormat,
            LayerHeight = 0.2,
            InfillPercentage = 20,
            PrintSpeed = 100,
            RawJson = processJson,
            Hash = Sha256(processJson),
            IsPublic = isPublic,
            CreatedByUserId = owner,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
        _ = db.FilamentProfiles.Add(new FilamentProfile
        {
            Id = filamentId,
            Name = $"{prefix} Filament",
            Material = "PLA",
            Manufacturer = "Test",
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            SlicerVersion = CalibrationContractConstants.SlicerVersion,
            ProfileFormat = CalibrationContractConstants.ProfileFormat,
            NozzleTemperature = 210,
            BedTemperature = 60,
            PrintSpeed = 100,
            RawJson = filamentJson,
            Hash = Sha256(filamentJson),
            IsPublic = isPublic,
            CreatedByUserId = owner,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        });
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
