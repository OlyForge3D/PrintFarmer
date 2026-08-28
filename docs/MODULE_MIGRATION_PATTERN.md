# Module Migration Pattern

Epic #2019 decomposes the `Farm.Web.Api` monolith into ~11 vertical-slice
module assemblies (`Farm.Modules.*`), one feature area at a time. Issue #2036
("Phase 8: Pilot — Farm.Modules.SmartPlug") was deliberately chosen as the
**smallest** module — SmartPlug/PowerMonitor device providers plus one admin
controller — to validate the whole move-a-module playbook once, cheaply,
before repeating it for the ten remaining modules (Phases 9–18).

This document is that playbook. It is written from the SmartPlug PR as the
worked example; each step below names the exact files that PR touched so a
future phase can diff against them.

## Prerequisite: the `IApiModule` seam

Phase 7 (#2035, merged before this pilot) added `Farm.Modules.Abstractions`
with the `IApiModule` interface the host uses to discover and wire up
feature modules without a compile-time reference back into each module's
internals:

```csharp
public interface IApiModule
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
```

`Farm.Web.Api`'s `Program.cs` discovers modules via a single
`builder.Services.AddApiModules(mvcBuilder, builder.Configuration,
typeof(SomeModule).Assembly)` call that accepts one assembly per module and
registers each module instance in DI. Routing later calls a bare
`app.MapApiModules()` with **no assembly arguments** — it iterates the
already-registered `IApiModule` instances from DI, so only the
`AddApiModules(...)` call site needs a new assembly argument per module.
Every new module therefore adds its own `typeof(XyzApiModule).Assembly`
argument at the `AddApiModules` call site only — it does not need its own
bespoke host wiring.

## Step-by-step

Do these **in order** and commit at the checkpoints noted — do not
squash the whole migration into one commit; a reviewer needs to see the
`InternalsVisibleTo` addition landing before any file move, exactly as it
did for SmartPlug.

### 1. `InternalsVisibleTo` first, before moving anything (commit #1)

If any moved production type exposes `internal` members that its own test
file touches (SmartPlug's `KasaSmartPlugProvider` did), add
`Properties/AssemblyInfo.TestsVisible.cs` under the **not-yet-created**
module directory:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Farm.Modules.<Name>.Tests")]
```

Scaffold both new `.csproj` files (see step 2) and commit this alone, before
touching a single moved file. This lets a reviewer see the access-seam
change in isolation from the (large, mechanical) file move that follows.

### 2. New project shapes

Production: `src/modules/Farm.Modules.<Name>/Farm.Modules.<Name>.csproj`
- Plain `Microsoft.NET.Sdk` (not `.Sdk.Web` — that's `Farm.Web.Api`'s only).
  Plain SDK projects do **not** get the same implicit global usings as a Web
  SDK project; add explicit `using` directives for
  `Microsoft.AspNetCore.*`/`Microsoft.Extensions.*` types as needed instead
  of relying on ambient usings.
- `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for
  controller/DI/hosting types.
- `<ProjectReference>` to `../../infra/Farm.Infrastructure.csproj` (almost
  every module needs this) and
  `../Farm.Modules.Abstractions/Farm.Modules.Abstractions.csproj` (for
  `IApiModule`).

Tests: `src/tests/Farm.Modules.<Name>.Tests/Farm.Modules.<Name>.Tests.csproj`
- Modeled on `Farm.Modules.Abstractions.Tests.csproj`.
- `<ProjectReference>` to the new production csproj plus
  `Farm.Infrastructure.csproj`.
- Explicit xunit/FluentAssertions/Moq package references and a `Usings.cs`
  with `global using` for the test framework — plain SDK test projects don't
  inherit these either.

### 3. Move files — namespaces unchanged (move-first-rename-last)

`git mv` (or move + re-stage) each production/test file into its new
directory. **Do not rename namespaces as part of the move.** A controller
that was `Farm.Web.Api.Controllers.Admin.FooController` keeps that literal
namespace even though it now lives in `Farm.Modules.<Name>.dll` — the
assembly changes, the namespace does not. This keeps the diff to "file moved
+ new project wiring" instead of "file moved + every reference updated",
and is what makes the route-table-snapshot diff in step 8 a pure
assembly-qualifier substitution instead of a namespace rewrite.

Watch for **file-path-scoped `.editorconfig` rules** that do not follow a
`git mv` automatically. `src/.editorconfig` has ~15+
`dotnet_diagnostic.*.severity = none` (or similar) blocks scoped to a
literal glob like `[api/Services/SmartPlug/*.cs]`. If a moved file was
covered by one of these, update the glob to the new path
(`[modules/Farm.Modules.<Name>/Services/SmartPlug/*.cs]`) in the **same**
commit as the move, or the analyzer/StyleCop warning it was suppressing
will reappear at the new location and silently fail a "no new warnings"
gate.

### 4. Write the `IApiModule` implementation

One `<Name>ApiModule : IApiModule` class per module. Move every
module-scoped DI registration (`AddSingleton<TInterface, TImpl>()`,
named `HttpClient` registrations, `AddHostedService<T>()`, etc.) out of the
host's `ServiceCollectionExtensions.cs` / `BackgroundServicesStartup.cs` and
into this class's `ConfigureServices`. Anything genuinely host-wide (nothing
was, for SmartPlug) stays behind.

### 5. Rewire the host

- `src/api/Farm.Web.Api.csproj`: add a `<ProjectReference>` to the new
  module csproj. Moved files simply disappear from the SDK-style implicit
  glob once deleted — no `<Compile Remove>` needed.
- `src/api/Infrastructure/ServiceCollectionExtensions.cs` /
  `src/api/Startup/BackgroundServicesStartup.cs`: delete the registrations
  that moved into the module's `ConfigureServices`. If removing a `using`
  orphans a comment, watch for StyleCop's blank-line-around-comment rules
  (`SA1512`/`SA1515`) — the fix is to move the blank line to *before* the
  comment, not delete it.
- `src/api/Program.cs`: add `typeof(<Name>ApiModule).Assembly` to the
  existing `AddApiModules(...)` call only — `MapApiModules()` takes no
  assembly arguments and needs no change.

### 6. `src/farm-web.sln`

Add both new projects using the same C# project-type GUID
(`{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`) as existing entries, with new
random project GUIDs, plus their `GlobalSection` build-configuration lines —
mirror the `Farm.Modules.Abstractions` / `Farm.Modules.Abstractions.Tests`
block exactly.

### 7. Route-table snapshot regeneration (if the module owns any controller)

`RouteTableSnapshotTests` (`src/tests/Farm.Web.Api.Tests/Startup/`) is a
guardrail against accidental route changes; it is not selected by a
filter like `FullyQualifiedName~<Name>` because its own test method name
never contains the module name — **run it explicitly by class name** in
addition to your module's targeted filter. Moving a controller changes only
its **assembly qualifier** in the checked-in snapshot text
(`Farm.Web.Api::Farm.Web.Api.Controllers.Admin.FooController` →
`Farm.Modules.<Name>::Farm.Web.Api.Controllers.Admin.FooController`) — the
namespace, HTTP verb, route template, and action name are all unchanged
because of step 3's move-first-rename-last rule. Regenerate with a targeted
string substitution (not a blind re-dump) so the diff is auditable as
"assembly qualifier only" and re-run the test to confirm it passes.

### 8. CI wiring

1. **`scripts/ci/dotnet-test-manifest.json`**: add a new entry —
   `name`, `testProject`, `productionProject`, `pathPrefixes`,
   `dependsOnProjects`, `defaultFilter`, `shards: []`,
   `requiresProviders: []`, `runIntegration: false`, and a `leg` matching
   `name`. Model it on the `Farm.Modules.SmartPlug.Tests` entry.
2. **`scripts/ci/select-dotnet-tests.sh`**: add a **narrow** bucket for
   `src/modules/Farm.Modules.<Name>/**` (and its test-path counterpart under
   `src/tests/`), ordered **before** the generic `src/modules/*` /
   `src/tests/*` catch-alls in `classify_path()` (first match wins — more
   specific patterns must come first). Do **not** put a concrete leaf
   module into the broad full-safe `modules` bucket that
   `Farm.Modules.Abstractions` uses — that bucket exists because the
   Abstractions seam is foundational and every future module references it;
   a concrete module is a leaf and must select only its own leg, per the
   "≤7 min, only its leg" acceptance criterion. If the module depends on
   `Farm.Infrastructure` (most will), also add its test project name to the
   existing `has_infra` block so an infra-only change doesn't miss it.
   **If the module owns a controller** (as SmartPlug does), and that
   controller's own coverage — `RouteTableSnapshotTests` and/or a
   `CustomWebApplicationFactory`-based integration test — stays behind in
   `Farm.Web.Api.Tests` rather than moving with the module (see step 7),
   the new bucket must **also** select `Farm.Web.Api.Tests`, or a change to
   that controller silently loses CI coverage of its own route/contract.
   A module that moves only services with no owned controller (e.g. a
   future pure-worker module) can stay as narrow as `orca_worker`.
3. **`scripts/ci/tests/test-select-dotnet-tests.sh`**: add positive
   (production-path-only), test-path-only, and mixed-path (module path +
   unrelated path, confirming it does *not* go full-safe) cases, and extend
   the existing infra case's assertions to include the new test project.
   Register every new `case_*` function in the `TESTS=(...)` list at the
   bottom.
4. **`scripts/ci/generate-codeql-slnf.sh`**: no code change — it derives the
   CodeQL build filter from `.sln` `Project(...)` entries, excluding
   anything under `tests/`. Regenerate and spot-check the new production
   project is present and the `.Tests` project is excluded.
5. **Dockerfiles**: `scripts/docker/dockerfiles/Dockerfile.api` and
   `scripts/docker/dockerfiles/Dockerfile` each restore a curated subset of
   `.csproj` files before the full `COPY src/ ./` for Docker layer-cache
   efficiency. Add
   `COPY src/modules/Farm.Modules.<Name>/*.csproj ./modules/Farm.Modules.<Name>/`
   immediately before the `COPY src/api/*.csproj ./api/` line in **both**
   files — it must precede the early API restore, since
   `Farm.Web.Api.csproj` now references it. `Dockerfile.multistage` needs no
   change; it copies the whole `src/` tree in one layer and has no per-project
   restore optimization to update. Per
   `.github/instructions/docker-file-hierarchy.instructions.md`, these two
   files under `scripts/docker/dockerfiles/` **are** the source of truth —
   do not edit the generated root/`dockerfiles/` copies directly.
6. **`docs/CI.md`**: add rows for the new bucket(s) to the "Bucket →
   downstream mapping" table, and extend the `infra` row if the module
   depends on `Farm.Infrastructure`.

### 9. Validate

- `cd src && dotnet build ./farm-web.sln -c Debug` — no new warnings.
- `dotnet test ./farm-web.sln -c Debug --no-build --filter "FullyQualifiedName~<Name>" --settings ./vstest.runsettings --blame-hang --blame-hang-timeout 10m` — covers the new test project; run `RouteTableSnapshotTests` explicitly too (step 7).
- `dotnet format ./farm-web.sln --verify-no-changes` — the `edit`/file-move
  tooling can silently strip a file's UTF-8 BOM; `src/.editorconfig`
  requires `charset = utf-8-bom` for all `.cs` files, and `dotnet format`
  reports a stripped BOM as a `CHARSET` error. Restore it with
  `[System.IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($true)))`
  if this happens.
- `bash scripts/ci/tests/test-dotnet-test-manifest.sh` and
  `bash scripts/ci/tests/test-select-dotnet-tests.sh` — both must be green
  with zero regressions to pre-existing cases.

## Non-goals (every phase)

No `/api/*` contract change, no behavior change, no test dropped or
duplicated (verify the moved-file count matches the new project's test
count exactly), no new required CI check names.

## Target invariant (Phase 20 guardrail, epic close-out)

Phase 20 (#2048) is the epic's final, cleanup-only phase — no further module
extraction happens here. It removed the last test-only product code that had
accumulated in `Farm.Web.Api` (`Services/TestHelpers/PrinterInfoFactory.cs`,
`TestStartupFilter.cs` — both zero-reference or test-only, never belonging in
the API DLL) and added a permanent guardrail so the host cannot silently
re-accumulate the kind of code this epic spent 19 phases removing.

**Guardrail test:**
`src/tests/Farm.Web.Api.Tests/Architecture/HostNamespaceGuardrailArchitectureTests.cs`
reflects over `typeof(Program).Assembly.GetTypes()` and asserts every
non-nested type under a `Farm.Web.Api.Controllers` or `Farm.Web.Api.Services`
namespace (or sub-namespace) is on an explicit allowlist of the host's actual
current scope. A new controller/service landing in the host without a
reviewed allowlist entry fails this test immediately, forcing the same
choice the epic made 11 times: move it into a `Farm.Modules.*` module, or
justify — in the allowlist comment — why it is genuinely host-scoped
(`Program.cs`/`ProgramHelpers.cs` wiring, `Startup/**`, `Middleware/**`,
`Infrastructure/**`, `Health/**`, `Authorization/**`,
`Validators/CreateManualTaskValidator.cs`, and the small set of controllers
and `Services/Startup`, `Services/StorageManagement`, `Services/SlicerHost`
types that were never module candidates).

The allowlist reflects the **actual** host-scoped surface as measured at
Phase 20, not the epic's original phase-planning snapshot — some controllers
that snapshot named as staying (e.g. `BackgroundServicesController`,
`SignalRTestController`) had since moved into `Farm.Modules.Observability` in
earlier phases, while other controllers not named in that snapshot
(`AssetsController`, `CalibrationCapabilitiesController`,
`InternalSlicerHostLookupsController`, `LibrarySyncController`,
`OctoPrintCompatController`, `PredictionController`,
`PrintProjectTemplatesController`, `ReportExportController`,
`SystemLogsController`) are genuinely host-scoped. Enforcing today's real
boundary — rather than the stale aspirational one — is what makes the
guardrail actually catch regressions instead of immediately failing or
under-protecting.

**Final measured `src/api` size** (production `.cs` files only, excluding
`obj`/`bin`): **63 files / ~8,515 LOC**, down from the epic's 61,068 LOC /
255-file starting point and comfortably inside the ≤10,000 LOC target; just
above the ≤60-file target (63 vs. 60) after accounting for a handful of
controllers/services that were always legitimately host-scoped and were
never candidates for module extraction — see the epic's own non-goal
("no further module extraction — cleanup only") for why this phase does not
chase the file count further.

**Manifest schema recap:** no new module or test project is introduced by
this phase, so `scripts/ci/dotnet-test-manifest.json` needs no new
top-level entry — but the guardrail test's new `Architecture/` namespace
*does* need to be added to an existing `Farm.Web.Api.Tests` shard's
`namespacePrefixes`/`filter` (it was added to the `core` shard), because
`scripts/ci/tests/test-dotnet-test-manifest.sh` asserts every namespace
subdirectory under a sharded test project's directory is claimed by
exactly one shard — an unclaimed namespace is silently excluded from every
CI leg. CI continues to assert every `*.Tests.csproj` is registered in the
manifest exactly once (`scripts/ci/tests/test-dotnet-test-manifest.sh`).
