---
# Legacy Base Worker (Removed)

The former generic base slicer worker and `Dockerfile.base` have been permanently removed.

All engine functionality now lives in per-engine workers layered on `Dockerfile.slicer-base`.

Refer instead to:

- `docs/slicer/adding-new-engine-worker.md`
- `docs/slicer/orcaslicer-worker.md`
- `docs/slicer/prusaslicer-worker.md`

This tombstone will be deleted after external links are updated.
