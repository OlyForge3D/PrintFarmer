using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Projects;
using Farm.Infrastructure.Services.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Services.Projects;

public sealed class PrintProjectServiceProgressProjectionTests
{
    private readonly ITestOutputHelper _output;

    public PrintProjectServiceProgressProjectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetProjectsAsync_PreservesProgressAndFiltersWithoutMaterializingFiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var capture = new ProjectListCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(capture)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        DateTime createdAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/project-progress",
            FolderType = "gcode"
        };
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "progress.gcode",
            FileName = "progress.gcode",
            FolderId = folder.Id,
            Folder = folder,
            FilePath = "/project-progress/progress.gcode",
            FileSizeBytes = 1,
            FileHash = new string('a', 64),
            UploadedAt = createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        var emptyProject = CreateProject("Empty project", PrintProjectStatus.Open, createdAt.AddDays(4));
        var partialProject = CreateProject("Partial project", PrintProjectStatus.InProgress, createdAt.AddDays(3));
        var completedProject = CreateProject("Completed project", PrintProjectStatus.Completed, createdAt.AddDays(2));
        var cancelledProject = CreateProject("Cancelled project", PrintProjectStatus.Cancelled, createdAt.AddDays(1));
        var deletedProject = CreateProject("Deleted project", PrintProjectStatus.Open, createdAt.AddDays(5));

        db.Set<FolderNode>().Add(folder);
        db.GcodeFiles.Add(gcodeFile);
        db.PrintProjects.AddRange(emptyProject, partialProject, completedProject, cancelledProject, deletedProject);
        db.PrintProjectFiles.AddRange(
            CreateProjectFile(partialProject, gcodeFile.Id, createdAt, 0, 3, 1),
            CreateProjectFile(partialProject, gcodeFile.Id, createdAt, 1, 2, 2),
            CreateProjectFile(partialProject, gcodeFile.Id, createdAt, 2, 4, 5),
            CreateProjectFile(completedProject, gcodeFile.Id, createdAt, 0, 2, 2),
            CreateProjectFile(completedProject, gcodeFile.Id, createdAt, 1, 1, 1),
            CreateProjectFile(cancelledProject, gcodeFile.Id, createdAt, 0, 5, 2));
        await db.SaveChangesAsync();

        var service = new PrintProjectService(
            db,
            NullLogger<PrintProjectService>.Instance,
            new Mock<IJobQueueService>().Object,
            new Mock<ISpoolmanService>().Object,
            new Mock<IPrinterGroupService>().Object);

        Assert.True(await service.DeleteProjectAsync(deletedProject.Id));
        db.ChangeTracker.Clear();
        capture.Reset();

        IReadOnlyList<PrintProjectListDto> projects = await service.GetProjectsAsync();

        Assert.Equal(
            new[] { emptyProject.Id, partialProject.Id, completedProject.Id, cancelledProject.Id },
            projects.Select(project => project.Id));
        Assert.DoesNotContain(projects, project => project.Id == deletedProject.Id);

        PrintProjectListDto empty = Assert.Single(projects, project => project.Id == emptyProject.Id);
        Assert.Equal(0, empty.TotalFiles);
        Assert.Equal(0, empty.CompletedFiles);
        Assert.Equal(0, empty.TotalPrints);
        Assert.Equal(0, empty.CompletedPrints);
        Assert.Equal(0, empty.ProgressPercent);
        Assert.Null(empty.EstimatedTotalCost);
        Assert.Null(empty.CompletedCost);

        PrintProjectListDto partial = Assert.Single(projects, project => project.Id == partialProject.Id);
        Assert.Equal(3, partial.TotalFiles);
        Assert.Equal(2, partial.CompletedFiles);
        Assert.Equal(9, partial.TotalPrints);
        Assert.Equal(8, partial.CompletedPrints);

        PrintProjectListDto completed = Assert.Single(projects, project => project.Id == completedProject.Id);
        Assert.Equal(2, completed.TotalFiles);
        Assert.Equal(2, completed.CompletedFiles);
        Assert.Equal(3, completed.TotalPrints);
        Assert.Equal(3, completed.CompletedPrints);

        PrintProjectListDto cancelled = Assert.Single(projects, project => project.Id == cancelledProject.Id);
        Assert.Equal(1, cancelled.TotalFiles);
        Assert.Equal(0, cancelled.CompletedFiles);
        Assert.Equal(5, cancelled.TotalPrints);
        Assert.Equal(2, cancelled.CompletedPrints);

        PrintProjectListDto searchResult = Assert.Single(await service.GetProjectsAsync(search: "Partial"));
        Assert.Equal(partialProject.Id, searchResult.Id);
        PrintProjectListDto statusResult = Assert.Single(await service.GetProjectsAsync(PrintProjectStatus.Cancelled));
        Assert.Equal(cancelledProject.Id, statusResult.Id);
        PrintProjectListDto combinedResult = Assert.Single(
            await service.GetProjectsAsync(PrintProjectStatus.Completed, "Completed"));
        Assert.Equal(completedProject.Id, combinedResult.Id);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetProjectsAsync(ct: cancellation.Token));

        db.ChangeTracker.Clear();
        capture.Reset();
        _ = await service.GetProjectsAsync();

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(0, capture.MaterializedProjectFiles);
        string sql = Assert.Single(capture.Commands);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Notes", sql, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Generated SQLite SQL:{Environment.NewLine}{sql}");

        await using SqliteCommand explain = connection.CreateCommand();
        explain.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        await using SqliteDataReader plan = await explain.ExecuteReaderAsync();
        while (await plan.ReadAsync())
        {
            _output.WriteLine($"SQLite plan: {plan.GetString(3)}");
        }

        PrintProject totalOverflow = CreateProject("Total overflow", PrintProjectStatus.Open, createdAt);
        PrintProject completedOverflow = CreateProject("Completed overflow", PrintProjectStatus.Open, createdAt);
        db.PrintProjects.AddRange(totalOverflow, completedOverflow);
        db.PrintProjectFiles.AddRange(
            CreateProjectFile(totalOverflow, gcodeFile.Id, createdAt, 0, int.MaxValue, 0),
            CreateProjectFile(totalOverflow, gcodeFile.Id, createdAt, 1, 1, 0),
            CreateProjectFile(completedOverflow, gcodeFile.Id, createdAt, 0, 1, int.MaxValue),
            CreateProjectFile(completedOverflow, gcodeFile.Id, createdAt, 1, 1, 1));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<OverflowException>(() => service.GetProjectsAsync(search: "Total overflow"));
        await Assert.ThrowsAsync<OverflowException>(() => service.GetProjectsAsync(search: "Completed overflow"));
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
        public List<string> Commands { get; } = [];

        public int MaterializedProjectFiles { get; private set; }

        public void Reset()
        {
            Commands.Clear();
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
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
