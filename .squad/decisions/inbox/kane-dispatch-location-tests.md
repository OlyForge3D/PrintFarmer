# Decision: Pre-Implementation Test Suites for Auto-Dispatch & Location Hierarchy

**Author:** Kane (Tester)
**Date:** 2026-03-07
**Status:** Implemented

## Context

Lambert is building auto-dispatch scoring (`DispatchScorer`) and Ripley is adding
location hierarchy tree operations. Both features need test coverage written in
advance so that when implementations land, tests are ready to validate correctness.

## Decision

Created 43 tests across 2 files, all passing against the current codebase:

### DispatchScorerTests (22 tests)
- **15 unit tests**: Scoring helper methods implementing the spec's weighted scoring
  algorithm (material=30, nozzle=25, buildVolume=15, nozzleHardness=10, enclosure=10,
  preference=5, queueDepth=5). Tests validate each individual scoring factor and
  elimination logic.
- **3 edge case tests**: No toolheads, no material requirements, GCode with no dimensions.
- **4 integration stubs**: Hit `/healthz` for now; will be updated to hit Lambert's
  dispatch endpoints when they land.

### LocationHierarchyTests (21 tests)
- **17 service-level tests**: Hierarchy CRUD (create with parent, path, depth),
  tree traversal (GetTree, GetAncestors, GetDescendants), move validation (circular
  reference, own-descendant, path update propagation), constraints (max depth, duplicate
  names, delete-with-children), and printer assignment/unassignment.
- **4 integration tests**: API endpoint tests for create-child, get-tree, move-valid,
  and move-circular-reference via HTTP.

## Key Learnings for Other Agents

1. **Manufacturer has UNIQUE constraint on NameLowered** — seed once, reuse
2. **Printer has UNIQUE constraint on ServerUrl** — use unique URLs per test printer
3. **Location has UNIQUE constraint on (ParentId, Name)** — DB enforces, not just service
4. **Creating Printers needs valid FK refs** — Manufacturer + PrinterModel must exist first

## Files

- `src/tests/Farm.Web.Api.Tests/Dispatch/DispatchScorerTests.cs`
- `src/tests/Farm.Web.Api.Tests/Locations/LocationHierarchyTests.cs`
