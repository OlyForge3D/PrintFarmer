---
name: testing
description: Run PrintFarmer .NET and React builds/tests efficiently. Use when asked to validate changes, run tests, verify compilation, investigate failures, or choose focused test commands.
---

## PrintFarmer Test Execution Skill

Use this skill whenever running builds, tests, linting, or investigating test failures.

## Core Rule

Run each expensive suite once per code state and capture output with `tee`. After a failure, inspect the saved log. Re-run only after editing code, losing/truncating the log, or intentionally running a focused test.

## Choose Scope

| Change area | Validation command |
|---|---|
| Backend/API/shared .NET build | `cd src && dotnet build ./farm-web.sln -c Debug 2>&1 | tee /tmp/printfarmer-dotnet-build.log` |
| Backend/API full tests | `cd src && dotnet test ./farm-web.sln -c Debug --no-build 2>&1 | tee /tmp/printfarmer-dotnet-test.log` |
| Slicer module tests | `cd src && dotnet test ./tests/Farm.Slicer.Module.Tests --no-restore 2>&1 | tee /tmp/printfarmer-slicer-test.log` |
| React build | `cd src/Web/ReactApp && npm run build 2>&1 | tee /tmp/printfarmer-react-build.log` |
| React tests | `cd src/Web/ReactApp && npm run test:run 2>&1 | tee /tmp/printfarmer-react-test.log` |
| React lint | `cd src/Web/ReactApp && npm run lint 2>&1 | tee /tmp/printfarmer-react-lint.log` |

Use repo-relative directories. Do not hardcode machine-specific absolute paths.

## Standard Flow

1. Build the affected layer first.
2. If the build succeeds, run the matching tests with `--no-build` when supported.
3. Read the log summary with `tail`.
4. If failures appear, inspect the saved log, fix code, then repeat only the affected validation.

Backend example:

```bash
cd src
dotnet build ./farm-web.sln -c Debug 2>&1 | tee /tmp/printfarmer-dotnet-build.log
dotnet test ./farm-web.sln -c Debug --no-build 2>&1 | tee /tmp/printfarmer-dotnet-test.log
```

Frontend example:

```bash
cd src/Web/ReactApp
npm run build 2>&1 | tee /tmp/printfarmer-react-build.log
npm run test:run 2>&1 | tee /tmp/printfarmer-react-test.log
npm run lint 2>&1 | tee /tmp/printfarmer-react-lint.log
```

## Read Logs

```bash
tail -40 /tmp/printfarmer-dotnet-test.log
grep -E "Failed|FAIL|Error|Passed|Skipped" /tmp/printfarmer-dotnet-test.log | tail -40

tail -30 /tmp/printfarmer-react-test.log
grep -E "FAIL|Error" /tmp/printfarmer-react-test.log
```

If a log is missing, empty, or truncated before the summary, re-run that same command once to capture a fresh log.

## Focused Reruns

Use focused reruns for known failing tests instead of re-running the full suite.

```bash
cd src
dotnet test ./farm-web.sln -c Debug --filter "FullyQualifiedName~TestClassName.TestMethodName" 2>&1 | tee /tmp/printfarmer-dotnet-focused-test.log

cd src/Web/ReactApp
npm run test:run -- path/to/test-file.test.tsx 2>&1 | tee /tmp/printfarmer-react-focused-test.log
```

## Timeouts

| Command | Minimum timeout |
|---|---|
| `dotnet build ./farm-web.sln -c Debug` | 150s |
| `dotnet test ./farm-web.sln -c Debug` | 240s |
| Focused `dotnet test --filter` | 60s |
| `npm run build` | 30s |
| `npm run test:run` | 60s |
| `npm run lint` | 60s |

Use `npm run test:run` for automated React tests. Do not use `npm test`, because it starts watch mode.