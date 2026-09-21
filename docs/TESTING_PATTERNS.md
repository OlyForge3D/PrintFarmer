# Testing Patterns & Best Practices

This document captures common patterns and best practices discovered during test development for PrintFarmer.

## React printer response contracts

`npm run typecheck:test` rejects every direct test, helper, imported-source, or
global diagnostic. `scripts/test-typecheck-baseline.json` retains only the
minimum test-file coverage floor; there is no diagnostic allowance. Keep the
application typecheck and source-coverage gate enabled alongside it.

Printer read fixtures must not invent required connection fields.
`GET /api/printers` returns `CompletePrinterDto`; `GET /api/printers/{id}`
returns `PrinterDto`. Both suppress `backendUrl` during serialization and
report reachability as `isOnline`, not `isReachable`. The latter is an optional
client-side alias, not an API requirement. Null-valued fields are omitted by
the controller serializer. `backend` and `motionType` use PascalCase string
enum values (`Moonraker`, `PrusaLink`, `CoreXY`), never numeric ordinals.

`src/test/types/printerResponseContract.test.ts` supplies compile-checked
response examples; `src/test/services/api.test.ts` checks API pass-through.
Run `npm run typecheck:test` as well as focused Vitest tests: Vitest alone
does not validate `satisfies Printer` assertions. Do not fix fixture errors by
adding fields that the backend deliberately omits.

## Direct motion-control testing

Current test entry points:

- `Farm.Modules.Printers.Tests.Controllers.PrinterDirectControlTests`: direct
  controller outcomes, SQLite actuation barriers, manual safety validation,
  expired-orphan admission races, and late-settlement successor protection.
- `Farm.Backend.Plugins.Tests.Backends.MoonrakerDirectControlTests`: authenticated
  movement observations, effective frames, invariant G-code state save/restore,
  and single-send behavior. Pair with `MoonrakerVerifiedSafetyTests` in the same
  project for backend safety evidence.
- `Farm.Web.Api.Tests.Security.RemovedPrinterControlRoutesTests`: authenticated
  ordinary/admin requests to removed admission, current-state, receipt, and
  recovery routes must return `404` without writes.
- `PrintersControllerControlGuardsTests` in the printers module test project:
  retained controller protections.
- `PrinterControlOwnershipTests`, `PrinterStatusClientTests`,
  `PrinterSafetyGuardTests`, and `RevisionConcurrencyProviderTests` in
  `Farm.Infrastructure.Tests`: plugin ownership, status/safety evidence, and
  provider concurrency behavior.
- `FinalFactCheckerRemediationTests` and `QueueProductionCallChainTests` in
  `Farm.Web.Api.Tests/Dispatch`: retained dispatch safeguards.
- `TrackedMotionRetirementMigrationTests`: populated legacy-storage retirement
  and preservation checks.

Exercise the existing `/home`, `/homexy`, `/homez`, `/move`, and `/moveto`
controller routes with fake backend I/O and real database actuation fencing.
Assert ordinary `CommandResult` acceptance, not physical completion. Cover
authorization and printer access, concurrent command/dispatch exclusion,
explicit backend failures, cancellation, the five-minute request bound, and
ambiguous writes without automatic retry.

Manual uncertainty must retain `Unknown` audit evidence and release its barrier
only after I/O settles. Test late-settlement successor protection and reclaiming
a crashed direct manual barrier on a new explicit admission only after its
timeout plus 45 seconds. Preserve unrelated print-job and attempt-bound fences.

HTTP contract regressions must assert `404` for all removed control-operations
admission, receipt, current-state, and recovery routes. Printer DTO and SignalR
contract tests must not expect `physicalControl` or
`printercontroloperationupdated`. Update the route snapshot rather than
preserving aliases for deleted routes.

Plugin tests use fake authenticated HTTP responses, not a tracked WebSocket
channel or real hardware. Verify a single command send, credential preservation,
G-code state save/restore, and zero sends for missing, stale, unhomed, nonfinite,
or out-of-envelope movement evidence. Include multi-axis and sparse requests,
effective G92-aware frames, and fresh authenticated position/homing observations.
Manual jog/absolute movement must not require automated minimum Z clearance;
keep separate regressions for unchanged automated clearance protections.

Shared service tests exercise semantic plugin delegation without constructing
backend-specific transports or scripts. Script assertions belong in the plugin
tests. Retain emergency-stop regressions independently of the deleted
tracked-motion machinery.

Upgrade tests must exercise populated legacy storage, not just empty-schema
creation. Verify tracking tables are renamed to unmapped archives, pending
tracking invalidations are dead-lettered without losing evidence, and
history/audits remain intact. Cleanup must be restricted to eligible manual-only
ownership without clearing active print, reconciliation, or attempt-bound fences.
Assert that down migration is unsupported. Keep provider migration lists and
model-drift checks aligned; generated PostgreSQL/SQL Server SQL is not evidence
of live-server execution. Deployment requires stopping all old API instances
and workers before migration; tests do not establish that a mixed-writer upgrade
is safe.

React and iOS regressions should assert request-bounded pending state, visible
failure/timeout outcomes, and no journal, receipt polling, recovery UI, operation
ID, or replay after reconnect. Guided/Expert presentation is a separate concern.

`MoonrakerCameraRoutingTests` uses fake HTTP responses to assert that discovery
and webcam-test requests reach the API port, while relative camera URLs retain
the frontend port. No live camera or printer is needed.

Run test projects sequentially when they share build output directories.
Capture console verbosity `normal` (or TRX) so failures retain their actual stack
traces; quiet-only output loses useful diagnostics. No physical printer or
deployment call is needed. Provider model changes require PostgreSQL, SQL Server
and maintained SQLite migrations; verify all deployment model-drift gates.

## SignalR Hub Testing

### Required Mocks
```csharp
private readonly Mock<IHubCallerClients> _clientsMock;
private readonly Mock<ISingleClientProxy> _callerMock;  // NOTE: ISingleClientProxy, not IClientProxy
private readonly Mock<IClientProxy> _groupMock;
private readonly Mock<IGroupManager> _groupsMock;
private readonly Mock<HubCallerContext> _contextMock;
```

### Setup Pattern
```csharp
// Hub context
_contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
_contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

// Clients
_clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);
_clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupMock.Object);

// Instantiate hub with properties
_hub = new YourHub(dependencies...)
{
    Clients = _clientsMock.Object,
    Groups = _groupsMock.Object,
    Context = _contextMock.Object
};
```

### Verification Pattern
```csharp
// Verify SendCoreAsync (not SendAsync)
_callerMock.Verify(c => c.SendCoreAsync(
    "eventname",
    It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], expectedData)),
    It.IsAny<CancellationToken>()), 
    Times.Once);

// Verify group operations
_groupsMock.Verify(g => g.AddToGroupAsync(
    "connection-id",
    "group-name",
    It.IsAny<CancellationToken>()), 
    Times.Once);
```

## Record Type DTOs

### Instantiation with Positional Parameters
```csharp
// ✅ CORRECT - Use named parameters
var dto = new DiscoveryProgressDto(
    SessionId: "test-session",
    CurrentNetwork: "192.168.1.0/24",
    CurrentIp: "192.168.1.100",
    TotalIps: 100,
    ScannedIps: 50,
    PrintersFound: 2,
    PrintersExcluded: 0,
    ProgressPercentage: 50,
    Status: DiscoveryStatus.Scanning
);

// ❌ WRONG - Object initializer syntax doesn't work with positional records
var dto = new DiscoveryProgressDto
{
    SessionId = "test-session",  // Compilation error!
    ProgressPercentage = 50
};
```

### Verification in Mocks
```csharp
// ✅ Use ReferenceEquals for record comparison
It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], expectedDto))

// ⚠️ Avoid == operator (causes warnings about reference comparison)
It.Is<object[]>(args => args.Length == 1 && args[0] == expectedDto)  // Warning CS0252
```

## Factory Pattern Testing

### Constructor Null Validation
```csharp
[Fact]
public void Constructor_WithNullDependency_ThrowsArgumentNullException()
{
    // Test EACH constructor parameter individually
    Assert.Throws<ArgumentNullException>(() => new Factory(
        null!,  // Test this parameter
        mock2.Object,
        mock3.Object
    ));
}
```

### GetClient Pattern
```csharp
[Fact]
public void GetClient_WithValidBackend_ReturnsCorrectClient()
{
    // Arrange
    var factory = CreateFactory();

    // Act
    var client = factory.GetClient(PrinterBackend.Moonraker);

    // Assert
    Assert.NotNull(client);
    Assert.IsAssignableFrom<IBackendClient>(client);  // Marker interface
    Assert.IsAssignableFrom<IMoonrakerClient>(client);  // Specific interface
    Assert.Same(_moonMock.Object, client);  // Reference equality
}
```

### Error Handling
```csharp
[Fact]
public void GetClient_WithInvalidBackend_ThrowsArgumentException()
{
    // Arrange
    var factory = CreateFactory();

    // Act & Assert
    var ex = Assert.Throws<ArgumentException>(() => 
        factory.GetClient((PrinterBackend)999));
    Assert.Contains("Unsupported printer backend", ex.Message);
}
```

## Logger Mock Verification

### ILogger Extension Method Pattern
```csharp
// Verify logging calls using ILogger extension method patterns
_loggerMock.Verify(
    x => x.Log(
        LogLevel.Information,
        It.IsAny<EventId>(),
        It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("expected text")),
        It.IsAny<Exception>(),
        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
    Times.Once);
```

## Test Organization

### File Structure
```
/tests/Farm.Web.Api.Tests/
├── Controllers/
│   └── PrintersControllerTests.cs
├── Services/
│   ├── BackendClientFactoryTests.cs
│   └── Printers/
│       └── PrintersServiceTests.cs
├── Hubs/
│   └── PrinterHubTests.cs
└── Integration/
    └── PrinterIntegrationTests.cs
```

### Test Naming Convention
```csharp
// Pattern: MethodName_Scenario_ExpectedBehavior
[Fact]
public void GetClient_WithMoonrakerBackend_ReturnsMoonrakerClient()
{
    // ...
}

[Fact]
public void Constructor_WithNullLogger_ThrowsArgumentNullException()
{
    // ...
}
```

### Test Categories
```csharp
// Use Arrange-Act-Assert pattern
[Fact]
public void SomeTest()
{
    // Arrange - Setup test data and mocks
    var expected = "value";
    var mock = new Mock<IDependency>();
    
    // Act - Execute the method under test
    var result = _sut.Method(expected);
    
    // Assert - Verify expected behavior
    Assert.Equal(expected, result);
}
```

## Common Pitfalls

### ❌ Wrong ClientProxy Type
```csharp
// WRONG - IClientProxy doesn't work for Caller
private readonly Mock<IClientProxy> _callerMock;

// CORRECT - Use ISingleClientProxy
private readonly Mock<ISingleClientProxy> _callerMock;
```

### ❌ Missing Hub Property Assignment
```csharp
// WRONG - Hub properties not set
var hub = new PrinterHub(deps...);

// CORRECT - Assign mock properties
var hub = new PrinterHub(deps...)
{
    Clients = _clientsMock.Object,
    Groups = _groupsMock.Object,
    Context = _contextMock.Object
};
```

### ❌ Record Equality Comparison
```csharp
// WRONG - == causes reference comparison warning
args[0] == expectedDto  // Warning CS0252

// CORRECT - Use ReferenceEquals explicitly
ReferenceEquals(args[0], expectedDto)
```

## Coverage Goals

### Target Metrics
- **Overall Method Coverage**: 50% (current: 34.3%)
- **Critical Components**: 80%+ (controllers, core services)
- **Utility Classes**: 60%+ (helpers, extensions)
- **Integration Tests**: Key user workflows covered

### High-Impact Areas
1. **Controllers** - REST endpoints (highest coverage per test)
2. **SignalR Hubs** - Real-time communication
3. **Core Services** - Business logic (PrintersService, SlicerService, etc.)
4. **Repository Layer** - Data access patterns
5. **Integration Tests** - End-to-end workflows

### Coverage Calculation
- **Approximate ratio**: ~175 tests per 1% method coverage increase
- **Session efficiency**: ~35 tests in 45 minutes = ~0.2% coverage gain
- **Target gap**: 15.7 percentage points = ~2,750 additional tests
- **Realistic timeline**: 6-8 sessions of similar scope to reach 50%

## Testing Checklist

Before committing new tests:
- [ ] All tests pass: `dotnet test ./farm-web.sln -c Debug`
- [ ] Build succeeds: `dotnet build ./farm-web.sln -c Debug`
- [ ] No new warnings introduced
- [ ] Coverage report shows improvement
- [ ] Tests follow naming conventions
- [ ] Arrange-Act-Assert pattern used consistently
- [ ] Null validation tests for all constructor parameters
- [ ] Error cases tested (exceptions, invalid inputs)
- [ ] Edge cases covered (empty collections, boundary values)
- [ ] Integration with existing test suite verified

## Bounded Test Execution

The documented full-solution command is `dotnet test ./farm-web.sln -c Debug --settings
./vstest.runsettings --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type mini`
(run from `src/`). Two independent bounds keep this command from ever hanging
indefinitely (issue #2013):

- **`src/vstest.runsettings`** sets `RunConfiguration/TestSessionTimeout` (40 minutes) —
  VSTest aborts the entire `dotnet test` invocation across every project in the solution
  if it runs longer than that, well above the normal full-suite duration observed in CI
  and on Windows/Linux hosts.
- **`--blame-hang-timeout 10m`** bounds an individual testhost: if VSTest observes no
  test start/finish activity for that long, it kills the stalled testhost (and any child
  processes) and writes a sequence file plus a `--blame-hang-dump-type mini` dump
  identifying exactly which test was in flight. This turns a silent multi-hour hang into
  a fast, diagnosable failure.

Issue #2013 reproduced `Farm.Slicer.Module.Tests`' testhost remaining hung for 4+ hours
on a macOS QA host with no further output, after `Farm.Web.Api.Tests` completed with 35
known environment-shaped failures (below). The exact hang was not reproducible on Windows
— the same project completes cleanly in a few minutes there — so rather than guess at a
macOS-only root cause blind, this bounding is a defensive guard: whatever stalls a
testhost locally now fails fast with actionable diagnostics instead of hanging silently
for hours. If the guard ever fires again, the blame sequence/dump files under the test
project's `TestResults/` directory identify the exact hung test, which should replace
this rationale with a targeted fix once known.

### Known environment-only local failures (macOS)

These `Farm.Web.Api.Tests` failure categories are environment/platform limitations, not
product regressions — exact-SHA GitHub CI is green for the same commit. Do not try to
force them to pass on an unprovisioned local host:

- **Unsupported artifact leases** — macOS-specific file-locking/lease APIs the test
  exercises are not available on that platform.
- **Unprovisioned PostgreSQL/SQL Server provider variables** — tests that require a real
  Postgres or SQL Server connection string are skipped in CI's matrix but fail locally
  when those env vars are not set; provision the provider (e.g. via Docker) or skip that
  filter locally, e.g. `dotnet test --filter "Category!=RequiresPostgres&Category!=RequiresSqlServer"`.
- **A downstream timeout** caused by the unprovisioned provider above.
- **A SQLite active-statement/collation error** — an ordering/locking difference in the
  SQLite native library between the CI Linux runners and local macOS.

## Useful Commands

```bash
# Run all tests
dotnet test ./farm-web.sln -c Debug --settings ./vstest.runsettings --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type mini

# Run specific test class
dotnet test --filter "FullyQualifiedName~BackendClientFactoryTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./tests/coverage

# Count tests
find tests -name "*Tests.cs" | wc -l

# Check coverage
dotnet test ./farm-web.sln -c Debug 2>&1 | grep -A 15 "Module"
```

## References

- [xUnit Documentation](https://xunit.net/)
- [Moq Documentation](https://github.com/moq/moq4)
- [ASP.NET Core Testing](https://docs.microsoft.com/en-us/aspnet/core/test/)
- [SignalR Testing](https://docs.microsoft.com/en-us/aspnet/core/signalr/testing)
- [FluentAssertions](https://fluentassertions.com/) (if used in project)
