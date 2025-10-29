This directory archives the former PrusaSlicer worker implementation that was removed from the active codebase on 2025-10-28.

Reason for archival
- The repository removed deployment-level PrusaSlicer worker artifacts and the team decided to decommission the prusaslicer worker implementation.

Contents archived
- `src/prusaslicer-worker/` (project files, binaries, and source code)

Notes
- The public interface `IPrusaLinkClient` remains in place to avoid breaking tests and compilation in areas where the interface is still referenced.
- The concrete implementation `PrusaLinkClient` and `PrusaLinkClientTestController` were removed and a `NoOpPrusaLinkClient` is registered in DI to provide safe behavior for environments that do not need Prusa support.

If you need to restore the prusaslicer worker implementation, retrieve it from the git history or this archive and re-add the files.
