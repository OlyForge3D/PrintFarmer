# Running E2E Tests

## Existing Visual / Navigation Tests

These run against the dev server alone (no API data required):

```bash
cd src/Web/ReactApp
npm run test:e2e                     # all E2E tests
npm run test:e2e:chromium            # Chromium only
npm run test:e2e -- --project=firefox  # Firefox only
```

## Monolith Smoke Journeys (Wire-Contract Corpus)

`e2e/monolith-smoke/` (issue #2286) is a small, bounded set of authenticated
browser/API journeys against the real monolith API + React app that assert
**intercepted response payloads** — not just rendered text — structurally
against the canonical, read-only wire-contract corpus at
`fixtures/wire-contracts/api/**` (issue #2238), via the shape comparator at
`src/test/wireContractShape.ts` built on the existing `src/test/
wireContracts.ts` loader.

This is deliberately narrow: it is **not** a conversion of the other 40+
existing e2e specs under `e2e/`, and it does not merge with
`tests/ui-validation/`. It covers exactly:

| Spec | Surface | Endpoint | Corpus fixture |
|------|---------|----------|-----------------|
| `admin-overview-smoke.spec.ts` | Admin/settings | `GET /api/admin/overview` | `api/admin-overview/overview.live-shape.json` |
| `print-queue-smoke.spec.ts` | Printer/queue | `GET /api/job-queue` (Print Queue → Timeline tab) | `api/print-queue/queue.empty-collection.json` |
| `tasks-widget-smoke.spec.ts` | Task | `POST` / `GET /api/tasks` | `api/tasks/tasks.populated.json`, `api/tasks/tasks.empty-collection.json` |
| `corpus-mutation-control-smoke.spec.ts` | (meta) | reuses `GET /api/admin/overview` | proves the shape assertion is non-vacuous |

`print-queue-smoke.spec.ts` (rather than the dashboard's printer-gated
`TasksWidget` "Create Task" UI flow) is the printer/queue journey because
`PrinterDashboard.tsx` only renders any dashboard widget once at least one
printer exists, and a fresh SQLite dev DB cannot currently have one seeded:
`DatabaseInitializer.SeedAllAsync`'s YAML catalog seed pipeline throws
partway through on a pre-existing, out-of-scope FK-constraint failure in
`SeedNozzlesAsync`, so `SeedPrinterModelsAsync` never runs and there is no
"Unknown" `PrinterModel` for `PrintersService` to fall back to. The Print
Queue page's Timeline tab has no such printer-gated empty state and always
issues a real `GET /api/job-queue` call, so it exercises a genuine,
network-intercepted browser journey without needing a printer to exist.
Because the corpus's own `queue.empty-collection.json` fixture is `[]`
(there is no populated-element template on a printer-less DB), this
particular assertion only proves array kind/emptiness, not per-element
structure — the populated-object shape check is instead covered by
`tasks-widget-smoke.spec.ts` against `tasks.populated.json`.

### Running locally

1. Start the API against an isolated, seeded SQLite DB:

   ```bash
   cd src
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   $env:ASPNETCORE_URLS = "http://localhost:5245"
   $env:DB_PROVIDER = "sqlite"
   $env:ConnectionStrings__Default = "Data Source=$env:TEMP\monolith-smoke.db"
   $env:NetworkDiscovery__EnableDiscovery = "false"
   # Required or the API host throws an unhandled startup exception
   # (SlicerApiKeyStartupValidationService).
   $env:WorkerAuth__SharedKey = "local-dev-worker-key"
   dotnet run --project ./api/Farm.Web.Api.csproj --no-launch-profile
   ```

2. Run the suite — Playwright's own `webServer` config starts the React dev
   server automatically:

   ```bash
   cd src/Web/ReactApp
   npm run test:e2e:monolith-smoke
   ```

CI runs the same suite in `.github/workflows/monolith-smoke.yml` (a new,
standalone workflow — not a change to `ci.yml` or `deployment-tests.yml`,
per the epic's file-ownership matrix).

## Running E2E Tests with the Moonraker Emulator

Printer-facing emulator specs live in `e2e/emulator/` (the `printer-*.spec.ts`,
`job-lifecycle.spec.ts`, and `discovery.spec.ts` files) and assert against a
**deterministic Moonraker emulator seed contract** rather than accepting
empty/optional UI states. They require:

1. The PrintFarmer API running and seeded with the five printers below,
   each backed by the real `Farm.Backend.Plugin.Moonraker` backend
   (`printer.backend === "Moonraker"`), pointed at the standalone Moonraker
   emulator containers (`src/moonraker-emulator/Farm.Moonraker.Emulator`).
2. The React dev server (`npm run dev`).
3. Direct network access to each scenario's emulator container control API
   (see below) for the job-lifecycle, files, history, and MMU specs — e.g. the
   `docker-compose.daily-validation.yml` overlay, which publishes the four
   ports loopback-only and enables `Emulator:EnableControlApi`.

### Deterministic seed contract

Confirmed directly against `src/moonraker-emulator/Farm.Moonraker.Emulator`
and `src/api/Services/Startup/MoonrakerEmulatorSeed*.cs`, which landed in
this worktree while these specs were being written:

| Printer name         | State                 | Notes                                                          |
|-----------------------|-----------------------|-------------------------------------------------------------------|
| `Moonraker Ready`      | Idle / Ready          | Online, no active job                                            |
| `Moonraker Printing`   | Printing              | Online, deterministic progress (0% until advanced — see below)   |
| `Moonraker Paused`     | Paused                | Online, resumable job at a seeded 20% (120s / 600s)               |
| `Moonraker Shutdown`   | Shutdown/error        | Connection to Moonraker up; Klipper firmware down                |
| `Moonraker Offline`    | Offline               | No backing container at all — a real connection failure          |

- `Moonraker Ready`/`Printing`/`Paused`/`Shutdown` are each served by their
  **own container instance** of the same emulator image (see
  `docker-compose.moonraker-emulator.yml`), not one shared instance.
  `Moonraker Offline` is seeded in the PrintFarmer DB pointing at
  `http://moonraker-offline:7125`, a hostname nothing listens on.
- Exactly one live virtual file is seeded per printer: `benchy.gcode`.
  `calibration_cube.gcode` is **not** a live file — it is confirmed to be
  only the filename of the one pre-seeded history entry.
- History starts with exactly one `completed` job
  (`calibration_cube.gcode`). There is no pre-seeded `cancelled` entry —
  `printer-history.spec.ts` drives a real start→cancel sequence itself to
  produce one, so the test doesn't depend on other files having run first.
- The `Printing` scenario's progress starts at exactly **0%** after a reset
  (the virtual clock's `TimeScale` defaults to 0, so nothing auto-ticks) —
  "stable" in this contract means "does not drift on its own", not
  "non-zero". Use the control API to advance it deterministically.
- A "Nozzle Cam" webcam fixture is seeded on every controllable printer
  (not `Offline`), giving at least one working **local** camera snapshot
  (no external network dependency).
- Discovery returns at least one deterministic candidate that is **not**
  one of the five printers above (i.e. it hasn't been added yet).
- Spoolman is treated as an independent integration boundary — the
  card/sidebar "Spool" section only renders when `/api/spoolman/config` +
  `/api/spoolman/health` report ready, or the printer already carries spool
  assignment data.

### Emulator control API

Confirmed from `Farm.Moonraker.Emulator.Endpoints.ControlApiEndpoints`. Only
mapped when `Emulator:EnableControlApi=true` (off by default; the
daily-validation compose overlay turns it on). **Each controllable scenario
is its own root emulator instance** — control calls are never
path-prefixed with a printer id (no `/printers/{id}/...`); instead each
scenario has its own independently configurable base URL:

| Scenario   | Default URL               | Override env var                |
|------------|----------------------------|-----------------------------------|
| `ready`    | `http://127.0.0.1:17125`   | `MOONRAKER_EMULATOR_URL_READY`     |
| `printing` | `http://127.0.0.1:17126`   | `MOONRAKER_EMULATOR_URL_PRINTING`  |
| `paused`   | `http://127.0.0.1:17127`   | `MOONRAKER_EMULATOR_URL_PAUSED`    |
| `shutdown` | `http://127.0.0.1:17128`   | `MOONRAKER_EMULATOR_URL_SHUTDOWN`  |

(`MOONRAKER_EMULATOR_HOST` overrides the default host, `127.0.0.1`, used
when composing a default port-based URL.) `Moonraker Offline` has no URL
and no control surface — it has no backing instance by design.

`e2e/fixtures/moonraker.ts`'s `createMoonrakerControl()` uses, against the
correct scenario's own root URL:

| Method | Path                        | Purpose                                                                 |
|--------|------------------------------|--------------------------------------------------------------------------|
| `POST` | `/__emulator/reset`          | Restore the instance's complete deterministic baseline, including printer state, virtual time, files, history/totals, Spoolman, MMU, and faults. |
| `POST` | `/__emulator/time/advance`   | Body `{ seconds }` — advances this instance's deterministic virtual clock; there is no direct "set progress" call, so `advancePrintProgress(scenario, percent)` converts percent → the equivalent `seconds` (progress = elapsedSeconds / 600). |
| `GET`  | `/__emulator/printers`       | Authoritative current state (Klippy state, print state, filename, progress, virtual time) for this instance's printer(s). |
| `POST` | `/__emulator/printer/mmu`     | Select `None`, `HappyHare`, `Afc`, `Qidibox`, or `SnapmakerU1` for deterministic protocol/UI coverage. |

These calls fail the test explicitly (naming the exact missing route and
status code) if the control API is unreachable — they never no-op or
silently skip.

Two more helpers call the *real* Moonraker file endpoints directly (not the
control API) for test setup/cleanup that doesn't disturb the guaranteed
`benchy.gcode` seed file, since file state isn't touched by `/reset`:
`uploadScratchGcodeFile` (`POST /server/files/upload`) and
`deleteScratchGcodeFile` (`DELETE /server/files/gcodes/{path}`).

### 1. Start the API + emulator

Refer to `docs/MOONRAKER_EMULATOR_VALIDATION.md` / the backend lane's setup
docs (and `docker-compose.moonraker-emulator.yml` /
`docker-compose.daily-validation.yml`) for standing up the four emulator
instances and seeding the API. The React E2E suite itself only needs:

- `API_BASE_URL` (default `http://127.0.0.1:5245`) reachable and healthy.
- The four scenario URLs in the table above reachable, with
  `Emulator:EnableControlApi=true`, for job-lifecycle/reset/upload control
  calls.
- Optionally, `E2E_ADMIN_USERNAME` + `E2E_ADMIN_PASSWORD` (and optional
  `E2E_ADMIN_EMAIL`) if an admin account was already provisioned externally
  (e.g. the daily immutable-image smoke setup) and `/api/setup/status`
  already reports `needsSetup: false` — see "Fixtures" below and issue
  #1586. Leave unset for a normal pristine-database local run; the fixture
  self-provisions its hardcoded `e2e-admin` default in that case.

### 2. Start the React dev server

```bash
cd src/Web/ReactApp
npm run dev
```

### 3. Run the Moonraker emulator E2E tests

Because these specs share a live, stateful backend (the same five named
printers are read and mutated across files), run them **serially** —
`test:e2e:moonraker` always passes `--workers=1`:

```bash
cd src/Web/ReactApp
npm run test:e2e:moonraker                              # Moonraker-tagged specs only, single worker
npx playwright test e2e/emulator/ --workers=1            # all emulator specs, single worker
npx playwright test e2e/emulator/ --workers=1 --headed   # watch in browser
```

### 4. Run a single spec

```bash
npx playwright test e2e/emulator/printer-status.spec.ts --workers=1
npx playwright test e2e/emulator/job-lifecycle.spec.ts --workers=1
npx playwright test e2e/emulator/discovery.spec.ts --workers=1
npx playwright test e2e/emulator/printer-files.spec.ts --workers=1
npx playwright test e2e/emulator/printer-history.spec.ts --workers=1
npx playwright test e2e/emulator/printer-camera.spec.ts --workers=1
npx playwright test e2e/emulator/printer-spoolman.spec.ts --workers=1
npx playwright test e2e/emulator/printer-mmu.spec.ts --workers=1
npx playwright test e2e/emulator/printer-responsive.spec.ts --workers=1
npx playwright test e2e/emulator/printer-accessibility.spec.ts --workers=1
```

### 5. Type-check and lint the E2E suite

```bash
npm run typecheck:e2e     # tsc --noEmit against e2e/**, strict mode
npm run lint               # eslint . (covers e2e/**/*.ts too)
npm run test:e2e:list      # Playwright test discovery dry-run (no server required)
npm run test:run -- e2e/fixtures/emulator-setup.unit.test.ts  # Vitest unit coverage for fixture logic (no server required)
```

`tsconfig.e2e.json` excludes `e2e/emulator/queue-realtime-auth.spec.ts` — a
pre-existing, unrelated auth/queue spec with type errors that predate this
change and are out of scope for the Moonraker printer-facing contract (see
"Out of scope" below).

## Fixtures

Shared test fixtures live in `e2e/fixtures/`:

- **`emulator-setup.ts`** — Base fixture that verifies the API is healthy,
  logs in a cached admin token, and injects it into `localStorage`. On a
  pristine database (`needsSetup: true`) it self-provisions its hardcoded
  `e2e-admin` default; if `E2E_ADMIN_USERNAME` + `E2E_ADMIN_PASSWORD` are
  set, it authenticates as that externally provisioned account instead
  (`resolveAdminCredentials`; see issue #1586 and
  `docs/MOONRAKER_EMULATOR_VALIDATION.md`). Also exports helpers:
  - `resolveAdminCredentials(env?)` — pure function resolving which admin
    account to use; covered directly by
    `e2e/fixtures/emulator-setup.unit.test.ts` (run via `npm run test:run`,
    not Playwright).
  - `waitForPrinterUpdate(page, printerName)` — hard-waits for a printer
    card's content to actually change (a real SignalR update), scoped by
    the printer's exact seeded name.
  - `getPrinterCards(page)` — returns all printer card locators.
  - `navigateToPrinter(page, printerName)` / `openPrinterDetails` — opens a
    printer's detail sidebar via its actual "Open details sidebar" button
    (the card itself has no click handler) and returns the sidebar's
    `complementary` landmark locator.
  - `getStoredAuthToken(page)` — reads back the JWT `emulatorReady` placed
    in `localStorage`, for making authenticated `page.request` calls.
- **`moonraker.ts`** — Moonraker seed-contract constants
  (`MOONRAKER_PRINTERS`, `MOONRAKER_FILES`), an auto-run `moonrakerSeedReady`
  fixture that verifies all five seeded printers exist with
  `backend === "Moonraker"` via the real API (failing loudly, naming the
  exact missing printer, if not), `createMoonrakerControl()` for the
  per-scenario control-API contract above (`reset`, `resetAll`,
  `advancePrintProgress`, `getPrinters`, `setMmuMode`),
  `uploadScratchGcodeFile` /
  `deleteScratchGcodeFile` for file-flow test setup/cleanup, and strict
  locator helpers (`getPrinterCardByName`, `expectPrinterStatus`,
  `openPrinterFiles`, `openPrinterHistory`, `getPrinterFileRow`,
  `getProgressBar`).

## Inventory: permissive fallbacks replaced by this pass

The previous `e2e/emulator/{printer-status,job-lifecycle,discovery,
printer-details,full-page-coverage}.spec.ts` relied heavily on
`isVisible().catch(() => false)` soft checks, `expect(x || y).toBeTruthy()`
either/or branches, and "at least N buttons exist" placeholders whenever a
specific control wasn't confirmed present. All five files were rewritten
against the Moonraker seed contract above: missing Pause/Resume/Cancel/
Emergency Stop/Firmware Restart controls, missing files/history/camera
content, and missing discovery results now fail the test outright. New
specs (`printer-files`, `printer-history`, `printer-camera`,
`printer-spoolman`, `printer-mmu`, `printer-responsive`,
`printer-accessibility`) cover
surfaces the old suite only smoke-tested via generic "no JS errors" checks.

### Production code changes made to support strict, accessible selectors

- `PrinterActionBar.tsx` / `PrinterDetailsSidebar.tsx`: the Pause/Resume,
  Cancel, and Emergency Stop/Firmware Restart control-pad buttons now carry
  an explicit `aria-label` matching their tooltip text. Previously their
  accessible name was computed from the wrapped icon's own default label
  (e.g. "Pause"/"Play"), which didn't match the intended action text and
  differed from the equivalent, already-correct pattern used elsewhere on
  the same card (icon-only buttons using `iconCenter` + explicit
  `aria-label`).
- `PrinterDetailsSidebar.tsx`: the sidebar's root container now has
  `role="complementary"` and an `aria-label` naming the printer, giving it
  a real landmark instead of an unlabeled `<div>`.
- `PrinterFilesModal.tsx`: the modal's main panel now has
  `role="dialog"`, `aria-modal="true"`, and `aria-labelledby` wired to its
  heading — it previously rendered with no dialog semantics at all (only
  the print/delete confirmation sub-dialogs, which use the shared `Modal`
  component, had them). Per-file hover-reveal action buttons
  (Queue/Copy/Download/Harvest/Delete) now also reveal on
  `group-focus-within`, not just `group-hover`, so keyboard users tabbing
  to them aren't left focusing an invisible control.
- `EditPrinterModal.tsx`: the Name/Manufacturer/Model fields now have
  matching `id`/`htmlFor` pairs so their `<label>` elements are
  programmatically associated with their controls (`getByLabel` now
  resolves them).

None of these changes alter visual layout or production behavior — they
only add/correct ARIA metadata and `id`/`htmlFor` wiring.

## Out of scope for this pass

- `cameras.spec.ts` (Admin Settings → Hardware → Cameras) and
  `filament-spools.spec.ts` (`/spools` catalog page) are **not**
  printer-page-bound — they test admin/catalog CRUD surfaces independent
  of any specific seeded printer — so their existing soft-fallback checks
  were left untouched, per this pass's "don't broaden into unrelated
  admin/auth cleanup" boundary. Printer-facing camera and Spoolman
  presentation are covered instead by `printer-camera.spec.ts` and
  `printer-spoolman.spec.ts`, which assert against the printer
  card/sidebar directly.
- `queue-realtime-auth.spec.ts` has pre-existing TypeScript errors (a
  `boolean | undefined` cast) unrelated to this change; it's excluded from
  `tsconfig.e2e.json` rather than silently "fixed" as part of this
  printer-facing pass.
- Exact discovery candidate names/count and exact history statistics
  totals are asserted structurally (non-zero, non-duplicate) rather than
  against fabricated fixed values, since the finalized emulator scenario
  catalog was not available in this worktree. Tighten these once the
  backend/emulator lane finalizes its fixture data — see the comments in
  `discovery.spec.ts` and `printer-history.spec.ts`.

## Tips

- The emulator broadcasts printer status updates on its own cadence via
  SignalR — use `waitForPrinterUpdate` / `expect(...).toPass({ timeout })`
  polling assertions, never a fixed sleep standing in for a real check.
- Deterministic progress values (job-lifecycle) come from the control API,
  not wall-clock waits — see `createMoonrakerControl().advancePrintProgress`.
- If tests fail with "API health check failed", ensure the API is running
  and reachable at `API_BASE_URL`.
- If tests fail with "Expected seeded Moonraker printer ... was not
  returned by GET /api/printers", the backend/emulator seeding step for
  this contract has not run (or hasn't finished) yet — this is a hard
  failure, not something the suite works around.
- Use `--headed` flag to watch tests execute in the browser for debugging.
- Screenshots on failure are saved to `test-results/`.