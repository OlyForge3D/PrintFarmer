# Tailwind v4 scans comments and test files — absence assertions can create the thing they test for

**By:** jpapiez (theme consolidation, #1117/#1129) with Kane (#1122)
**Date:** 2026-08-04
**References:** #1117, #1122, #1123, #1129, #1102

## The finding

Tailwind v4's automatic source detection scans **any file in the project tree it has not been told to ignore — including comments, test files, and scratch harnesses.** Naming a utility class anywhere in the tree causes Tailwind to emit that class into the built stylesheet.

Two sessions hit this independently, in different subsystems, on the same day:

- **#1117 / #1129 (themes):** a comment written to explain that `[background:none]` had been *removed* regenerated the rule into the build. The thing documented as deleted was shipping *because* it was documented. Separately, a measurement harness that merely named `pf-animate-progress` in a string caused that otherwise-dead utility to be emitted. (#1123 later corrected the resulting false claim in `controls.css`.)
- **#1122 (stylesheet guard):** the first revision of the guard named its absence canary contiguously in a TypeScript assertion. Tailwind emitted the class and the guard went red — the test caused the condition it was testing for.

Two independent reproductions in different subsystems makes this a property of the toolchain, not a mistake either session made.

## Why it is more than a nuisance

It makes a whole class of test **self-defeating**: any guard asserting a class is ABSENT from the built CSS can CAUSE it to be present, purely by naming it. The test then either fails forever, or — worse — passes for the wrong reason.

It also silently invalidates verification. On #1129 the claim "the dead rule is absent from the build" was false for an entire review round, because the search used the unescaped string. In built CSS it is `.\[background\:none\]`, so the naive pattern matched nothing and the empty result read as success. **For a negative assertion, a malformed pattern and a genuine absence are indistinguishable.**

## Practice

1. **Keep measurement harnesses and scratch scripts outside the scanned tree** — `node_modules/.pf-harness/` works.
2. **Assemble class names from runtime fragments** in any test asserting absence, so no literal appears in a scanned file. This is what #1122's committed guard does.
3. **Positively validate the search mechanism before trusting a negative result.** Confirm the pattern matches when the thing IS present, then assert it does not. Otherwise a broken query is indistinguishable from success.
4. **Explaining a deletion in a comment can undo it.** Prefer describing the shape of the removed thing over naming it literally.

## The general rule: assert on outputs, not inputs

Both sessions reached the same lesson from opposite directions. The clearest formulation: *a token-declaration parser cannot establish visual equivalence or theme inertness.*

Generalised: **do not assert on an artefact's inputs when the defect lives in its output.** `className` is an input. Source text is an input. Declared custom properties are an input. The built stylesheet and computed style are the output, and a cascade regression is only visible there.

On #1129 this was not academic — three legacy theme stylesheets were deleted as "inert" with build, lint, tsc and 3,947 tests green. One was not inert and the deletion was a visible regression. Every gate in the repo agreed with the wrong answer, because none of them looks at what the browser paints.

Ranked by what earned its keep:

1. **Computed style in a real browser**, against the built `dist`, transitions suppressed. Compare computed values, never declared ones.
2. **Whole-selector-set A/B diff** of built CSS against a baseline build of the merge-base — caught a regression no targeted probe was looking for, precisely because it asserts nothing and simply reports what changed. **Investigative evidence, not a standing gate:** it needs the merge-base commit, and `actions/checkout` defaults to `fetch-depth: 1`, so on CI that commit is absent; making it permanent costs extra history plus a second full production build per PR.
3. **Hand-picked probe lists — weakest and actively misleading.** The probe list gets chosen by the same misunderstanding that caused the bug, so it confirms the error.

## Falsify every guard

A gate that has only ever been green is ambiguous between "correct" and "cannot see the problem". Reintroduce the defect and confirm the gate goes red before trusting it. On #1129 two of nine guards only became genuine because of this step — one passed happily when the thing it required was commented out.

Prefer falsifying **end to end**. Mutating the built artifact tests the validator; mutating the source and rebuilding tests the pipeline. If the toolchain ever changes how it emits layers, a validator only ever exercised on hand-edited output keeps passing while the real build stops producing the structure it checks. Prefer `enforce: 'pre'` and **remove the rule from its layer, then re-append it unlayered** — that proves the validator distinguishes *misplaced* from *absent*; appending alone does not.

### Vitest/jsdom constraint on programmatic Vite builds

Recorded verbatim from the #1122 session, because it is the constraint that shapes any such guard:

> Importing Vite programmatically inside PrintFarmer's normal Vitest realm loads esbuild under jsdom globals and fails esbuild's startup invariant: `new TextEncoder().encode("") instanceof Uint8Array` is false. Switching the file to the Node environment is not sufficient, because the repository-wide setup file accesses `window`. Run programmatic Vite builds in a child Node process (or child harness) so esbuild sees a single native realm while the parent test retains the configured jsdom setup.

Where mutating a shared source file is unsafe — multiple live worktrees, and a killed process leaves the mutation behind — use a Vite `transform`/`load` hook keyed to the file path to mutate the module in memory for one build. Nothing touches disk and no sibling session can observe it.

## Postscript: this note was nearly lost to a known tooling defect

The first attempt to record this used the `squad_state` decision tool, which wrote to `<worktree>/decisions/inbox/` — the repo root, **not** `.squad/`, despite its own documentation stating keys are relative to `.squad/`. `.gitignore` already carries a section headed *"Misplaced Squad outputs — Squad state belongs under .squad/, never the repo root"* covering `/decisions/`, `/agents/`, `/orchestration-log/` and `/log/`, so the misroute is known and was silenced rather than fixed.

The consequence is worse than clutter: because the stray path is gitignored, `git status` reports **clean**, the write looks successful, and the record is invisible to every other worktree and never committed. A sibling session could not see this note and correctly declined to bypass governance to reach it. Anything written that way dies with the worktree.

**If you use the decision tool, verify the file landed under `.squad/` before treating it as recorded.**
