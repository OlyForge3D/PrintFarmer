# Decision: UI Validation Test Suite Architecture

**Author:** Kane (Tester)
**Date:** 2026-03-06

## Decision

Created a standalone Playwright-based UI validation test suite at `tests/ui-validation/` that starts its own .NET API server and React dev server with a fresh SQLite database for each run. This is separate from the existing `src/Web/ReactApp/e2e/` tests.

## Rationale

- **Self-contained**: Tests manage their own server lifecycle — no manual server startup needed
- **Clean state**: Fresh temp SQLite DB every run eliminates test pollution
- **Validation focus**: Tests validate key features still work after code changes, not comprehensive UI testing
- **Non-destructive**: Runs against temp database, doesn't touch dev database

## Key Technical Notes

- API timeout must be 180s+ (includes dotnet build step)
- Catalog API has a pre-existing DI bug causing 500s — tests accept this
- The `/health` endpoint returns 503 when catalog is unhealthy — tests accept 200 or 503
- React app shows "Initializing system..." on first load before interactive elements render

## Impact

- All squad members can run `cd tests/ui-validation && npm test` to validate changes
- CI/CD can integrate this as a smoke test gate
