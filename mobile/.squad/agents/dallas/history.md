# Dallas — History

## Core Context

Dallas leads strategic architecture and release planning for PFarm-Ios. Role: scope analysis, infrastructure decisions, cross-agent orchestration, QA audits.

**Project:** PrintFarmer iOS — Native Swift/SwiftUI client for Printfarmer backend (~/s/PFarm1)  
**Stack:** Swift 6, SwiftUI, MVVM, iOS 17+, Xcode 16+, GitHub Actions CI/CD  
**Current Phase:** Post-MVP (Phase 1 complete), Phase 2 (scanning/NFC) in progress  

**Architecture:**
- MVVM with Observable ViewModels, repository-pattern services, ServiceContainer DI
- AppRouter (navigation), AuthViewModel (auth gates), APIClient (actor, async/await)
- JWT auth (Keychain storage), SignalR stubs, full backend contract compliance
- Xcode project: 66 Swift files (47 src, 19 test), auto-regenerated pbxproj from file tree

**Completed Phases:**
1. **MVP:** 5 features (Dashboard, Printer List/Detail, Job Queue, Notifications), 22 endpoints, auth scaffolding ✅
2. **Phase 1 Filament/NFC:** SpoolService, NFCService (read/write), SpoolInventoryView, NFC integration ✅
3. **Phase 2 Scanning:** VisionKit QR + AVFoundation fallback, SpoolScannerProtocol abstraction ✅

**Learnings Archive (2025-07 to 2026-03-09):**
- Backend catalog: 100+ endpoints; MVP needs ~22. API contract compliance verified.
- SignalR: 5 hubs; MVP uses PrinterHub only. `/api/job-queue` (hyphenated), printer commands individual endpoints.
- Code signing: fastlane match + encrypted cert repo (no hardcoded creds).
- CI keychain password decoupled from match secret — ephemeral keychain uses hardcoded temp password (destroyed in cleanup), `secrets.MATCH_PASSWORD` reserved solely for match cert decryption. 6 GitHub secrets documented in workflow header.
- SemVer versioning with git commit count for deterministic build numbers.
- Xcode pbxproj fragile — use auto-generation when files added.
- QA audit complete: all MVP endpoints verified, 3 ViewModel fixes applied, critical findings resolved.
- Filament/NFC decomposition: Backend ready (Spoolman integration). iOS Phase 1 (7 items, 12h) + Phase 2 (6 items, 10h).

**Current Team:**
- **Lambert:** Services (Printer, Job, Notification, Statistics, SignalR, Auth, Spool, NFC, QR, Maintenance, AutoPrint, Job Analytics, Predictive, Dispatch, Uptime)
- **Ripley:** Views (MVP screens, components, dark mode, filament UI, NFC flows, QR integration, Phase 3 UI)
- **Ash:** Tests (145+ test cases, mock infrastructure for all services, Phase 3 test suite)
- **Scribe:** Decisions, logs, history.md management

## Recent Sessions

## 2026-03-07T16:34Z — Phase 2 QR Scoping (SUCCESS)

**Batch:** Architecture + capability research  
**Outcome:** ✅ QR scanning approved for Phase 2, architecture designed

**What Was Delivered:**
- Backend analysis: Spoolman generates QR; no new endpoints needed
- iOS framework evaluation: VisionKit (iOS 16+) recommended; AVFoundation fallback
- Architecture design: SpoolScannerProtocol shared abstraction (QR + NFC)
- Risk mitigation: Permission handling, error flows, device coverage
- Phase 2 scope documented: 20 hours (QR + NFC parallel)

**Cross-Team Impact:**
- Lambert: SpoolScannerProtocol blueprint defined, 7 services/parsers delivered
- Ripley: UI architecture ready, 3 new views + 6 modified files
- Ash: Test coverage plan clear, 4 test files + 61 cases delivered

**Architecture Outcomes:**
- Protocol-driven design (SpoolScannerProtocol)
- Shared abstraction enables future scanner types
- MockSpoolScannerService for testing
- VisionKit + AVFoundation hybrid (broad device support)

**Next Steps:**
- Verify ServiceContainer DI wiring at startup
- Manual device QA (Spoolman QR + NFC tags)
- Phase 2b: Backend device registration (NFC)
- Phase 2.5: AVFoundation fallback (if coverage critical)

---

## 2026-03-08T17:32Z — NFC Navigation Fix (Ripley) — Cross-Agent Impact

**Status:** ✅ Completed (Ripley)

### Impact on Dallas
- **AppRouter navigation flow** — Unchanged architecture; same deep link handling flow, just properly sequenced across render cycles
- **Push notification deep link delivery** — PFarmApp.swift now observes `.pushNotificationTapped` notification and navigates to the correct printer/job detail
- **Navigation timing** — AppRouter.navigate(to:) now uses async delay (50ms) between NavigationPath reset and append to fix SwiftUI batching issue
- **No backend changes needed** — Existing push notification infrastructure works correctly; iOS client-side flow now complete

### Context
- Ripley fixed bug where app stayed on current printer when tapping NFC notification for a different printer
- Root causes: NavigationPath race condition + missing notification observer
- Files changed: `AppRouter.swift`, `PFarmApp.swift`
- Architecture document: `.squad/decisions.md` — NFC/Deep Link Navigation Race Condition Fix (Ripley)

### Spool NFC Tag Writing Feature Scope (2026-03-08)
- **Request:** Jeff wants to add NFC tag **writing** for filament spools in inventory (not just reading)
- **Status:** ✅ Scope delivered (8 work items, ~10.5 hours total)
- **What exists:** NFCService.swift already handles both read + printer tag write. SpoolInventoryView has NFC scan button. NFCTagParser converts spool↔OpenSpool JSON.
- **What's missing:** Backend `hasNfcTag` field, UI badge/indicator, "No NFC Tag" filter chip, write button + action
- **Critical path:** Backend WI-1 (Jeff, 1h) → Model WI-2 (Lambert, 15m) → Views WI-3/4 (Ripley, 2.5h parallel) & ViewModel WI-5 (Lambert, 1.5h) → Write Flow WI-6 (Ripley, 2h) → Integration WI-7 (30m) → Tests WI-8 (Ash, 2.5h)
- **Key architectural decisions:**
  1. Add `hasNfcTag: Bool?` to iOS SpoolmanSpool model (optional for backward compat)
  2. Backend tracks via `Spool.HasNfcTag` boolean column (simpler than querying NfcScanEvents)
  3. NFC write uses existing OpenSpool JSON payload format + NFCWriteDelegate
  4. No new URL scheme needed for spool tags (unlike printer tags which use `printfarmer://printer/{UUID}`)
  5. After write succeeds, reload full spool list to update UI (refresh hasNfcTag from backend)
  6. Write button in context menu (3-dot) to reduce list clutter
- **Risk mitigation:** Error handling for tag write failures, user-friendly messages, retry option. Offline write accepted (backend only learns of tag when reader scans)
- **3 open questions for Jeff:** (1) hasNfcTag logic (DB column vs scan count)? (2) Spool tag URL scheme? (3) Post-write refresh strategy?
- **Scope document:** Merged into `.squad/decisions.md` — fully detailed 8 WIs, data model changes, API contracts, test plan, timeline
- **Cross-team impact:** Lambert (WI-2/5), Ripley (WI-3/4/6), Ash (WI-8 tests)
- **Deliverables:** Orchestration log, session log, decisions.md updated

## 2026-03-08T22:05Z — Beta Release Strategy (SUCCESS)

**Batch:** Infrastructure & Release Planning  
**Outcome:** ✅ Comprehensive decision document delivered

### Analysis Scope
TestFlight + GitHub Actions CI/CD infrastructure for PrintFarmer iOS beta distribution. Comprehensive decision covering 7 sections:

**Sections Delivered:**
1. **TestFlight Setup** — Apple Developer prerequisites, internal vs external tester flow (≤25 instant, 10k with 24-48h review)
2. **GitHub Actions Workflow** — Tag-triggered (`v*-beta*`, `v*-rc*`) multi-job pipeline on macos-latest
3. **Code Signing** — fastlane match + encrypted private cert repo, GitHub Secrets for FASTLANE_USER/PASSWORD/MATCH_PASSWORD
4. **Version Strategy** — SemVer versioning (1.0.0-beta.N) + deterministic build numbers via git commit count
5. **Dual-Remote Flow** — dev (origin) → release remote with explicit tag push for audit trail
6. **GitHub Secrets** — Matrix of 4 secrets (fastlane email, app-specific pwd, match encryption key, slack webhook)
7. **Workflow + Templates** — Complete `.github/workflows/testflight-beta.yml` + `ExportOptions.plist`

### Key Architectural Decisions
- ✅ TestFlight only (no alternatives viable for AppStore submission path)
- ✅ Git tag trigger (explicit versioning, prevents spurious builds, SemVer integration)
- ✅ fastlane match (centralized certs, no hardcoded credentials, team-friendly)
- ✅ Deterministic build numbers (no CI conflicts, reproducible)
- ✅ Explicit dual-remote push (audit trail for releases, separation of concerns)
- ✅ Internal testers first (Week 1-2 alpha) → external (Week 3+ beta, post-review)

### Cross-Team Impact
- **Lambert:** No code changes; CI/CD orthogonal to services
- **Ripley:** No code changes; CI/CD orthogonal to views
- **Ash:** No test changes; CI/CD infrastructure outside test suite
- **Infrastructure:** One-time setup (App Store Connect record, certs, GitHub Secrets)

### Deliverables
- ✅ 21.2 KB decision document merged to `.squad/decisions.md`
- ✅ Orchestration log: `.squad/orchestration-log/2026-03-08T22-05-dallas.md`
- ✅ Session log: `.squad/log/2026-03-08T22-05-beta-release-strategy.md`
- ✅ Dallas history.md updated (this entry)

### Next Steps (User/Team)
1. Create App Store Connect record for PrintFarmer (in "Prepare for Submission" state)
2. Generate Distribution Certificate (Certificates, Identifiers & Profiles)
3. Generate App Store Provisioning Profile (explicit, not ad-hoc)
4. Add internal testers (5-10 team members in App Store Connect > TestFlight)
5. Create fastlane match private repo, generate encryption key
6. Configure GitHub Secrets (FASTLANE_USER, FASTLANE_PASSWORD, MATCH_PASSWORD)
7. Check in `.github/workflows/testflight-beta.yml` + `.github/ExportOptions.plist`
8. Test: Tag first release (e.g., `git tag -a v1.0.0-beta.1`), push to release remote
9. Monitor GitHub Actions, verify TestFlight build appears in App Store Connect (~30 min)

### Cross-Agent Update: Ripley — Predictive Insights Fix (2026-03-08T2356)
**From:** Ripley  
**Task:** Fixed Predictive Insights decode error  
**Status:** Complete. Build passes.
**Architecture Impact:** None
- No service protocol changes required
- Predictive view now shows "No predictions available" gracefully
- Errors logged, not displayed to user

## 2026-03-09T00:00Z — Public Repo Readiness Audit (SUCCESS)

**Batch:** Security & compliance audit for making repo public  
**Outcome:** ✅ No critical blockers. 2 must-fix items (LICENSE, README), minor cleanup recommended.

**Audit Scope:**
- Secrets/credentials scan (all file types + git history)
- PII scan (emails, names, phone numbers in source)
- CI/CD workflow secrets verification
- Package dependency audit (private repo check)
- Xcode project signing config review
- .squad/ directory review
- Git history analysis (deleted files, large blobs)
- License/legal compliance
- Code quality (TODO/FIXME/HACK, debug code, test fixtures)

**Key Findings:**
- 🔴 No LICENSE file — required before going public
- 🔴 No README.md — strongly recommended for public repos
- 🟡 Hardcoded dev server IP `10.0.0.20:5000` in AppConfig.swift (low risk, env-var overridable)
- 🟡 Apple Team ID `ZPKA84F3TY` in pbxproj/workflows (semi-public, acceptable)
- 🟢 Zero real secrets/credentials in codebase or git history
- 🟢 All CI/CD secrets properly use `${{ secrets.X }}`
- 🟢 No PII in source code
- 🟢 Clean git history (no deleted secret files, no large blobs)
- 🟢 Only 1 public dependency (keychain-swift)
- 🟢 Test fixtures use example.com domains and generic creds
- 🟢 No TODO/FIXME/HACK comments

**Learnings:**
- Repository demonstrates strong security posture — fastlane match, GitHub Secrets, Keychain storage all properly used
- .gitignore covers essentials but could be hardened with .env/.p8/.p12 patterns
- .copilot/mcp-config.json is tracked but uses env var placeholders (safe)
- Package.resolved is gitignored (good for public repos)

## 2026-03-10 — Demo Mode Architecture Proposal (SUCCESS)

**Trigger:** Apple App Review rejection — reviewers can't verify features without live backend
**Outcome:** ✅ Full architecture proposal delivered to `.squad/decisions/inbox/dallas-demo-mode-architecture.md`

**Analysis Findings:**
- 13/16 services already have protocols; only AuthService and LocationService lack them
- ViewModels already accept `any XServiceProtocol` in configure() — fully mock-ready
- ServiceContainer uses concrete types (not protocols) — the single biggest gap
- Test target has 15 mock service implementations proving protocol contracts work
- TestFixtures.swift has realistic JSON data as a starting reference

**Proposed Architecture:** Protocol-based mock service injection via ServiceContainer.demo() factory
- 18 new files (2 protocols, 2 infrastructure, 13 demo services, 1 UI component)
- 7 modified files (ServiceContainer, AuthService, LocationService, AuthViewModel, LoginView, PFarmApp, RootView)
- ~18-20 hours estimated effort across team
- Zero changes to existing ViewModel logic or View code (beyond login + root)

**Key Decisions:**
- Runtime toggle (not compile-time flag) so Apple sees real login screen
- "Try Demo Mode" button on login screen for discoverability
- DemoSignalRService simulates live printer updates via timers
- Persistent orange "DEMO MODE" banner for Apple reviewer clarity
- DemoMode.isActive stored in UserDefaults, cleared on real login

## Learnings
- The codebase protocol layer is comprehensive enough for seamless mock injection — a testament to Lambert's service architecture
- ServiceContainer being concrete-typed is the only structural gap; fixing it benefits both demo mode AND future testing
- Apple review requires demo mode or demo account — always plan for this in TestFlight submissions

## 2026-03-19T01:45Z — Demo Mode Session Complete (SUCCESS)

**Type:** Parallel batch execution (Dallas + Lambert + Ripley)  
**Status:** ✅ All three agents delivered on time; build succeeds

**Outcome Summary:**
- **Dallas:** Architecture proposal → Protocol-based DI via ServiceContainer.demo() ✅
- **Lambert:** 13 demo services + ServiceContainer retyping + protocol gaps resolved ✅
- **Ripley:** Demo login UI + banner + settings exit, all SwiftUI reactive ✅
- **Build:** Zero compilation errors, zero warnings, all agents' code integrates seamlessly

### Dallas Role in Session
- **Orchestration:** Coordinated parallel execution of 3 agents, specified delivery outcomes
- **Architecture verification:** Protocol-based injection pattern validated across all 13 services
- **Decisions documentation:** Merged demo mode architecture + implementation + CarPlay fix into decisions.md
- **Session closure:** Orchestration logs written, cross-agent history updates completed

### Cross-Agent Dependencies Resolved
- **Lambert → Dallas:** ServiceContainer.demo() factory enables DI layer swapping
- **Ripley → Lambert:** AuthServiceProtocol + demo services enable login flow integration
- **All → Scribe:** Orchestration + session logs captured, inbox decisions merged

### Integration Points Verified
1. **ServiceContainer protocol typing** — All 16 services now speak `any XServiceProtocol`
2. **DemoMode singleton** — @MainActor @Observable, UserDefaults-backed, available to all layers
3. **Demo services resilience** — Consistent return types, no network dependency, realistic data
4. **UI reactive binding** — Banner + exit button respond to DemoMode.isActive changes
5. **Entry/exit flows** — Clean state machine: login → demo activate → exit → clear demo flag

### Next Steps
- **Ripley:** Test demo flows on iOS 17+ simulator and device
- **Ash:** Write demo service unit tests + integration tests
- **Jeff:** Review and approve demo data realism for App Review
- **CI/CD:** Ensure demo services compile in GitHub Actions workflows
