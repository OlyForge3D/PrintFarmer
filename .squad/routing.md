# Work Routing

How to decide who handles what.

## 🍏 Host Capability Gating — iOS build/test requires macOS

This repo is shared across dev machines with different operating systems. iOS
work has a hard platform dependency: **anything that builds, tests, signs, or
runs the iOS app requires a macOS (Darwin) host with Xcode.** This rule is
universal — it applies identically on every machine.

**Before running any iOS build/test/sign step, preflight the host OS**
(`uname -s` must be `Darwin`). Commands that are gated: `xcodebuild`, iOS
Simulator, `xcrun`, `swift build/test`, `fastlane`, `PrintFarmerTests` /
`PrintFarmerUITests`, TestFlight/APNs signing — anything under `mobile/`.

**On a non-macOS host:**
- Do **not** attempt iOS build/test/sign — they will fail and are not valid there.
- Pure source edits to `mobile/` *may* proceed, but the change is **not
  validated** until a macOS host builds/tests it. Mark such work
  `needs-macos-validation` and hand verification to a macOS machine (or defer).
  Never claim an iOS task complete without a macOS build/test pass.
- `area:ios` acceptance gates (XCTest/XCUI green, simulator screenshots) must be
  satisfied on macOS.

**On a macOS host:** iOS build/test/sign run normally.

### Per-machine scope overrides (machine-local, not committed)

A machine may narrow its own focus (e.g. "this Mac works iOS-only") in
**`.squad/machine-local.md`** — a **gitignored** file that never ships to other
machines. If that file exists, honor it in addition to the capability rule above.
If it is absent, follow the normal routing tables below.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, design, scope | Dallas | System design, technical decisions, cross-domain concerns |
| React, TypeScript, UI, frontend | Ripley | Components, pages, styling, frontend state, SignalR client |
| C#, .NET, API, database, backend | Lambert | Controllers, EF Core, migrations, backend plugins, SignalR hubs |
| Code review (pre-commit gate) | Bishop + Hicks + Vasquez | Triple-model review: Claude Opus 5, GPT-5.6 Sol, Gemini 3.1 Pro Preview — all three review in parallel; must achieve consensus APPROVE before PR creation |
| Testing, QA, coverage | Kane | Write tests, find edge cases, run test suites, coverage analysis |
| Documentation, API docs, user guides, README | Ash | API reference, user docs, changelogs, migration guides, config docs |
| Research, competitive analysis, features | Brett | Market research, competitor analysis, feature recommendations, trends |
| DevOps, Docker, deployment, CI/CD, infra | Parker | Dockerfiles, compose, deploy scripts, GitHub Actions, install automation |
| UI/UX design, visual quality, styling, themes, design system | Newt | Component aesthetics, color systems, layout, spacing, dark theme, design tokens, visual audits |
| Scope & priorities | Dallas | What to build next, trade-offs, decisions |
| Async issue work (bugs, tests, small features) | @copilot 🤖 | Well-defined tasks matching capability profile |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, evaluate @copilot fit, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |
| `squad:copilot` | Assign to @copilot for autonomous work (if enabled) | @copilot 🤖 |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, evaluating @copilot's capability profile, assigning the right `squad:{member}` label, and commenting with triage notes.
2. **@copilot evaluation:** The Lead checks if the issue matches @copilot's capability profile (🟢 good fit / 🟡 needs review / 🔴 not suitable). If it's a good fit, the Lead may route to `squad:copilot` instead of a squad member.
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

## Repo Routing

When work is repo-specific, pass the correct repo path as `WORKTREE_PATH` in the spawn prompt.

| Work Domain | Repo | Path |
|-------------|------|------|
| Backend API, EF Core, migrations, SignalR hubs, slicer workers | PFarm1 | `/Users/jpapiez/s/PFarm1` |
| React dashboard, Tailwind, Vitest frontend tests | PFarm1 | `/Users/jpapiez/s/PFarm1` |
| iOS app, SwiftUI views, Swift models, XCTest | PFarm-Ios | `/Users/jpapiez/s/PFarm-Ios` |
| Spoolman Python backend, FastAPI routes, Alembic migrations | spoolman_pf | `/Users/jpapiez/s/spoolman_pf` |
| Spoolman React frontend, Ant Design, Refine | spoolman_pf | `/Users/jpapiez/s/spoolman_pf` |
| Cross-repo API contracts, shared types | Both PFarm1 + spoolman_pf | Both paths |

### Agent Repo Assignments

| Agent | Primary Repo(s) | Notes |
|-------|----------------|-------|
| Lambert 🔧 | PFarm1 | C#/.NET — will step into spoolman_pf Python with explicit instruction |
| Ripley ⚛️ | PFarm1, spoolman_pf | React/TypeScript in both repos |
| Parker ⚙️ | PFarm1, spoolman_pf | Docker/CI touches both |
| Kane 🧪 | All three | Tests span repos |
| Ash 📝 | All three | Docs span repos |
| Dallas 🏗️ | All three | Architecture is cross-repo |
| Newt 🎨 | PFarm1, spoolman_pf, PFarm-Ios | React UI + iOS HIG/SwiftUI design |
| Hudson 📱 | PFarm-Ios | All SwiftUI views and navigation |
| Gorman 🌐 | PFarm-Ios | All networking, REST clients, SignalR, Swift models |
| Brett 🔍 | All three | Research has no repo boundary |

> **iOS work:** PFarm-Ios uses Swift/SwiftUI. Lambert and Ripley are not Swift experts — for iOS-specific implementation, prefer asking Dallas to scope the work and spawn a focused task, or use @copilot for well-defined Swift changes. Brett can research Swift patterns.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **@copilot routing** — when evaluating issues, check @copilot's capability profile in `team.md`. Route 🟢 good-fit tasks to `squad:copilot`. Flag 🟡 needs-review tasks for PR review. Keep 🔴 not-suitable tasks with squad members.
9. **Machine-Local Policy (Jeff Papiez's current machine)** — Implementation agents (Lambert, Ripley, Hudson, Gorman, Parker) use maximum reasoning effort. Code reviewers (Bishop, Hicks, Vasquez) use medium reasoning effort. Agents do not self-impose time, tool-call, review-round, or iteration budgets. Work continues until verified and mandatory gates pass. Unavoidable platform/provider hard limits still apply.
