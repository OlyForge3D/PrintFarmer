using System.Data.Common;
using System.Reflection;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class PrinterModelAliasServiceTests
{
    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void IsUniqueConstraintViolation_SqlServerUniqueIndexOrConstraintNumber_ReturnsTrue(int number)
    {
        // #2080 N-NORM-1 (review finding, Vasquez): SqlException does not override
        // DbException.ErrorCode -- that still returns the exception's HResult, a generic
        // ADO.NET provider code unrelated to the SQL Server error number. The real error number
        // (2601/2627 for a unique-index/constraint violation) is only exposed via
        // SqlException.Number. FakeSqlException below reproduces exactly that shape: ErrorCode
        // returns an unrelated HResult while Number carries the real SQL Server error number, so
        // this test fails if the production code ever goes back to reading ErrorCode instead of
        // Number.
        FakeSqlException fakeSqlException = new(number, unrelatedErrorCode: -2146232060);
        DbUpdateException dbUpdateException = new("save failed", fakeSqlException);

        bool result = InvokeIsUniqueConstraintViolation(dbUpdateException);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintViolation_SqlServerUnrelatedNumber_ReturnsFalse()
    {
        FakeSqlException fakeSqlException = new(number: 547, unrelatedErrorCode: -2146232060);
        DbUpdateException dbUpdateException = new("save failed", fakeSqlException);

        bool result = InvokeIsUniqueConstraintViolation(dbUpdateException);

        result.Should().BeFalse();
    }

    private static bool InvokeIsUniqueConstraintViolation(DbUpdateException ex)
    {
        MethodInfo method = typeof(PrinterModelAliasService).GetMethod(
                "IsUniqueConstraintViolation",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "PrinterModelAliasService.IsUniqueConstraintViolation not found via reflection.");
        return (bool)method.Invoke(null, new object[] { ex })!;
    }

    /// <summary>
    /// Minimal stand-in for Microsoft.Data.SqlClient.SqlException: matched by type-name substring
    /// (as the production code does, to avoid a direct SqlClient dependency), exposes a settable
    /// <see cref="Number"/> distinct from the inherited, HResult-backed <see cref="ErrorCode"/> --
    /// mirroring the real SqlException, where ErrorCode is NOT the SQL Server error number.
    /// </summary>
    private sealed class FakeSqlException : DbException
    {
        public FakeSqlException()
        {
        }

        public FakeSqlException(string message)
            : base(message)
        {
        }

        public FakeSqlException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public FakeSqlException(int number, int unrelatedErrorCode)
        {
            Number = number;
            HResult = unrelatedErrorCode;
        }

        public int Number { get; }

        public override int ErrorCode => HResult;
    }

    [Fact]
    public async Task ResolveAndEnsure_AreCaseInsensitive_WithRelationalSqliteProvider()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new AppDbContext(options);
        _ = await dbContext.Database.EnsureCreatedAsync();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        dbContext.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Voron Design",
        });
        dbContext.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Micron 180",
        });
        dbContext.PrinterModelAliases.Add(new PrinterModelAlias
        {
            PrinterModelId = modelId,
            SlicerModelName = "Micron 180",
            SlicerType = "OrcaSlicer",
            CreatedAt = DateTime.UtcNow
        });
        dbContext.PrinterModelAliases.Add(new PrinterModelAlias
        {
            PrinterModelId = modelId,
            SlicerModelName = "Generic Micron",
            SlicerType = null,
            CreatedAt = DateTime.UtcNow
        });
        dbContext.PrinterModelAliases.Add(new PrinterModelAlias
        {
            PrinterModelId = modelId,
            SlicerModelName = "Prusa Only",
            SlicerType = "PrusaSlicer",
            CreatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();

        var service = new PrinterModelAliasService(dbContext);

        Guid? resolved = await service.ResolveModelAliasAsync("micron 180", "orcaslicer");
        await service.EnsureModelAliasAsync(modelId, "MICRON 180", "ORCASLICER");
        IReadOnlyList<SlicerModelAliasEntry> aliases =
            await service.ListAliasesAsync("OrcaSlicer");

        resolved.Should().Be(modelId);
        (await dbContext.PrinterModelAliases.CountAsync()).Should().Be(3);
        aliases.Select(alias => alias.SlicerModelName)
            .Should().Equal("Generic Micron", "Micron 180");
        (await service.ListAliasesAsync("MissingSlicer"))
            .Should().ContainSingle(alias => alias.SlicerModelName == "Generic Micron");
    }
}
