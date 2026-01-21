using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for FilamentTypeService
/// Tests filament type management, creation, updates, deletion, and preset handling
/// Fast executing (~3-4 seconds for 18 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class FilamentTypeServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public FilamentTypeServiceIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    /// <summary>
    /// Helper to run async code within a properly disposed scope.
    /// This ensures DisposeAsync() is called instead of Dispose() on the scope.
    /// </summary>
    private async Task<T> RunInScopeAsync<T>(Func<IServiceScope, Task<T>> work)
    {
        AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        try
        {
            return await work(scope);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    private async Task<FilamentTypeDto> CreateTestFilamentAsync(
        string? name = null,
        int hotendTemp = 200,
        int bedTemp = 60)
    {
        AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        try
        {
            IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

            // Generate unique name if not provided
            string uniqueName = name ?? $"test-filament-{Guid.NewGuid().ToString().Substring(0, 8)}";

            var request = new CreateFilamentTypeRequest(
                uniqueName,
                new TempTargets(hotendTemp, bedTemp)
            );

            return await service.CreateFilamentTypeAsync(request, CancellationToken.None);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    #region GetFilamentTypesAsync Tests

    [Fact]
    public async Task GetFilamentTypesAsync_WhenReady_ReturnsFilamentTypes()
    {
        // Arrange
        IReadOnlyList<FilamentTypeDto> result = await RunInScopeAsync(async scope =>
        {
            IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

            string name1 = $"pla-{Guid.NewGuid().ToString().Substring(0, 8)}";
            string name2 = $"petg-{Guid.NewGuid().ToString().Substring(0, 8)}";

            await CreateTestFilamentAsync(name1);
            await CreateTestFilamentAsync(name2);

            // Act
            return await service.GetFilamentTypesAsync(CancellationToken.None);
        });

        // Assert
        result.Should().NotBeNull();
        result.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetFilamentTypesAsync_IncludesTemperatureData()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string absName = $"abs-{Guid.NewGuid().ToString().Substring(0, 8)}";
        FilamentTypeDto created = await CreateTestFilamentAsync(absName, 240, 100);

        // Act
        IReadOnlyList<FilamentTypeDto> result = await service.GetFilamentTypesAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        FilamentTypeDto? abs = result.FirstOrDefault(f => f.Id == created.Id);
        abs.Should().NotBeNull();
        abs!.DefaultTemperatures.Hotend.Should().Be(240);
        abs.DefaultTemperatures.Bed.Should().Be(100);
    }

    #endregion

    #region GetFilamentPresetsAsync Tests

    [Fact]
    public async Task GetFilamentPresetsAsync_WhenReady_ReturnsPresets()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        // Act
        FilamentPresetsDto result = await service.GetFilamentPresetsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Presets.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFilamentPresetsAsync_ReturnsPresetsAsReadOnlyDictionary()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        // Act
        FilamentPresetsDto result = await service.GetFilamentPresetsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Presets.Should().NotBeNull();
        result.Presets.Should().BeOfType<Dictionary<string, TempTargets>>();
    }

    #endregion

    #region CreateFilamentTypeAsync Tests

    [Fact]
    public async Task CreateFilamentTypeAsync_WithValidRequest_CreatesFilament()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string uniqueName = $"new-filament-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var request = new CreateFilamentTypeRequest(
            uniqueName,
            new TempTargets(210, 65)
        );

        // Act
        FilamentTypeDto result = await service.CreateFilamentTypeAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBe(Guid.Empty);
        result.Name.Should().Be(uniqueName);
        result.DefaultTemperatures.Hotend.Should().Be(210);
        result.DefaultTemperatures.Bed.Should().Be(65);
    }

    [Fact]
    public async Task CreateFilamentTypeAsync_WithDifferentTemperatures_StoresTemperatures()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string uniqueName = $"nylon-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var request = new CreateFilamentTypeRequest(
            uniqueName,
            new TempTargets(260, 80)
        );

        // Act
        FilamentTypeDto result = await service.CreateFilamentTypeAsync(request, CancellationToken.None);

        // Assert
        result.DefaultTemperatures.Hotend.Should().Be(260);
        result.DefaultTemperatures.Bed.Should().Be(80);
    }

    [Fact]
    public async Task CreateFilamentTypeAsync_WithNullRequest_ThrowsArgumentException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateFilamentTypeAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFilamentTypeAsync_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        var request = new CreateFilamentTypeRequest(
            "   ",
            new TempTargets(200, 60)
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateFilamentTypeAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFilamentTypeAsync_WithDuplicateName_ThrowsInvalidOperationException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string uniqueName = $"duplicate-test-{Guid.NewGuid().ToString().Substring(0, 8)}";
        var request = new CreateFilamentTypeRequest(
            uniqueName,
            new TempTargets(200, 60)
        );

        // Create first filament
        await service.CreateFilamentTypeAsync(request, CancellationToken.None);

        // Act & Assert - Creating with same name should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateFilamentTypeAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFilamentTypeAsync_GeneratesUniqueIds()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        var request1 = new CreateFilamentTypeRequest(
            $"filament-1-{Guid.NewGuid().ToString().Substring(0, 8)}",
            new TempTargets(200, 60)
        );

        var request2 = new CreateFilamentTypeRequest(
            $"filament-2-{Guid.NewGuid().ToString().Substring(0, 8)}",
            new TempTargets(210, 65)
        );

        // Act
        FilamentTypeDto result1 = await service.CreateFilamentTypeAsync(request1, CancellationToken.None);
        FilamentTypeDto result2 = await service.CreateFilamentTypeAsync(request2, CancellationToken.None);

        // Assert
        result1.Id.Should().NotBe(result2.Id);
    }

    #endregion

    #region UpdateFilamentTypeAsync Tests

    [Fact]
    public async Task UpdateFilamentTypeAsync_WithValidId_UpdatesFilament()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        FilamentTypeDto created = await CreateTestFilamentAsync("to-update", 200, 60);

        var updateRequest = new UpdateFilamentTypeRequest(
            "updated-name",
            new TempTargets(220, 70)
        );

        // Act
        await service.UpdateFilamentTypeAsync(created.Id, updateRequest, CancellationToken.None);

        // Assert - Verify update by fetching
        IReadOnlyList<FilamentTypeDto> types = await service.GetFilamentTypesAsync(CancellationToken.None);
        FilamentTypeDto? updated = types.FirstOrDefault(f => f.Id == created.Id);
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("updated-name");
        updated.DefaultTemperatures.Hotend.Should().Be(220);
        updated.DefaultTemperatures.Bed.Should().Be(70);
    }

    [Fact]
    public async Task UpdateFilamentTypeAsync_WithNonExistentId_ThrowsKeyNotFoundException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        var nonExistentId = Guid.NewGuid();
        var updateRequest = new UpdateFilamentTypeRequest(
            "new-name",
            new TempTargets(200, 60)
        );

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateFilamentTypeAsync(nonExistentId, updateRequest, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateFilamentTypeAsync_WithNullRequest_ThrowsArgumentException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        FilamentTypeDto created = await CreateTestFilamentAsync("test", 200, 60);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateFilamentTypeAsync(created.Id, null!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateFilamentTypeAsync_PartialUpdate_UpdatesOnlyProvidedFields()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        FilamentTypeDto created = await CreateTestFilamentAsync("partial-update", 200, 60);

        // Update only temperatures
        var updateRequest = new UpdateFilamentTypeRequest(
            null!,
            new TempTargets(230, 80)
        );

        // Act
        await service.UpdateFilamentTypeAsync(created.Id, updateRequest, CancellationToken.None);

        // Assert
        IReadOnlyList<FilamentTypeDto> types = await service.GetFilamentTypesAsync(CancellationToken.None);
        FilamentTypeDto? updated = types.FirstOrDefault(f => f.Id == created.Id);
        updated.Should().NotBeNull();
        updated!.DefaultTemperatures.Hotend.Should().Be(230);
        updated.DefaultTemperatures.Bed.Should().Be(80);
    }

    #endregion

    #region DeleteFilamentTypeAsync Tests

    [Fact]
    public async Task DeleteFilamentTypeAsync_WithValidId_DeletesFilament()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        FilamentTypeDto created = await CreateTestFilamentAsync("to-delete", 200, 60);

        // Act
        await service.DeleteFilamentTypeAsync(created.Id, CancellationToken.None);

        // Assert - Verify deletion by attempting to fetch
        IReadOnlyList<FilamentTypeDto> types = await service.GetFilamentTypesAsync(CancellationToken.None);
        FilamentTypeDto? deleted = types.FirstOrDefault(f => f.Id == created.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFilamentTypeAsync_WithNonExistentId_ThrowsKeyNotFoundException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DeleteFilamentTypeAsync(nonExistentId, CancellationToken.None));
    }

    #endregion

    #region SaveFilamentPresetsAsync Tests

    [Fact]
    public async Task SaveFilamentPresetsAsync_WithValidPresets_SavesPresets()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string uniqueName1 = $"pla-{Guid.NewGuid().ToString().Substring(0, 8)}";
        string uniqueName2 = $"petg-{Guid.NewGuid().ToString().Substring(0, 8)}";

        var presets = new FilamentPresetsDto(
            new Dictionary<string, TempTargets>
            {
                { uniqueName1, new TempTargets(200, 60) },
                { uniqueName2, new TempTargets(240, 70) }
            }
        );

        // Act
        await service.SaveFilamentPresetsAsync(presets, CancellationToken.None);

        // Assert - Verify by fetching
        FilamentPresetsDto saved = await service.GetFilamentPresetsAsync(CancellationToken.None);
        saved.Presets.Should().ContainKey(uniqueName1);
        saved.Presets.Should().ContainKey(uniqueName2);
    }

    [Fact]
    public async Task SaveFilamentPresetsAsync_WithNullPresets_ThrowsArgumentException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveFilamentPresetsAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task SaveFilamentPresetsAsync_WithEmptyPresets_ThrowsArgumentException()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        var presets = new FilamentPresetsDto(null!);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveFilamentPresetsAsync(presets, CancellationToken.None));
    }

    [Fact]
    public async Task SaveFilamentPresetsAsync_UpdatesExistingPresets()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string uniqueName = $"tpu-{Guid.NewGuid().ToString().Substring(0, 8)}";

        // Save initial presets
        var initialPresets = new FilamentPresetsDto(
            new Dictionary<string, TempTargets>
            {
                { uniqueName, new TempTargets(220, 60) }
            }
        );
        await service.SaveFilamentPresetsAsync(initialPresets, CancellationToken.None);

        // Update presets with different temperature
        var updatedPresets = new FilamentPresetsDto(
            new Dictionary<string, TempTargets>
            {
                { uniqueName, new TempTargets(230, 65) }
            }
        );

        // Act
        await service.SaveFilamentPresetsAsync(updatedPresets, CancellationToken.None);

        // Assert
        FilamentPresetsDto saved = await service.GetFilamentPresetsAsync(CancellationToken.None);
        saved.Presets[uniqueName].Hotend.Should().Be(230);
        saved.Presets[uniqueName].Bed.Should().Be(65);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task CreateMultipleFilaments_ThenListAll()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IFilamentTypeService service = scope.ServiceProvider.GetRequiredService<IFilamentTypeService>();

        string[] filaments = new[] { "pla", "petg", "abs", "tpu" };
        var createdIds = new List<Guid>();

        // Create multiple with unique names
        foreach (string? name in filaments)
        {
            string uniqueName = $"{name}-{Guid.NewGuid().ToString().Substring(0, 8)}";
            var request = new CreateFilamentTypeRequest(
                uniqueName,
                new TempTargets(200 + (filaments.Length / 4), 60)
            );
            FilamentTypeDto created = await service.CreateFilamentTypeAsync(request, CancellationToken.None);
            createdIds.Add(created.Id);
        }

        // Act
        IReadOnlyList<FilamentTypeDto> all = await service.GetFilamentTypesAsync(CancellationToken.None);

        // Assert
        foreach (Guid id in createdIds)
        {
            all.FirstOrDefault(f => f.Id == id).Should().NotBeNull();
        }
    }

    #endregion
}
