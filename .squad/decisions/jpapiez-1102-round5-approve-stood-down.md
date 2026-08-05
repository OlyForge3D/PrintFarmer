# #1102 — round-5 review: unanimous APPROVE on 11d914d5a (branch stood down)

**Author:** jpapiez · **Date:** 2026-08-05 · **Branch:** `dev/jpapiez/1102-reconciled` @ `11d914d5a`
**Status:** approved, NOT shipping. Coordinator stood the branch down; reference-only, no PR.

## Verdict

Bishop / Hicks / Vasquez converged: **APPROVE**. All six round-4 MUST-FIX items were
re-verified independently against the built artifact, not against the author's assertions.

Reproduced independently by the reviewers:

- Layer order holds in `dist/assets/*.css`: `@layer components` at 12868 precedes
  `@layer utilities` at 21827; variant defaults at 19156, caller paint at 79066 /
  165923. Caller wins in both the resting and hover directions.
- Active pills `text-[var(--pf-text-inverse)]`: worst **4.92** across 8 palettes
  (was 1.37 with `text-white`, failing AA in 7 of 8).
- `ring-pf-accent` selected-row affordance: contrast-neutral, 8/8 palettes.
- ExplorerView delete `text-[var(--pf-on-danger)]`: worst **7.17**, versus 3.87 for
  the `text-pf-error` it replaced. The fallout commit improves contrast, not just hover.
- Both guards falsified by injection with byte deltas printed before trusting the result.
- Gates on the reviewers' own capture: lint clean, 356 files / 3993 tests, all pass.

## Known-imprecise claims in the approved artifact

Recorded rather than rewritten, because `11d914d5a` is the SHA the approval names and
an approved artifact should not move underneath its approval.

1. `src/test/styles/ghostButtonBuiltStylesheet.test.ts:400-402` — "a repo-wide sweep
   finds 20 instances; the 16 outside this list" conflates two different sweeps. The
   Button-specific regex the guard itself uses gives 16 total / 12 outside on base;
   the figure 20 only reproduces under an all-JSX-element sweep at HEAD. Raised by
   Hicks, accepted NON-GATING by all three: the scoping decision is sound at any of
   those counts and no in-scope offender is hidden. Tighten or drop the count when
   this file is next touched.
2. `11d914d5a` commit body cites `git check-ignore -q` as proof the rescued verdicts
   are durable. That tests ignorability, not placement — `git check-ignore -q
   state/foo.md` exits 1 while the path is just as lost (measured). The conclusion
   still holds under the stronger check: 6/6 verdict files tracked under
   `.squad/decisions/` with non-zero size, and `git ls-files decisions/ agents/
   orchestration-log/ log/` returns empty. Credit to Parker via session 32ac0c89.

## Hazard if a core-only #1102 ships instead

The core commit alone is not safe to land. It frees caller paint without the caller
remedies, which is precisely the CALLER CONTRACT fallout documented in controls.css:
three menu sites resolve to the same colour at rest and on hover and lose their hover
entirely — the folder-menu delete and new-folder items, and the shared context-menu
item. Measured dead in every palette on the original base, and 0/8 dead only once the
fallout commit lands. This is why core and fallout were sequenced as one inseparable
delivery.

Whoever picks up #1137 should treat this branch as a measured reference rather than
re-deriving: the remedies, the 8-palette contrast data, and the harness are all here.

Relates to #1102, #1122, #1130, #1137
