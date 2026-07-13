using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Infrastructure.Idempotency;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
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
/// Focused tests for <see cref="IdempotencyFilter"/> driven directly against a
/// <see cref="DefaultHttpContext"/>. The filter is exercised at the same seam
/// MVC uses (<see cref="IAsyncResourceFilter.OnResourceExecutionAsync"/>) so
/// the tests remain host-agnostic while still verifying the header contract,
/// feature-gate bypass, replay behavior, and hash-conflict / abandon paths.
/// A live <see cref="IdempotencyStore"/> against SQLite-in-memory is used so
/// the filter <-> store handshake is not mocked away.
/// </summary>
public class IdempotencyFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<Farm.Infrastructure.Data.AppDbContext> _options;
    private readonly IdempotencyStore _store;

    public IdempotencyFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<Farm.Infrastructure.Data.AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = db.Database.EnsureCreated();

        Mock<IDbContextFactory<Farm.Infrastructure.Data.AppDbContext>> factoryMock = new();
        _ = factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Farm.Infrastructure.Data.AppDbContext(_options));
        _store = new IdempotencyStore(factoryMock.Object, NullLogger<IdempotencyStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IOperatorFeatureGate GateWith(bool enabled)
    {
        Mock<IOperatorFeatureGate> gate = new();
        _ = gate.Setup(g => g.IsEnabled(OperatorFeature.OfflineWriteReplay)).Returns(enabled);
        return gate.Object;
    }

    private static ResourceExecutingContext CreateContext(
        string routeKey,
        string? idempotencyKey,
        byte[]? body,
        string? userId = "user-42",
        string? contentType = "application/json",
        string path = "/test",
        IReadOnlyDictionary<string, object?>? routeValues = null)
    {
        DefaultHttpContext http = new();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = path;
        if (idempotencyKey is not null)
        {
            http.Request.Headers[IdempotencyKeyUtilities.HeaderName] = idempotencyKey;
        }

        if (body is not null)
        {
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = body.Length;
            http.Request.ContentType = contentType;
        }

        http.Response.Body = new MemoryStream();

        if (userId is not null)
        {
            ClaimsIdentity id = new(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test");
            http.User = new ClaimsPrincipal(id);
        }

        ControllerActionDescriptor descriptor = new()
        {
            EndpointMetadata = new List<object> { new IdempotentAttribute(routeKey) },
            RouteValues = new Dictionary<string, string?>(),
        };
        RouteData routeData = new();
        if (routeValues is not null)
        {
            foreach (KeyValuePair<string, object?> kvp in routeValues)
            {
                routeData.Values[kvp.Key] = kvp.Value;
            }
        }

        ActionContext actionContext = new(http, routeData, descriptor);
        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    private IdempotencyFilter CreateFilter(bool featureEnabled = true)
        => new(_store, GateWith(featureEnabled), NullLogger<IdempotencyFilter>.Instance);

    private IdempotencyFilter CreateFilter(IIdempotencyStore store, bool featureEnabled = true)
        => new(store, GateWith(featureEnabled), NullLogger<IdempotencyFilter>.Instance);

    private static async Task RunAsync(
        IdempotencyFilter filter,
        ResourceExecutingContext context,
        int statusCode,
        string responseBody,
        string responseContentType = "application/json")
    {
        Task<ResourceExecutedContext> Next()
        {
            context.HttpContext.Response.StatusCode = statusCode;
            context.HttpContext.Response.ContentType = responseContentType;
            byte[] bytes = Encoding.UTF8.GetBytes(responseBody);
            context.HttpContext.Response.Body.Write(bytes, 0, bytes.Length);
            return Task.FromResult(new ResourceExecutedContext(context, context.Filters));
        }

        await filter.OnResourceExecutionAsync(context, Next);
    }

    private static async Task RunAsync(
        IdempotencyFilter filter,
        ResourceExecutingContext context,
        int statusCode,
        string responseBody,
        Action onExecute,
        string responseContentType = "application/json")
    {
        Task<ResourceExecutedContext> Next()
        {
            onExecute();
            context.HttpContext.Response.StatusCode = statusCode;
            context.HttpContext.Response.ContentType = responseContentType;
            byte[] bytes = Encoding.UTF8.GetBytes(responseBody);
            context.HttpContext.Response.Body.Write(bytes, 0, bytes.Length);
            return Task.FromResult(new ResourceExecutedContext(context, context.Filters));
        }

        await filter.OnResourceExecutionAsync(context, Next);
    }

    [Fact]
    public async Task FeatureDisabled_Bypasses_Store_And_Executes_Pipeline()
    {
        IdempotencyFilter filter = CreateFilter(featureEnabled: false);
        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "k-1", Encoding.UTF8.GetBytes("{\"any\":true}"));

        await RunAsync(filter, ctx, 200, "{\"ok\":1}");

        _ = ctx.HttpContext.Response.StatusCode.Should().Be(200);
        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "the store must not be touched when the feature is off");
    }

    [Fact]
    public async Task NoHeader_Executes_Pipeline_WithoutPersistence()
    {
        IdempotencyFilter filter = CreateFilter();
        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, idempotencyKey: null,
            body: Encoding.UTF8.GetBytes("{\"any\":true}"));

        await RunAsync(filter, ctx, 200, "{\"ok\":1}");

        _ = ctx.HttpContext.Response.StatusCode.Should().Be(200);
        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task MalformedKey_Returns_400_ProblemDetails()
    {
        IdempotencyFilter filter = CreateFilter();
        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.TaskComplete,
            idempotencyKey: "has space",
            body: Array.Empty<byte>());

        await filter.OnResourceExecutionAsync(ctx, () => throw new InvalidOperationException("pipeline must not run"));

        _ = ctx.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Replay_Returns_StoredResponse_WithReplayHeader()
    {
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"payload\":42}");

        ResourceExecutingContext firstCtx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-123", body);
        await RunAsync(filter, firstCtx, 201, "{\"result\":\"created\"}", "application/json");
        _ = firstCtx.HttpContext.Response.StatusCode.Should().Be(201);

        ResourceExecutingContext replayCtx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-123", body);

        bool pipelineRan = false;
        await filter.OnResourceExecutionAsync(replayCtx, () =>
        {
            pipelineRan = true;
            return Task.FromResult(new ResourceExecutedContext(replayCtx, replayCtx.Filters));
        });

        _ = pipelineRan.Should().BeFalse("replay must short-circuit the pipeline");
        _ = replayCtx.Result.Should().NotBeNull();

        ActionContext execCtx = new(replayCtx.HttpContext, replayCtx.RouteData, replayCtx.ActionDescriptor);
        await replayCtx.Result!.ExecuteResultAsync(execCtx);
        replayCtx.HttpContext.Response.Body.Position = 0;
        string replayed = new StreamReader(replayCtx.HttpContext.Response.Body).ReadToEnd();
        _ = replayed.Should().Be("{\"result\":\"created\"}");
        _ = replayCtx.HttpContext.Response.StatusCode.Should().Be(201);
        _ = replayCtx.HttpContext.Response.Headers[IdempotencyFilter.ReplayHeaderName].ToString()
            .Should().Be("true");
    }

    [Fact]
    public async Task SameKey_DifferentBody_Returns_409_HashConflict()
    {
        IdempotencyFilter filter = CreateFilter();
        ResourceExecutingContext firstCtx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-123", Encoding.UTF8.GetBytes("{\"a\":1}"));
        await RunAsync(filter, firstCtx, 200, "{\"ok\":true}");

        ResourceExecutingContext conflictCtx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-123", Encoding.UTF8.GetBytes("{\"a\":999}"));

        await filter.OnResourceExecutionAsync(conflictCtx,
            () => throw new InvalidOperationException("pipeline must not run on hash-conflict"));

        _ = conflictCtx.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task ServerError_AbandonsRecord_SoRetryCanReplayFreshly()
    {
        IdempotencyFilter filter = CreateFilter();
        ResourceExecutingContext failCtx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-500", Encoding.UTF8.GetBytes("{\"a\":1}"));

        await RunAsync(filter, failCtx, 500, "{\"error\":\"boom\"}");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "5xx responses must not be cached; abandon must delete the processing row");
    }

    [Fact]
    public async Task Exception_AbandonsRecord_AndRethrows()
    {
        IdempotencyFilter filter = CreateFilter();
        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "abc-throw", Encoding.UTF8.GetBytes("{\"a\":1}"));

        Func<Task> act = () => filter.OnResourceExecutionAsync(
            ctx, () => throw new InvalidOperationException("db went down"));
        _ = await act.Should().ThrowAsync<InvalidOperationException>();

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "exceptions must not leave a poisoned processing row");
    }

    [Fact]
    public async Task DifferentUsers_SameKey_Do_Not_Cross_Contaminate()
    {
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");
        ResourceExecutingContext userA = CreateContext(IdempotencyRouteKeys.TaskComplete, "shared", body, userId: "user-A");
        ResourceExecutingContext userB = CreateContext(IdempotencyRouteKeys.TaskComplete, "shared", body, userId: "user-B");

        await RunAsync(filter, userA, 200, "{\"user\":\"A\"}");
        await RunAsync(filter, userB, 200, "{\"user\":\"B\"}");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        int count = await db.IdempotencyRecords.CountAsync(CancellationToken.None);
        _ = count.Should().Be(2, "distinct users must not share replay state even with matching key");
    }

    [Fact]
    public async Task SameKey_SameBody_DifferentResolvedPath_BothExecute_AsSeparateRows()
    {
        // Cross-resource replay guard: the same client key reused against two
        // different resolved paths (e.g. two different {id}s) must NOT replay one
        // resource's response for the other. Both mutations execute and persist
        // independent rows because the resolved path is folded into the identity.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "shared-key", body,
            path: "/api/tasks/11111111-1111-1111-1111-111111111111/complete");
        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "shared-key", body,
            path: "/api/tasks/22222222-2222-2222-2222-222222222222/complete");

        bool secondPipelineRan = false;
        await RunAsync(filter, first, 200, "{\"task\":\"one\"}");
        await filter.OnResourceExecutionAsync(second, () =>
        {
            secondPipelineRan = true;
            second.HttpContext.Response.StatusCode = 200;
            second.HttpContext.Response.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes("{\"task\":\"two\"}");
            second.HttpContext.Response.Body.Write(bytes, 0, bytes.Length);
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = secondPipelineRan.Should().BeTrue("a different resolved path must execute its own mutation, not replay");
        _ = second.Result.Should().BeNull("the second call is a fresh mutation, not a short-circuited replay");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(2, "each resolved path is a distinct idempotency identity");
    }

    [Fact]
    public async Task SameKey_EmptyBody_DifferentId_BothExecute_NoSilentDataLoss()
    {
        // TaskComplete-shape: empty request body. Without the resolved path in the
        // identity, an empty body would hash identically for every {id}, so reusing
        // one key across two task ids would silently drop the second completion.
        IdempotencyFilter filter = CreateFilter();

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "same-key", Array.Empty<byte>(),
            path: "/api/tasks/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/complete");
        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "same-key", Array.Empty<byte>(),
            path: "/api/tasks/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/complete");

        bool secondPipelineRan = false;
        await RunAsync(filter, first, 204, string.Empty);
        await filter.OnResourceExecutionAsync(second, () =>
        {
            secondPipelineRan = true;
            second.HttpContext.Response.StatusCode = 204;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = secondPipelineRan.Should().BeTrue("the second task completion must not be silently dropped");
        _ = second.Result.Should().BeNull();

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(2, "two distinct task ids must each get their own completion record");
    }

    [Fact]
    public async Task SameKey_SameBody_SamePath_Replays()
    {
        // The classic replay case: identical key, body, AND resolved path → the
        // second call must short-circuit to the stored response.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");
        const string path = "/api/tasks/33333333-3333-3333-3333-333333333333/complete";

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "replay-key", body, path: path);
        await RunAsync(filter, first, 201, "{\"result\":\"created\"}");

        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.TaskComplete, "replay-key", body, path: path);
        bool pipelineRan = false;
        await filter.OnResourceExecutionAsync(second, () =>
        {
            pipelineRan = true;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = pipelineRan.Should().BeFalse("an identical key+body+path must replay, not re-execute");
        _ = second.Result.Should().BeOfType<IdempotencyReplayResult>();

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "an exact replay must not create a second record");
    }

    [Fact]
    public async Task PartsAdjust_SameKey_SameBody_DifferentSkuCasing_Replays_NotReExecutes()
    {
        // Hicks r2 blocker 1: the domain resolves the parts-adjust target by NORMALIZED
        // (case-insensitive, trimmed) SKU, so /abc/adjust, /ABC/adjust and /Abc/adjust are the
        // SAME resource. The idempotency identity must fold in the normalized SKU rather than
        // the raw request path — otherwise a same-key retry that differs only in SKU casing
        // creates a distinct record and double-applies the stock delta. All three casings must
        // therefore share ONE record and only the first request may execute; the rest replay.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"delta\":1}");
        const string key = "casing-key";

        int executionCount = 0;

        // First request: sku "abc" — executes the adjust and persists a Completed record.
        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, key, body,
            path: "/api/parts-inventory/abc/adjust",
            routeValues: new Dictionary<string, object?> { ["sku"] = "abc" });
        await RunAsync(filter, first, 200, "{\"onHand\":1}", () => executionCount++);
        _ = first.HttpContext.Response.StatusCode.Should().Be(200);

        // Second request: sku "ABC" (upper) — same normalized identity → must replay.
        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, key, body,
            path: "/api/parts-inventory/ABC/adjust",
            routeValues: new Dictionary<string, object?> { ["sku"] = "ABC" });
        await filter.OnResourceExecutionAsync(second, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });
        _ = second.Result.Should().BeOfType<IdempotencyReplayResult>(
            "a same-key retry that differs only in SKU casing must replay, not re-execute");

        // Third request: sku "Abc" (mixed) — same normalized identity → must also replay.
        ResourceExecutingContext third = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, key, body,
            path: "/api/parts-inventory/Abc/adjust",
            routeValues: new Dictionary<string, object?> { ["sku"] = "Abc" });
        await filter.OnResourceExecutionAsync(third, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(third, third.Filters));
        });
        _ = third.Result.Should().BeOfType<IdempotencyReplayResult>(
            "SKU casing must not create a distinct idempotency identity");

        _ = executionCount.Should().Be(1,
            "only the first request may run the adjust; the two casing variants must replay");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "all three SKU casings must share ONE idempotency record");
    }

    [Fact]
    public async Task TaskComplete_SameKey_SameBody_DifferentGuidCasing_Replays_NotReExecutes()
    {
        // Hicks r3 blocker 1: the {id:guid} route constraint validates but does NOT canonicalize,
        // so /api/tasks/{GUID-UPPER}/complete and its lowercase form bind to the SAME parsed Guid
        // and the SAME action — yet their raw paths differ by case. The idempotency identity must
        // fold in the CANONICAL ("D"-form) GUID rather than the raw path, or a same-key retry that
        // differs only in GUID casing would mint a distinct record and double-execute the
        // completion. Distinct raw paths here prove it is the canonical GUID (not the path
        // fallback) driving the replay.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");
        const string key = "task-guid-casing";
        const string upper = "ABCDEF01-2345-6789-ABCD-EF0123456789";
        const string lower = "abcdef01-2345-6789-abcd-ef0123456789";

        int executionCount = 0;

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.TaskComplete, key, body,
            path: $"/api/tasks/{upper}/complete",
            routeValues: new Dictionary<string, object?> { ["id"] = upper });
        await RunAsync(filter, first, 200, "{\"task\":\"done\"}", () => executionCount++);
        _ = first.HttpContext.Response.StatusCode.Should().Be(200);

        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.TaskComplete, key, body,
            path: $"/api/tasks/{lower}/complete",
            routeValues: new Dictionary<string, object?> { ["id"] = lower });
        await filter.OnResourceExecutionAsync(second, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = second.Result.Should().BeOfType<IdempotencyReplayResult>(
            "a same-key retry that differs only in GUID casing must replay, not re-execute");
        _ = executionCount.Should().Be(1, "only the first completion may run; the casing variant must replay");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "both GUID casings must share ONE idempotency record");
    }

    [Fact]
    public async Task JobQueueHarvest_SameKey_SameBody_DifferentGuidFormat_Replays_NotReExecutes()
    {
        // Hicks r3 blocker 1 (format tolerance beyond casing): the {id:guid} constraint uses
        // Guid.TryParse, which also accepts the braced ("B") form, so /{id} and /{braced-id} bind
        // to the same Guid. Canonicalizing to the "D" form collapses braced/hyphenless variants
        // onto one idempotency record so a same-key retry cannot double-execute the harvest. The
        // two raw paths differ, isolating the canonicalization from the path fallback.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"h\":1}");
        const string key = "harvest-guid-format";
        const string plain = "11111111-2222-3333-4444-555555555555";
        const string braced = "{11111111-2222-3333-4444-555555555555}";

        int executionCount = 0;

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.JobQueueHarvest, key, body,
            path: $"/api/job-queue/{plain}/harvest",
            routeValues: new Dictionary<string, object?> { ["id"] = plain });
        await RunAsync(filter, first, 200, "{\"harvest\":\"ok\"}", () => executionCount++);

        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.JobQueueHarvest, key, body,
            path: $"/api/job-queue/{braced}/harvest",
            routeValues: new Dictionary<string, object?> { ["id"] = braced });
        await filter.OnResourceExecutionAsync(second, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = second.Result.Should().BeOfType<IdempotencyReplayResult>(
            "the braced GUID form must canonicalize to the same identity and replay");
        _ = executionCount.Should().Be(1, "only the first harvest may run; the format variant must replay");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "braced and plain GUID forms must share ONE idempotency record");
    }

    [Fact]
    public async Task SpoolBind_SameKey_SameBody_DifferentGuidCasingAndIntForm_Replays_NotReExecutes()
    {
        // Hicks r3 blocker 1: the {id:guid}/toolheads/{toolheadIndex:int} route has TWO typed
        // values. GUID casing and integer leading-zeros ("1" vs "01") both bind to identical
        // parsed arguments but differ in raw path text. Canonicalizing the GUID ("D" form) AND the
        // integer (invariant parse) collapses those variants onto ONE idempotency record so a
        // same-key retry cannot double-execute the spool bind.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"spoolId\":7}");
        const string key = "spool-bind-canon";
        const string upper = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        const string lower = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        int executionCount = 0;

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.PrinterToolheadSpoolBind, key, body,
            path: $"/api/printers/{upper}/toolheads/1/spool",
            routeValues: new Dictionary<string, object?> { ["id"] = upper, ["toolheadIndex"] = "1" });
        await RunAsync(filter, first, 200, "{\"bound\":true}", () => executionCount++);

        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.PrinterToolheadSpoolBind, key, body,
            path: $"/api/printers/{lower}/toolheads/01/spool",
            routeValues: new Dictionary<string, object?> { ["id"] = lower, ["toolheadIndex"] = "01" });
        await filter.OnResourceExecutionAsync(second, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = second.Result.Should().BeOfType<IdempotencyReplayResult>(
            "GUID casing and integer leading-zero variants must canonicalize to one identity and replay");
        _ = executionCount.Should().Be(1, "only the first spool bind may run; the canonical variant must replay");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "GUID casing + int form variants must share ONE idempotency record");
    }

    [Fact]
    public async Task TaskComplete_SameKey_DifferentGuids_BothExecute_AsSeparateRows()
    {
        // Guard against over-collapsing: canonicalizing GUID FORMAT must not merge genuinely
        // DIFFERENT ids. Two distinct task GUIDs under one client key must each execute and
        // persist their own record — the canonicalization narrows format variance without eroding
        // the cross-resource replay guard.
        IdempotencyFilter filter = CreateFilter();
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");
        const string key = "distinct-guids";
        const string idA = "11111111-1111-1111-1111-111111111111";
        const string idB = "22222222-2222-2222-2222-222222222222";

        int executionCount = 0;

        ResourceExecutingContext first = CreateContext(
            IdempotencyRouteKeys.TaskComplete, key, body,
            path: $"/api/tasks/{idA}/complete",
            routeValues: new Dictionary<string, object?> { ["id"] = idA });
        await RunAsync(filter, first, 200, "{\"task\":\"a\"}", () => executionCount++);

        ResourceExecutingContext second = CreateContext(
            IdempotencyRouteKeys.TaskComplete, key, body,
            path: $"/api/tasks/{idB}/complete",
            routeValues: new Dictionary<string, object?> { ["id"] = idB });
        await filter.OnResourceExecutionAsync(second, () =>
        {
            executionCount++;
            return Task.FromResult(new ResourceExecutedContext(second, second.Filters));
        });

        _ = second.Result.Should().BeNull("distinct GUIDs are distinct resources and must not replay");
        _ = executionCount.Should().Be(2, "each distinct task id must execute its own completion");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(2, "two distinct task ids must each get their own record");
    }

    [Fact]
    public async Task OversizedBody_WithKey_Returns_413_AndPersistsNothing()
    {
        // A body over the buffering limit cannot be hashed for the replay contract.
        // The filter must reject it with 413 rather than silently bypassing
        // protection (which would let an oversized retry double-apply).
        IdempotencyFilter filter = CreateFilter();
        byte[] huge = new byte[(3 * IdempotencyFilter.MaxBufferedRequestBytes) / 2]; // 1.5 MiB
        Array.Fill(huge, (byte)'x');

        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, "big-key", huge,
            path: "/api/parts-inventory/RD-500/adjust");

        await filter.OnResourceExecutionAsync(ctx,
            () => throw new InvalidOperationException("pipeline must not run for an oversized body"));

        ObjectResult result = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        _ = result.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        _ = result.Value.Should().BeOfType<ProblemDetails>();

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "a rejected oversized request must not persist an idempotency record");
    }

    [Fact]
    public async Task PostMutation_FlushFailure_DoesNotAbandonProcessingRow()
    {
        // Hicks H-1/H-2: a failure AFTER the mutation succeeded (here, flushing the
        // buffered response to the real stream throws) must NOT abandon the Processing
        // row. Abandoning would delete it and let a retry re-execute the already-applied
        // mutation with no replay protection (the double-execution window).
        InstrumentedStore store = new(_store);
        IdempotencyFilter filter = CreateFilter(store);

        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, "flush-fail-key",
            Encoding.UTF8.GetBytes("{\"delta\":1}"),
            path: "/api/parts-inventory/RD-1/adjust");

        // The real response stream throws on write; next() succeeds (200) writing to the
        // filter's buffer, and the post-mutation flush onto this stream then fails.
        ctx.HttpContext.Response.Body = new WriteThrowingStream();

        Func<Task> act = () => RunAsync(filter, ctx, StatusCodes.Status200OK, "{\"ok\":true}");

        _ = await act.Should().ThrowAsync<IOException>(
            "the post-mutation flush failure must surface to the caller");

        _ = store.AbandonCalls.Should().Be(0,
            "a post-mutation flush failure must not abandon the Processing row");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        IdempotencyRecord row = await db.IdempotencyRecords.SingleAsync(CancellationToken.None);
        _ = row.Status.Should().Be(IdempotencyRecordStatus.Processing,
            "the row must remain Processing so retries get 409 until staleness reclaim frees it");
    }

    [Fact]
    public async Task PostMutation_CompleteAsyncFailure_LeavesProcessingRow()
    {
        // Hicks H-1/H-2: if CompleteAsync throws after the mutation already succeeded, the
        // filter must leave the Processing row in place (not abandon) and rethrow.
        InstrumentedStore store = new(_store)
        {
            ThrowFromComplete = new InvalidOperationException("simulated store write failure"),
        };
        IdempotencyFilter filter = CreateFilter(store);

        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, "complete-fail-key",
            Encoding.UTF8.GetBytes("{\"delta\":1}"),
            path: "/api/parts-inventory/RD-2/adjust");

        Func<Task> act = () => RunAsync(filter, ctx, StatusCodes.Status201Created, "{\"created\":true}");

        _ = await act.Should().ThrowAsync<InvalidOperationException>(
            "a post-mutation CompleteAsync failure must surface to the caller");

        _ = store.CompleteCalls.Should().Be(1,
            "CompleteAsync must have been attempted after the successful mutation");
        _ = store.AbandonCalls.Should().Be(0,
            "a post-mutation CompleteAsync failure must not abandon the Processing row");

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        IdempotencyRecord row = await db.IdempotencyRecords.SingleAsync(CancellationToken.None);
        _ = row.Status.Should().Be(IdempotencyRecordStatus.Processing,
            "the row must remain Processing so retries get 409 until staleness reclaim frees it");
    }

    [Fact]
    public async Task ChunkedOversizedBody_UnknownLength_Returns_413_AndPersistsNothing()
    {
        // Hicks H-6: the existing 413 test only covers the ContentLength-known path. A
        // chunked upload has no Content-Length and streams its payload across multiple
        // reads. The filter must still reject an over-limit body with 413, never invoke
        // the action, and never persist a record.
        IdempotencyFilter filter = CreateFilter();

        ResourceExecutingContext ctx = CreateContext(
            IdempotencyRouteKeys.PartsInventoryAdjust, "chunked-big-key", body: null,
            path: "/api/parts-inventory/RD-3/adjust");

        long total = (3L * IdempotencyFilter.MaxBufferedRequestBytes) / 2; // 1.5 MiB
        ctx.HttpContext.Request.Body = new ChunkedBodyStream(total, 64 * 1024);
        ctx.HttpContext.Request.ContentLength = null;
        ctx.HttpContext.Request.ContentType = "application/json";

        bool pipelineRan = false;
        await filter.OnResourceExecutionAsync(ctx, () =>
        {
            pipelineRan = true;
            throw new InvalidOperationException("pipeline must not run for an oversized chunked body");
        });

        _ = pipelineRan.Should().BeFalse("the action delegate must not be invoked for an oversized body");

        ObjectResult result = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        _ = result.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        _ = result.Value.Should().BeOfType<ProblemDetails>();

        using Farm.Infrastructure.Data.AppDbContext db = new(_options);
        _ = (await db.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "a rejected oversized chunked request must not persist an idempotency record");
    }

    /// <summary>
    /// Delegating <see cref="IIdempotencyStore"/> that counts Abandon/Complete calls and
    /// can force <see cref="CompleteAsync"/> to throw, so the filter's post-mutation
    /// abandon asymmetry can be asserted without mocking the store handshake away.
    /// </summary>
    private sealed class InstrumentedStore : IIdempotencyStore
    {
        private readonly IIdempotencyStore _inner;

        public InstrumentedStore(IIdempotencyStore inner) => _inner = inner;

        public int AbandonCalls;
        public int CompleteCalls;
        public Exception? ThrowFromComplete { get; init; }

        public Task<IdempotencyLookupResult> TryBeginAsync(
            string userId, string routeKey, string idempotencyKey, string requestHash, CancellationToken ct)
            => _inner.TryBeginAsync(userId, routeKey, idempotencyKey, requestHash, ct);

        public Task CompleteAsync(
            Guid recordId, int statusCode, string? contentType, byte[] responseBody, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref CompleteCalls);
            return ThrowFromComplete is not null
                ? throw ThrowFromComplete
                : _inner.CompleteAsync(recordId, statusCode, contentType, responseBody, ct);
        }

        public Task AbandonProcessingAsync(Guid recordId, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref AbandonCalls);
            return _inner.AbandonProcessingAsync(recordId, ct);
        }

        public Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct)
            => _inner.PruneExpiredAsync(now, ct);
    }

    /// <summary>A writable stream that throws on every write, to simulate a flush failure.</summary>
    private sealed class WriteThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
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

    /// <summary>
    /// A non-seekable, unknown-length read stream that yields a fixed number of bytes
    /// across multiple reads, modelling a chunked upload with no Content-Length.
    /// </summary>
    private sealed class ChunkedBodyStream : Stream
    {
        private long _remaining;
        private readonly int _chunk;

        public ChunkedBodyStream(long total, int chunk)
        {
            _remaining = total;
            _chunk = chunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int n = (int)Math.Min(Math.Min(count, _chunk), _remaining);
            Array.Fill(buffer, (byte)'x', offset, n);
            _remaining -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return new ValueTask<int>(0);
            }

            int n = (int)Math.Min(Math.Min(buffer.Length, _chunk), _remaining);
            buffer.Span[..n].Fill((byte)'x');
            _remaining -= n;
            return new ValueTask<int>(n);
        }
    }
}
