# Architecture Review Findings — 2026-03-06

**Decision by:** Dallas (Lead/Architect)

## Summary

Comprehensive architecture review completed. The system is well-structured with a solid plugin architecture and clean layering. Several areas need attention but nothing blocking.

## Key Findings

### Strengths (preserve these patterns)
- Backend plugin architecture is excellent — extensible, well-contracted
- Unit of Work + Repository in infra is clean
- Feature-folder organization on frontend is correct
- Multi-database provider support is production-grade
- Discovery confidence scoring is a smart approach

### Concerns to address (prioritized)
1. **P1 — `api.ts` is 3,458 lines.** Split into domain-specific service modules.
2. **P2 — 3 controllers bypass repository layer** (inject AppDbContext directly). Route through services.
3. **P2 — Orphaned directories** (`shared/`, `signalr/`, `prusaslicer-worker/`). Clean up or document.
4. **P3 — Backend plugins reference Infra directly.** Consider introducing a thin abstractions package to reduce coupling.
5. **P3 — API project has 26 references.** This is the composition root so it's somewhat expected, but review if all are necessary.

## Impact on Team

- Frontend work (Ripley): Plan to split `api.ts` into feature-scoped service files
- Backend work (Lambert): Fix direct DbContext usage in controllers; clean orphaned dirs
- Testing (Kane): No blockers; test infrastructure is solid
