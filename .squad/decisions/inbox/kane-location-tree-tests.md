# Decision: Location Tree Test Strategy — Proactive Testing

**Author:** Kane (Tester)
**Date:** 2026-03-10
**Status:** IMPLEMENTED — 50 tests passing

## Context

Ripley is building enhanced location tree UI components in `features/locations/components/`. Tests written proactively against the existing common component interfaces to establish coverage before the new implementations land.

## Decision

Tests import from `@/common/components/` (current implementations) rather than waiting for non-existent feature-level components. When Ripley's enhanced components are ready, update import paths in:
- `features/locations/components/__tests__/LocationTreePicker.test.tsx`
- `features/locations/components/__tests__/LocationBreadcrumb.test.tsx`

## Key Findings

1. **Pre-existing test failures**: 45 tests in SystemLogsContent and GcodeLibraryPage fail due to `localStorage` mock issues — not related to location work. Should be investigated separately.
2. **API client coverage gap fixed**: `locationService.test.ts` was missing tests for `getLocationTree`, `getLocationAncestors`, and `moveLocation` — now covered (9 new tests).
3. **Total location test count**: 50 new + 30 existing (common components) + 8 (LocationSelector) = 88 total location-related tests.

## Action Items

- [ ] Ripley: update import paths when feature-level components are created
- [ ] Team: investigate and fix `localStorage` mock failures in SystemLogs/GcodeLibrary tests
