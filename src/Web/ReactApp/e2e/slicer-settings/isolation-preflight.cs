#:property PublishAot=false
#:project ../../../../api/Farm.Web.Api.csproj
#:project ../../../../slicer/Farm.Slicer.Module.Api/Farm.Slicer.Module.Api.csproj
#:project ../../../../orcaslicer-worker/Farm.OrcaSlicer.Worker.csproj
#:project ../../../../moonraker-emulator/Farm.Moonraker.Emulator/Farm.Moonraker.Emulator.csproj

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Infrastructure.Temp;
using Farm.Web.Api.Services.StorageManagement;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length != 1)
{
    throw new InvalidOperationException("Supply the API content root; the private run root comes from the launcher");
}

string root = Path.GetFullPath(Environment.GetEnvironmentVariable("PF_SETTINGS_RUN_ROOT")
    ?? throw new InvalidOperationException("Missing private run root"));
using JsonDocument run = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "run.json")));
string? runId = Environment.GetEnvironmentVariable("PF_SETTINGS_RUN_ID");
if (string.IsNullOrWhiteSpace(runId) || run.RootElement.GetProperty("runId").GetString() != runId
    || run.RootElement.GetProperty("root").GetString() != root)
{
    throw new InvalidOperationException("The private run manifest does not match this launcher");
}

Directory.SetCurrentDirectory(root);
string contentRoot = Path.GetFullPath(args[0]);
Dictionary<string, string> paths = new(StringComparer.Ordinal);

// No application host is built or started. Resolve the same provider ordering
// as API Program.cs, including its higher-precedence PFARM__ provider.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = contentRoot,
    EnvironmentName = "Development",
    ApplicationName = typeof(DataProtectionStartup).Assembly.GetName().Name
});
builder.Configuration.AddEnvironmentVariables("PFARM__");
ConfigurationManager configuration = builder.Configuration;

CheckPath("workingDirectory", Directory.GetCurrentDirectory());
CheckPath("processTemp", Path.GetTempPath());
CheckPath("dataProtection", Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_PATH"));
CheckPath("configuredTemp", configuration["TempStorage:Path"]);
CheckPath("modelAudit", configuration["ModelStorage:Path"]);
CheckPath("gcodeAudit", configuration["GcodeStorage:Path"]);
CheckPath("calibrationBlobs", configuration["Calibration:BlobStorage:RootPath"]);
CheckPath("appData", Environment.GetEnvironmentVariable("APPDATA"));
CheckPath("localAppData", Environment.GetEnvironmentVariable("LOCALAPPDATA"));
CheckPath("xdgConfig", Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
CheckPath("xdgCache", Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
CheckPath("nugetPackages", Environment.GetEnvironmentVariable("NUGET_PACKAGES"));
CheckPath("nugetHttpCache", Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH"));
CheckPath("nugetScratch", Environment.GetEnvironmentVariable("NUGET_SCRATCH"));
CheckPath("nugetPlugins", Environment.GetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH"));

if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Default"))
    || string.IsNullOrWhiteSpace(configuration.GetConnectionString("SlicerDatabase")))
{
    throw new InvalidOperationException("Explicit private app and slicer database destinations are required");
}

CheckDatabase("appDatabase", DatabaseProviderConfiguration.FromConfiguration(configuration));
CheckDatabase("slicerDatabase", DatabaseProviderConfiguration.ForSlicerDatabase(configuration));

StoragePathService storage = new(new AspNetCorePathProvider(builder.Environment), configuration, NullLogger<StoragePathService>.Instance);
CheckPath("gcodeStorage", storage.GetGcodeStorageDirectory());
CheckPath("modelStorage", storage.GetModelUploadDirectory());
CheckPath("profileStorage", storage.GetSlicerProfilesDirectory());
CheckPath("snapshots", storage.GetSnapshotStorageDirectory());
CheckPath("thumbnails", storage.GetPrinterFileThumbnailDirectory());
ArtifactStorageSettings artifacts = configuration.GetSection(ArtifactStorageSettings.SectionName)
    .Get<ArtifactStorageSettings>() ?? throw new InvalidOperationException("Missing artifact storage configuration");
CheckPath("artifacts", Path.IsPathRooted(artifacts.RootPath) ? artifacts.RootPath : Path.Join(contentRoot, artifacts.RootPath));
CheckPath("localSlicerStorage", new LocalFileStorageOptions().BasePath);

foreach (string name in new[] { "ORCA_PROFILES_PATH", "ORCA_CUSTOM_PROFILES_PATH", "PROFILE_CACHE_PATH", "Worker__WorkingDirectory" })
{
    CheckPath(name, Environment.GetEnvironmentVariable(name));
}

// Constructors below may create directories, so all their inputs and fallback
// temp destinations have already passed the containment gate.
DefaultTempPathProvider apiTemp = new(configuration);
CheckPath("resolvedApiTemp", apiTemp.GetTempRoot());
ServiceCollection services = new();
services.AddSingleton(configuration);
services.AddLogging();
services.AddPrintFarmerDataProtection(builder.Environment, contentRoot);
services.AddSlicerApiServices(configuration);
using ServiceProvider provider = services.BuildServiceProvider();
CheckPath("resolvedSlicerTemp", provider.GetRequiredService<ISlicerTempPathProvider>().TempPath);
if (provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository is not FileSystemXmlRepository repository)
{
    throw new InvalidOperationException("DataProtection is not using the verified private filesystem repository");
}

CheckPath("resolvedDataProtection", repository.Directory.FullName);
string output = Path.Join(root, "isolation.json");
using (FileStream outputStream = File.Create(output))
using (Utf8JsonWriter writer = new(outputStream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteNumber("schema", 1);
    writer.WriteString("runId", runId);
    writer.WriteString("root", root);
    writer.WriteBoolean("hostStarted", false);
    writer.WriteString("dependencyCacheMode", "private-restore; existing cache used as a read-only local feed");
    writer.WriteString("readOnlyDependencyFeed", Environment.GetEnvironmentVariable("PF_SETTINGS_READONLY_NUGET_FEED"));
    writer.WriteStartObject("paths");
    foreach ((string name, string path) in paths)
    {
        writer.WriteString(name, path);
    }

    writer.WriteEndObject();
    writer.WriteEndObject();
}

Console.WriteLine($"Private configuration preflight passed: {output}");

void CheckDatabase(string name, DatabaseProviderConfiguration database)
{
    if (!database.IsSqlite)
    {
        throw new InvalidOperationException($"{name}: non-SQLite database is forbidden for this suite");
    }

    SqliteConnectionStringBuilder connection = new(database.ConnectionString);
    if (connection.Mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(connection.DataSource))
    {
        throw new InvalidOperationException($"{name}: an explicit private database file is required");
    }

    CheckPath(name, connection.DataSource);
}

void CheckPath(string name, string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{name}: missing explicit private destination");
    }

    string fullPath = Path.GetFullPath(value);
    string relative = Path.GetRelativePath(root, fullPath);
    if (Path.IsPathRooted(relative) || relative == ".."
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{name}: effective destination escapes the private run");
    }

    for (string? current = fullPath; current is not null && current != root; current = Path.GetDirectoryName(current))
    {
        if ((Directory.Exists(current) || File.Exists(current))
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{name}: reparse-point destination is not permitted");
        }
    }

    paths[name] = fullPath;
}
