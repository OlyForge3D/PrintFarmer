using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Infrastructure.Idempotency;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure.Idempotency;

/// <summary>
/// End-to-end guard for Hicks r2 blocker 2: a parts-adjust request that carries an
/// <c>Idempotency-Key</c> header but omits the body <c>operationKey</c> must not
/// double-apply the stock delta even when a post-mutation flush failure leaves the
/// Processing row to be reclaimed and the same header key is retried.
///
/// <para>
/// The r1 fix (Hicks H-1/H-2) deliberately RETAINS the Processing row on a post-<c>next()</c>
/// flush failure so a naive retry gets 409 InProgress until the staleness reclaim frees it.
/// But once that row is reclaimed, nothing in the store prevents the retry from re-executing.
/// The blocker-2 fix closes that gap by synthesizing a deterministic <c>operationKey</c> from
/// the idempotency identity when the client omits one, so the domain's natural
/// <c>(PartInventoryId, OperationKey)</c> uniqueness backstops the filter. This test wires the
/// real filter, store and <see cref="PartInventoryService"/> together over one SQLite database
/// and proves the delta lands exactly once across the failure+reclaim+retry sequence.
/// </para>
/// </summary>
public class PartInventoryAdjustDoubleApplicationTests : IDisposable
{
    private const string Sku = "RD-1";
    private const string IdempotencyKey = "no-opkey-K";
    private const string UserId = "user-42";
    private const string Path = "/api/parts-inventory/RD-1/adjust";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IdempotencyStore _store;
    private readonly PartInventoryService _service;

    public PartInventoryAdjustDoubleApplicationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (AppDbContext db = new(_options))
        {
            _ = db.Database.EnsureCreated();
        }

        Mock<IDbContextFactory<AppDbContext>> factoryMock = new();
        _ = factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factoryMock.Object;

        // ProcessingStaleness = Zero makes a retained Processing row reclaimable as soon as any
        // time has elapsed; the test also backdates CreatedAt so the reclaim is deterministic.
        _store = new IdempotencyStore(
            _factory,
            NullLogger<IdempotencyStore>.Instance,
            new IdempotencyOptions { ProcessingStaleness = TimeSpan.Zero });

        _service = new PartInventoryService(_factory, NullLogger<PartInventoryService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AdjustWithoutOperationKey_FlushFailsThenReclaimRetry_AppliesDeltaOnce()
    {
        await SeedSkuAsync(Sku, onHand: 0);
        IdempotencyFilter filter = CreateFilter();

        // ---- Request 1: mutation commits, then the post-next() flush fails ----
        // The response body is a stream that throws on write, so FlushBufferedBodyAsync throws
        // AFTER the service has already committed the +1. Per the r1 fix the Processing row is
        // retained (not abandoned), reproducing the exact window blocker 2 must protect.
        ResourceExecutingContext first = CreateContext(new ThrowOnWriteStream());
        Func<Task> runFirst = () => filter.OnResourceExecutionAsync(first, () => ControllerNextAsync(first));
        _ = await runFirst.Should().ThrowAsync<IOException>(
            "the simulated post-mutation flush failure must surface to the caller");

        _ = (await OnHandAsync()).Should().Be(1, "the first adjust committed its +1 before the flush failed");
        _ = (await LedgerCountAsync()).Should().Be(1, "the first adjust wrote exactly one ledger entry");
        _ = (await ProcessingRecordCountAsync()).Should().Be(1,
            "a post-mutation flush failure must RETAIN the Processing row (Hicks H-1/H-2), not abandon it");

        // Age the retained Processing row so the staleness reclaim engages deterministically on
        // the retry (independent of wall-clock resolution between the two requests).
        await BackdateProcessingRecordAsync(TimeSpan.FromMinutes(1));

        // ---- Request 2: same Idempotency-Key, no operationKey; reclaim re-executes ----
        // The reclaim purges the stale Processing row and the filter re-runs the mutation. The
        // synthesized operationKey is a pure function of the (unchanged) idempotency identity,
        // so the domain's (PartInventoryId, OperationKey) uniqueness recognizes the replay and
        // the delta is NOT applied a second time.
        ResourceExecutingContext second = CreateContext(new MemoryStream());
        await filter.OnResourceExecutionAsync(second, () => ControllerNextAsync(second));

        _ = second.HttpContext.Response.StatusCode.Should().Be(200, "the reclaim retry completes normally");
        _ = (await OnHandAsync()).Should().Be(1,
            "the synthesized operationKey backstop must prevent the reclaim retry from double-applying the delta");
        _ = (await LedgerCountAsync()).Should().Be(1,
            "the retry must not append a second ledger entry for the same logical operation");
    }

    private IdempotencyFilter CreateFilter()
    {
        Mock<IOperatorFeatureGate> gate = new();
        _ = gate.Setup(g => g.IsEnabled(OperatorFeature.OfflineWriteReplay)).Returns(true);
        return new IdempotencyFilter(_store, gate.Object, NullLogger<IdempotencyFilter>.Instance);
    }

    /// <summary>
    /// Models <c>PartsInventoryController.AdjustAsync</c> for a client that omitted the body
    /// operationKey: it reads the synthesized-key fallback the filter stashed in
    /// <see cref="HttpContext.Items"/>, calls the real service, and writes a JSON response.
    /// </summary>
    private async Task<ResourceExecutedContext> ControllerNextAsync(ResourceExecutingContext context)
    {
        HttpContext http = context.HttpContext;

        // request.OperationKey is null (client omitted it) → fall back to the synthesized key.
        string? operationKey = null;
        if (string.IsNullOrWhiteSpace(operationKey)
            && http.Items.TryGetValue(IdempotencyFilter.SynthesizedOperationKeyItemKey, out object? synthesized)
            && synthesized is string synthesizedKey
            && !string.IsNullOrWhiteSpace(synthesizedKey))
        {
            operationKey = synthesizedKey;
        }

        AdjustResult result = await _service.AdjustAsync(
            Sku,
            new AdjustCommand(1, PartAdjustmentReason.Manual, null, null, null, operationKey, UserId),
            CancellationToken.None);

        http.Response.StatusCode = 200;
        http.Response.ContentType = "application/json";
        byte[] payload = Encoding.UTF8.GetBytes($"{{\"onHand\":{result.NewOnHand}}}");
        http.Response.Body.Write(payload, 0, payload.Length);
        return new ResourceExecutedContext(context, context.Filters);
    }

    private ResourceExecutingContext CreateContext(Stream responseBody)
    {
        // Same body bytes across both requests so the request hash matches; the body omits
        // operationKey (that is the whole point of the test).
        byte[] body = Encoding.UTF8.GetBytes("{\"delta\":1,\"reason\":\"Manual\"}");

        DefaultHttpContext http = new();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = Path;
        http.Request.Headers[IdempotencyKeyUtilities.HeaderName] = IdempotencyKey;
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        http.Request.ContentType = "application/json";
        http.Response.Body = responseBody;

        ClaimsIdentity identity = new(new[] { new Claim(ClaimTypes.NameIdentifier, UserId) }, "test");
        http.User = new ClaimsPrincipal(identity);

        ControllerActionDescriptor descriptor = new()
        {
            EndpointMetadata = new List<object> { new IdempotentAttribute(IdempotencyRouteKeys.PartsInventoryAdjust) },
            RouteValues = new Dictionary<string, string?>(),
        };
        RouteData routeData = new();
        routeData.Values["sku"] = Sku;

        Microsoft.AspNetCore.Mvc.ActionContext actionContext = new(http, routeData, descriptor);
        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    private async Task SeedSkuAsync(string sku, int onHand)
    {
        await using AppDbContext db = new(_options);
        _ = db.PartInventories.Add(new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            Name = sku,
            OnHand = onHand,
            ReorderPoint = 0,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await db.SaveChangesAsync();
    }

    private async Task<int> OnHandAsync()
    {
        await using AppDbContext db = new(_options);
        return (await db.PartInventories.SingleAsync()).OnHand;
    }

    private async Task<int> LedgerCountAsync()
    {
        await using AppDbContext db = new(_options);
        return await db.PartInventoryAdjustments.CountAsync();
    }

    private async Task<int> ProcessingRecordCountAsync()
    {
        await using AppDbContext db = new(_options);
        return await db.IdempotencyRecords.CountAsync(r => r.Status == IdempotencyRecordStatus.Processing);
    }

    private async Task BackdateProcessingRecordAsync(TimeSpan age)
    {
        await using AppDbContext db = new(_options);
        IdempotencyRecord record = await db.IdempotencyRecords
            .SingleAsync(r => r.Status == IdempotencyRecordStatus.Processing);
        DateTime backdated = DateTime.UtcNow - age;
        record.CreatedAt = backdated;
        record.UpdatedAt = backdated;
        _ = await db.SaveChangesAsync();
    }

    /// <summary>
    /// Response stream that throws <see cref="IOException"/> on any write, modelling a
    /// post-<c>next()</c> flush failure (e.g. a client disconnect) AFTER the mutation has
    /// already committed inside the action.
    /// </summary>
    private sealed class ThrowOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }

        public override void Flush()
        {
            // A flush with no buffered bytes is a no-op; the write is where failure surfaces.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("simulated post-mutation flush failure");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new IOException("simulated post-mutation flush failure");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("simulated post-mutation flush failure");
    }
}
