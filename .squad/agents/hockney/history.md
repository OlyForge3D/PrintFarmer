# Hockney History

## Core Context

Hockney is the test engineer on PrintFarmer. Focus: unit tests for high-risk pure functions, deterministic test design, exposing real bugs without modifying production code to make tests pass.

---

## Cut Algorithm Unit Tests (2026-04-25)

**Bead:** PFarm1-b00r — Add unit tests for cutting algorithms

### Work

Added Vitest coverage for the four highest-risk pure functions in
`src/Web/ReactApp/src/features/slicer/components/viewer/CutPlaneOverlay.tsx`:

- `splitGeometryAtPlane(geometry, axis, worldPlanePos, modelMatrix?)`
- `earClipTriangulate(polygon, axis)`
- `orderCapEdges(edges, epsilon?)`
- `filterDegenerateTriangles(verts, minArea2?)`

### Learnings

- The four cut algorithm functions are now exported from `CutPlaneOverlay.tsx`
  (added `export` keyword only — no behavioral change).
- Test file location: `src/Web/ReactApp/src/features/slicer/components/viewer/__tests__/CutPlaneOverlay.test.ts`
  (`.ts`, not `.tsx` — these are pure-math functions; no React render needed).
- Vitest + Three.js test pattern works without jsdom for these pure functions.
  The default vitest environment is sufficient; no special setup required.
- Real signature of `splitGeometryAtPlane` is `(geometry, axis, worldPlanePos, modelMatrix?)`,
  not `(geometry, mesh, worldPlanePos, cutAxis)`. Tests were written against the
  actual signature; one test still constructs a `THREE.Mesh` and passes
  `mesh.matrixWorld` (identity) to verify the optional matrix path.
- Total: **14 tests added, 14 passed, 0 failed** on first clean run.
- No bugs discovered. All four functions behave correctly on the tested cases:
  cube-at-z=0 cleanly splits with non-empty halves; out-of-range cuts produce
  one empty geometry and one with all 36 vertices; CCW square triangulates
  to 2 tris; concave L-shape triangulates to 4 non-degenerate tris;
  ear-clip output preserves input vertex references; cap-edge ordering
  correctly identifies single and disjoint loops; degenerate-triangle filter
  removes zero-area triangles and preserves valid ones.
