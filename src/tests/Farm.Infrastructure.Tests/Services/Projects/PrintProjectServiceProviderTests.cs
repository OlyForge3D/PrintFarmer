using System.Data.Common;
using System.Diagnostics;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Projects;
using Farm.Infrastructure.Services.Queue;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Services.Projects;

[Collection(ProviderDatabaseTestCollection.Name)]
public sealed class PrintProjectServiceProviderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("postgres", "PFARM_TEST_POSTGRES_CONN")]
    [InlineData("sqlserver", "PFARM_TEST_SQLSERVER_CONN")]
    [Trait("Category", "DbHeavy")]
    public async Task GetProjectsAsync_ProductionProvider_PreservesParityAndLogsPlan(
        string provider,
        string connectionVariable)
    {
        string? configuredConnection = Environment.GetEnvironmentVariable(connectionVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(configuredConnection),
            $"{provider} project-list verification did not run: set {connectionVariable}.");
        string isolatedConnection = CreateIsolatedConnection(provider, configuredConnection);
        var capture = new ProjectListCapture();
        DbContextOptions<AppDbContext> options = CreateOptions(provider, isolatedConnection, capture);

        await using var db = new AppDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            await SeedProjectsAsync(db);
            var service = new PrintProjectService(
                db,
                NullLogger<PrintProjectService>.Instance,
                new Mock<IJobQueueService>().Object,
                new Mock<ISpoolmanService>().Object,
                new Mock<IPrinterGroupService>().Object);

            IReadOnlyList<PrintProjectListDto> previousResults = await GetPreviousListResultsAsync(db);
            db.ChangeTracker.Clear();
            capture.Reset();
            IReadOnlyList<PrintProjectListDto> projectedResults = await service.GetProjectsAsync();

            Assert.Equal(previousResults, projectedResults);
            Assert.Equal(0, capture.MaterializedProjectFiles);
            string sql = Assert.IsType<string>(capture.ProjectListSql);
            Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
            output.WriteLine($"{provider} generated project-list SQL:{Environment.NewLine}{sql}");

            await WriteMeasuredProviderTimingsAsync(db, service, provider);
            capture.Reset();
            await WriteQueryPlanAsync(db, sql, provider);

            await SeedOverflowProjectsAsync(db);
            await Assert.ThrowsAsync<OverflowException>(
                () => service.GetProjectsAsync(search: "Total overflow"));
            await Assert.ThrowsAsync<OverflowException>(
                () => service.GetProjectsAsync(search: "Completed overflow"));
            Assert.Equal(0, capture.MaterializedProjectFiles);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(
        string provider,
        string connectionString,
        ProjectListCapture capture)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        switch (provider)
        {
            case "postgres":
                options.UseNpgsql(
                    connectionString,
                    providerOptions => providerOptions.MigrationsAssembly("Farm.Migrations.PostgreSQL"));
                break;
            case "sqlserver":
                options.UseSqlServer(
                    connectionString,
                    providerOptions => providerOptions.MigrationsAssembly("Farm.Migrations.SqlServer"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider.");
        }

        return options.AddInterceptors(capture).Options;
    }

    private static string CreateIsolatedConnection(string provider, string configuredConnection)
    {
        string databaseName = $"pfarm_project_list_{Guid.NewGuid():N}";
        return provider switch
        {
            "postgres" => new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName }.ConnectionString,
            "sqlserver" => new SqlConnectionStringBuilder(configuredConnection) { InitialCatalog = databaseName }.ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider.")
        };
    }

    private static async Task SeedProjectsAsync(AppDbContext db)
    {
        DateTime createdAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = $"/project-list-{Guid.NewGuid():N}",
            FolderType = "gcode"
        };
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "project-list.gcode",
            FileName = "project-list.gcode",
            FolderId = folder.Id,
            Folder = folder,
            FilePath = $"{folder.Path}/project-list.gcode",
            FileSizeBytes = 1,
            FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            UploadedAt = createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        var projects = new List<PrintProject>
        {
            CreateProject("Empty project", PrintProjectStatus.Open, createdAt.AddDays(4)),
            CreateProject("Partial project", PrintProjectStatus.InProgress, createdAt.AddDays(3)),
            CreateProject("Completed project", PrintProjectStatus.Completed, createdAt.AddDays(2)),
            CreateProject("Cancelled project", PrintProjectStatus.Cancelled, createdAt.AddDays(1))
        };
        var files = new List<PrintProjectFile>
        {
            CreateProjectFile(projects[1], gcodeFile.Id, createdAt, 0, 3, 1),
            CreateProjectFile(projects[1], gcodeFile.Id, createdAt, 1, 2, 2),
            CreateProjectFile(projects[1], gcodeFile.Id, createdAt, 2, 4, 5),
            CreateProjectFile(projects[2], gcodeFile.Id, createdAt, 0, 2, 2),
            CreateProjectFile(projects[2], gcodeFile.Id, createdAt, 1, 1, 1),
            CreateProjectFile(projects[3], gcodeFile.Id, createdAt, 0, 5, 2)
        };

        for (int projectIndex = 0; projectIndex < 100; projectIndex++)
        {
            PrintProject project = CreateProject(
                $"Benchmark project {projectIndex:D3}",
                PrintProjectStatus.Open,
                createdAt.AddMinutes(-projectIndex - 1));
            projects.Add(project);
            for (int fileIndex = 0; fileIndex < 20; fileIndex++)
            {
                files.Add(CreateProjectFile(project, gcodeFile.Id, createdAt, fileIndex, 2, fileIndex % 3));
            }
        }

        db.AddRange(folder, gcodeFile);
        db.PrintProjects.AddRange(projects);
        db.PrintProjectFiles.AddRange(files);
        await db.SaveChangesAsync();
    }

    private static async Task SeedOverflowProjectsAsync(AppDbContext db)
    {
        GcodeFile gcodeFile = await db.GcodeFiles.AsNoTracking().FirstAsync();
        DateTime createdAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PrintProject totalOverflow = CreateProject("Total overflow", PrintProjectStatus.Open, createdAt);
        PrintProject completedOverflow = CreateProject("Completed overflow", PrintProjectStatus.Open, createdAt);
        db.PrintProjects.AddRange(totalOverflow, completedOverflow);
        db.PrintProjectFiles.AddRange(
            CreateProjectFile(totalOverflow, gcodeFile.Id, createdAt, 0, int.MaxValue, 0),
            CreateProjectFile(totalOverflow, gcodeFile.Id, createdAt, 1, 1, 0),
            CreateProjectFile(completedOverflow, gcodeFile.Id, createdAt, 0, 1, int.MaxValue),
            CreateProjectFile(completedOverflow, gcodeFile.Id, createdAt, 1, 1, 1));
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<PrintProjectListDto>> GetPreviousListResultsAsync(AppDbContext db)
    {
        List<PrintProject> projects = await db.PrintProjects
            .Include(project => project.Files)
            .AsNoTracking()
            .OrderByDescending(project => project.CreatedAt)
            .ToListAsync();
        return projects.Select(project => new PrintProjectListDto(
            project.Id,
            project.Name,
            project.Description,
            project.Status,
            project.Priority,
            project.DueDate,
            project.Files.Count,
            project.Files.Count(file => file.PrintedCount >= file.PrintCount),
            project.Files.Sum(file => file.PrintCount),
            project.Files.Sum(file => file.PrintedCount),
            EstimatedTotalCost: null,
            CompletedCost: null,
            project.CreatedAt,
            project.CompletedAt)).ToList();
    }

    private async Task WriteMeasuredProviderTimingsAsync(
        AppDbContext db,
        PrintProjectService service,
        string provider)
    {
        _ = await GetPreviousListResultsAsync(db);
        _ = await service.GetProjectsAsync();
        double[] previousSamples = new double[5];
        double[] projectedSamples = new double[5];

        for (int sample = 0; sample < previousSamples.Length; sample++)
        {
            var timer = Stopwatch.StartNew();
            _ = await GetPreviousListResultsAsync(db);
            timer.Stop();
            previousSamples[sample] = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            _ = await service.GetProjectsAsync();
            timer.Stop();
            projectedSamples[sample] = timer.Elapsed.TotalMilliseconds;
        }

        output.WriteLine(
            $"{provider} list timings in the CI provider database (milliseconds, n=5; max is the sample maximum, not a production p95): " +
            $"previous p50={Percentile(previousSamples, 0.50):F2}, max={previousSamples.Max():F2}; " +
            $"projected p50={Percentile(projectedSamples, 0.50):F2}, max={projectedSamples.Max():F2}.");
    }

    private async Task WriteQueryPlanAsync(AppDbContext db, string sql, string provider)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            if (provider == "postgres")
            {
                await using DbCommand explain = db.Database.GetDbConnection().CreateCommand();
                explain.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {sql}";
                await using DbDataReader plan = await explain.ExecuteReaderAsync();
                while (await plan.ReadAsync())
                {
                    output.WriteLine($"PostgreSQL actual plan: {plan.GetString(0)}");
                }

                return;
            }

            await using (DbCommand enableStatistics = db.Database.GetDbConnection().CreateCommand())
            {
                enableStatistics.CommandText = "SET STATISTICS XML ON;";
                _ = await enableStatistics.ExecuteNonQueryAsync();
            }

            try
            {
                await using DbCommand query = db.Database.GetDbConnection().CreateCommand();
                query.CommandText = sql;
                await using DbDataReader plans = await query.ExecuteReaderAsync();
                do
                {
                    while (await plans.ReadAsync())
                    {
                        for (int column = 0; column < plans.FieldCount; column++)
                        {
                            if (!await plans.IsDBNullAsync(column))
                            {
                                output.WriteLine($"SQL Server actual plan: {plans.GetValue(column)}");
                            }
                        }
                    }
                }
                while (await plans.NextResultAsync());
            }
            finally
            {
                await using DbCommand disableStatistics = db.Database.GetDbConnection().CreateCommand();
                disableStatistics.CommandText = "SET STATISTICS XML OFF;";
                _ = await disableStatistics.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        Array.Sort(values);
        int index = Math.Max(0, (int)Math.Ceiling(values.Length * percentile) - 1);
        return values[index];
    }

    private static PrintProject CreateProject(string name, PrintProjectStatus status, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private static PrintProjectFile CreateProjectFile(
        PrintProject project,
        Guid gcodeFileId,
        DateTime createdAt,
        int plateIndex,
        int printCount,
        int printedCount) => new()
        {
            Id = Guid.NewGuid(),
            PrintProjectId = project.Id,
            GcodeFileId = gcodeFileId,
            PrintCount = printCount,
            PrintedCount = printedCount,
            PlateIndex = plateIndex,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private sealed class ProjectListCapture : DbCommandInterceptor, IMaterializationInterceptor
    {
        public string? ProjectListSql { get; private set; }

        public int MaterializedProjectFiles { get; private set; }

        public void Reset()
        {
            ProjectListSql = null;
            MaterializedProjectFiles = 0;
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is PrintProjectFile)
            {
                MaterializedProjectFiles++;
            }

            return entity;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PrintProjectFiles", StringComparison.Ordinal) &&
                command.CommandText.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            {
                ProjectListSql = command.CommandText;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
