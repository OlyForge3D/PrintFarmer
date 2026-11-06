using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Integration tests for issue #1666's acceptance criteria: <c>POST /api/files/local</c> (the
/// OctoPrint-compat upload endpoint) must reject an enqueue attempt from a caller with no
/// <c>queue:*</c> permission, and must reject an enqueue attempt targeting a printer in a
/// <see cref="PrinterGroup"/> the caller cannot submit to — in both cases without creating a
/// <see cref="PrintJob"/> row. A third test confirms the legitimate, correctly-scoped upload
/// flow still works end to end.
/// </summary>
/// <remarks>
/// The endpoint is decorated with
/// <c>[Authorize(Policy = OctoPrintUploadPolicy.Name)]</c>, whose policy names explicit
/// schemes and therefore bypasses <c>TestAuthHandler</c>'s <c>X-Test-*</c> header shortcut
/// entirely (that shortcut only applies when the host's <c>DefaultAuthenticateScheme</c> is
/// consulted). These tests use a real JWT (test 1, minted directly via
/// <see cref="CustomWebApplicationFactory.CreateAuthenticatedClientAsync"/>, which seeds a user
/// with zero role assignments) and a real seeded <see cref="ApiKeyPurpose.OctoPrint"/> API key
/// (tests 2 and 3, whose owning user's permissions are read live from the database on every
/// request — see <c>OctoPrintAuthService.ResolveApiKeyPrincipalAsync</c>).
/// </remarks>
public sealed class OctoPrintUploadAuthorizationTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public OctoPrintUploadAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_OptionalAnonymous_PersistsFileAndOnlyRequestedPrintJob(bool print)
    {
        await SetApiKeyRequirementAsync(false);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        using HttpClient client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "anonymous-upload-test.gcode");
        form.Add(new StringContent(print ? "true" : "false"), "print");

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/files/local?printerId={fixture.RestrictedPrinterId}", form);

        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            print ? HttpStatusCode.Accepted : HttpStatusCode.OK,
            $"explicit trusted-network mode permits credential-free uploads. Response body: {responseBody}");
        using JsonDocument body = JsonDocument.Parse(responseBody);
        Guid fileId = Guid.Parse(body.RootElement.GetProperty("file").GetProperty("id").GetString()!);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GcodeFiles.AnyAsync(file => file.Id == fileId)).Should().BeTrue();

        if (print)
        {
            body.RootElement.GetProperty("status").GetString().Should().Be("Queued");
            Guid jobId = body.RootElement.GetProperty("jobId").GetGuid();
            PrintJob job = await db.PrintJobs.SingleAsync(job => job.Id == jobId);
            job.GcodeFileId.Should().Be(fileId);
            job.AssignedPrinterId.Should().Be(fixture.RestrictedPrinterId);
            job.CreatorSubject.Should().BeNull("only configured anonymous uploads use the trusted queue caller");
        }
        else
        {
            body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse();
            (await db.PrintJobs.CountAsync()).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_UserWithNoQueuePermission_Returns403AndCreatesNoPrintJob(bool requireApiKey)
    {
        await SetApiKeyRequirementAsync(requireApiKey);
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            $"no-perm-{Guid.NewGuid():N}",
            $"no-perm-{Guid.NewGuid():N}@example.test",
            "TestPassword123!");

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "no-permission-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync("/api/files/local", form);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a caller with no queue:* permission must be denied by the upload permission policy before the " +
            "action body (and therefore any enqueue) ever runs — see issue #1666");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GcodeFiles.CountAsync()).Should().Be(0, "permission checks must precede file persistence");
        (await db.PrintJobs.CountAsync()).Should().Be(
            0,
            "no PrintJob row should ever be created for a request denied at the permission gate");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_ApiKeyUserBarredFromTargetPrinterGroup_Returns403AndCreatesNoPrintJob(bool requireApiKey)
    {
        await SetApiKeyRequirementAsync(requireApiKey);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", fixture.RawApiKey);

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "barred-group-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/files/local?printerId={fixture.RestrictedPrinterId}",
            form);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an ApiKeyPurpose.OctoPrint key for a user with queue:write, but no submit access to " +
            "the target printer's group, must still be denied — the group-ACL check is " +
            "independent of the queue:write permission gate (see issue #1666)");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrintJobs.CountAsync(job => job.AssignedPrinterId == fixture.RestrictedPrinterId))
            .Should().Be(0, "no PrintJob row should ever be created against the barred printer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_ApiKeyUserWithGroupAccess_SucceedsAndQueuesToThatPrinter(bool requireApiKey)
    {
        await SetApiKeyRequirementAsync(requireApiKey);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync(grantCallerAccess: true);
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", fixture.RawApiKey);

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "authorized-upload-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/files/local?printerId={fixture.RestrictedPrinterId}",
            form);

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            $"a correctly-scoped OctoPrint key with submit access to the target printer's group " +
            $"must still be able to upload and enqueue end to end. Response body: {body}");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrintJobs.CountAsync(job => job.AssignedPrinterId == fixture.RestrictedPrinterId))
            .Should().Be(1, "the legitimate upload+print flow must still create exactly one PrintJob");
        (await db.PrintJobs.SingleAsync(job => job.AssignedPrinterId == fixture.RestrictedPrinterId))
            .CreatorSubject.Should().Be(fixture.UserId.ToString(), "authenticated users must never become trusted null callers");
    }

    [Fact]
    public async Task Upload_OrcaUploadAndPrintWithoutPrinterId_ReusesFileAndQueuesAfterAdmissionFailureRetry()
    {
        await SetApiKeyRequirementAsync(false);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync(printerAvailable: false);
        string gcode = OrcaGcodeContent(fixture.ModelName, nozzleDiameter: 0.4, material: "PLA");
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gcode)));
        List<string?> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == Farm.Web.Api.Telemetry.OctoPrintUploadMetrics.MeterName &&
                instrument.Name == "octoprint.upload_print.queue_admissions")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcome = tag.Value?.ToString();
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected upload metric tag: {tag.Key}");
                }
            }

            measurements.Add(outcome);
        });
        listener.Start();

        using HttpClient client = _factory.CreateClient();
        using (MultipartFormDataContent unavailableForm = CreateOrcaUploadForm(gcode))
        using (HttpResponseMessage unavailableResponse = await client.PostAsync("/api/files/local", unavailableForm))
        {
            string body = await unavailableResponse.Content.ReadAsStringAsync();
            unavailableResponse.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                $"Orca treats a successful HTTP response as upload success, so failed queue admission must be non-2xx. Body: {body}");
            using JsonDocument responseBody = JsonDocument.Parse(body);
            responseBody.RootElement.GetProperty("status").GetString().Should().Be("UploadedOnly");
            responseBody.RootElement.GetProperty("reasonCode").GetString().Should().Be("queue_admission_failed");
            responseBody.RootElement.GetProperty("message").GetString().Should().Contain("No print job was created");
            responseBody.RootElement.GetProperty("file").EnumerateObject()
                .Select(property => property.Name).Should().BeEquivalentTo(["id", "fileName"]);
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid fileId = await db.GcodeFiles.Select(file => file.Id).SingleAsync();
        (await db.GcodeFiles.SingleAsync(file => file.Id == fileId)).FileHash.Should().Be(expectedHash);
        (await db.PrintJobs.CountAsync()).Should().Be(0);

        Printer printer = await db.Printers.SingleAsync(candidate => candidate.Id == fixture.RestrictedPrinterId);
        printer.IsAvailable = true;
        await db.SaveChangesAsync();

        using MultipartFormDataContent retryForm = CreateOrcaUploadForm(gcode);
        using HttpResponseMessage retryResponse = await client.PostAsync("/api/files/local", retryForm);
        string retryBody = await retryResponse.Content.ReadAsStringAsync();
        retryResponse.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            $"Orca's multipart print=true request should queue after a compatible printer becomes available. Body: {retryBody}");
        using JsonDocument queuedResponse = JsonDocument.Parse(retryBody);
        queuedResponse.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        queuedResponse.RootElement.GetProperty("file").GetProperty("id").GetString().Should().Be(fileId.ToString());
        queuedResponse.RootElement.GetProperty("file").GetProperty("extractedNozzleDiameter").GetDouble().Should().Be(0.4);
        queuedResponse.RootElement.GetProperty("file").GetProperty("extractedMaterial").GetString().Should().Be("PLA");

        Guid jobId = queuedResponse.RootElement.GetProperty("jobId").GetGuid();
        PrintJob job = await db.PrintJobs.SingleAsync();
        job.Id.Should().Be(jobId);
        job.GcodeFileId.Should().Be(fileId);
        job.AssignedPrinterId.Should().Be(fixture.RestrictedPrinterId);
        job.EstimatedFilamentUsage.Should().Be(50000, "estimated filament quantity is advisory, not a queue-admission veto");
        (await db.GcodeFiles.CountAsync()).Should().Be(1, "retrying identical G-code reuses the stored file");
        measurements.Should().Contain("rejected");
        measurements.Should().Contain("queued");
    }

    [Fact]
    public async Task Upload_OrcaUploadAndPrintWithoutPrinterId_RejectsIncompatibleNozzle()
    {
        await SetApiKeyRequirementAsync(false);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent form = CreateOrcaUploadForm(
            OrcaGcodeContent(fixture.ModelName, nozzleDiameter: 0.6, material: "PLA"));
        using HttpResponseMessage response = await client.PostAsync("/api/files/local", form);

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using JsonDocument responseBody = JsonDocument.Parse(body);
        JsonElement uploadedFile = responseBody.RootElement.GetProperty("file");
        uploadedFile.EnumerateObject()
            .Select(property => property.Name).Should().BeEquivalentTo(["id", "fileName"]);
        AssertGenericQueueAdmissionFailure(responseBody.RootElement);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GcodeFiles.CountAsync()).Should().Be(1);
        (await db.PrintJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Upload_ApiKeyUserWithoutAccessToAutoMatchedPrinter_ReturnsGenericRejectionAndLogsReason()
    {
        await SetApiKeyRequirementAsync(false);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", fixture.RawApiKey);
        LoggerFactory loggerFactory = (LoggerFactory)_factory.Services.GetRequiredService<ILoggerFactory>();
        using CapturingWarningLoggerProvider warningProvider = new();
        loggerFactory.AddProvider(warningProvider);

        using MultipartFormDataContent form = CreateOrcaUploadForm(
            OrcaGcodeContent(fixture.ModelName, nozzleDiameter: 0.4, material: "PLA"));

        using HttpResponseMessage response = await client.PostAsync("/api/files/local", form);

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using JsonDocument responseBody = JsonDocument.Parse(body);
        AssertGenericQueueAdmissionFailure(responseBody.RootElement);
        JsonElement file = responseBody.RootElement.GetProperty("file");
        file.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["id", "fileName"]);
        body.Should().NotContain(fixture.RestrictedPrinterId.ToString());
        body.Should().NotContain(fixture.ModelName);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid fileId = Guid.Parse(file.GetProperty("id").GetString()!);
        (await db.GcodeFiles.AnyAsync(candidate => candidate.Id == fileId)).Should().BeTrue();
        (await db.PrintJobs.CountAsync()).Should().Be(0);

        using MultipartFormDataContent unknownModelForm = CreateOrcaUploadForm(
            OrcaGcodeContent($"{fixture.ModelName} unknown", nozzleDiameter: 0.4, material: "PLA"));
        using HttpResponseMessage unknownModelResponse = await client.PostAsync("/api/files/local", unknownModelForm);
        string unknownModelBody = await unknownModelResponse.Content.ReadAsStringAsync();
        unknownModelResponse.StatusCode.Should().Be(response.StatusCode);
        using JsonDocument unknownModelResponseBody = JsonDocument.Parse(unknownModelBody);
        AssertGenericQueueAdmissionFailure(unknownModelResponseBody.RootElement);
        unknownModelBody.Should().NotContain(fixture.RestrictedPrinterId.ToString());
        unknownModelBody.Should().NotContain(fixture.ModelName);

        (string Category, string Message)[] controllerWarnings = warningProvider.Warnings
            .Where(warning => warning.Category.EndsWith("OctoPrintCompatController", StringComparison.Ordinal))
            .ToArray();
        controllerWarnings.Should().Contain(
            warning => warning.Message.Contains("ReasonCode=printer_access_denied", StringComparison.Ordinal));
        controllerWarnings.Should().NotContain(
            warning => warning.Message.Contains(fixture.RestrictedPrinterId.ToString(), StringComparison.Ordinal) ||
                warning.Message.Contains(fixture.ModelName, StringComparison.Ordinal) ||
                warning.Message.Contains("micron-orca-upload.gcode", StringComparison.Ordinal) ||
                warning.Message.Contains("G28", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_OrcaUploadAndPrintWithUnknownModel_ReportsModelMismatch()
    {
        await SetApiKeyRequirementAsync(false);
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        string gcode = OrcaGcodeContent($"{fixture.ModelName} unknown", nozzleDiameter: 0.4, material: "PLA");
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent form = CreateOrcaUploadForm(gcode);
        using HttpResponseMessage response = await client.PostAsync("/api/files/local", form);

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using JsonDocument responseBody = JsonDocument.Parse(body);
        AssertGenericQueueAdmissionFailure(responseBody.RootElement);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GcodeFiles.CountAsync()).Should().Be(1);
        (await db.PrintJobs.CountAsync()).Should().Be(0);
    }

    private static MultipartFormDataContent CreateOrcaUploadForm(string gcode)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(gcode));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "micron-orca-upload.gcode");
        form.Add(new StringContent("true"), "print");
        form.Add(new StringContent(""), "path");
        form.Add(new StringContent("1"), "plateindex");
        return form;
    }

    private static void AssertGenericQueueAdmissionFailure(JsonElement responseBody)
    {
        responseBody.GetProperty("status").GetString().Should().Be("UploadedOnly");
        responseBody.GetProperty("reasonCode").GetString().Should().Be("queue_admission_failed");
        responseBody.GetProperty("message").GetString()
            .Should().Be("File uploaded, but the print request could not be queued. No print job was created.");
        responseBody.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["file", "status", "reasonCode", "message"]);
        responseBody.GetProperty("file").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["id", "fileName"]);
    }

    private sealed class CapturingWarningLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, string Message)> _warnings = new();
        private volatile bool _disposed;

        public IReadOnlyCollection<(string Category, string Message)> Warnings => _warnings.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingWarningLogger(categoryName, _warnings, this);

        public void Dispose()
        {
            _disposed = true;
        }

        private bool IsEnabled(LogLevel logLevel) => !_disposed && logLevel >= LogLevel.Warning;

        private sealed class CapturingWarningLogger(
            string categoryName,
            ConcurrentQueue<(string Category, string Message)> warnings,
            CapturingWarningLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    warnings.Enqueue((categoryName, formatter(state, exception)));
                }
            }
        }
    }

    private static string OrcaGcodeContent(string printerModel, double nozzleDiameter, string material) =>
        $"; generated by OrcaSlicer 2.3.1\n; printer_model = {printerModel}\n; nozzle_diameter = {nozzleDiameter.ToString("F1", CultureInfo.InvariantCulture)}\n; filament_type = {material}\n; filament used [g] = 50000\nG28\nG1 X0 Y0 Z0.2 F1200\n";

    private async Task SetApiKeyRequirementAsync(bool required)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISettingsService>()
            .Save(new OctoPrintSettings { RequireApiKey = required });
    }

    private static string MinimalGcodeContent() =>
        "; generated by OctoPrintUploadAuthorizationTests\nG28\nG1 X0 Y0 Z0.2 F1200\n";

    private async Task<RestrictedPrinterFixture> SeedRestrictedPrinterFixtureAsync(
        bool grantCallerAccess = false,
        bool printerAvailable = true)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;

        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"OctoPrint auth maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Micron upload test model {Guid.NewGuid():N}",
        };
        const string orcaModelName = "Micron 180";
        var modelAlias = new PrinterModelAlias
        {
            PrinterModelId = model.Id,
            SlicerModelName = orcaModelName,
            SlicerType = "OrcaSlicer",
        };
        var restrictedGroup = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"OctoPrint auth restricted {Guid.NewGuid():N}",
        };
        var restrictedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Micron upload test printer {Guid.NewGuid():N}",
            ServerUrl = $"http://octoprint-auth-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = restrictedGroup.Id,
            IsEnabled = true,
            IsAvailable = printerAvailable,
        };
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = restrictedPrinter.Id,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
            SupportedMaterials = ["PLA"],
        };

        // A role distinct from the caller's, granted Submit access to the restricted group.
        // Because PrinterGroupAccess rows exist for this group, CanUserAccessGroupAsync denies
        // access to any role NOT listed here (see QueueResourceAuthorizationService) — an empty
        // rule set would instead default-allow everyone, which would defeat this fixture.
        var otherRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"octoprint-auth-other-{Guid.NewGuid():N}",
            DisplayName = "OctoPrint auth other role",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var callerRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"octoprint-auth-caller-{Guid.NewGuid():N}",
            DisplayName = "OctoPrint auth caller role",
            CreatedAt = now,
            UpdatedAt = now,
        };

        Resource queueResource = await GetOrCreateResourceAsync(db, "queue");
        UserAction writeAction = await GetOrCreateActionAsync(db, "write");

        var callerUser = new User
        {
            Id = Guid.NewGuid(),
            Username = $"octoprint-auth-caller-{Guid.NewGuid():N}",
            Email = $"octoprint-auth-caller-{Guid.NewGuid():N}@example.test",
            PasswordHash = "unused",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.PrinterModelAliases.Add(modelAlias);
        db.PrinterGroups.Add(restrictedGroup);
        db.Printers.Add(restrictedPrinter);
        db.Toolheads.Add(toolhead);
        db.Roles.AddRange(otherRole, callerRole);
        db.Users.Add(callerUser);
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = restrictedGroup.Id,
            RoleId = grantCallerAccess ? callerRole.Id : otherRole.Id,
            AccessLevel = PrinterGroupAccessLevel.Submit,
        });
        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = callerRole.Id,
            ResourceId = queueResource.Id,
            ActionId = writeAction.Id,
            Granted = true,
            CreatedAt = now,
        });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = callerUser.Id,
            RoleId = callerRole.Id,
            AssignedAt = now,
            IsActive = true,
        });

        string rawKey = $"octoprint-auth-key-{Guid.NewGuid():N}";
        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = callerUser.Id,
            Name = "octoprint-auth-test-key",
            KeyHash = ComputeSha256Hash(rawKey),
            Purpose = ApiKeyPurpose.OctoPrint,
            IsActive = true,
            CreatedAt = now,
        });

        await db.SaveChangesAsync();

        return new RestrictedPrinterFixture(restrictedPrinter.Id, rawKey, callerUser.Id, orcaModelName);
    }

    private static async Task<Resource> GetOrCreateResourceAsync(AppDbContext db, string name)
    {
        Resource? existing = await db.Resources.FirstOrDefaultAsync(r => r.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var resource = new Resource
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            ResourceType = "system",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Resources.Add(resource);
        await db.SaveChangesAsync();
        return resource;
    }

    private static async Task<UserAction> GetOrCreateActionAsync(AppDbContext db, string name)
    {
        UserAction? existing = await db.UserActions.FirstOrDefaultAsync(a => a.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var action = new UserAction
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.UserActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    private sealed record RestrictedPrinterFixture(Guid RestrictedPrinterId, string RawApiKey, Guid UserId, string ModelName);
}
