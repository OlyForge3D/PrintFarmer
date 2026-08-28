using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class UnifiedFilesQueryServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _slicerConnection;
    private readonly SqliteConnection _appConnection;
    private readonly SlicerDbContext _slicerDb;
    private readonly AppDbContext _appDb;
    private readonly CountingCommandInterceptor _commandCounter = new();
    private readonly Mock<ITagRepository> _tagRepository;
    private readonly UnifiedFilesQueryService _service;
    private IReadOnlyCollection<Guid> _lastRequestedModelTagIds = [];

    public UnifiedFilesQueryServiceTests()
    {
        _slicerConnection = new SqliteConnection("Data Source=:memory:");
        _appConnection = new SqliteConnection("Data Source=:memory:");
        _slicerConnection.Open();
        _appConnection.Open();
        _slicerDb = new SlicerDbContext(
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(_slicerConnection)
                .AddInterceptors(_commandCounter)
                .Options);
        _appDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_appConnection)
                .AddInterceptors(_commandCounter)
                .Options);
        _slicerDb.Database.EnsureCreated();
        _appDb.Database.EnsureCreated();
        using (SqliteCommand command = _appConnection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF";
            _ = command.ExecuteNonQuery();
        }

        var fileOperations = new Mock<IStoredFileOperationsService>(MockBehavior.Strict);
        fileOperations
            .Setup(service => service.BuildModel3DFileUrl(It.IsAny<Guid>(), It.IsAny<ModelFileFormat>()))
            .Returns<Guid, ModelFileFormat>((id, _) => $"/api/3d-models/file/{id}");
        fileOperations
            .Setup(service => service.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/3d-models/thumbnail/{id}");
        fileOperations
            .Setup(service => service.BuildGcodeFileUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/gcode-files/file/{id}");
        fileOperations
            .Setup(service => service.BuildGcodeThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(id => $"/api/gcode-files/thumbnail/{id}");

        _tagRepository = new Mock<ITagRepository>(MockBehavior.Strict);
        _tagRepository
            .Setup(repository => repository.GetTagsByObjectsAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(),
                "Model3D",
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<Guid>, string, CancellationToken>(
                (ids, _, _) => _lastRequestedModelTagIds = ids)
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<Tag>>());

        _service = new UnifiedFilesQueryService(
            _slicerDb,
            _appDb,
            fileOperations.Object,
            _tagRepository.Object);
        _commandCounter.Reset();
    }

    [Fact]
    public async Task QueryAsync_MixedSourcesOnSecondPage_ReturnsExactGlobalRanks()
    {
        const int totalItems = 150;
        for (int rank = 1; rank <= totalItems; rank++)
        {
            string extension = rank % 2 == 0 ? "gcode" : "stl";
            string name = $"item-{rank:D4}.{extension}";
            if (rank % 2 == 0)
            {
                _appDb.GcodeFiles.Add(CreateGcode(name, rank));
            }
            else
            {
                _slicerDb.Models3D.Add(CreateModel(name, rank));
            }
        }

        await SaveChangesAsync();

        UnifiedFilesQueryResponse response = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Page = 2,
                PageSize = 50,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = UnifiedFileSortOrder.Asc,
            },
            CancellationToken.None);

        response.TotalItems.Should().Be(totalItems);
        response.Page.Should().Be(2);
        response.Items.Select(item => item.Name).Should().Equal(
            Enumerable.Range(51, 50).Select(rank =>
                $"item-{rank:D4}.{(rank % 2 == 0 ? "gcode" : "stl")}"));
        response.Items.Select(item => item.Source).Should().ContainInOrder(
            UnifiedFileSource.Model,
            UnifiedFileSource.Gcode);
    }

    [Fact]
    public async Task QueryAsync_MoreThanOneThousandPerSource_ReturnsTrueTotals()
    {
        const int itemsPerSource = 1001;
        _slicerDb.Models3D.AddRange(
            Enumerable.Range(1, itemsPerSource).Select(index => CreateModel($"item-{(index * 2) - 1:D4}.stl", 1)));
        _appDb.GcodeFiles.AddRange(
            Enumerable.Range(1, itemsPerSource).Select(index => CreateGcode($"item-{index * 2:D4}.gcode", 1)));
        await SaveChangesAsync();
        _commandCounter.Reset();

        UnifiedFilesQueryResponse response = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Page = 41,
                PageSize = 25,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = UnifiedFileSortOrder.Asc,
            },
            CancellationToken.None);

        response.Items.Should().HaveCount(25);
        response.TotalItems.Should().Be(itemsPerSource * 2);
        response.TotalSize.Should().Be(itemsPerSource * 2);
        response.TotalPages.Should().Be(81);
        response.Items.Select(item => item.Name).Should().Equal(
            Enumerable.Range(1001, 25).Select(rank =>
                $"item-{rank:D4}.{(rank % 2 == 0 ? "gcode" : "stl")}"));
        _lastRequestedModelTagIds.Should().HaveCountLessThanOrEqualTo(13);
        _commandCounter.ReaderCount.Should().BeLessThan(40);
    }

    [Fact]
    public async Task QueryAsync_SearchTypeAndPrinterFilters_AppliesBeforeTotalsAndPaging()
    {
        Guid requestedPrinterId = Guid.NewGuid();
        _slicerDb.Models3D.AddRange(
            CreateModel("target-part.obj", 10),
            CreateModel("target-part.stl", 20),
            CreateModel("ignored-part.obj", 30));
        _appDb.GcodeFiles.AddRange(
            CreateGcode("target-print.bin", 40, requestedPrinterId),
            CreateGcode("target-print.gcode", 50, requestedPrinterId),
            CreateGcode("target-binary.bgcode", 55, requestedPrinterId),
            CreateGcode("target-other.gcode", 60, Guid.NewGuid()));
        await SaveChangesAsync();

        UnifiedFilesQueryResponse otherResponse = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Search = "TARGET",
                Filter = UnifiedFileTypeFilter.Other,
                PrinterId = requestedPrinterId,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = UnifiedFileSortOrder.Asc,
            },
            CancellationToken.None);
        UnifiedFilesQueryResponse gcodeResponse = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Search = "target",
                Filter = UnifiedFileTypeFilter.Gcode,
                PrinterId = requestedPrinterId,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = UnifiedFileSortOrder.Asc,
            },
            CancellationToken.None);

        otherResponse.Items.Select(item => item.Name).Should().Equal("target-part.obj", "target-print.bin");
        otherResponse.TotalItems.Should().Be(2);
        otherResponse.TotalSize.Should().Be(50);
        gcodeResponse.Items.Select(item => item.Name).Should().Equal(
            "target-binary.bgcode",
            "target-print.gcode");
        gcodeResponse.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task QueryAsync_BinaryNameOrdering_PreservesMixedCasePageBoundary()
    {
        _slicerDb.Models3D.AddRange(
            CreateModel("Zulu.stl", 1),
            CreateModel("Éclair.stl", 1));
        _appDb.GcodeFiles.AddRange(
            CreateGcode("alpha.gcode", 1),
            CreateGcode("beta.gcode", 1));
        await SaveChangesAsync();

        UnifiedFilesQueryResponse response = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Page = 2,
                PageSize = 2,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = UnifiedFileSortOrder.Asc,
            },
            CancellationToken.None);

        response.Items.Select(item => item.Name).Should().Equal("beta.gcode", "Éclair.stl");
    }

    [Theory]
    [InlineData(UnifiedFileSortOrder.Asc, "😀.gcode")]
    [InlineData(UnifiedFileSortOrder.Desc, "\uE000.stl")]
    public async Task QueryAsync_BinaryNameOrdering_MatchesUnicodeScalarPageBoundary(
        UnifiedFileSortOrder sortOrder,
        string expectedSecondName)
    {
        _slicerDb.Models3D.Add(CreateModel("\uE000.stl", 1));
        _appDb.GcodeFiles.Add(CreateGcode("😀.gcode", 1));
        await SaveChangesAsync();

        UnifiedFilesQueryResponse response = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Page = 2,
                PageSize = 1,
                SortBy = UnifiedFileSortBy.Name,
                SortOrder = sortOrder,
            },
            CancellationToken.None);

        response.Items.Should().ContainSingle()
            .Which.Name.Should().Be(expectedSecondName);
    }

    [Fact]
    public async Task QueryAsync_TaggedModel_PreservesTagsInUnifiedResponse()
    {
        Model3D model = CreateModel("tagged.stl", 1);
        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = "Functional",
            Category = "manual",
            Color = "#123456",
            Revision = 3,
            ConcurrencyToken = Guid.NewGuid(),
        };
        _slicerDb.Models3D.Add(model);
        await SaveChangesAsync();
        _tagRepository
            .Setup(repository => repository.GetTagsByObjectsAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(model.Id)),
                "Model3D",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<Tag>>
            {
                [model.Id] = [tag],
            });

        UnifiedFilesQueryResponse response = await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto
            {
                Filter = UnifiedFileTypeFilter.Models,
            },
            CancellationToken.None);

        response.Items.Should().ContainSingle();
        response.Items[0].Tags.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                tag.Id,
                tag.Name,
                tag.Category,
                tag.Color,
                tag.Revision,
                tag.ConcurrencyToken,
            });
    }

    [Fact]
    public async Task QueryAsync_CancelledRequest_StopsDatabaseWork()
    {
        _slicerDb.Models3D.Add(CreateModel("cancel.stl", 1));
        await _slicerDb.SaveChangesAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> act = async () => await _service.QueryAsync(
            new UnifiedFilesQueryRequestDto(),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void UnifiedFilesQueryResponse_JsonContract_IsCamelCaseWithStringEnums()
    {
        var response = new UnifiedFilesQueryResponse(
            [
                new UnifiedFileDto(
                    UnifiedFileSource.Model,
                    Guid.Empty,
                    "/",
                    "part.stl",
                    "stored.stl",
                    42,
                    "stl",
                    DateTime.UnixEpoch,
                    "/api/3d-models/file/0",
                    null),
            ],
            2002,
            42,
            2,
            50,
            41);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());

        string json = JsonSerializer.Serialize(response, options);

        json.Should().Contain("\"totalItems\":2002");
        json.Should().Contain("\"source\":\"Model\"");
        json.Should().NotContain("\"TotalItems\"");
    }

    public async ValueTask DisposeAsync()
    {
        await _slicerDb.DisposeAsync();
        await _appDb.DisposeAsync();
        await _slicerConnection.DisposeAsync();
        await _appConnection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task SaveChangesAsync()
    {
        await _slicerDb.SaveChangesAsync(CancellationToken.None);
        await _appDb.SaveChangesAsync(CancellationToken.None);
    }

    private static Model3D CreateModel(string name, long size)
    {
        DateTime now = DateTime.UtcNow;
        return new Model3D
        {
            Id = Guid.NewGuid(),
            Name = name,
            FileName = $"{Guid.NewGuid()}{Path.GetExtension(name)}",
            FilePath = "/models",
            FolderId = Guid.NewGuid(),
            FileSizeBytes = size,
            FileHash = Guid.NewGuid().ToString("N"),
            FileFormat = Path.GetExtension(name).Equals(".obj", StringComparison.OrdinalIgnoreCase)
                ? ModelFileFormat.OBJ
                : ModelFileFormat.STL,
            IsValid = true,
            UploadedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static GcodeFile CreateGcode(string name, long size, Guid? printerId = null)
    {
        DateTime now = DateTime.UtcNow;
        return new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = name,
            FileName = $"{Guid.NewGuid()}{Path.GetExtension(name)}",
            FilePath = "/gcode",
            FolderId = Guid.NewGuid(),
            FileSizeBytes = size,
            FileHash = Guid.NewGuid().ToString("N"),
            Source = GcodeSource.Upload,
            SourcePrinterId = printerId,
            UploadedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _readerCount;

        public int ReaderCount => _readerCount;

        public void Reset()
        {
            _readerCount = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _readerCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
