# Bishop — History

## Core Context

- Code Reviewer on PrintFarmer project
- Uses GPT-5.4 model for review perspective diversity
- Part of triple-model pre-commit review gate (with Hicks and Vasquez)
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

### 2026-05-21: PR #299 review (jog subgroup)
- Verdict ✅ approved via `--comment` (Ruling G — self-PR cannot `--approve`).
- Coordinator squash-merged with `--admin`.

## Review Pass 2026-05-28

- PrintFarmerMobile #1 — REQUEST_CHANGES: missing capability-gated states, wrong jog default, and wrong Home XY/Z API routes in the spec.
- PrintFarmerMobile #2 — REQUEST_CHANGES: omitted capability keys decode unsafely and fall back to an optimistic table instead of defaulting missing booleans to false.
- PrintFarmerMobile #3 — APPROVE: farm_admin gate treats nil as non-admin and has solid role-case coverage.
- PrintFarmerMobile #4 — APPROVE: temp/home/move routing looks correct, 409 conflict remains distinguishable, and nil fields are omitted from JSON.
- PrintFarmer #313 — REQUEST_CHANGES: live-status override clobbers the disabled/unsupported-backend reason path and lacks a regression test for that case.

## Review Pass r4 2026-05-28

- PrintFarmerMobile #1 — APPROVE: capability gating, 1 mm jog default, and /homexy + /homez route fixes are all present.
- PrintFarmer #313 — APPROVE: stale override is idle-only in both badge and overlay, and unsupported-backend regressions are covered.
- PrintFarmerMobile #9 — APPROVE: Printer.progress is now canonical 0-100 end to end, view bindings convert only where needed, no persisted progress migration risk was found, and tests pin 42.7 / 100.0 / 150.0 / -5.0.
