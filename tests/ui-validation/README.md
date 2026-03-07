# PrintFarmer UI Validation Tests

End-to-end validation suite that spins up a real PrintFarmer instance (API + React) with a fresh SQLite database and verifies key features work correctly.

Use this suite as a smoke test after frontend or backend changes to catch regressions before they reach production.

## Prerequisites

- **Node.js 24+** and npm
- **.NET 10 SDK** (10.0.101+)
- npm dependencies installed in `src/Web/ReactApp/` (`npm install`)
- .NET solution restored (`cd src && dotnet restore ./farm-web.sln`)

## Quick Start

```bash
cd tests/ui-validation
npm install
npx playwright install chromium
npm test
```

## Available Commands

| Command | Description |
|---|---|
| `npm test` | Run all tests headless (default) |
| `npm run test:headed` | Run with visible browser window |
| `npm run test:ui` | Open Playwright's interactive UI mode |
| `npm run test:debug` | Run with Playwright inspector for step-through debugging |
| `npm run test:report` | Open the last HTML test report |

## What Gets Tested

| Test File | Feature | What It Validates |
|---|---|---|
| `01-health.spec.ts` | Health endpoints | `/healthz` and `/health` respond correctly |
| `02-app-loads.spec.ts` | Application loads | Homepage renders, no JS errors, React root has content |
| `03-api-connectivity.spec.ts` | API connectivity | Printers endpoint, manufacturers seeded, camelCase JSON |
| `04-setup-wizard.spec.ts` | Setup wizard | First-run wizard appears on empty database |
| `05-navigation.spec.ts` | Navigation | Nav elements present, key routes accessible |
| `06-signalr.spec.ts` | SignalR hub | Real-time hub negotiate endpoint reachable |
| `07-printer-management.spec.ts` | Printer management | Printers page loads, add printer UI accessible |
| `08-catalog.spec.ts` | Catalog data | 8+ manufacturers seeded, catalog page loads |

## How It Works

1. **Global setup** creates a temp directory with a fresh SQLite database
2. Starts the .NET API server (`dotnet run`) pointed at the temp database
3. Starts the React dev server (`vite`) 
4. Waits for both servers to respond to health checks
5. Runs all Playwright tests against the live servers
6. **Global teardown** kills both servers and deletes the temp database

## Architecture

```
tests/ui-validation/
├── playwright.config.ts   # Playwright configuration
├── global-setup.ts        # Starts API + React servers
├── global-teardown.ts     # Stops servers, cleans temp DB
├── package.json           # Dependencies
├── tsconfig.json          # TypeScript config
└── tests/                 # Test specs (numbered for execution order)
    ├── 01-health.spec.ts
    ├── 02-app-loads.spec.ts
    ├── 03-api-connectivity.spec.ts
    ├── 04-setup-wizard.spec.ts
    ├── 05-navigation.spec.ts
    ├── 06-signalr.spec.ts
    ├── 07-printer-management.spec.ts
    └── 08-catalog.spec.ts
```

## Troubleshooting

- **Tests time out during setup**: Ensure .NET 10 SDK is installed and `dotnet restore` has been run in `src/`
- **Port already in use**: Kill any existing processes on ports 5245 (API) or 3000 (React)
- **Playwright not installed**: Run `npx playwright install chromium`
- **React dev server fails**: Ensure `npm install` has been run in `src/Web/ReactApp/`
