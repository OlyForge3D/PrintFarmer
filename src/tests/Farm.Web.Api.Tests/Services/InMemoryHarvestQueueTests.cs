using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Telemetry;
using FluentAssertions;
using Moq;

namespace Farm.Web.Api.Tests.Services;

public class InMemoryHarvestQueueTests : IDisposable
{
    private readonly Mock<IUnifiedLoggingService> _mockLogger;
    private InMemoryHarvestQueue? _queue;

    public InMemoryHarvestQueueTests()
    {
        _mockLogger = new Mock<IUnifiedLoggingService>();
        _queue = new InMemoryHarvestQueue(_mockLogger.Object);
    }

    public void Dispose()
    {
        try
        {
            _queue?.Dispose();
        }
        catch
        {
            // Channel may already be closed, ignore
        }
    }

    private HarvestFileJob CreateTestJob(string fileName = "test.gcode")
    {
        return new HarvestFileJob
        {
            OperationId = Guid.NewGuid(),
            FileName = fileName,
            FilePath = $"/path/to/{fileName}",
            FileSize = 1024,
            PrinterId = Guid.NewGuid(),
            ServerUrl = "http://localhost"
        };
    }

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Action act = () => new InMemoryHarvestQueue(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void QueueDepth_When_Not_Disposed_Returns_Zero()
    {
        _queue!.QueueDepth.Should().Be(0);
    }

    [Fact]
    public void QueueDepth_When_Disposed_Returns_Zero()
    {
        _queue!.Dispose();

        _queue.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_WithValidJob_Succeeds()
    {
        var job = CreateTestJob();

        Func<Task> act = () => _queue!.EnqueueAsync(job);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnqueueAsync_WithNullJob_Throws()
    {
        Func<Task> act = () => _queue!.EnqueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EnqueueAsync_When_Disposed_Throws()
    {
        _queue!.Dispose();
        var job = CreateTestJob();

        Func<Task> act = () => _queue.EnqueueAsync(job);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DequeueAsync_WithNoJobs_Returns_Empty()
    {
        _queue!.CompleteAdding();

        var jobs = new List<HarvestFileJob>();
        await foreach (var job in _queue.DequeueAsync())
        {
            jobs.Add(job);
        }

        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task DequeueAsync_With_Single_Job_Returns_That_Job()
    {
        var job = CreateTestJob();

        await _queue!.EnqueueAsync(job);
        _queue.CompleteAdding();

        var jobs = new List<HarvestFileJob>();
        await foreach (var dequeuedJob in _queue.DequeueAsync())
        {
            jobs.Add(dequeuedJob);
        }

        jobs.Should().HaveCount(1);
        jobs[0].FileName.Should().Be(job.FileName);
    }

    [Fact]
    public async Task DequeueAsync_With_Multiple_Jobs_Returns_All()
    {
        var job1 = CreateTestJob("test1.gcode");
        var job2 = CreateTestJob("test2.gcode");

        await _queue!.EnqueueAsync(job1);
        await _queue.EnqueueAsync(job2);
        _queue.CompleteAdding();

        var jobs = new List<HarvestFileJob>();
        await foreach (var job in _queue.DequeueAsync())
        {
            jobs.Add(job);
        }

        jobs.Should().HaveCount(2);
        jobs[0].FileName.Should().Be("test1.gcode");
        jobs[1].FileName.Should().Be("test2.gcode");
    }

    [Fact]
    public async Task CompleteAdding_Signals_No_More_Jobs()
    {
        var job = CreateTestJob();

        await _queue!.EnqueueAsync(job);
        _queue.CompleteAdding();

        Func<Task> act = async () =>
        {
            await _queue.EnqueueAsync(job);
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void CompleteAdding_When_Already_Completed_Throws()
    {
        _queue!.CompleteAdding();

        // When channel is already completed, calling Complete again throws
        Action act = () => _queue.CompleteAdding();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompleteAdding_When_Disposed_Does_Nothing()
    {
        _queue!.Dispose();

        Action act = () => _queue.CompleteAdding();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DequeueAsync_When_Disposed_Yields_No_Items()
    {
        var job = CreateTestJob();

        await _queue!.EnqueueAsync(job);
        _queue.Dispose();

        var jobs = new List<HarvestFileJob>();
        await foreach (var dequeuedJob in _queue.DequeueAsync())
        {
            jobs.Add(dequeuedJob);
        }

        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispose_Can_Be_Called_Multiple_Times()
    {
        Action act = () =>
        {
            _queue!.Dispose();
            _queue.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task EnqueueAsync_Logs_Debug_Message()
    {
        var job = CreateTestJob();

        await _queue!.EnqueueAsync(job);

        _mockLogger.Verify(
            x => x.LogDebug(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce()
        );
    }

    [Fact]
    public async Task CompleteAdding_Logs_Information()
    {
        _queue!.CompleteAdding();

        _mockLogger.Verify(
            x => x.LogInformation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce()
        );
    }

    [Fact]
    public async Task Constructor_Logs_Initialization()
    {
        _mockLogger.Verify(
            x => x.LogInformation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce()
        );
    }

    [Fact]
    public async Task DequeueAsync_Preserves_Job_Properties()
    {
        var printerId = Guid.NewGuid();

        var job = new HarvestFileJob
        {
            OperationId = Guid.NewGuid(),
            FileName = "detailed-test.gcode",
            FilePath = "/path/to/detailed-test.gcode",
            FileSize = 4096,
            PrinterId = printerId,
            ServerUrl = "http://printer.local"
        };

        await _queue!.EnqueueAsync(job);
        _queue.CompleteAdding();

        var jobs = new List<HarvestFileJob>();
        await foreach (var dequeuedJob in _queue.DequeueAsync())
        {
            jobs.Add(dequeuedJob);
        }

        jobs.Should().HaveCount(1);
        jobs[0].FileName.Should().Be("detailed-test.gcode");
        jobs[0].FileSize.Should().Be(4096);
        jobs[0].PrinterId.Should().Be(printerId);
        jobs[0].ServerUrl.Should().Be("http://printer.local");
    }

    [Fact]
    public async Task EnqueueAsync_Multiple_Sequential_Jobs()
    {
        var jobs = new[]
        {
            CreateTestJob("file1.gcode"),
            CreateTestJob("file2.gcode"),
            CreateTestJob("file3.gcode")
        };

        foreach (var job in jobs)
        {
            await _queue!.EnqueueAsync(job);
        }

        _queue!.CompleteAdding();

        var dequeuedJobs = new List<HarvestFileJob>();
        await foreach (var job in _queue.DequeueAsync())
        {
            dequeuedJobs.Add(job);
        }

        dequeuedJobs.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
        {
            dequeuedJobs[i].FileName.Should().Be($"file{i + 1}.gcode");
        }
    }

    [Fact]
    public async Task Concurrent_Enqueue_Operations()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _queue!.EnqueueAsync(CreateTestJob($"test{i}.gcode")))
            .ToList();

        await Task.WhenAll(tasks);
        _queue!.CompleteAdding();

        var jobs = new List<HarvestFileJob>();
        await foreach (var job in _queue.DequeueAsync())
        {
            jobs.Add(job);
        }

        jobs.Should().HaveCount(10);
    }
}
