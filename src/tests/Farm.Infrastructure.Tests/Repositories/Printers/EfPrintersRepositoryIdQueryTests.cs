using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Printers;

/// <summary>
/// Issue #3906: backend-capabilities requests authorize an ID-only projection and then load
/// only the authorized printer rows, instead of materializing and decrypting the whole fleet twice.
/// </summary>
public sealed class EfPrintersRepositoryIdQueryTests
{
    [Fact]
    public async Task GetAllIdsAsync_ProjectsIdsWithoutCredentialColumnsOrDecryption()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        List<string> sql = new();
        DbContextOptions<AppDbContext> options = CreateOptions(connection, sql);
        List<Guid> seeded = await SeedAsync(options, count: 3);
        var protector = new CountingProtector();
        sql.Clear();

        await using AppDbContext db = new(options);
        List<Guid> ids = await new EfPrintersRepository(db, protector).GetAllIdsAsync(CancellationToken.None);

        ids.Should().BeEquivalentTo(seeded);
        protector.UnprotectCalls.Should().Be(0);
        string commandText = string.Join('\n', sql);
        commandText.Should().NotContain("ApiKey").And.NotContain("Password");
    }

    [Fact]
    public async Task GetByIdsAsync_LoadsOnlyRequestedRowsWithCredentials()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        List<string> sql = new();
        DbContextOptions<AppDbContext> options = CreateOptions(connection, sql);
        List<Guid> seeded = await SeedAsync(options, count: 4);
        var protector = new CountingProtector();

        await using AppDbContext db = new(options);
        List<Printer> printers = await new EfPrintersRepository(db, protector).GetByIdsAsync(
            [seeded[1], seeded[3], seeded[1], Guid.NewGuid()],
            CancellationToken.None);

        printers.Select(printer => printer.Id).Should().BeEquivalentTo([seeded[1], seeded[3]]);
        printers.Should().OnlyContain(printer =>
            printer.Credential != null && printer.Credential.ApiKey == "plain-key-" + printer.Name);
        protector.UnprotectCalls.Should().Be(2, "only the two authorized rows hold encrypted passwords to decrypt");
        db.ChangeTracker.Entries<Printer>().Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdsAsync_Empty_ExecutesNoQuery()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        List<string> sql = new();
        DbContextOptions<AppDbContext> options = CreateOptions(connection, sql);
        _ = await SeedAsync(options, count: 2);
        sql.Clear();

        await using AppDbContext db = new(options);
        List<Printer> printers = await new EfPrintersRepository(db, new CountingProtector())
            .GetByIdsAsync([], CancellationToken.None);

        printers.Should().BeEmpty();
        sql.Should().NotContain(message => message.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetByIdsAsync_LargeIdSet_IsChunkedBelowProviderParameterLimits()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        List<string> sql = new();
        DbContextOptions<AppDbContext> options = CreateOptions(connection, sql);
        List<Guid> seeded = await SeedAsync(options, count: 3);
        Guid[] requested = Enumerable.Range(0, 2500).Select(_ => Guid.NewGuid()).Concat(seeded).ToArray();
        sql.Clear();

        await using AppDbContext db = new(options);
        List<Printer> printers = await new EfPrintersRepository(db, new CountingProtector())
            .GetByIdsAsync(requested, CancellationToken.None);

        printers.Select(printer => printer.Id).Should().BeEquivalentTo(seeded);
        sql.Count(message => message.Contains("FROM \"Printers\"", StringComparison.Ordinal))
            .Should().Be(6, "2503 distinct IDs split into 500-ID chunks need six queries");
    }

    private static DbContextOptions<AppDbContext> CreateOptions(SqliteConnection connection, List<string> sql) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .LogTo(sql.Add, Microsoft.Extensions.Logging.LogLevel.Information)
            .Options;

    private static async Task<List<Guid>> SeedAsync(DbContextOptions<AppDbContext> options, int count)
    {
        await using AppDbContext db = new(options);
        _ = await db.Database.EnsureCreatedAsync();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestCo" });
        _ = db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "Model", ManufacturerId = manufacturerId });
        List<Guid> ids = new(count);
        for (int i = 0; i < count; i++)
        {
            Guid id = Guid.NewGuid();
            ids.Add(id);
            _ = db.Printers.Add(new Printer
            {
                Id = id,
                Name = $"P{i}",
                ServerUrl = $"http://printer-{i}.local",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                ApiKey = $"plain-key-P{i}",
                Password = CountingProtector.EncryptedPrefix + new string('x', 100) + i,
            });
        }

        _ = await db.SaveChangesAsync();
        return ids;
    }

    private sealed class CountingProtector : ISensitiveDataProtector
    {
        public const string EncryptedPrefix = "CfDJ8synthetic-not-a-secret-";

        public int UnprotectCalls { get; private set; }

        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText)
        {
            UnprotectCalls++;
            return protectedText;
        }
    }
}
