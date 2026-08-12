using System;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure.Idempotency;

/// <summary>
/// Model-configuration guard for the #715 and #1465 remediations. Every
/// client-owned identity / idempotency column that backs a unique index MUST carry the
/// binary, culture-invariant, case-sensitive SQL Server collation
/// <c>Latin1_General_100_BIN2</c> so that byte-exact store comparison matches the
/// application's ordinal (post-NFKC) comparison.
///
/// <para>
/// SQL Server's default catalog collation (<c>SQL_Latin1_General_CP1_CI_AS</c>) is a
/// LINGUISTIC collation whose equivalence classes diverge from BOTH ordinal .NET
/// comparison and Unicode NFKC folding: it is case-, width- and Kana-type-insensitive and
/// folds assorted Latin phonetic letters onto their ASCII base. Under that collation a
/// value whose identity is DISTINCT to the application (e.g. Hiragana か U+304B vs
/// Katakana カ U+30AB SKUs, or small-capital-I U+026A vs ASCII <c>i</c> operation keys)
/// collapses onto the SAME physical row — double-applying a stock delta or false-deduping
/// a genuinely distinct operation. Forcing these columns to BIN2 closes every such class
/// at the storage layer at once (Hicks r5 blockers 1 &amp; 2).
/// </para>
///
/// <para>
/// DB-collation <em>behaviour</em> cannot be exercised over the SQLite / InMemory test
/// providers (each has its own collation), so this asserts the model metadata directly
/// under a SQL Server model. Building the model does not open a connection. The
/// end-to-end DDL + runtime behaviour is proven by provider-specific migrations and
/// <c>has-pending-model-changes</c> checks.
/// </para>
/// </summary>
public sealed class CaseSensitiveCollationModelTests
{
    private const string BinaryCaseSensitiveCollation = "Latin1_General_100_BIN2";

    private static IModel BuildModel(Action<DbContextOptionsBuilder<AppDbContext>> configureProvider)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new();
        configureProvider(builder);
        using AppDbContext context = new(builder.Options);
        // The runtime model (context.Model) is read-optimized and drops relational
        // annotations such as collation; the design-time model retains them.
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IModel BuildSqlServerModel()
        // A syntactically valid connection string is enough to select the provider; the
        // model is built (OnModelCreating) without ever opening the connection.
        => BuildModel(b => b.UseSqlServer(
            "Server=(localdb)\\ModelOnly;Database=model_only;Trusted_Connection=True;TrustServerCertificate=True"));

    private static IModel BuildSqliteModel()
        => BuildModel(b => b.UseSqlite("DataSource=:memory:"));

    private static string? CollationOf(IModel model, Type entityType, string propertyName)
    {
        IProperty? property = model.FindEntityType(entityType)?.FindProperty(propertyName);
        property.Should().NotBeNull(
            "{0}.{1} must exist in the model for the collation guard to be meaningful",
            entityType.Name,
            propertyName);
        return property!.GetCollation();
    }

    [Theory]
    // r6 (Frost): printed-part identity / natural-idempotency columns.
    [InlineData(typeof(PartInventory), nameof(PartInventory.Sku))]
    [InlineData(typeof(Bin), nameof(Bin.Code))]
    [InlineData(typeof(PartInventoryAdjustment), nameof(PartInventoryAdjustment.OperationKey))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.HarvestOperationKey))]
    // r1 (Kane): the original IdempotencyRecords columns this pattern extends from.
    [InlineData(typeof(IdempotencyRecord), nameof(IdempotencyRecord.RouteKey))]
    [InlineData(typeof(IdempotencyRecord), nameof(IdempotencyRecord.IdempotencyKey))]
    [InlineData(typeof(IdempotencyRecord), nameof(IdempotencyRecord.UserId))]
    [InlineData(typeof(BedClearCommandRecord), nameof(BedClearCommandRecord.IdempotencyKey))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.IdempotencyScope))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.IdempotencyKey))]
    public void CaseSensitiveIdentityColumns_UseBinaryCollation_OnSqlServer(
        Type entityType,
        string propertyName)
    {
        IModel model = BuildSqlServerModel();

        string? collation = CollationOf(model, entityType, propertyName);

        collation.Should().Be(
            BinaryCaseSensitiveCollation,
            "{0}.{1} backs a unique idempotency/identity index and must compare byte-exact on SQL Server so the store agrees with the application's ordinal comparison (#715)",
            entityType.Name,
            propertyName);
    }

    [Theory]
    [InlineData(typeof(PartInventory), nameof(PartInventory.Sku))]
    [InlineData(typeof(Bin), nameof(Bin.Code))]
    [InlineData(typeof(PartInventoryAdjustment), nameof(PartInventoryAdjustment.OperationKey))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.HarvestOperationKey))]
    [InlineData(typeof(BedClearCommandRecord), nameof(BedClearCommandRecord.IdempotencyKey))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.IdempotencyScope))]
    [InlineData(typeof(PrintJob), nameof(PrintJob.IdempotencyKey))]
    public void CaseSensitiveIdentityColumns_DoNotPinSqlServerCollation_OnNonSqlServerProviders(
        Type entityType,
        string propertyName)
    {
        // The collation is applied ONLY inside the SQL Server provider branch of
        // OnModelCreating. Other providers (PostgreSQL, SQLite) already compare these
        // columns byte-exact under their default deterministic collation, and the
        // SQL-Server-specific collation name is meaningless there. This guards the
        // provider conditional so a future refactor cannot leak a SQL-Server-only
        // collation onto another provider's model (which would break its migrations).
        IModel model = BuildSqliteModel();

        string? collation = CollationOf(model, entityType, propertyName);

        collation.Should().BeNull(
            "{0}.{1} must not carry the SQL-Server-specific BIN2 collation on a non-SQL-Server provider",
            entityType.Name,
            propertyName);
    }

    [Fact]
    public void BedClearCommandIndexes_SupportAuthoritativeAndOutboxLookups()
    {
        IModel model = BuildSqlServerModel();
        IEntityType entity = model.FindEntityType(typeof(BedClearCommandRecord))
            ?? throw new InvalidOperationException("BedClearCommandRecord is missing from the model.");

        IIndex jobLookup = entity.GetIndexes().Single(index =>
            index.GetDatabaseName() == "IX_BedClearCommandRecords_Job_Created_Id");
        jobLookup.Properties.Select(property => property.Name).Should().Equal(
            nameof(BedClearCommandRecord.JobId),
            nameof(BedClearCommandRecord.CreatedAtUtc),
            nameof(BedClearCommandRecord.Id));
        jobLookup.IsDescending.Should().Equal(false, true, true);

        IIndex outboxLookup = entity.GetIndexes().Single(index =>
            index.GetDatabaseName() == "UX_BedClearCommandRecords_OutboxEventId");
        outboxLookup.Properties.Select(property => property.Name).Should().Equal(
            nameof(BedClearCommandRecord.OutboxEventId));
        outboxLookup.IsUnique.Should().BeTrue();
    }
}
