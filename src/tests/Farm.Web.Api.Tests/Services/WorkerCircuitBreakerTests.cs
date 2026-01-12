using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Services.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class WorkerCircuitBreakerTests
{
    private readonly Mock<ILogger<WorkerCircuitBreakerService>> _loggerMock;
    private readonly Mock<IWorkerRepository> _workerRepoMock;
    private readonly CircuitBreakerSettings _settings;

    public WorkerCircuitBreakerTests()
    {
        _loggerMock = new Mock<ILogger<WorkerCircuitBreakerService>>();
        _workerRepoMock = new Mock<IWorkerRepository>();
        _settings = new CircuitBreakerSettings
        {
            FailureThreshold = 3,
            WindowSeconds = 60,
            CooldownSeconds = 30,
            SuccessThresholdToClose = 2
        };
    }

    [Fact]
    public async Task RecordJobFailure_OpensCircuit_WhenThresholdExceeded()
    {
        // Arrange
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(_settings));
        Guid workerId = Guid.NewGuid();

        // Act - record failures up to threshold
        for (int i = 0; i < _settings.FailureThreshold; i++)
        {
            await service.RecordJobFailureAsync(workerId, _workerRepoMock.Object);
        }

        // Assert
        Assert.Equal(CircuitState.Open, service.GetCircuitState(workerId));
        _workerRepoMock.Verify(r => r.DisableWorkerAsync(workerId, It.IsAny<string>()), Times.Once);
        _workerRepoMock.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task RecordJobFailure_DoesNotOpenCircuit_BelowThreshold()
    {
        // Arrange
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(_settings));
        Guid workerId = Guid.NewGuid();

        // Act - record failures below threshold
        for (int i = 0; i < _settings.FailureThreshold - 1; i++)
        {
            await service.RecordJobFailureAsync(workerId, _workerRepoMock.Object);
        }

        // Assert
        Assert.Equal(CircuitState.Closed, service.GetCircuitState(workerId));
        _workerRepoMock.Verify(r => r.DisableWorkerAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CheckCircuits_TransitionsToHalfOpen_AfterCooldown()
    {
        // Arrange
        CircuitBreakerSettings shortCooldown = new CircuitBreakerSettings
        {
            FailureThreshold = 2,
            WindowSeconds = 60,
            CooldownSeconds = 1, // 1 second cooldown for test
            SuccessThresholdToClose = 2
        };
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(shortCooldown));
        Guid workerId = Guid.NewGuid();

        // Act - open circuit
        for (int i = 0; i < shortCooldown.FailureThreshold; i++)
        {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
            service.RecordJobFailureAsync(workerId, _workerRepoMock.Object).Wait();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
        }
        Assert.Equal(CircuitState.Open, service.GetCircuitState(workerId));

        // Wait for cooldown
        Thread.Sleep(TimeSpan.FromSeconds(shortCooldown.CooldownSeconds + 0.5));

        // Check circuits (should transition to half-open)
        service.CheckCircuits();

        // Assert
        Assert.Equal(CircuitState.HalfOpen, service.GetCircuitState(workerId));
    }

    [Fact]
    public async Task RecordJobSuccess_ClosesCircuit_FromHalfOpen()
    {
        // Arrange
        CircuitBreakerSettings shortCooldown = new CircuitBreakerSettings
        {
            FailureThreshold = 2,
            WindowSeconds = 60,
            CooldownSeconds = 1,
            SuccessThresholdToClose = 2
        };
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(shortCooldown));
        Guid workerId = Guid.NewGuid();

        // Open circuit
        for (int i = 0; i < shortCooldown.FailureThreshold; i++)
        {
            await service.RecordJobFailureAsync(workerId, _workerRepoMock.Object);
        }
        Assert.Equal(CircuitState.Open, service.GetCircuitState(workerId));

        // Wait for cooldown and transition to half-open
        Thread.Sleep(TimeSpan.FromSeconds(shortCooldown.CooldownSeconds + 0.5));
        service.CheckCircuits();
        Assert.Equal(CircuitState.HalfOpen, service.GetCircuitState(workerId));

        // Act - record successes to close circuit
        for (int i = 0; i < shortCooldown.SuccessThresholdToClose; i++)
        {
            await service.RecordJobSuccessAsync(workerId, _workerRepoMock.Object);
        }

        // Assert
        Assert.Equal(CircuitState.Closed, service.GetCircuitState(workerId));
    }

    [Fact]
    public void ResetCircuit_ClearsState()
    {
        // Arrange
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(_settings));
        Guid workerId = Guid.NewGuid();

        // Open circuit
        for (int i = 0; i < _settings.FailureThreshold; i++)
        {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
            service.RecordJobFailureAsync(workerId, _workerRepoMock.Object).Wait();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
        }
        Assert.Equal(CircuitState.Open, service.GetCircuitState(workerId));

        // Act
        service.ResetCircuit(workerId);

        // Assert
        Assert.Equal(CircuitState.Closed, service.GetCircuitState(workerId));
    }

    [Fact]
    public async Task RecordJobFailure_IgnoresEmptyWorkerId()
    {
        // Arrange
        WorkerCircuitBreakerService service = new WorkerCircuitBreakerService(_loggerMock.Object, Options.Create(_settings));

        // Act
        await service.RecordJobFailureAsync(Guid.Empty, _workerRepoMock.Object);

        // Assert
        _workerRepoMock.Verify(r => r.DisableWorkerAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }
}
