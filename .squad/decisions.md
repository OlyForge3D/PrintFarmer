# Decisions

# 2026-08-15: Reviewer-rejection revision ownership — canonical rule reinstated (reviewer-invoked lockout)

**By:** Dallas (Lead), resolving GitHub issue #1622, per repo-owner-authored task instructions.
**Status:** Authoritative. Supersedes the "RESCINDED" entry above for any future ambiguity.

## Decision

The blanket "there is no author lockout, ever" position above does not match this repository's
actual practice: PR #1619 shows a reviewer (Hicks) explicitly invoking lockout in round 5, which
was honored and compounded through round 7 to a clean merge. Issue #1622 asked for a deliberate
policy decision reconciling that practice with `.github/copilot-instructions.md`'s Pre-PR Review
Gate flow (which read as always-self-revise) and `.github/skills/reviewer-protocol/SKILL.md`
(which read as always-locked-out).

**The canonical rule, now recorded in `.github/copilot-instructions.md` § "Post-Rejection
Revision Ownership":**

- Self-revision is permitted **by default** on an ordinary rejection.
- Strict lockout activates **only when a reviewer explicitly invokes it** (e.g. "a different
  agent must revise this") — never automatically from a bare REQUEST_CHANGES/BLOCK.
- Once invoked, lockout compounds on repeated rejection, and roster exhaustion escalates to
  the user, per the rules in that section.

All files listed in the "Files corrected" table above (`squad.agent.md`, both reviewer/
agent-collaboration skills, the Rai/Fact Checker charter and policy copies, both
issue-lifecycle copies) have been re-aligned to this rule: none of them assert unconditional
lockout, and none of them assert lockout can never be invoked. Each links to
`.github/copilot-instructions.md` § "Post-Rejection Revision Ownership" as canonical rather
than restating the rule, so this cannot drift a third time.

This entry does not touch agent `history.md` files or `drake/charter.md` — those are
per-agent learning/staffing records outside this task's scope; if they still cite the
"no lockout ever" position, treat this entry as superseding that citation.

## Cost of the rule, for the record

During epic #931 a fourth frontend dev (**Drake**) was added purely because Ripley, Newt and Lambert
were each considered locked out of the same artifact. That hire was unnecessary. Earlier, on
2026-05-12, the rule had to be explicitly overridden twice because Lambert was the only Backend Dev
on the roster and the escalation path was unavailable.

## What is retained

Multi-reviewer consensus with fresh-hand rotation stays — it is genuinely effective and caught real
defects across four gate rounds. What is dropped is only the part that **barred the author from
their own fix**.

---
## Decision: 3-Way Review Consensus Verdict on Fix Obico ML Badge Test Leak

| Field | Value |
|-------|-------|
| **Date** | 2026-08-04T13:45:20-07:00 |
| **Agent** | 3-Way Review Panel (Bishop/Hicks/Vasquez) |
| **Requested by** | Jeff Papiez |
| **Status** | REJECT (prose-accuracy only) |

## Core Findings

1. **Panel Gated on Its Own Defect**: The commit body and issue #1106 both cited "~562ms transform vs ~32ms import" as evidence, which Bishop produced during the FIRST gate cycle. This claim ("resetModules re-incurs the import tier, not the transform tier") is false, not merely misscoped. The reported "import" figure is summed file collection time in Vitest runner (`runner.importFile(...,"collect")`), so resetModules-induced re-imports land in the "tests" phase, never "import". Meanwhile, "transform" accumulates every fetch duration, including cached fetches, so the cache-miss re-fetch feeds "transform". Neither figure measures `MdiIcons` or the reset penalty.
2. **Consistency of Review Standard**: Since the panel gated f2f0df88 for false mechanism claims in durable prose in the last cycle, consistency required a REJECT verdict on this cycle.
3. **Imprecise Verb Choice**: The verb "re-resolve" in the proposed comment is incorrect. `vi.resetModules()` clears evaluation state but leaves the node in the module map; resolution is not cleared. The correct verb is "re-evaluate" or "re-execute".
4. **Transform Cache Behavior Confirmed**: The core technical mechanism claim "resetModules clears evaluation state, not the transform cache" is true. This contrast with `invalidateModule`, which does clear transform state, was verified. The subsequent import correctly re-executes cached transformed code. `ShieldIcon` is a stateless pure SVG function.
5. **Ruling on Land-vs-Close**: Unanimously ruled to **LAND** (do not close #1106 as churn). The false comment was already merged in development, so declining to correct it leaves an actively misleading explanation in mainline; correcting it is necessary to properly teach future maintainers.
6. **Proportionality Finding Against the Panel**: The panel rejected a working fix over a comment, the code merged anyway via a concurrent actor (PR #1099), and the author opened a second PR purely to satisfy the panel. One bounded amendment is still justified because a false rationale in an unmerged commit body can be corrected cheaply now, but "approve with follow-up" cannot repair a merged commit body.

---
## Decision: 3-Way Review Consensus Verdict on Fix Obico ML Badge Test Leak (Cycle 3)

| Field | Value |
|-------|-------|
| **Date** | 2026-08-04T14:17:07-07:00 |
| **Agent** | 3-Way Review Panel (Bishop/Hicks/Vasquez) |
| **Requested by** | Jeff Papiez |
| **Status** | APPROVED |

## Core Findings

1. **Unanimous Approval**: Under Cycle 3, the panel unanimously APPROVED the comment-only correction (1 file, +4/-4) at HEAD `7aada99dd`, resolving the prose-accuracy issue of the previous cycles.
2. **Technical Accuracy Verified**: The panel confirmed that the corrected text is accurate. Sharpest test applied: a post-reset re-import DOES fire fetchModule with `cached:true` (`vite/dist/node/module-runner.js:1083,1086`), the server returns 304 (`:1094`), and the round-trip duration IS added to `transformTime` (`vitest cli-api.B7PN_QUv.js:7296-7299`) — so "transform" is affected by the re-import. But the re-evaluation itself runs in `directRequest` (`module-runner.js:1101-1146`) inside test execution, outside both the timed fetch RPC and the collect phase. Therefore, "neither number measures this re-evaluation" is true of the evaluation cost.
3. **Other Claims Validated**: "resets every non-mock evaluated module" is accurate (per `vitest utils.DvEY5TfP.js:22-35` which exempts Vitest internals, not only mocks). The quoted "pay the full transform cost on every run" is faithful to the removed lines. The diff is exactly one file / one commit whose parent is the base (clean rebase, nothing dropped or duplicated), and concurrent work remains intact.
4. **Acceptable Maintainer Shorthand**: The panel declined to overrule the author's declared deviation (using "transform cache" rather than `transformResult`), ruling it as acceptable maintainer shorthand.

---
## Decision: Squad State Bridge Root-Path Loss (#1130)

| Field | Value |
|-------|-------|
| **Date** | 2026-08-04T20:34:00.249-07:00 |
| **Agent** | Squad (Coordinator) |
| **Requested by** | Jeff Papiez |
| **Status** | RESOLVED — fixed by PR #1136, #1130 closed 2026-08-05 |

## Core Findings

1. **State writes were misrouted**: The filesystem-backed `squad_state` bridge resolved keys into repo-root paths such as `decisions/inbox/` instead of the tracked `.squad/` tree. Writes reported success but were invisible to every other worktree. Root cause was `"teamRoot": "."` in `.squad/config.json`, which put the SDK in remote mode and resolved the team directory to the workspace root. Composition is `<root>/<key>` with the root resolved once, so **every** key was affected, not only `decisions`.
2. **The misroute had two failure modes**: where the root path was covered by `.gitignore` (`decisions/`, `agents/`, `orchestration-log/`, `log/`) the loss was *silent* — `git status` stayed clean and the records simply vanished. Where it was not covered (`memory/`) the loss was *dangerous* — the files showed as untracked, so an unrelated `git add -A` would commit Squad state into the repository. Final measured residue: 42 files across 13 locations in 9 worktrees.
3. **Canonical locations**: Accepted decisions belong in `.squad/decisions.md`; pending proposals and coordination drop-box entries belong in `.squad/decisions/inbox/`. Repo-root state paths are invalid and must not be treated as durable.
4. **Verification method — the original interim rule was wrong and is superseded**: The rule first recorded here directed writing accepted decisions straight to `.squad/decisions.md` and confirming durability with `git check-ignore` / `git status`. **Those checks cannot establish durability.** In the quarantined mode they are actively blind: a misrouted write into an ignored root path is untracked *by construction*, so `git check-ignore`, `git status`, and `git ls-files` each return a clean answer precisely when the bug fires. In the un-quarantined mode (`memory/`) they do signal, but they signal the wrong property — "this file is not tracked" is not the same fact as "state landed outside `.squad/`", and a clean result never distinguishes correct placement from a write that was quarantined out of sight. Every one of these probes reports a true fact about a *git* property while the defect is *filesystem placement*. Durability must therefore be asserted by filesystem existence — no state key materialises at the repo root — never by a git property. Demonstrated: 13 real stranded files on disk while `git ls-files 'decisions/'` returned empty.
5. **Recovery boundary**: Do not move or delete stranded files while their owning sessions are active. Recovery and migration are tracked under #1135 and must be deliberate, owned, and verified.

## Resolution

Fixed by PR #1136 (merged 2026-08-05): removes `"teamRoot": "."` from `.squad/config.json` and pins `SQUAD_TEAM_ROOT` to the workspace folder in `.mcp.json`. `/memory/` was added to the `.gitignore` quarantine block, root-anchored so it cannot shadow the tracked `.squad/memory/`. A CI fixture (`scripts/ci/tests/test-squad-state-root.mjs`) asserts filesystem non-existence at the fixture root; it is sound and goes red on the injected defect, though its key coverage is currently `decisions`-only — see #1130 for the coverage-hardening note and the allowlist trap that makes a naive enumeration pass vacuously.
| **Status** | INCIDENT / ACTION REQUIRED |

## Core Findings

1. **State writes are misrouted**: The current filesystem-backed `squad_state` bridge resolves some keys into repo-root paths such as `decisions/inbox/` instead of the tracked `.squad/` tree. The writes report success but are invisible to other worktrees.
2. **The misroute is silently ignored**: Root `decisions/`, `agents/`, `orchestration-log/`, and `log/` paths are covered by `.gitignore`, so `git status` does not expose the lost records.
3. **Canonical locations**: Accepted decisions belong in `.squad/decisions.md`; pending proposals and coordination drop-box entries belong in `.squad/decisions/inbox/`. Repo-root state paths are invalid and must not be treated as durable.
4. **Interim operating rule**: Until #1130 is fixed and verified, accepted decisions must be written directly to the tracked `.squad/decisions.md` path and checked with `git check-ignore`/`git status`; do not rely on `squad_state` for decision durability.
5. **Recovery boundary**: Do not move or delete stranded files while their owning sessions are active. Recovery and migration must be deliberate, owned, and verified through #1130/#1131.

---
#### 2026-08-26: Make yamllint fail closed with a pragmatic workflow policy
**By:** Parker
**What:** Run yamllint on every pull request, preserve its real exit status through
artifact upload, and fail explicitly afterward. Use a checked-in policy that disables
`document-start`, excludes mapping keys from `truthy`, reports lines over 120 characters
as warnings, and promotes comment spacing to an error. Pin workflow YAML to LF in
`.gitattributes`.
**Why:** The previous `main` branch filter skipped the repository's `development` PR
flow, while `|| true` made every captured status zero. GitHub Actions expressions and
shell commands are frequently unsafe to wrap, but structural YAML, trailing whitespace,
bracket spacing, and comment spacing remain low-risk blocking checks. A workflow-scoped
LF attribute prevents Windows checkouts from reintroducing tracked CRLF blobs.

---
#### 2026-08-25: Profile-family Phases 0–1 frontend implementation
**By:** Ripley
**What:** Removed the auto-opened `CloneProfilesModal` flow from `/slicer` and replaced the generic no-machine-profile line with a reason-specific, accessible card. `no_profiles_for_model` explains that OrcaSlicer has no coverage and exposes a disabled **Create profile family** action with an associated **Coming soon** explanation. `alias_matched_no_profiles` identifies likely profile-coverage/engine-version drift and deliberately does not offer family creation. Missing or unknown codes render a generic, non-prescriptive load/empty state.
**Why:** The deleted modal cloned process profiles and could not repair a missing machine family, so auto-opening it made the user feel trapped in a loop. The new state reports the actual failure and reserves the correct remedy for the future Phase 3 wizard.

## Files changed

- `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx` — removed clone-modal import/state/effects/rendering; captured the machine-profile query error; added typed reason-code narrowing and the reason-specific card.
- `src/Web/ReactApp/src/features/slicer/pages/__tests__/NewSliceJobPage.test.tsx` — removed the obsolete modal mock/timer assertion; added coverage for both known codes, disabled-action accessibility, and a code-less fallback.
- `src/Web/ReactApp/src/test/features/slicer/pages/NewSliceJobPageOnboarding.test.tsx` — removed the obsolete clone-modal mock.

## Assumed backend contract

The HTTP 404 response body is assumed to be camelCase JSON:

```json
{
  "code": "no_profiles_for_model | alias_matched_no_profiles",
  "detail": "optional human-readable diagnostic"
}
```

The existing `apiClient` interceptor exposes that wire body as:

```text
{ message?: string, statusCode?: number, data?: { code?: string, detail?: string } }
```

The page reads only `error.data.code`; `detail` is retained in the local type but not rendered. If `data`, `code`, or a recognized value is absent, the generic fallback is used.

## Phase 0 interaction

Phase 1 deletes the `/slicer` `CloneProfilesModal` and its success callback entirely. Therefore the Phase 0 `['customProfiles']` addition has no surviving invalidation site in the combined end state; keeping an unused callback solely to preserve that line would be dead code. The future Phase 3 family mutation must invalidate `['customProfiles']`, `['machineProfilesForModel']`, `['slicerProfilesExtended']`, and `['slicerProfilesHierarchy']` as approved in §B6.2.

## Validation

- `npm run build` — passed. Existing Vite native-loader/plugin and large-chunk warnings remain; none originate in the changed files.
- `npm run test:run` — passed once: **463 files, 5,135 tests**. Changed suites: `NewSliceJobPage.test.tsx` **53 passed**; `NewSliceJobPageOnboarding.test.tsx` **8 passed**.
- `npm run lint` — passed with **0 errors, 1 pre-existing warning** in untouched `SlicerWorkspace.tsx` (unused eslint-disable directive).
- Initial build found dependencies absent (`vite` not found); `npm install --no-audit --no-fund` restored the existing lockfile dependencies without changing manifests.

Implemented with accessibility in mind using semantic heading/section structure, polite announcement, and the shared explained-disabled button pattern. Manual browser/assistive-technology testing was not performed.
---
# 2026-09-03: iOS Navigation Redesign Direction Confirmed — Two Hats, Adaptive Architecture

**Date**: 2026-09-03T14-35-00-07-00
**Agent**: Coordinator (on behalf of Frost/@jpapiez)
**Status**: APPROVED — Epic #2410 + 17 child issues (#2411–#2427)

## Core Decision: Shell Architecture is A′ · Two Hats, Adaptive

The iOS client shell is derived from server signals, never asked during onboarding. The shell adapts between:
- **Simple Shell** (default): 4 tabs (Attention · Farm · Inventory · Oversight)
- **Two-Modes Shell** (when staffed): Floor mode (Attention · Farm · Tasks · Inventory) + Oversight mode (Overview · Fleet · Jobs · Upkeep · Reports)

Shell selection is determined by `shiftPlanEnabled` backend flag.

## Signal Interpretation and Defaults

**`shiftPlanEnabled` is a negative signal only.** The flag defaults to `true`, so it acts as a blocklist rather than an allowlist. Treating it as a positive signal would derive every stock server as staffed and the Simple shell would never ship in default configurations. Under this rule, only servers with explicit `shiftPlanEnabled = false` drop to Simple mode.

## Farm-Shape Exposure via API

Farm-shape counts (total printers, queued jobs, etc.) are exposed as a **nullable authenticated-only sibling field `farmShape` on `PlatformCapabilitiesDto`**, NOT nested inside `operatorFeatures` (which remains a closed bool-only record).

**Cache and anonymous access rules:**
- `/api/system/capabilities` is `[AllowAnonymous]` with a public 30-second cache.
- Counts must be omitted for anonymous callers to prevent farm reconnaissance leaks to unauthenticated LAN devices.
- Cache header changed from default to prevent profile leakage.

## Navigation Structure Stability

- **Attention stays in the tab bar** — proposal to move it to a nav bar was rejected. Attention is first-class, not subordinate.
- **Scan removed as a tab** — becomes an input method inside Tasks, plus one nav-bar icon on the Inventory tab.
- **Nothing is hidden by a layout setting** — visibility is not a configuration option. Only `AdvancedPrinterControlsView` has an off-by-default gate because misuse damages hardware.

## Onboarding and Upgrade Flow

- **No first-run questionnaire** — the shell derives from the server signal automatically.
- **Upgrade offered inline, dismissible** — when the shell migrates from Simple to Two-Modes (or vice versa), an inline notification is shown once. The user may dismiss it; no auto-application occurs.

## Related Epic and Issues

- Epic #2410 approved, tasks distributed across team members:
  - Hudson (#2412, #2416–#2423, #2427): Core shell and navigation implementation
  - Lambert (#2411): Shell state management and signaling
  - Drake (#2413): Tests and validation
  - Ash (#2414): Accessibility and UI refinement
  - Gorman (#2415): Farm-shape API endpoint
  - Kane (#2424, #2425): Analytics and telemetry
  - Newt (#2426): Documentation

