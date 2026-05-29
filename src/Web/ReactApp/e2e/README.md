# Running E2E Tests

## Existing Visual / Navigation Tests

These run against the dev server alone (no API data required):

```bash
cd src/Web/ReactApp
npm run test:e2e                     # all E2E tests
npm run test:e2e:chromium            # Chromium only
npm run test:e2e -- --project=firefox  # Firefox only
```

## Running E2E Tests with Backend Emulator

Emulator-backed tests live in `e2e/emulator/` and require the API running
with the **TestEmulator** plugin enabled plus the React dev server.

### 1. Start the API in test mode

```bash
cd src
PFARM__TestEmulator__Enabled=true dotnet run --project ./api/Farm.Web.Api.csproj
```

The emulator registers three virtual printers:

| Name                | State              | Notes                            |
|---------------------|--------------------|----------------------------------|
| Test Printer Alpha  | Idle               | Ambient temps, no active job     |
| Test Printer Beta   | Printing at 42%    | Job: test-print-benchy.gcode     |
| Test Printer Gamma  | Offline / Error    | Simulates unreachable printer    |

### 2. Start the React dev server

```bash
cd src/Web/ReactApp
npm run dev
```

### 3. Run the emulator E2E tests

```bash
cd src/Web/ReactApp
npx playwright test e2e/emulator/             # all emulator tests
npm run test:e2e:emulator                      # same via npm script
npx playwright test e2e/emulator/ --headed     # watch in browser
```

### 4. Run a single spec

```bash
npx playwright test e2e/emulator/printer-status.spec.ts
npx playwright test e2e/emulator/job-lifecycle.spec.ts
npx playwright test e2e/emulator/discovery.spec.ts
npx playwright test e2e/emulator/full-page-coverage.spec.ts
```

## Fixtures

Shared test fixtures live in `e2e/fixtures/`:

- **`emulator-setup.ts`** — Base fixture that verifies the API is healthy
  and the emulator is active. Also exports helpers:
  - `waitForPrinterUpdate(page, printerId)` — waits for a SignalR update
  - `getPrinterCards(page)` — returns all printer card locators
  - `navigateToPrinter(page, printerName)` — clicks a card to open details

## Tips

- The emulator broadcasts printer status updates every **2 seconds** via
  SignalR. Use generous timeouts (≥ 6 s) for assertions that depend on
  real-time data.
- If tests fail with "API health check failed", ensure the API is running
  with `PFARM__TestEmulator__Enabled=true`.
- Use `--headed` flag to watch tests execute in the browser for debugging.
- Screenshots on failure are saved to `test-results/`.
