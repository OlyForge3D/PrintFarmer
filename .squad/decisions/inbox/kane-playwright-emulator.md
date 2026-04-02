## Playwright Emulator E2E Test Infrastructure Decisions

**Date:** 2026-07-18
**Author:** Kane (QA)
**Status:** IMPLEMENTED

### Decision 1: Separate emulator tests from existing E2E tests

Emulator-backed tests live in `e2e/emulator/` with a dedicated npm script `test:e2e:emulator`, separate from the existing visual/navigation/layout tests in `e2e/`.

**Rationale:** Emulator tests require the API running with `PFARM__TestEmulator__Enabled=true` — a different startup sequence than existing E2E tests which only need the React dev server. Mixing them would cause CI confusion and false failures.

### Decision 2: Fixture-based API health verification

The `emulator-setup.ts` fixture auto-runs before every emulator test, hitting `/healthz` and `/health` to confirm the API is alive and the emulator is active.

**Rationale:** Fail fast with a clear diagnostic message rather than letting tests hang or produce cryptic timeout errors when the API isn't running.

### Decision 3: Resilient selectors with graceful fallback

Tests use multiple selector strategies: `.pf-detailed-printer-card` CSS class, `div[role="progressbar"]`, `span[title="..."]` for temps, and text content filtering. Where a UI control might be behind a menu or not yet implemented, tests check for visibility and gracefully skip.

**Rationale:** The emulator plugin is being built in parallel (Lambert). The UI for emulator-specific actions (start print, pause, cancel) may not exist yet. Tests are written to pass once the emulator is running, with fallback assertions that verify the structural contract (buttons exist, cards render, status badges show).

### Decision 4: Conservative timeouts for SignalR-dependent assertions

Emulator broadcasts every ~2 s. Tests use 10-15 s timeouts for initial card rendering and 5-6 s waits for real-time updates.

**Rationale:** SignalR connection setup + first broadcast can take 3-5 s on slow machines. Being generous prevents flaky CI failures while remaining fast enough for local development feedback.
