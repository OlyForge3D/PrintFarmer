// <copyright file="DispatchSettingsControllerClockTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Modules.PrintQueue.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Pins <see cref="DispatchSettingsController"/>'s <c>UpdatedAt</c> writes to the injected
/// <see cref="TimeProvider"/> (#2972).
/// </summary>
public sealed class DispatchSettingsControllerClockTests : IDisposable
{
    private static readonly DateTimeOffset Anchor = new(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public DispatchSettingsControllerClockTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpdateSettings_WithFakeClock_StampsUpdatedAtFromInjectedClock()
    {
        DispatchSettings seeded = await _db.DispatchSettings.AsNoTracking().SingleAsync();
        DispatchSettingsController controller = CreateController();
        controller.Request.Headers.IfMatch = RevisionETag.EncodeQuoted(seeded.Revision);

        IActionResult result = await controller.UpdateSettingsAsync(
            new UpdateDispatchSettingsDto
            {
                AutoDispatchEnabled = true,
                AutoDispatchMode = AutoDispatchMode.Suggest,
                IdleThresholdSeconds = 10,
                MinimumScoreThreshold = 50,
                MaxConcurrentDispatches = 2,
                LoadBalancingStrategy = seeded.LoadBalancingStrategy,
            },
            CancellationToken.None);

        DispatchSettingsDto dto = result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<DispatchSettingsDto>().Subject;
        dto.UpdatedAt.Should().Be(Anchor.UtcDateTime);
        _db.ChangeTracker.Clear();
        (await _db.DispatchSettings.SingleAsync()).UpdatedAt.Should().Be(Anchor.UtcDateTime);
    }

    private DispatchSettingsController CreateController() =>
        new(_db, NullLogger<DispatchSettingsController>.Instance, new ManualTimeProvider(Anchor))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
