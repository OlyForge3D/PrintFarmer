# Testing Patterns & Best Practices

This document captures common patterns and best practices discovered during test development for PrintFarmer.

## Queue lifecycle clocks

Inject the same `TimeProvider` into dispatch claims, reconciliation, the SignalR
outbox publisher, and the print-job repository when testing a connected lifecycle.
Forward it to `QueueAuditWriter.Add` and `AddLifecycleOutboxEventAsync`; omitting it
uses the system clock even when the caller has a fake clock. `DispatchLog` requires
a caller-owned instant and initializes all three creation timestamps from it.
Its private parameterless constructor is only for EF materialization.

Pin exact persisted timestamps after reloading the context, not merely
`timestamp <= DateTime.UtcNow`. Cover strict stale-lease/attempt boundaries,
inclusive retry/lookback boundaries, preserved acceptance/end timestamps, and
completion sampled after delivery. Advance fake timers for polling rather than
sleeping. `QueueProductionCallChainTests` contains the reconciliation/publisher
clock cases, including timer cancellation.

This first wave (#2880) does not make every queue service deterministic:
second-wave service clocks, upload-progress timing, retention, and settings
timestamps are tracked separately in #2972.

## React printer response contracts

`npm run typecheck:app` and `npm run typecheck:test` reject every diagnostic,
including direct test, helper, imported-source, and global errors. Neither gate
has a diagnostic allowance: swapping errors while holding the count constant
still fails, and removing all errors passes without a baseline edit (#2827).
Diagnostic debt was removed by #2901 and #2932; fingerprint allowances are
unnecessary under this strict-zero policy.

`scripts/test-typecheck-baseline.json` retains only the minimum test-file
coverage floor. `scripts/app-typecheck-baseline.json` retains the application
file floor and exact `@ts-nocheck` count. Keep both compiler gates and
`typecheck:src-coverage` enabled; diagnostic cleanup must not weaken coverage.

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

### Typecheck cleanup contract audit (#2799)

The historical diagnostic counts describe different compiler projects and
different commits, not additive error totals. `typecheck:test` reports direct
test/helper diagnostics separately from imported source diagnostics;
`typecheck:app` checks the application project, including files not imported by
tests. Record the SHA and all three counts together. File floors and
`typecheck:src-coverage` must remain enabled when diagnostic debt is removed.

The following contracts were traced on development
`83f1419816cbcd038f0885ad74506b47599690f5`; already-merged fixes are retained,
not reimplemented. Paths below are relative to the repository root.

**Queue copies and ID.** `src/infra/Dtos/QueueDtos.cs` exposes flat job `id`,
`copies`, `completedCopies`, `remainingCopies`. The analytics envelope in
`src/infra/Dtos/PrintQueue/PrintQueueDtos.cs` instead contains `job.id`, with no
second top-level `id`. React `PrintJob`, `QueuedPrintJobDto` and
`QueuedPrintJobWithFileMetaDto` preserve that distinction.
`src/tests/Farm.Web.Api.Tests/Contracts/PrintQueueContractTests.cs` checks real
HTTP counts and omitted optional fields;
`src/Web/ReactApp/src/services/__tests__/printJobQueueService.test.ts` verifies
the flat enqueue response.

**Printer URL, reachability, enums.** `src/infra/Dtos/PrinterDto.cs` and
`CompletePrinterDto.cs` suppress `backendUrl`. `isOnline` is the wire field;
optional `isReachable` is client-only. Existing string enum values are retained,
including `ToolheadType.Physical`, rather than numeric fixture ordinals.
Evidence: `src/Web/ReactApp/src/test/types/printerResponseContract.test.ts`,
`enumWireContract.test.ts` in that directory, and
`src/Web/ReactApp/src/features/slicer/utils/__tests__/profileMatcher.test.ts`.

**Notification user.** `src/infra/Models/Notifications/Notification.cs` has
non-null `Guid UserId`; `NotificationsController.GetNotificationsAsync` returns
these records. `notificationsApi.getNotifications` passes them through and
`NotificationDto.userId` is a required string, not a field to remove from
fixtures. `src/Web/ReactApp/src/test/components/NotificationDrawer.test.tsx`
uses typed notifications with `userId`.

**Failure fields.** Main MVC and SignalR serializers omit nulls
(`src/api/Startup/ControllerStartup.cs`, `SignalRStartup.cs`).
Queue `FailureReason` is nullable C# and optional TypeScript.
Slicer `SliceJobStatusResponse.failureReason` accepts omission and explicit
null; its enum classification is distinct from queue free-text failure reasons.
Do not globally replace optional fields with required nulls.
`PrintQueueContractTests.cs` and `SliceJobContractTests.cs` under
`src/tests/Farm.Web.Api.Tests/Contracts/` assert missing failure keys from actual
HTTP responses.

**NFC read time.** `src/infra/Services/NfcDevices/NfcTagService.cs`,
`ProcessTagReadAsync`, emits `readAt`; `LinkNfcTagRequest.ReadAt` in
`src/infra/Dtos/NfcDeviceDtos.cs` is optional for manual linking. Both NFC
binding/pairing modals forward the scan timestamp; `LinkTagAsync` uses it for
spool last-seen state. Event `readAt` remains required, request `readAt` optional.
`src/Web/ReactApp/src/features/nfc/components/__tests__/NfcBindingModal.test.tsx`
checks timestamp forwarding and an omitted spool ID.

**File size.** `src/infra/Dtos/GcodeFileDtos.cs` exposes required `long FileSize`,
serialized as numeric `fileSize`. `GcodeFileCard` and `GcodeFileBrowser` consume
it. Analytics `QueueGcodeFileMetaDto.FileSizeBytes` is a different DTO and retains
`fileSizeBytes`; do not rename either globally. Evidence:
`src/Web/ReactApp/src/features/gcode/components/__tests__/GcodeFileCard.test.tsx`
and `src/Web/ReactApp/src/features/fileBrowser/__tests__/fileBrowserViews.test.tsx`.

**Slicer job counts.** `slicerRegistry.getSlicers` maps `/workers/` records into a
discovery `SlicerDto` without `activeJobs`/`queuedJobs`; its callers must not
invent required count fields. `SlicerServiceResponse` in
`src/slicer/Farm.Slicer.Module/Contracts/SlicerDtos.cs` also has no such fields.
Worker management's separate `WorkerResponse.ActiveJobs` in
`src/slicer/Farm.Slicer.Module/Contracts/WorkerDtos.cs` is real and retained.
`src/Web/ReactApp/src/services/__tests__/slicerRegistry.test.ts`
checks the exact mapped discovery shape; `workersService.test.ts` in that
directory covers worker-management counts.

Controller and SignalR serialization use camelCase names and string enums
(`ControllerStartup.cs:28-36`, `SignalRStartup.cs:35-45`). These conclusions do
not authorize changes to mobile DTOs, backend serialization, or worker contracts.
Runtime tests alone do not prove fixture typings: run both compiler gates.

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

### Host-state persistence platform prerequisites

Production `HostStateFileSecurity` (`src/infra/Services/HostUpdates/HostStateStorage.cs`)
is fail-closed on two host properties, and `Farm.Infrastructure.Tests` must never relax
either check to make a local run pass (issue #2977):

- **No symlink/reparse path components.** Any symlink in a host-state path is rejected
  with `host_state_reparse_path_rejected`. The default macOS `TMPDIR`
  (`/var/folders/...`) goes through the `/var` -> `/private/var` symlink, so host-update
  tests build host-state paths from `HostStateTestPaths.TempRoot` or
  `HostStateTestPaths.CreateTempSubdirectory(prefix)` (in
  `src/tests/Farm.Infrastructure.Tests/Services/HostUpdates/HostStateTestPlatform.cs`),
  which resolve `Path.GetTempPath()` to its physical path once. They do not use
  `Path.GetTempPath()` / `Directory.CreateTempSubdirectory()` directly. If the temp
  directory cannot be resolved, every test fails with a message that says to point
  `TMPDIR` at a physical directory.
- **Owner validation is Linux- (or Windows-attestation-) only.** A validated host-state
  root needs the Linux `statx` owner check or `WindowsSecurityAttested`. On every other
  platform, including macOS, root validation fails closed with
  `host_state_unix_owner_validation_unavailable`. Tests that need a validated root use
  `[HostStateOwnerValidationFact]` / `[HostStateOwnerValidationTheory]`, which xUnit
  reports as **skipped with that reason** rather than failed.
  `HostStateRoot_FailsClosedWhereUnixOwnerValidationIsUnavailable`
  (`[HostStateOwnerValidationUnavailableFact]`) runs only on those platforms. It asserts
  that both a secure root and an insecure root are rejected with that code, and that
  nothing is written.

With both in place, this supported local command reports no host-state failures on macOS.
It shows the owner-validation skips, and CI on Linux runs every case:

```bash
cd src
dotnet test tests/Farm.Infrastructure.Tests/Farm.Infrastructure.Tests.csproj -c Debug \
  --settings ./vstest.runsettings --filter "FullyQualifiedName~Services.HostUpdates"
```

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
