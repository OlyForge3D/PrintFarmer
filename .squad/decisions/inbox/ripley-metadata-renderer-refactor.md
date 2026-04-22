# Decision: Extract reusable metadata renderer components

**Author:** Ripley (Frontend Developer)
**Date:** 2025-08-01
**Bead:** PFarm1-ugub

## Context

`MetadataProfileRenderer.tsx` was a 976-line monolith containing types, constants, helper functions, and three internal components (`OrcaIcon`, `MetadataSection`, `MetadataTab`). None of these could be imported independently, making reuse impossible and the file difficult to navigate.

## Decision

Extract the monolith into five focused modules:

| File | Responsibility |
|---|---|
| `metadataTypes.ts` | Shared types, constants (KNOWN_ENUMS, TEXTAREA_KEYS, etc.), helper functions |
| `OrcaIcon.tsx` | Blue-tinted OrcaSlicer section icon component |
| `MetadataSettingRow.tsx` | Single-field renderer (all control types + paired temperature rows) |
| `MetadataSection.tsx` | Section group renderer with view-mode filtering and paired temp detection |
| `MetadataTabRenderer.tsx` | Tab-level renderer mapping sections to MetadataSection |

`MetadataProfileRenderer.tsx` becomes a ~100-line thin facade that re-exports everything, preserving all existing import paths.

## Trade-offs

- **More files** — 5 new files instead of 1, but each is <300 lines and single-purpose
- **Paired hook workaround** — `useChangeTracking` for the optional paired temperature key is always called (with a fallback key when absent) to satisfy React's rules-of-hooks
- **OrcaIcon separated** — moved to its own `.tsx` file to avoid `react-refresh/only-export-components` lint error on the pure `.ts` types file

## Validation

- ✅ ESLint: 0 errors (1 pre-existing warning in SettingRow.tsx)
- ✅ Tests: 1734/1734 pass, 12 skipped, 0 failures
- ✅ Backward compatibility: all existing consumers unchanged
