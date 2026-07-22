---
## Merged from Inbox: 2026-07-13T13:10:45Z

## Decision: Hicks Reviewer Model Upgrade to gpt-5.6-sol

| Field | Value |
|-------|-------|
| **Date** | 2026-07-12T17:17:53.916-07:00 |
| **Agent** | Scribe |
| **Status** | Proposed |
| **Requested by** | Jeff Papiez |

## Directive

Update the GPT code reviewer (Hicks) persistently from GPT-5.5/older GPT-5.x to model `gpt-5.6-sol` with reasoning effort `max`.

## Scope

- `.squad/team.md`
- `.squad/routing.md`
- `.squad/config.json`
- `.squad/casting/registry.json`
- `.squad/agents/hicks/charter.md`

## Rationale

Model upgrade enables deeper reasoning capability for complex backend code reviews. Sol variant optimized for coding tasks; max reasoning maximizes rigor for triple-consensus gate.

## Notes

- Preserve append-only semantics in all files.
- Do not modify application code, GitHub issues, or PR #741.
- Applied: Hicks history updated; used `gpt-5.6-sol/max` for 2026-07-13 #708 review.

---
## Decision: Narrow Global Heading Typography Scope

| Field | Value |
|-------|-------|
| **Date** | 2026-06-03T12:42:57-07:00 |
| **Agent** | Lambert |
| **Status** | Proposed |

## Decision

Limit the shared display-heading rule in `src/Web/ReactApp/src/index.css` to `h1` and `h2`.
Leave `h3` through `h6` on the default semantic typography path unless a surface opts into display styling explicitly.

## Rationale

The previous global rule forced Bebas and uppercase styling onto every heading level,
which made secondary headings overly loud on settings and unrelated pages.
Narrowing the rule solves the issue at the design-system layer instead of relying on
page-specific overrides.

## Impact

Pages such as Settings and API Keys can use `h3`-`h6` without inheriting display
heading treatment automatically.
Teams that want display styling below `h2` should add it intentionally at the component
or page level.

---
## Decision: Phase 1 Settings IA Query Model

| Field | Value |
|-------|-------|
| **Date** | 2026-06-06T08:58:45.350-07:00 |
| **Agent** | Ripley |
| **Requested by** | Jeff Papiez |
| **Status** | Proposed |
| **Scope** | React settings information architecture |

## Recommendation

Use a normalized query model inside the existing settings shell:

- `scope=user|system|admin`
- `tab=<category>`
- `sub=<sub-page>`

Keep the current `/settings` route alive for Phase 1, but resolve legacy `?tab=` links through shared navigation helpers so the shell can infer the correct scope and destination.

## Why

This lets the frontend ship the new User/System/Admin information architecture without moving page components or changing route ownership yet.
It also keeps existing bookmarks working while the sidebar, command palette, and search all move onto the new scope-aware model.

## Compatibility Notes

- Legacy `tab=notifications` now resolves to User Settings → Profile → Notifications.
- Legacy `tab=users&sub=api-keys` now resolves to User Settings → Profile → API Keys.
- Legacy operational tabs such as `system`, `users`, and `data` continue to resolve into Admin categories.


---
## Decision: Lockout Policy Dropped for #785/#816/#817

**Date:** 2026-07-21
**Author:** coordinator (via owner directive from jpapiez)
**Status:** ACTIVE — supersedes all prior clean-room / author-lockout guidance on the mobile snapshot line

**Ruling:**
Owner (jpapiez) has rescinded the #785/#816/#817 clean-room lockout policy. The #816 and #817 GitHub issue bodies have been amended at source to remove the hardcoded lockout rosters; those issue bodies are now authoritative.

**New governing rules:**
1. **No agent lockouts** on #785/#816/#817. No one is barred from implementing, revising, advising, contributing, or producing evidence.
2. **Author owns all revisions, including post-REJECT.** On a trio REJECT, the SAME author revises their own branch (standard iterate-on-feedback). No forced handoff, no reviewer-authored-fix routing.
3. **Clean-room isolation rescinded.** Prior-attempt learnings (Dietrich, Apone, Crowe, Morse, or any other source) may be shared and consulted freely if useful. Deriving criteria from acceptance criteria + iOS Review Rubric remains good hygiene, but is not a rule.
4. **Findings-hygiene "describe properties not mechanisms" cancelled.** Reviewers may give direct, specific, actionable findings including concrete mechanism suggestions and fixes. Standard code-review posture.

**Gates that STAND:**
- 3-way adversarial trio review (Bishop / Hicks / Vasquez must converge on unanimous APPROVE) before any PR opens. Owner dropped lockout, not the review gate.

**Ownership per owner decision:**
- #816: Clemens is sole owner. Iterates own branch on REJECT. Under review at HEAD `6c57ec067010626f631c933550db475eb7e9d317`.
- #817: Drake is owner. Parked on #816-merge data dependency (not a lockout). Session `bfb226f0-4d0a-49ce-bb65-3e7cb61338b1`.
- Kane: unblocked.

**Prior orders now VOID:** all "no standby-author content relays," "clean-room isolation halts," "no locked-author-attributed review lenses," "no Apone intel to Drake," "reviewer-authored-fix workflow," and "describe properties not mechanisms" guidance issued earlier in the mobile snapshot coordination session.

**Coordinator handoff:** feature/705 session (this coordinator, `10d59103`) now owns the mobile snapshot coordination end-to-end. Prior coordinator session (`7188e04c`) stood down after handing over.

**References:** #785, #816, #817

---
## Decision: Sync Feature 705 After Every Merge

**Date:** 2026-07-22
**Author:** coordinator (via owner directive from jpapiez)
**Status:** ACTIVE

After each issue pull request merges, immediately synchronize the local
`feature/705-operator-redesign` branch with
`origin/feature/705-operator-redesign` before dispatching, rebasing,
validating, or integrating dependent work.

Confirm local and remote alignment, then use the refreshed remote tip as the
base for subsequent issue branches and immutable-SHA reviews.
