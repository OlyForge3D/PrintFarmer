# Work Routing

How to decide who handles what.

## Routing Table

| Work Type                        | Route To     | Examples                                                                                          |
| -------------------------------- | ------------ | ------------------------------------------------------------------------------------------------- |
| Architecture, design, scope      | Dallas       | System design, technical decisions, cross-domain concerns |
| React, TypeScript, UI, frontend  | Ripley | Components, pages, styling, frontend state, SignalR client |
| C#, .NET, API, database, backend | Lambert | Controllers, EF Core, migrations, backend plugins, SignalR hubs |
| Testing, QA, coverage            | Kane | Write tests, find edge cases, run test suites, coverage analysis |
| Documentation, API docs, user guides, README | Ash | API reference, user docs, changelogs, migration guides, config docs |
| Research, competitive analysis, features | Brett | Market research, competitor analysis, feature recommendations, trends |
| DevOps, Docker, deployment, CI/CD, infra | Parker | Dockerfiles, compose, deploy scripts, GitHub Actions, install automation |
| UI/UX design, visual quality, styling, themes, design system | Newt | Component aesthetics, color systems, layout, spacing, dark theme, design tokens, visual audits |
| Scope & priorities               | Dallas | What to build next, trade-offs, decisions |
| Async issue work (bugs, tests, small features) | @copilot 🤖 | Well-defined tasks matching capability profile |
| Session logging                 | Scribe       | Automatic — never needs routing                                                                   |
| RAI review                      | Rai          | Content safety, bias checks, credential detection, ethical review                                 |
| Verification / devil's advocate | Fact Checker | Claim verification, hallucination checks, pre-mortem / counter-arguments                          |

## Issue Routing

| Label          | Action                                               | Who           |
| -------------- | ---------------------------------------------------- | ------------- |
| `squad` | Triage: analyze issue, evaluate @copilot fit, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |
| `squad:copilot` | Assign to @copilot for autonomous work (if enabled) | @copilot 🤖 |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, **Dallas** triages it — analyzing content, evaluating @copilot's capability profile, assigning the right `squad:{member}` label, and commenting with triage notes.
2. **@copilot evaluation:** **Dallas** checks if the issue matches @copilot's capability profile (🟢 good fit / 🟡 needs review / 🔴 not suitable). If it's a good fit, the Lead may route to `squad:copilot` instead of a squad member.
3. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
4. When `squad:copilot` is applied and auto-assign is enabled, `@copilot` is assigned on the issue and picks it up autonomously.
5. Members can reassign by removing their label and adding another member's label.
6. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

### Lead Triage Guidance for @copilot

When triaging, the Lead should ask:

1. **Is this well-defined?** Clear title, reproduction steps or acceptance criteria, bounded scope → likely 🟢
2. **Does it follow existing patterns?** Adding a test, fixing a known bug, updating a dependency → likely 🟢
3. **Does it need design judgment?** Architecture, API design, UX decisions → likely 🔴
4. **Is it security-sensitive?** Auth, encryption, access control → always 🔴
5. **Is it medium complexity with specs?** Feature with clear requirements, refactoring with tests → likely 🟡

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn Kane to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. Dallas handles all `squad` (base label) triage.
8. **@copilot routing** — when evaluating issues, check @copilot's capability profile in `team.md`. Route 🟢 good-fit tasks to `squad:copilot`. Flag 🟡 needs-review tasks for PR review. Keep 🔴 not-suitable tasks with squad members.
9. **A documentation-only PR needs one reviewer, not the full unanimous round.** What qualifies, which carve-outs keep the full gate, and how to pick that one reviewer are defined once in `.squad/skills/agent-collaboration/SKILL.md` — do not restate them here. Rule 9 above is unaffected.
10. **Read-only reviewer precedence** — Bishop, Hicks, and Vasquez review sessions are
    read-only and take precedence over generic process-tracking instructions. They MUST
    NOT create, edit, or delete `Copilot-Processing.md`, any tracking file, or
    implementation files. Dispatches must direct them to use only tools exposed in the
    current session; they must not assume tool names from another host. If a required
    read-only capability is unavailable, the reviewer reports an explicit environment
    blocker naming the capability and blocked review step. Implementation agents retain
    the full process-tracking requirement.
11. **Read-only agents are `task` calls, not sessions.** Code reviewers in particular are always spawned with `task`, never `create_session`. Same file, same reason: one definition, no drift.
12. **Risk-based review scope is canonical.** Follow `.github/copilot-instructions.md` § "Risk-Based
    Review Scope": standard and documentation-only changes use one qualified non-author reviewer;
    high-risk changes use the Bishop + Hicks + Vasquez panel. Do not restate the scope definition
    here.
13. **Excluded roster entries (📋 Scribe, 🔄 Ralph) are never dispatch owners, but their
    `squad:*` labels stay resolvable — for two different reasons.** 📋 Scribe (Session Logger)
    and 🔄 Ralph (Work Monitor) are infrastructure roles, not implementation owners, so
    `squad-triage.yml`, `sync-squad-labels.yml`, and `.squad/templates/ralph-triage.js` all
    exclude them from the routable/labelled member list via `isRosterExcluded()` in
    `scripts/ci/squad-routing.cjs`. `squad-issue-assign.yml` (the manual-label path) is
    different: it must still resolve `squad:scribe` / `squad:ralph` to a name so it can post a
    legible **refusal** — "🚫 Not a dispatchable owner" — instead of a silent no-op, but it must
    never post a work-assignment comment for them. This is distinct from a genuinely **retired**
    label whose member has left `.squad/team.md` entirely (e.g. old `squad:kaylee` /
    `squad:mal` / `squad:apone` / `squad:crowe` labels on closed issues): those already fail to
    match any roster row and fall through to the pre-existing "⚠️ no member found" warning,
    unchanged, which preserves how historical assignment comments render. See
    `scripts/ci/squad-routing.cjs`'s `isRosterExcluded()` doc comment and
    `.github/workflows/squad-issue-assign.yml` for the full policy.
