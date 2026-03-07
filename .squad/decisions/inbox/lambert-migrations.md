# Decision: EF Core Migrations for Location & Dispatch Entities

**Author:** Lambert (Backend Dev)
**Date:** 2026-03-07
**Status:** Implemented

## Context

Three new domain entities (Location tree structure, DispatchLog audit trail, DispatchSettings singleton) and three new PrintJob columns (DispatchedAt, DispatchScore, DispatchMode) had entity configurations but no EF Core migrations.

## Decision

Created `AddLocationDispatchEntities` migration for both PostgreSQL and SqlServer providers. Key schema choices:

1. **Location self-referential FK uses `Restrict` delete** — prevents accidentally deleting a parent that has children. Application must handle cascading moves/deletes.
2. **Replaced unique `IX_Locations_Name` with composite `IX_Locations_ParentId_Name`** — allows duplicate names under different parents (e.g., "Shelf 1" under different rooms).
3. **DispatchLog FKs use `Cascade` delete** — logs are deleted when the parent PrintJob or Printer is deleted (audit trail tied to entity lifecycle).
4. **DispatchSettings singleton seeded via HasData** — ensures exactly one row (Id=1) exists with safe defaults (auto-dispatch OFF).

## Impact

- All 1952 tests pass (1504 API + 448 Slicer)
- Build succeeds with 0 errors
- Both PostgreSQL and SqlServer providers have identical schema semantics
