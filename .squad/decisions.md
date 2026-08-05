# Decisions

date: 2026-07-25
owner: Multiple
status: closed
issue: 931
---

## Epic #931 Resolved — Admin Console & Settings Redesign (Four Gate Rounds, Six Defects Fixed)

**Scope**: Issues #932–#941, merged to `feature/admin-console-redesign` PR #955.

### Final Status

- **All ten child issues merged**: #932 (UI primitives), #933 (admin overview), #934 (destination registry), #935 (settings rebuild), #936 (Control Center hub), #937 (essential manifest), #938 (command palette), #939 (redirects), #940 (architecture docs), #941 (regressions).
- **Four gate rounds conducted**: Round 1 (Bishop, Hicks, Vasquez), Round 2 (all three), Round 3 (Hicks locked out; Drake, Newt, Ripley, Lambert). Round 4 (Ripley regate).
- **Six defects found and fixed**:
  1. Deep links to command palette items used bare property names → collided across sections.
  2. DOM `id` attributes from `prop.name` → duplicate IDs on multi-section pages.
  3. Form `name` attributes in `SettingsPagelet` → duplicates across 5 input types on pages rendering multiple sections.
  4. `SettingsPage.extractFieldErrors` pinned validation errors to first section containing field name instead of section that was edited.
  5. Empty Slicing Defaults tab (backend group mapping mismatch).
  6. Authorization regression: per-section save endpoint missing `farm_admin` role guard and validator invocation.

### Most Important Decision for Future Work

**`prop.name` in the settings system is the camelCase JSON wire name, not the .NET property name.** `getSettings` returns raw wire JSON with no normalization. `enabled` appears on 13 settings classes, `intervalSeconds` on 4, `baseUrl` on 3, and several render on the same page. This mismatch caused four separate defects: broken command-palette deep-links, mispaired DOM `id`/`htmlFor`, wrong form `name` attributes, mis-attributed validation errors.

**Treat any bare-property-name lookup in this codebase as suspect until proven otherwise.** Any identifier keyed on `prop.name` alone is a bug and blocks review. Always use section-qualified identifiers: `` `${sectionKey}.${propName}` `` for DOM `id`, DOM `name`, aria relationships, error attribution, and deep-link lookups.

### Additional Process Lessons

1. **ValidationException memberless vs. member-named throws**: A memberless throw (`throw new ValidationException("reason")`) produces an error keyed by section rather than field. **21 of 23 throw sites across settings classes are memberless.** Both backend response shape and frontend error extraction must handle this. When a failure is naturally scoped to a specific property, use member-names form for superior UX; memberless form is acceptable for whole-section errors only.

2. **FieldErrors self-healing vs. sectionErrors non-healing**: `fieldErrors` self-heals via `handleFieldChange` recomputing on every keystroke. Server-sourced `sectionErrors` cannot be re-derived on the client and must be explicitly cleared. Copying the merge pattern between them caused a real bug.

3. **Green test suite insufficient**: Nine implementation agents each delivered fully-passing lint/build/test runs that still contained real defects, including a privilege regression. Every finding was verified by reading the code. Tests cannot substitute for careful code review.

4. **Independent reviewers more effective than any single model**: Round 1 produced three confidently-stated findings factually incorrect on inspection. Round 3 one reviewer cleared code the other correctly flagged. Multi-model review across independent agents caught what any single reviewer missed.

### Decision: Admin Settings Per-Section Save Requires Auth and Validation

**Date**: 2026-07-25 (Hicks)

Both `POST /api/settings` (bulk) and `POST /api/settings/{keyName}` (per-key) now require `[Authorize(Roles = "farm_admin")]` and invoke `IValidatableSetting.Validate()`. Both return the same 400 shape on validation failure. This was missing on the per-key endpoint after #935 moved the settings page's primary save path onto it.

**Rule for future settings work**: Any new endpoint mutating a section must include HTTP-level tests: (1) Unauthenticated → 401, (2) Authenticated but under-privileged → 403, (3) Correctly privileged + valid payload → success + persistence verified, (4) Correctly privileged + invalid `IValidatableSetting` → 400 with populated `errors` dict, (5) Blocklisted section (Telegram/HomeAssistant) → 404 even for admin.

### Decision: Settings Error Attribution — Memberless vs. Member-Named ValidationException Throws

**Date**: 2026-07-25 (Ripley)

- **Memberless throw** (`throw new ValidationException("reason")`): Quick; error only reaches user via the section-level `message` field (alert banner + save banner). Does NOT highlight a specific field.
- **Member-names throw** (`throw new ValidationException(new ValidationResult("reason", new[] { nameof(FooProperty) }))`): Error attached inline next to the field via FormField `error` slot. Visually superior for field-scoped errors.

**Guidance for future authors**: If failure is naturally scoped to a property (bad URL, negative timeout, missing token when toggle is on), use member-names. If failure crosses properties or section-wide (required combination missing, conflict between fields), memberless is acceptable — the alert lands on the card header where whole-section errors belong.

**Do NOT add more section-name-aware heuristics to `SettingsPage.extractFieldErrors`.** Current implementation recognizes exactly one convention (bare key == section key) and further magic will conceal bugs. If frontend needs nuance, backend should send it explicitly.

### Related Architecture Decisions (Settled in This Epic)

- **Settings navigation restructure** (#931 epic scope): Two primary destinations (Settings with User/System scopes, and Admin) with per-group save model. No Save All button in production (`saveAllSettings` is dead code).
- **Essential/Everything toggle via client-side manifest** (#937): Not a backend attribute. Manifest keys on backend `SectionName`; hidden fields survive round-trips unchanged. Search always searches everything.
- **Command palette global mount** (#938): Moved from `SettingsShell` (route-scoped) to `GlobalCommandPaletteProvider` wrapping `<Outlet />` in `Layout.tsx`. Ctrl+K now works on every authenticated route.
- **Admin UI primitives frozen** (#932): `AdminLoading`, `AdminEmpty`, `AdminError`, `AdminSaveBar`, `useDirtyState`, `adminToast`. All downstream pages adopt these; do NOT fork.
- **Admin destination registry contract** (#934): Single typed source of truth (`src/Web/ReactApp/src/features/admin/registry/`). ALL admin UI consumes registry; hardcoded lists are banned.
- **Admin overview endpoint architectural notes** (#933): Composed from existing health checks; 8s timeout; no 500s (hub is what users hit when broken); graceful degradation on probe failure.
- **Settings architecture documentation** (#940): `docs/SETTINGS_ARCHITECTURE.md` is canonical reference for future agents. Covers two-layer architecture, three routes/scopes, URL contract, deep-link sections, per-group save, essential manifest, global command palette, legacy redirects.
- **Heading typography narrowed** (#941 gate finding): Global display-heading rule limited to `h1` and `h2`; `h3`-`h6` use default semantic path unless opted into display style explicitly.

### Recommendation: Phase 1 Settings IA Query Model

Use normalized query inside existing shell: `scope=user|system|admin`, `tab=<category>`, `sub=<sub-page>`. Resolve legacy `?tab=` links through shared helpers. Keeps existing bookmarks working while new IA rolls out.

### Special Team Directive (User Input)

**Date**: 2026-06-06T10:11:08-07:00

Vasquez must use GPT-5.5 for all 3-way pre-PR reviews (user request from Jeff Papiez).

---
## Decision: Admin Overview Endpoint Response Contract & Aggregation (#933)

**Date**: 2026-07-25
**Agent**: Lambert
**Status**: closed

**Route**: `GET /api/admin/overview` (auth: `farm_admin`, no caching).

**Response** (camelCase, string enums): `AdminOverviewDto` with `checkedAt`, `subsystems`, `attention`.

**Subsystems** (composed from health checks; aggregates `comprehensive`, `signalr`, `spoolman`):
- `api`: Healthy by responding
- `database`: From `comprehensive` sub-payload
- `backends`: From `comprehensive` sub-payload; per-printer failures become individual `attention` items
- `signalr`: Direct 1:1 mapping; non-Healthy → one `attention` item
- `spoolman`: Direct 1:1 when configured; hidden when "not configured"

**Graceful degradation**: 8s hard timeout. On timeout/exception/partial failure: `api` downgrades to `Degraded`, others become `Unknown`, `admin-overview-probe-failed` `Error` attention item emitted. **A single failing subsystem check must never 500 this endpoint.**

**Attention items** (sorted Error → Warning → Info, stable by title): Database unhealthy, printer backends unreachable, registered health check non-Healthy.

**Enum vocabulary** (serialized as strings): `SubsystemStatus: "Healthy" | "Degraded" | "Unhealthy" | "Unknown"`, `AttentionSeverity: "Info" | "Warning" | "Error"`.

**Client routing**: `actionRoute` always client route (`/admin/system`, `/printers`), never raw API URL.

---
## Team Decision: Bare Property-Name Lookups Are Bugs (#931)

**Date**: 2026-07-25
**Agent**: Newt
**Status**: closed

**Root cause across four defects**: Code treating bare property name as identifier when name is only unique inside its section.

**Instances**:
1. Command-palette deep links keyed on `prop.name` collided across sections.
2. DOM `id`s keyed on `prop.name` produced duplicate ids (connections page renders 5 sections; `enabled` declared on 13 settings classes).
3. Form `name` attributes in `SettingsPagelet` (5 control types) emitted duplicates.
4. `SettingsPage.extractFieldErrors` pinned errors to first section whose properties contained field name (was almost never the one being edited).

Only safe global identifier: `` `${key}.${name}` ``. `SettingsPagelet.tsx:101` computes this as `fieldId` — just wasn't used consistently.

**Team rule**: Any identifier keyed on `prop.name` alone is a bug and blocks review. Applies to DOM ids, DOM `name`, routing hrefs, deep-link params, error routing, lookups.

**Concrete guidance**:
- Metadata-driven forms: Always use section-qualified id (`` `${sectionKey}.${propName}` ``) for DOM `id`, DOM `name`, aria relationships, error attribution.
- Backend returns bare or `section.field` error keys: Bare form is by contract tied to section just posted. Never search metadata to guess.
- Two-sided data structures: Test guards must assert **both** directions.

**Related lesson**: Deliberate-break proofs mandatory for every new test. Revert fix, run test, confirm fail, restore. Six agents shipped green suite with real defects; agent doing proofs shipped clean. Cost under a minute per fix.

---
# 2026-07-26: Reviewer-rejection author lockout — RESCINDED

**By:** Jeff (repo owner) — directive, restated multiple times
**Status:** Authoritative. Supersedes every prior lockout decision in this file and in
`decisions-archive.md`.

## Decision

**When a reviewer rejects work, the original author fixes it.** There is no author lockout.
Nobody is ever excluded from an artifact because they authored a version of it that was rejected.

- Reviewers report *what* is wrong and *why*. They do not assign who does the fix.
- Repeated rejection is a signal to give the author better information — the specific finding,
  a failing test, a clearer spec — not a signal to rotate authors.
- Reassignment is a normal routing decision (skill gap, capacity, explicit user request) and must
  be justified on those grounds. It is never a consequence of rejection.
- Do not add a team member to work around a rejection.

## Why this kept coming back

The user rescinded this rule more than once and it kept reappearing. The cause was **duplication**,
not forgetfulness: the rule was written into twelve separate files, including the coordinator's own
agent definition (reloaded into the system prompt every session) and several agent `history.md`
files (read at every spawn). Recording the rescission in `decisions.md` alone could never win
against those, because they are loaded earlier and treated as governance.

## Files corrected

| File | Why it mattered |
|---|---|
| `.github/agents/squad.agent.md` | Coordinator definition — loaded into system prompt every session. **Primary cause.** |
| `.github/skills/reviewer-protocol/SKILL.md` | An entire skill dedicated to enforcing lockout |
| `.github/skills/agent-collaboration/SKILL.md` | Told every agent with review authority to lock the author out |
| `.github/rai-policy.md`, `.squad/templates/rai-policy.md` | Rai red-verdict escalation path |
| `.github/Rai-charter.md`, `.squad/templates/rai-charter.md`, `.squad/agents/Rai/charter.md` | Rai's charter, three copies |
| `.squad/templates/fact-checker-policy.md` | Fact Checker contradiction path |
| `.squad/issue-lifecycle.md`, `.squad/templates/issue-lifecycle.md` | Squad-member PR review |
| `.squad/agents/{bishop,brett,dallas,hicks,kane,lambert,ripley,vasquez}/history.md` | Carried it as a **learning**, re-teaching it at every spawn |
| `.squad/agents/drake/charter.md` | Seat was justified by the rescinded rule |

Historical records in `decisions.md` and `decisions-archive.md` are left intact — they document
what happened at the time and are append-only. This entry is the current word.

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
