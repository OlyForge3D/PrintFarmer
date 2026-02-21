---
name: testing
description: Run .NET and React tests efficiently. Use when asked to run tests, verify changes compile, investigate test failures, or validate code changes. Covers dotnet test, npm test, build verification, and failure investigation workflows.
---

# PrintFarmer Test Execution Skill

Use this skill whenever running tests, verifying builds, or investigating test failures.

## Cardinal Rule: Run Tests ONCE, Capture Output

**NEVER run the same test suite more than once per change cycle.** Tests are expensive (3+ minutes for .NET, 12+ seconds for React). Always capture output to a log file on the first run, then use that log for all analysis.

## .NET Tests

### Run and capture in a single pass

```bash
cd /home/jpapiez/s/pfarm1/src
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/dotnet-test-results.log
```

- Timeout: **240 seconds minimum** (typical: ~3m 30s)
- NEVER cancel this command early
- The `tee` command displays output AND saves it simultaneously

### Investigate results from the log — do NOT re-run tests

```bash
# Quick summary (pass/fail counts)
tail -30 /tmp/dotnet-test-results.log

# Find failures
grep -E "Failed|FAIL|Error" /tmp/dotnet-test-results.log

# Find specific test names that failed
grep -B2 "Failed" /tmp/dotnet-test-results.log

# Count totals
grep -E "Passed|Failed|Skipped" /tmp/dotnet-test-results.log | tail -5
```

### If you need to re-run a specific failing test (not the whole suite)

```bash
cd /home/jpapiez/s/pfarm1/src
dotnet test ./farm-web.sln -c Debug --filter "FullyQualifiedName~TestClassName.TestMethodName" 2>&1 | tee /tmp/dotnet-test-single.log
```

## React Tests

### Run and capture in a single pass

```bash
cd /home/jpapiez/s/pfarm1/src/Web/ReactApp
npm run test:run 2>&1 | tee /tmp/react-test-results.log
```

- Timeout: **60 seconds minimum** (typical: ~12s)
- Always use `test:run` (non-interactive). NEVER use `npm test` which enters watch mode.

### Investigate results from the log

```bash
# Quick summary
tail -20 /tmp/react-test-results.log

# Find failures
grep -E "FAIL|✗|×|Error" /tmp/react-test-results.log

# Find specific failing test files
grep "FAIL " /tmp/react-test-results.log
```

## Build Verification

Always build before testing to catch compilation errors early:

```bash
# .NET
cd /home/jpapiez/s/pfarm1/src
dotnet build ./farm-web.sln -c Debug 2>&1 | tee /tmp/dotnet-build.log

# React (if needed)
cd /home/jpapiez/s/pfarm1/src/Web/ReactApp
npm run build 2>&1 | tee /tmp/react-build.log
```

- Build timeout: **150 seconds** for .NET, **30 seconds** for React
- Check build result: `tail -5 /tmp/dotnet-build.log`

## Complete Validation Workflow (after code changes)

This is the full sequence. Each step runs ONCE:

1. **Build**: `cd /home/jpapiez/s/pfarm1/src && dotnet build ./farm-web.sln -c Debug 2>&1 | tee /tmp/dotnet-build.log`
2. **Check build result**: `tail -5 /tmp/dotnet-build.log` — must show "succeeded"
3. **Test**: `dotnet test ./farm-web.sln -c Debug --no-build 2>&1 | tee /tmp/dotnet-test-results.log`
4. **Check test result**: `tail -30 /tmp/dotnet-test-results.log`
5. **If failures exist**: `grep -B2 "Failed" /tmp/dotnet-test-results.log` — investigate from the log, fix code, then re-run only step 1-4

## Anti-Patterns (NEVER DO THESE)

- **NEVER** run the full test suite just to check if a specific test passed — use `--filter`
- **NEVER** run tests twice to "see the output differently" — use grep/tail/head on the log
- **NEVER** run tests without `tee` or `2>&1` — you'll lose the output and have to re-run
- **NEVER** run tests with insufficient timeout — this kills the process mid-run
- **NEVER** use `npm test` for automated testing — it enters watch mode and hangs
- **NEVER** run `dotnet test` without building first (or use `--no-build` after a confirmed build)

## Timeout Reference

| Command | Typical Time | Minimum Timeout |
|---|---|---|
| `dotnet build -c Debug` | ~82s | 150s |
| `dotnet test -c Debug` | ~3m 30s | 240s |
| `dotnet test --no-build` | ~3m | 240s |
| `dotnet test --filter "Name"` | ~10-30s | 60s |
| `npm run test:run` | ~12s | 60s |
| `npm run build` | ~10s | 30s |
| `npm run lint` | ~30s | 60s |
