## PrintFarmer Agent Instructions

PrintFarmer is a two-tier 3D printer farm management system:

- Backend: C#/.NET API and services in `src/`.
- Frontend: React TypeScript app in `src/Web/ReactApp/`.
- Mobile: SwiftUI iOS app in `mobile/` (Xcode project, shares the API with the React frontend).
- Database: EF Core with SQLite for local development and PostgreSQL/SQL Server support for deployments.
- Real-time updates: SignalR hubs for printer and slicer events.
- Slicing: OrcaSlicer and PrusaSlicer worker services plus slicer-host APIs.

Use these instructions for durable repo conventions only. Prefer the specialized skills for detailed workflows:

- API/container debugging: `.github/skills/api-debugging/SKILL.md`
- Build and test validation: `.github/skills/testing/SKILL.md`
- OrcaSlicer profile lookup issues: `.github/skills/orcaslicer-profiles/SKILL.md`
- OrcaSlicer upgrades: `.github/skills/orcaslicer-upgrade/SKILL.md`
- PR issue-linkage: `.squad/skills/pr-issue-linkage/SKILL.md`

## Working Directories

Always run commands from the directory expected by the tool:

| Work | Directory |
|---|---|
| Git commands | repo root |
| .NET restore/build/test/format | `src/` |
| React npm commands | `src/Web/ReactApp/` |
| Xcode / swift / fastlane (iOS app) | `mobile/` |
| Docker deploy scripts and compose | repo root |

Do not rely on the terminal's current directory. `cd` explicitly before running commands.

## Validation Commands

Use the smallest validation that covers the change. For broad or cross-layer changes, run both backend and frontend checks.

Backend:

```bash
cd src
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
dotnet test ./farm-web.sln -c Debug 2>&1 | tee /tmp/printfarmer-dotnet-test.log
dotnet format ./farm-web.sln --verify-no-changes
```

Frontend:

```bash
cd src/Web/ReactApp
npm install
npm run build
npm run test:run 2>&1 | tee /tmp/printfarmer-react-test.log
npm run lint
```

Rules:

- Do not cancel long-running restore/build/test/format commands; use generous timeouts.
- Capture long test output with `tee` and inspect the log instead of rerunning tests just to see failures.
- New warnings introduced by the current change are blockers. Existing warnings in untouched code are not a reason to widen the task unless the user asks for warning cleanup.
- If a build, test, or deployment fails, fix the failing step and rerun only the relevant failed validation.

## Local Development

Run local development natively, not in Docker:

- API: `cd src && dotnet run --project ./api/Farm.Web.Api.csproj`
- React: `cd src/Web/ReactApp && npm run dev`
- API URL: `http://localhost:5245`
- React URL: `http://localhost:3000`
- Health checks: `http://localhost:5245/healthz` and `http://localhost:5245/health`

Keep API servers and test commands in separate terminals or background processes. Verify the API is running before endpoint testing.

## Mobile App

The SwiftUI iOS app lives in `mobile/` and was merged in from `OlyForge3D/PFarm-Ios`. It targets iOS 17+ and requires Xcode 26+ (Swift 5.9+). Architecture is MVVM + repository pattern.

API integration:

- The app supports multiple registered servers. `PRINTFARMER_API_URL` seeds/overrides
  the initial development server; for local PrintFarmer dev, use
  `http://localhost:5245` so it matches the .NET API. Legacy `pf_server_url`
  installs migrate into the registry on first launch.
- The mobile app consumes the same `/api/*` JSON contract as the React frontend — camelCase property names, string enums (see Serialization Rules below). Do not introduce mobile-only DTOs unless absolutely required; extend the shared API instead.

Common commands (run from `mobile/`):

```bash
xcodebuild -scheme PrintFarmer -destination 'platform=iOS Simulator,name=iPhone 15' build
xcodebuild test -scheme PrintFarmer -destination 'platform=iOS Simulator,name=iPhone 15'
fastlane beta   # release pipeline
```

Test suites: `PrintFarmerTests` (unit) and `PrintFarmerUITests` (UI). The app has its own `mobile/squad.config.ts` and `mobile/AGENTS.md` for agent guidance, and shares the consolidated release pipeline with the main app. See `mobile/README.md` for full setup details.

## Architecture Invariants

- The React app talks to the API at port 5245 in local development.
- The iOS app in `mobile/` consumes the same `/api/*` contract as the React frontend; both must remain compatible with the backend's camelCase + string-enum serialization.
- In microservices deployments, slicer routes are handled by slicer-host on port 5246 and routed by nginx.
- Docker is for deployment, not normal local development.
- Backend plugins contain backend-specific clients, validators, and discovery probes.
- The discovery framework interfaces live under `src/discovery/`; concrete probes live in backend plugin projects.

Route ownership in microservices mode:

| Service | Routes |
|---|---|
| Main API | Most `/api/*` endpoints |
| Slicer host | `/api/workers`, `/api/slicers`, `/api/slicer`, `/api/slice`, `/api/3d-models`, `/api/artifacts`, `/api/admin/slicer`, `/hubs/slicer` |

## Admin And Settings Surface

The admin and settings UI is a single URL-driven React shell that reads its layout from
the current `?scope`, `?tab`, and `?sub` search params. See
`docs/SETTINGS_ARCHITECTURE.md` for full detail; the rules that most often bite agents:

- **Three routes, three scopes.** `/settings` renders the `user` scope (any authenticated
  user). `/admin/settings` renders the `system` scope and `/admin/manage` renders the
  `admin` scope (both `farm_admin` only). `/admin` itself is the Admin Control Center hub
  and is not a shell route.
- **URL contract.** `?scope`, `?tab`, `?sub`, `?q`, and `?field` fully describe the
  current page. Exactly ONE `<SettingsPage>` mounts at a time.
- **Save is per-group.** `POST /api/settings/{keyName}` saves one settings section. There
  is no "Save All" button in the UI; the batch `POST /api/settings` endpoint and its API
  wrapper `saveAllSettings` are dead code from a UX perspective (tests explicitly assert
  the wrapper is not called on save). Do not add a Save-All button.
- **Navigation is canonical-only.** When a React route changes, update every internal
  caller and affected test in the same change. Do not add bookmark-compatibility aliases
  or a redirect registry.
- **Palette is global.** `GlobalCommandPaletteProvider` is mounted in `Layout.tsx`, so
  `Ctrl+K` (or `Cmd+K`) works on every authenticated route, not just settings.
- **Palette deep-links are section-qualified.** `?field=Section.Property` (e.g.
  `?field=SystemLog.Enabled`) — `Enabled` alone appears on 13 settings classes so a bare
  property name resolves to the wrong row.
- **⚠️ Essential-manifest gotcha.** `src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`
  keys off the backend `SectionName` and `JsonPropertyName`. **Renaming either silently
  demotes the property from Essential to Advanced** without any build error or warning.
  If you rename a settings section or property, update `essential-manifest.ts` in the
  same PR.
- **Admin overview.** `GET /api/admin/overview` aggregates existing `HealthCheckService`
  results into subsystem tiles plus a ranked attention list. It is `farm_admin`-only,
  has an 8s timeout, never returns 500, and serializes `SubsystemStatus` and
  `AttentionSeverity` as string enums via `JsonStringEnumConverter`. To add a new tile
  or attention item, register the probe with the existing `comprehensive` health check
  and update `AdminOverviewService`.

## Pre-PR Review Gate

**All code MUST pass 3-way adversarial review before any PR is opened.** Bishop, Hicks, and Vasquez review the branch together, debate thoroughly, and deliver a single consensus verdict. Do not open a PR until they APPROVE.

> **⚠️ This is a quality gate, not an independence gate.** Every squad agent — reviewers
> and authors alike — runs under the repository owner's authority and acts through the
> owner's token. Bishop reviewing Lambert's work is therefore the owner reviewing the
> owner's own work. It is genuinely useful (a second agent with fresh context catches
> what the author re-reading its own output misses), but it is **self-attested review**:
> it provides **no separation of duties**, satisfies **no four-eyes requirement**, and
> must never be presented as independent approval. The owner has accepted this trade
> deliberately for single-maintainer operation — see issue #1310 and § "Repository
> verdict evidence".

Flow:

1. Commit code to a feature branch (do not push yet).
2. Request review from Bishop, Hicks, Vasquez (mention all three).
3. Reviewers converge adversarially on the branch — no serial review or independence.
4. If consensus is APPROVE, proceed to step 5. If REJECT or BLOCK, fix the code on the branch and re-request.
5. Once APPROVED, open the PR via `gh pr create`.
6. After the PR exists, each reviewer records their review as a PR comment in the
   canonical format below. The `squad-review-verdict.yml` workflow re-evaluates
   automatically on every comment, review, and push.

This is a hard gate enforced by team policy. The trio's consensus verdict gates the PR creation step itself.

The single exception is a documentation-only change — see the next section. Nothing else
reduces the trio to fewer than three reviewers.

### Documentation-Only Changes: One Reviewer

**This section is the canonical definition of the documentation-only review exemption. Every
other mention of it in this repository must link here rather than restate it, so the definition
cannot drift.**

**A documentation-only change requires ONE reviewer, not three.** Three specialist reviewers
have essentially nothing to assess in prose that changes no runtime behaviour, so the full gate
burns three dispatches for no signal. This reduces reviewer **count**, not review **rigour** —
the single reviewer still performs a real review and can still REJECT.

**Definition (allowlist).** A change is documentation-only when **every** changed path is prose
or agent-instruction content:

- `**/*.md`
- `docs/**`
- `.squad/**`
- `.github/agents/**`
- `.github/instructions/**`
- `.copilot/skills/**` and `.github/skills/**`
- `LICENSE` and similar top-level prose files

**Denylist — if ANY changed file falls outside the allowlist, the change is NOT
documentation-only and the full three-reviewer gate applies in full.** This includes, but is not
limited to: source files, tests, `package.json` or any dependency manifest, lockfiles, workflow
YAML, scripts, EF Core migrations, and binary or image assets. Match manifests and lockfiles by
basename, wherever they live in the tree. A PR that touches both a markdown file and a source
file is **not** documentation-only.

**Carve-outs that always keep the full gate**, even when only markdown changes:

- Anything under `.github/workflows/**`. Workflow YAML is not documentation.
- Any change to a SECURITY policy, threat model, licensing terms, or a published API contract
  document. These are prose whose contents carry real consequences.
- Any change that alters an agent's **safety boundary**, merge-safety rules, or
  destructive-operation permissions. A markdown file that governs whether an autopilot agent
  may merge, delete, or force-push is not low-risk prose. This carve-out matters specifically
  because the agent-instruction files under `.squad/` and `.copilot/skills/` are documentation
  by path but govern real behaviour.

**Be conservative — when in doubt, use the full gate.** Fail toward the stricter path whenever
the classification is not obvious.

**How the gate automates this.** `scripts/ci/squad-verdict-gate.mjs` implements the allowlist
and the denylist, and takes the conservative reading of the safety-boundary carve-out: `.github/**`
in full, plus `.squad/**`, `.copilot/**`, `.claude/**` and root agent-instruction files
(`AGENTS.md`, `CLAUDE.md`), **always** take the full gate. Whether a given edit moves an agent's
safety boundary cannot be judged from the path, and `.github/**` is process configuration rather
than product documentation — it holds the merge-evidence rules that the unattended merger itself
obeys, so a single verdict must not be able to rewrite them. Prose is matched by extension
(`.md`, `.markdown`, `.rst`, `.adoc`, `.txt`), so binary or image assets under `docs/` correctly
take the full gate. If a change is misclassified as full-gate, the cost is two extra verdict
comments; the reverse would be a real review gap.

**Who the single reviewer is.** Route to the reviewer whose domain the document actually
concerns — for example, Bishop for storage/integration docs, Hicks for testing and contract
docs, Vasquez for security and concurrency docs. **Default to the squad Lead (Dallas) when the
domain is unclear.** Everything else in this section — the verdict evidence rules, supersession
on head movement, and the reviewer session contract below — applies unchanged to the single
reviewer's verdict.

### Repository verdict evidence

> **⚠️ What a green `squad/pre-pr-verdict` actually means.** It means *a reviewer agent
> examined this exact commit and recorded its findings*. It is **self-attested**. It does
> **not** mean a second party approved the change, and it is **not** separation of duties,
> four-eyes, or an independent audit control. Every squad agent runs under the repository
> owner's authority through the owner's token, so agent review is the owner reviewing the
> owner's work. Do not cite this status as independent approval in a release note, audit
> response, or compliance artefact. The only status form that reflects a real
> authorisation by a distinct principal is `APPROVE (owner)`.
>
> This was a deliberate decision. The previous gate demanded a non-author administrator
> who does not exist, so it never once passed. Parker's analysis on issue #1310 showed
> that every alternative preserving genuine separation of duties needs a second human, a
> machine identity controlled by someone other than the owner, or a real independent CI
> adjudicator. None exists here, and manufacturing the *appearance* of independence
> (e.g. bot-hopping the dispatch so `github.actor` becomes `github-actions[bot]`) would
> be worse than no gate: false assurance. The owner chose the honest option — keep the
> controls that are real, and say plainly that independence is not one of them.

#### What keeps outsiders out — and why author authentication is mandatory

**Repository permissions are the control that prevents an outsider merging.** Only
`jpapiez` has write access to `OlyForge3D/PrintFarmer` and `OlyForge3D/PrintFarmerDesktop`.
The verdict gate is **not** that control and must never be described as if it were. Its job
is to establish that a review actually happened at the current head before Ralph merges.

But both repositories are **public**, and on a public repository **any GitHub user can
comment on a pull request** — no write access, no collaborator status, no permission at
all. Because Ralph merges autonomously using the *owner's* write access, a forgeable
record would effectively lend the owner's privileges to whoever forged it. The attack
chain that closes:

1. An outsider forks the repository and opens a PR containing malicious code.
2. The outsider posts a comment in the canonical record format: `APPROVE` at the PR's
   current head SHA.
3. Ralph sees "record present at current head, CI green" and merges it, unattended,
   into `development`.

That is an unauthenticated path from a stranger to the default branch. Author
authentication is therefore mandatory, not optional:

- **Write or better, verified live.** Every record's author is resolved through
  `GET /repos/{owner}/{repo}/collaborators/{login}/permission` at evaluation time.
  Only `admin`, `maintain`, `write`/`push` are accepted. `read` — which is what a
  non-collaborator returns on a public repository — and `triage` are rejected.
- **`author_association` is a pre-filter only.** `NONE`, `FIRST_TIME_CONTRIBUTOR`,
  `FIRST_TIMER`, `CONTRIBUTOR` and `MANNEQUIN` are rejected outright, but a permitted
  association is never sufficient on its own, because its meaning varies with
  organisation configuration.
- **Fail closed.** A failed, rate-limited, or unexpectedly shaped permission lookup
  yields `unresolved`, which is not write access. An unverifiable author is never
  acceptable, and the block reason says `no authenticated review` so the failure is not
  mistaken for "nobody reviewed".
- **Identity comes from the account, never the text.** The owner-override path requires
  the API-supplied comment author to *be* an administrator whose login matches the named
  reviewer. A comment merely claiming to be from `jpapiez` proves nothing.
- **Fork PRs get no agent record at all.** The gate reads no record from a fork — anyone
  can open one on a public repository, so a record there could only be self-asserted by an
  unauthenticated party. It posts
  `BLOCKED @ <sha12>: fork PR needs a repository administrator`. An administrator's native
  GitHub approval at the current head is still honoured on that path and is evaluated by
  the same code, because it reads only API-supplied logins and the live head SHA — never
  fork-controlled input.
- **No bot identity is allowlisted.** Allowlisting one would re-create the bot-hop
  laundering pattern rejected above.

Reviewers record reviews as PR comments. `.github/workflows/squad-review-verdict.yml`
re-evaluates on every PR comment, review, and push, and publishes the
`squad/pre-pr-verdict` commit status on the PR's live head SHA. The canonical
comment format is:

```text
<!-- squad-verdict -->
Squad-Reviewer: bishop
Squad-Verdict: APPROVE
Squad-Head-SHA: 0123456789abcdef0123456789abcdef01234567
```

- `Squad-Reviewer` is a squad identity, validated against the repository's
  `squad:{member}` labels. It should differ from the squad member who authored the
  PR — a **quality heuristic**, since fresh context catches more than self-review,
  and explicitly *not* an independence guarantee. Authorship is resolved from
  `Squad-Author:` in the PR body, then the `squad:{member}` label on the issues the
  PR closes, then the head branch name. GitHub-account authorship is deliberately
  *not* used: every agent acts through the same owner token, which is exactly what
  made the previous gate unsatisfiable.
- `Squad-Verdict` is `APPROVE` (the reviewer agent found nothing blocking) or
  `REQUEST_CHANGES` (it did). It records that agent's conclusion; it authorises
  nothing.
- `Squad-Head-SHA` must equal the PR's live head SHA when the gate runs. A
  record naming any other SHA is stale and does not count, and can never
  displace a reviewer's record on the current head.
- The `<!-- squad-verdict -->` marker is required. Fenced code blocks (including
  an unterminated fence, which GitHub renders as code to the end of the comment),
  quoted (`>`) lines, and every other HTML comment are stripped before parsing.
  Prose that illustrates, quotes, or hides the format is therefore not a binding
  record — what the gate counts is what a human reading the thread can see, which
  is what makes the audit trail meaningful. Each field must appear exactly once in
  what remains. **Put the record block first in the comment:** sanitisation fails
  closed, so an unterminated `<!--` or code fence earlier in the comment hides
  everything after it — exactly as GitHub renders it — and would drop your record.
- The commenting account must be authenticated as holding real repository write access,
  resolved live through the collaborator permission API. Both repositories are public,
  so anyone can comment on a PR; `author_association` is a pre-filter only and is never
  sufficient on its own. Lookups fail closed. See "What keeps outsiders out" above.
- A repository administrator satisfies the gate unconditionally, either by
  approving through GitHub's native review UI at the current head, or by posting
  a record whose `Squad-Reviewer` is their own GitHub login. The owner
  is never locked out.

**What this record genuinely buys you**, despite being self-attested:

- **SHA binding** — a record is valid only for the exact commit it names. Push again
  and it goes stale and the gate fails. This really does prevent review-then-push-more,
  and it is the strongest control here.
- **Presence** — the gate fails when nothing reviewed the change at all, catching the
  real failure mode of a PR merging with zero examination.
- **Audit trail** — which reviewer agent ran, which SHA it examined, what it concluded.
- **Legible failure** — the status names the exact failing condition.

The workflow job always succeeds; the gate outcome is the commit status, which
takes exactly one of four forms:

| Status | Description | Verifier classification |
| --- | --- | --- |
| `success` | `REVIEWED (self-attested) @ <sha12> by <agents>` | `REVIEWED` (exit 0) |
| `success` | `APPROVE (owner) @ <sha12> by <login>` | `APPROVED` (exit 0) |
| `failure` | `REQUEST_CHANGES @ <sha12> by <reviewer>` | `CHANGES_REQUESTED` (exit 2) |
| `failure` | `BLOCKED @ <sha12>: <reason>` | `MISSING` (exit 3) |

`REVIEWED` and `APPROVED` are kept distinct on purpose: only the latter is an
authorisation by a distinct principal. `REQUEST_CHANGES` is a reviewer decision and
routes back to the author. `BLOCKED` means **no usable review record was accepted**,
which is not the same as "nobody looked". Its reason names the exact condition and the
verifier preserves that reason verbatim, because the subcases differ materially:

| `BLOCKED` reason | What it means |
| --- | --- |
| `no review recorded for <sha12>` | Nothing was posted for this head. |
| `no authenticated review for <sha12> (N unauthenticated)` | Records exist, but their authors could not be authenticated with repository write access. **Security-relevant — do not read this as "nobody reviewed".** |
| `fork PR needs a repository administrator` | Fork PR; agent records are not read at all. |
| `have <n>/<required>[, missing <agents>][ (stale at <agent>@<sha12>, …)]` | Too few accepted records for this change's scope. `missing` lists expected panel members with no record; the `stale at` clause lists reviewers whose only record names a superseded head. Both clauses are omitted when they do not apply, so a docs-only PR reads e.g. `have 0/1 (stale at dallas@<sha12>)` and a code PR reads e.g. `have 1/3, missing hicks+vasquez`. |
| `reviewer <agent> is the PR author` | The only record came from the authoring agent. |

A session reading the failure therefore knows what to do instead of parking indefinitely.
The verifier preserves this text verbatim in `blockedReason`, so it is safe to branch on —
but match the `have …` case as a pattern, not as a fixed string.

Run the verifier before treating the squad record as merge evidence:

```bash
node scripts/ci/verify-squad-verdict.mjs \
  --repo OlyForge3D/PrintFarmer \
  --pr <number> \
  --json
```

Pass `--expected-head <recorded-sha>` when auditing a previously recorded
review after the PR head may have moved. The verifier then reports
`SUPERSEDED` for either an old review record or an old rejection.

Any head movement supersedes the recorded review. This rule applies equally
to `REVIEWED`, `APPROVED` and `REQUEST_CHANGES`, including rebases and force
pushes. The panel must review the new head and record fresh reviews naming it.

Missing, invalid, or superseded squad evidence never becomes a review record.
After verification, merge with
`gh pr merge <number> --match-head-commit <reviewedHeadSha> ...` so a
force-push between verification and merge cannot substitute unreviewed code.

### Reviewer Session Contract

Bishop, Hicks, and Vasquez are read-only reviewers. **They are always dispatched with the
`task` tool, never with `create_session`** — a review produces a verdict, not commits, so a
sub-session would only consume one of Ralph's limited dispatch slots and leave a stale
worktree behind. This holds even when all three review concurrently. Their review dispatch
takes precedence over the generic Copilot process-tracking instruction: reviewers MUST NOT
create, edit, or delete `Copilot-Processing.md` or any other tracking file, and MUST NOT modify
implementation files. They should inspect the branch with the read-only tools exposed by
their session and must not assume tool names from another host or prompt. If a required
read-only capability is genuinely unavailable, the reviewer reports an explicit
environment blocker naming that capability and the blocked review step.

This exemption is limited to read-only review sessions. Implementation agents continue to
create and maintain `Copilot-Processing.md` under the process-tracking instruction.

## Pull Request Issue Linkage

When opening a PR, the body **MUST** include `Closes #N` (or `Fixes #N` / `Resolves #N`) for every GitHub issue the PR resolves. GitHub will auto-close the issue when the PR merges.

**What works (GitHub auto-closes on merge):**

```
Closes #350
Closes #351
```

**What does NOT work (no auto-close):**

- Parenthetical in title: `feat(x): thing (#350)` — GitHub ignores this.
- Bead-style syntax: `[closes PFarm1-350]` — legacy, GitHub does not recognize.
- `relates to #350` — informational only, no auto-close.
- Issue number only in commit message, not in PR body.

**Verification:** After creating a PR, run `gh pr view <number> --json closingIssuesReferences` to confirm the issues are detected. If empty, update the PR body.

See `.squad/skills/pr-issue-linkage/SKILL.md` for full details and recovery procedures.

## Serialization Rules

- All API and SignalR JSON payloads must use camelCase property names.
- Configure SignalR JSON serialization the same way as controllers.
- Frontend TypeScript interfaces must match backend JSON casing.
- Backend enums are serialized as strings through `JsonStringEnumConverter`.
- Do not parse enum API values as integers in the frontend; use string enum names such as `Brass` or `HardenedSteel`.

SignalR rules:

- SignalR event names are lowercase, such as `printerupdated` and `discoveryprogress`.
- Do not add duplicate PascalCase listeners or senders.
- Payloads such as printer status updates, discovery progress, and slicer job updates must preserve camelCase JSON.

## Data And Migrations

- Create EF Core migrations for schema changes that affect deployment databases.
- Create migrations for every affected context/provider pair.
- Main app schema changes use `Farm.Migrations.PostgreSQL` and `Farm.Migrations.SqlServer` with `AppDbContext`.
- Slicer schema changes use `Farm.Slicer.Migrations.PostgreSQL` and `Farm.Slicer.Migrations.SqlServer` with `SlicerDbContext`.
- Use descriptive PascalCase migration names.
- SQLite local development may use `EnsureCreated`; production deployments use migrations.
- Every PR is gated in CI by `dotnet ef migrations has-pending-model-changes` for all four main app and slicer context/provider migration projects.
- Verify generated migration files exist under the affected `src/migrations/*/Migrations/` project directories.

Common main app migration commands, from `src/`:

```bash
DB_PROVIDER=postgres dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL \
  --context AppDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Migrations.SqlServer \
  --context AppDbContext
```

Common slicer migration commands, from `src/`:

```bash
DB_PROVIDER=postgres dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --context SlicerDbContext

DB_PROVIDER=sqlserver dotnet ef migrations add <MigrationName> \
  --project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --context SlicerDbContext
```

## Slicer Profiles

Keep these data sources distinct:

- New slice job profile selection reads worker profiles from OrcaSlicer resources.
- Slicer Profiles management pages operate on user-owned database profiles.
- Do not merge worker-library browsing with user-owned profile management unless the user explicitly asks for that architecture change.

OrcaSlicer profile loading relies on:

- `machine_model_list`, `machine_list`, `process_list`, and `filament_list` bundles.
- `compatible_printers_condition` expressions evaluated against loaded machine profiles.
- resolved `compatible_printers` arrays for matching filament and process profiles to machine variants.

Use the OrcaSlicer profile skill for profile hierarchy, alias, and empty-profile debugging.

## Documentation And Markdown

- Update existing documentation when code changes alter user-visible behavior, setup, deployment, or architecture.
- Do not create one-off implementation markdown files unless the user asks or the content truly does not fit existing docs.
- Keep markdown concise and structured with H2/H3 headings, fenced code blocks with language identifiers, and descriptive links.

## Docker And Deployment

- Deployment scripts must run from the repo root.
- Docker file changes should respect the template hierarchy documented in `.github/instructions/docker-file-hierarchy.instructions.md`.
- When changing deployment scripts or compose templates, run the deployment script test suite described in `docs/DEPLOYMENT_TESTING_CHECKLIST.md`.
- The deployment tooling depends on Python `ruamel.yaml` for compose generation.

## Code Style

- C#: PascalCase for types/members, camelCase for locals/parameters, conventional ASP.NET Core and EF Core patterns.
- TypeScript: camelCase for variables/functions, PascalCase for components/types.
- Prefer existing local helpers, services, DTOs, query conventions, and component patterns over new abstractions.
- Keep changes focused on the user request; do not refactor unrelated code.

## Security And Secrets

- Never print or commit credentials, API keys, JWT signing keys, printer passwords, or deployment secrets.
- Read generated deployment credentials from local config only when needed for debugging, and avoid echoing secret values in final responses.
- Keep printer credentials and tokens out of tracked configuration.