---
description: "Use when working on tools"
applyTo: "tools/**"
---

---
description: 'PrintFarmer tools area: standalone developer utilities for debugging, migration, and testing outside the main solution'
applyTo: 'tools/**'
---

# Tools Area

Standalone developer and admin utilities that live **outside** `src/farm-web.sln`. These are not part of the main build or test pipeline.

## Directory Structure

| Path | Language | Purpose |
|---|---|---|
| `tools/ProfileParserTester/` | C# (.NET 10) | Test harness for OrcaSlicer profile parsing |
| `tools/signalr-debug-client.js` | Node.js (CJS) | Live SignalR hub debug client |
| `tools/migrate_printer_export.py` | Python 3 | Migrates old printer export JSON to new format |
| `tools/installer/dist/` | Binaries | Pre-built cross-platform installer executables |

## ProfileParserTester (C#)

- **Not** included in `src/farm-web.sln` — must be built/run independently
- References `src/orcaslicer-worker` and `src/infra` via `<ProjectReference>`
- Uses real `OrcaProfilesService` with a `NullLoggingService` stub; no mock parsers
- Build and run from **repo root** (paths in the `.csproj` are relative to root):

```bash
dotnet build tools/ProfileParserTester/ProfileParserTester.csproj -c Debug
dotnet run --project tools/ProfileParserTester/ProfileParserTester.csproj
# Inspect a specific profile:
dotnet run --project tools/ProfileParserTester/ProfileParserTester.csproj -- /path/to/profile.json
```

- Reads profiles from `~/.config/OrcaSlicer/` on the local machine at runtime

## signalr-debug-client.js (Node.js)

- Uses `@microsoft/signalr` (CommonJS, `"type": "commonjs"` in `package.json`)
- Connects to `SIGNALR_URL` env var or defaults to `http://localhost:5245/hubs/printers`
- Listens for `PrinterUpdated` events and logs raw JSON — useful for verifying camelCase serialization

```bash
cd tools
npm install
node signalr-debug-client.js
# or:
SIGNALR_URL=http://myserver:5245/hubs/printers node signalr-debug-client.js
```

## migrate_printer_export.py (Python 3)

- No external dependencies — standard library only (`json`, `pathlib`, `sys`)
- Converts `ipAddress`-only exports to `serverUrl` + `backendPort` + `frontendPort` format
- Writes output to `<input>-migrated.json` by default; accepts optional second arg for output path

```bash
python3 tools/migrate_printer_export.py printfarmer-printers-export.json
python3 tools/migrate_printer_export.py input.json output.json
```

## Conventions

- Tools are **fire-and-forget** utilities; no shared framework, no automated tests
- Each tool is self-contained — avoid adding cross-tool dependencies
- Keep DI wiring in C# tools minimal (only what the target service actually requires)
- For new C# tools that reference `src/` projects, keep them outside `farm-web.sln` and run via `dotnet run --project`
- See [`.github/instructions/csharp.instructions.md`](.github/instructions/csharp.instructions.md) for C# style
- See [`.github/instructions/nodejs-javascript-vitest.instructions.md`](.github/instructions/nodejs-javascript-vitest.instructions.md) for JS style
