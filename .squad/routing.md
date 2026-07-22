# Work Routing

How to decide who handles what.

> ## 🍏 Host Capability Gating — iOS build/test requires macOS
>
> This repo is shared across dev machines with different operating systems. iOS
> work has a hard platform dependency: **anything that builds, tests, signs, or
> runs the iOS app requires a macOS (Darwin) host with Xcode.** This rule is
> universal — it applies identically on every machine.
>
> **Before running any iOS build/test/sign step, preflight the host OS**
> (`uname -s` must be `Darwin`). Commands that are gated:
> `xcodebuild`, iOS Simulator, `xcrun`, `swift build/test`, `fastlane`,
> `PrintFarmerTests` / `PrintFarmerUITests`, TestFlight/APNs signing — anything
> under `mobile/`.
>
> **On a non-macOS host:**
> - Do **not** attempt iOS build/test/sign — they will fail and are not valid there.
> - Pure source edits to `mobile/` *may* proceed, but the change is **not
>   validated** until a macOS host builds/tests it. Mark such work
>   `needs-macos-validation` and hand the verification to a macOS machine (or
>   defer). Never claim an iOS task complete without a macOS build/test pass.
> - `area:ios` acceptance gates (XCTest/XCUI green, simulator screenshots) must be
>   satisfied on macOS.
>
> **On a macOS host:** iOS build/test/sign run normally.
>
> ### Per-machine scope overrides (machine-local, not committed)
>
> A machine may narrow its own focus (e.g. "this Mac works iOS-only") in
> **`.squad/machine-local.md`** — a **gitignored** file that never ships to other
> machines. If that file exists, honor it in addition to the capability rule above.
> If it is absent, this machine follows the normal routing tables below.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, design, scope | Dallas | System design, technical decisions, cross-domain concerns |
| React, TypeScript, UI, frontend | Ripley | Components, pages, styling, frontend state, SignalR client |
| C#, .NET, API, database, backend | Lambert | Controllers, EF Core, migrations, backend plugins, SignalR hubs |
| Swift concurrency, iOS lifecycle, authentication state, offline authority | Frost | Actor ordering, session transitions, SignalR ownership, cache leases, deterministic XCTest/XCUI |
| iOS persistence, atomic storage, cache envelopes, data integrity | Dietrich | Actor-isolated stores, Codable envelopes, atomic writes, quarantine, namespace and generation fencing |
| iOS auth epochs, secure canonical cache payloads, awaited purge | Apone | Login/restore/logout authority, typed secret-free projections, fail-closed monotonic storage, fault-injected concurrency proofs |
| iOS snapshot transactions, actor reentrancy, atomic filesystem replacement | Crowe | Post-await epoch fences, concurrent commit ordering, purge tombstones, complete cached-card projections, real removal paths |
| iOS snapshot transaction recovery, compare-and-delete safety, purge serialization | Morse | Preserve prior bytes across replacement, settle server/user authority, durable tombstones, quarantine ABA prevention, fractional monotonic timestamps |
| iOS offline owner identity, mint-time authority, rejected-write isolation | Clemens | Per-server stable user identity, origin-pinned activation, failure-atomic version promotion, post-move fencing, schema enforcement |
| iOS SwiftUI offline UX, accessibility, iPhone/iPad XCUI | Drake | View/ViewModel composition, read-only degraded states, VoiceOver, stale banners, no-flash UI acceptance |
| Code review (pre-commit gate) | Bishop + Hicks + Vasquez | Triple-model review: Claude Opus 4.8, GPT-5.6 Sol, Gemini 3.1 Pro Preview — all three review in parallel; must achieve consensus APPROVE before PR creation |
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

**Single repo: PrintFarmer (`OlyForge3D/PrintFarmer`).** The old `PFarm-Ios` repo was
merged in — the iOS app now lives in the **`mobile/`** subdirectory of this repo, not a
separate checkout. On this iOS-only machine, all in-scope work happens in `mobile/`.
Pass the current worktree path as `WORKTREE_PATH` in spawn prompts.

| Work Domain | Location in repo |
|-------------|------------------|
| iOS app, SwiftUI views, Swift models, ViewModels, XCTest/XCUI | `mobile/` |
| Shared `/api/*` + SignalR contract the iOS client consumes | `src/api/` (contract reference for the client; backend changes are the backend track's, not iOS-client work) |

### iOS Agent Assignments

Which agent owns which slice of the iOS app (all under `mobile/`). This maps skills
to work and is machine-neutral; **whether a given machine actually runs iOS
build/test is governed by the Host Capability Gating rule above** (macOS only),
and a machine may further narrow its focus via `.squad/machine-local.md`.

| Agent | iOS focus (in `mobile/`) |
|-------|--------------------------|
| Hudson 📱 | SwiftUI views and navigation |
| Gorman 🌐 | Networking, REST clients, SignalR, Swift models |
| Frost 📱 | Swift concurrency, auth/session lifecycle, actor ordering, cache authority, deterministic tests |
| Dietrich 📱 | Local persistence, cache envelopes, atomic storage, data-integrity and namespace fencing |
| Apone 📱 | Auth-epoch integration, secure canonical cache projection, fail-closed storage, complete purge |
| Crowe 📱 | Snapshot transaction ordering, atomic filesystem replacement, purge/lifecycle integration |
| Morse 📱 | Snapshot transaction recovery, compare-and-delete quarantine, durable purge serialization |
| Clemens 📱 | Offline owner identity, origin-pinned session minting, failure-atomic snapshot promotion |
| Drake 📱 | SwiftUI offline shell, stale/read-only presentation, accessibility, iPhone/iPad XCUI |
| Newt 🎨 | iOS HIG/SwiftUI visual design |
| Dallas 🏗️ | iOS architecture & scope |
| Kane 🧪 | XCTest/XCUI tests & coverage |
| Ash 📝 | iOS docs (mobile/README, CHANGELOG) |
| Brett 🔍 | Swift/iOS research |
| Bishop / Hicks / Vasquez 🔍 | Triple-model review gate (applies the iOS review rubric on `area:ios` diffs) |

> Non-iOS agents (Lambert, Ripley, Parker) own their own domains via the main
> Routing Table and are not part of the iOS bench. A machine that scopes itself
> iOS-only (via `.squad/machine-local.md`) simply doesn't spawn them locally.

> **iOS work:** The mobile app uses Swift/SwiftUI. Route general SwiftUI views to Hudson, networking/API integration to Gorman, actor/session lifecycle work to Frost, durable local persistence/data integrity to Dietrich, auth-epoch/secure-persistence integration to Apone, snapshot transaction/atomic-filesystem work to Crowe, rejected-transaction recovery work to Morse, offline owner/session authority recovery to Clemens, and offline SwiftUI/accessibility acceptance to Drake. Lambert and Ripley are not Swift experts. Brett can research Swift patterns.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **@copilot routing** — when evaluating issues, check @copilot's capability profile in `team.md`. Route 🟢 good-fit tasks to `squad:copilot`. Flag 🟡 needs-review tasks for PR review. Keep 🔴 not-suitable tasks with squad members.
9. **Machine-Local Policy (Jeff Papiez's current machine)** — Implementation agents (Lambert, Ripley, Hudson, Gorman, Parker) and code reviewers (Bishop, Hicks, Vasquez) use maximum reasoning effort (max for all except Vasquez, who uses high) with no self-imposed time, tool-call, review-round, or iteration budgets. Work continues until verified and mandatory gates pass. Unavoidable platform/provider hard limits still apply.
