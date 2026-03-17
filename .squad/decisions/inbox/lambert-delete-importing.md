# Decision: Remove Farm.Importing Project

**Author:** Lambert (Backend Dev)  
**Date:** 2025-07-26  
**Status:** Implemented (not yet committed)

## Context
The `Farm.Importing` project (`src/import/`) contained CSV/JSON import parsing services (`IImportParserService`, `IImportProcessorService`). This functionality was superseded by inline parsing in `PrintersService` which handles the same CSV/JSON import flows directly.

## Decision
Delete `Farm.Importing` entirely — project, tests, DI registrations, and all references.

## Impact
- Reduces solution complexity (2 fewer projects to build)
- No runtime behavior change — PrintersService already handles all import paths
- Build: clean (0 errors, 0 warnings), Tests: 2091 passing
