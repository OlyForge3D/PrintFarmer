using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Resilience;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers
{
    /// <summary>
    /// Tests for PrinterStatusFallbackService - timeout and fallback handling
    /// </summary>
    public class PrinterStatusFallbackServiceTests
    {
        private static Printer CreateTestPrinter()
        {
            return new Printer
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = "http://printer.local",
                ApiKey = "test_key",
                Backend = (int)PrinterBackend.Moonraker
            };
        }

        #region ExecuteWithFallbackAsync Tests

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithSuccessfulOperation_ReturnsOperationResult()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object expectedResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(10, ct);
                return expectedResult;
            };

            // Act
            object result = await service.ExecuteWithFallbackAsync(
                printer, operation, TimeSpan.FromSeconds(5), () => new object());

            // Assert
            result.Should().Be(expectedResult);
        }

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithTimeout_ReturnsFallback()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object fallbackResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(2000, ct);
                return new object();
            };

            // Act
            object result = await service.ExecuteWithFallbackAsync(
                printer, operation, TimeSpan.FromMilliseconds(100), () => fallbackResult);

            // Assert
            result.Should().Be(fallbackResult);
        }

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithException_ReturnsFallback()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object fallbackResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(10, ct);
                throw new InvalidOperationException("Test exception");
            };

            // Act
            object result = await service.ExecuteWithFallbackAsync(
                printer, operation, TimeSpan.FromSeconds(5), () => fallbackResult);

            // Assert
            result.Should().Be(fallbackResult);
        }

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);

            Func<CancellationToken, Task<object>> operation = async ct => new object();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithFallbackAsync(null!, operation, TimeSpan.FromSeconds(5), () => new object()));
        }

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithNullOperation_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithFallbackAsync(printer, null!, TimeSpan.FromSeconds(5), () => new object()));
        }

        [Fact]
        public async Task ExecuteWithFallbackAsync_WithNullFallback_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();

            Func<CancellationToken, Task<object>> operation = async ct => new object();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithFallbackAsync(printer, operation, TimeSpan.FromSeconds(5), null!));
        }

        #endregion

        #region ExecuteWithCircuitBreakerAsync Tests

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithSuccessfulOperation_ReturnsResult()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var circuitBreakerService = new CircuitBreakerService(NullLogger<CircuitBreakerService>.Instance);
            var service = new PrinterStatusFallbackService(circuitBreakerService, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object expectedResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(10, ct);
                return expectedResult;
            };

            // Act
            object result = await service.ExecuteWithCircuitBreakerAsync(
                printer, "test-key", operation, TimeSpan.FromSeconds(5), () => expectedResult);

            // Assert
            result.Should().Be(expectedResult);
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithTimeout_ReturnsFallback()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var circuitBreakerService = new CircuitBreakerService(NullLogger<CircuitBreakerService>.Instance);
            var service = new PrinterStatusFallbackService(circuitBreakerService, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object fallbackResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(2000, ct);
                return new object();
            };

            // Act
            object result = await service.ExecuteWithCircuitBreakerAsync(
                printer, "test-key", operation, TimeSpan.FromMilliseconds(100), () => fallbackResult);

            // Assert
            result.Should().Be(fallbackResult);
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithException_ReturnsFallback()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var circuitBreakerService = new CircuitBreakerService(NullLogger<CircuitBreakerService>.Instance);
            var service = new PrinterStatusFallbackService(circuitBreakerService, loggerMock.Object);
            Printer printer = CreateTestPrinter();
            object fallbackResult = new object();

            Func<CancellationToken, Task<object>> operation = async ct =>
            {
                await Task.Delay(10, ct);
                throw new InvalidOperationException("Test exception");
            };

            // Act
            object result = await service.ExecuteWithCircuitBreakerAsync(
                printer, "test-key", operation, TimeSpan.FromSeconds(5), () => fallbackResult);

            // Assert
            result.Should().Be(fallbackResult);
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);

            Func<CancellationToken, Task<object>> operation = async ct => new object();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithCircuitBreakerAsync(null!, "key", operation, TimeSpan.FromSeconds(5), () => new object()));
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithNullKey_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();

            Func<CancellationToken, Task<object>> operation = async ct => new object();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithCircuitBreakerAsync(printer, null!, operation, TimeSpan.FromSeconds(5), () => new object()));
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithNullOperation_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithCircuitBreakerAsync(printer, "key", null!, TimeSpan.FromSeconds(5), () => new object()));
        }

        [Fact]
        public async Task ExecuteWithCircuitBreakerAsync_WithNullFallback_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);
            Printer printer = CreateTestPrinter();

            Func<CancellationToken, Task<object>> operation = async ct => new object();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExecuteWithCircuitBreakerAsync(printer, "key", operation, TimeSpan.FromSeconds(5), null!));
        }

        #endregion

        #region CircuitBreakerState Tests

        [Fact]
        public void IsCircuitBreakerOpen_WithClosedCircuit_ReturnsFalse()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var circuitBreakerService = new CircuitBreakerService(NullLogger<CircuitBreakerService>.Instance);
            var service = new PrinterStatusFallbackService(circuitBreakerService, loggerMock.Object);

            // Act
            bool isOpen = service.IsCircuitBreakerOpen("test-key");

            // Assert
            isOpen.Should().BeFalse();
        }

        [Fact]
        public void IsCircuitBreakerOpen_WithNullKey_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => service.IsCircuitBreakerOpen(null!));
        }

        [Fact]
        public void GetCircuitBreakerState_WithNullKey_ThrowsArgumentNullException()
        {
            // Arrange
            var cbMock = new Mock<ICircuitBreakerService>();
            var loggerMock = new Mock<ILogger<PrinterStatusFallbackService>>();
            var service = new PrinterStatusFallbackService(cbMock.Object, loggerMock.Object);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => service.GetCircuitBreakerState(null!));
        }

        #endregion
    }
}
