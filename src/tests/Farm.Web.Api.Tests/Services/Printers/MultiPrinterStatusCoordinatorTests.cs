using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

public class MultiPrinterStatusCoordinatorTests
{
    private static List<Printer> CreateTestPrinters(int count = 3)
    {
        var printers = new List<Printer>();
        for (int i = 0; i < count; i++)
        {
            printers.Add(new Printer
            {
                Id = Guid.NewGuid(),
                Name = $"Test Printer {i + 1}",
                ServerUrl = $"http://printer{i + 1}.local",
                ApiKey = $"key_{i + 1}",
                Backend = (int)PrinterBackend.Moonraker
            });
        }
        return printers;
    }

    #region ExecuteParallelAsync Tests

    [Fact]
    public async Task ExecuteParallelAsync_WithSuccessfulOperations_ReturnsAllResults()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(3);
        var resultMap = printers.ToDictionary(p => p.Id, p => $"Result_{p.Name}");

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            await Task.Delay(10, ct);
            return resultMap[p.Id];
        };

        Action<Printer, Exception> onError = (p, ex) => { };

        // Act
        string?[] actual = await coordinator.ExecuteParallelAsync(printers, operation, onError);

        // Assert
        actual.Should().HaveCount(3);
        actual.Should().AllSatisfy(r => r.Should().StartWith("Result_"));
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithEmptyPrinters_ReturnsEmptyArray()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act
        string?[] actual = await coordinator.ExecuteParallelAsync(Enumerable.Empty<Printer>(), operation, onError);

        // Assert
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithExceptionInOneOperation_CallsErrorHandlerAndReturnsNull()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(2);
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type
        var erroredPrinter = (Printer)null;
        var errorException = (Exception)null;
#pragma warning restore CS8600

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            if (p.Name.Contains("2"))
            {
                throw new InvalidOperationException("Test error");
            }
            await Task.Delay(10, ct);
            return "Success";
        };

        Action<Printer, Exception> onError = (p, ex) =>
        {
            erroredPrinter = p;
            errorException = ex;
        };

        // Act
        string?[] actual = await coordinator.ExecuteParallelAsync(printers, operation, onError);

        // Assert
        actual.Should().HaveCount(2);
        actual[0].Should().Be("Success");
        actual[1].Should().BeNull();
        erroredPrinter.Should().NotBeNull();
        erroredPrinter!.Name.Should().Contain("2");
        errorException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithNullPrinters_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelAsync(null!, operation, onError));
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithNullOperation_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters();

        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelAsync<string>(printers, null!, onError));
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithNullErrorHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters();

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelAsync(printers, operation, null!));
    }

    [Fact]
    public async Task ExecuteParallelAsync_WithTimeoutPerPrinter_ReturnsNullForSlowOperations()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(2);
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type
        var timedOutPrinter = (Printer)null;
#pragma warning restore CS8600

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            if (p.Name.Contains("2"))
            {
                await Task.Delay(2000, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
            return "result";
        };

        Action<Printer, Exception> onError = (p, ex) =>
        {
            timedOutPrinter = p;
        };

        // Act
        string?[] actual = await coordinator.ExecuteParallelAsync(printers, operation, onError, CancellationToken.None);

        // Assert
        actual.Should().HaveCount(2);
        actual[0].Should().Be("result");
        actual[1].Should().Be("result");
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithSuccessfulOperations_ReturnsAllResults()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(2);
        var resultMap = printers.ToDictionary(p => p.Id, p => $"Result_{p.Name}");

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            await Task.Delay(10, ct);
            return resultMap[p.Id];
        };

        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act
        string?[] actual = await coordinator.ExecuteParallelWithTimeoutAsync(
            printers, operation, TimeSpan.FromSeconds(5), onTimeout, onError);

        // Assert
        actual.Should().HaveCount(2);
        actual.Should().AllSatisfy(r => r.Should().StartWith("Result_"));
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithTimeout_CallsTimeoutHandlerAndReturnsNull()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(2);
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type
        var timedOutPrinter = (Printer)null;
#pragma warning restore CS8600

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            if (p.Name.Contains("2"))
            {
                // Use a long delay to ensure it always exceeds the 100ms timeout
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
            return "result";
        };

        Action<Printer> onTimeout = p =>
        {
            timedOutPrinter = p;
        };

        Action<Printer, Exception> onError = (p, ex) => { };

        // Act
        string?[] actual = await coordinator.ExecuteParallelWithTimeoutAsync(
            printers, operation, TimeSpan.FromMilliseconds(500), onTimeout, onError);

        // Assert
        actual.Should().HaveCount(2);
        actual[0].Should().Be("result");
        actual[1].Should().BeNull();
        timedOutPrinter.Should().NotBeNull();
        timedOutPrinter!.Name.Should().Contain("2");
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithException_CallsErrorHandler()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(1);
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type
        var erroredPrinter = (Printer)null;
#pragma warning restore CS8600

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            await Task.Delay(10, ct);
            throw new InvalidOperationException("Test error");
        };

        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) =>
        {
            erroredPrinter = p;
        };

        // Act
        string?[] actual = await coordinator.ExecuteParallelWithTimeoutAsync(
            printers, operation, TimeSpan.FromSeconds(5), onTimeout, onError);

        // Assert
        actual.Should().HaveCount(1);
        actual[0].Should().BeNull();
        erroredPrinter.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithEmptyPrinters_ReturnsEmptyArray()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act
        string?[] actual = await coordinator.ExecuteParallelWithTimeoutAsync(
            Enumerable.Empty<Printer>(), operation, TimeSpan.FromSeconds(5), onTimeout, onError);

        // Assert
        actual.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithNullPrinters_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelWithTimeoutAsync(null!, operation, TimeSpan.FromSeconds(5), onTimeout, onError));
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithNullOperation_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters();

        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelWithTimeoutAsync<string>(printers, null!, TimeSpan.FromSeconds(5), onTimeout, onError));
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithNullTimeoutHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters();

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelWithTimeoutAsync(printers, operation, TimeSpan.FromSeconds(5), null!, onError));
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithNullErrorHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters();

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) => "result";
        Action<Printer> onTimeout = p => { };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            coordinator.ExecuteParallelWithTimeoutAsync(printers, operation, TimeSpan.FromSeconds(5), onTimeout, null!));
    }

    [Fact]
    public async Task ExecuteParallelWithTimeoutAsync_WithCancellation_PropagateCancellation()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<MultiPrinterStatusCoordinator>>();
        var coordinator = new MultiPrinterStatusCoordinator(loggerMock.Object);
        List<Printer> printers = CreateTestPrinters(1);
        var cts = new CancellationTokenSource();

        Func<Printer, CancellationToken, Task<string>> operation = async (p, ct) =>
        {
            await Task.Delay(1000, ct);
            return "result";
        };

        Action<Printer> onTimeout = p => { };
        Action<Printer, Exception> onError = (p, ex) => { };

        // Act & Assert
        cts.Cancel();
        // TaskCanceledException is thrown, which is a subclass of OperationCanceledException
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            coordinator.ExecuteParallelWithTimeoutAsync(printers, operation, TimeSpan.FromSeconds(5), onTimeout, onError, cts.Token));
    }

    #endregion
}
