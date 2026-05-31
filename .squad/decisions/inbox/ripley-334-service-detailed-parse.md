# Decision: IGcodePreviewService extended with parseGCodeDetailed

**Author:** Ripley
**Date:** 2026-05-31
**Status:** Implemented (PR #369)

## Context

`GCodeViewer3D` needs full XYZ point data per layer for Three.js Line rendering. The original `parseGCode()` returns only metadata (z, commandCount, lineNumber) — insufficient for the viewer canvas.

## Decision

Added `parseGCodeDetailed(gcodeText: string): Promise<DetailedParsedGCode>` to `IGcodePreviewService`. Returns:
- `layers: DetailedLayer[]` — each layer has `points: GCodePoint[]` with x/y/z/e/feedRate/type/tool
- `tools: number[]` — discovered T-commands for filter UI

The original `parseGCode()` remains for lightweight metadata consumers. Both methods will be swapped to the Web Worker implementation in v2.

## Impact

- **Ripley:** Component tests mock `parseGCodeDetailed` — stable contract for future v2 worker swap.
- **Lambert/Dallas:** No backend impact. Service is purely frontend.
- **Future:** v2 worker must implement both `parseGCode` and `parseGCodeDetailed`.
