# History

## Core Context

PrintFarmer is a .NET 10 and React 19 application for 3D-printer farm management. The current work is requested by jpapiez.

## Learnings

- 2026-07-22: PR #856 CI run 29955960867 failed only in
  `LocationSubtreeTests.GetSubtreePrinters_WithDeepHierarchy_ReturnsAllDescendantPrinters`
  because three randomized `ServerUrl` values collided with the pre-existing unique
  index. The test and index configuration are byte-identical on HEAD, the stacked
  base, and main; the targeted test passed on rerun, so no stack patch was made.
