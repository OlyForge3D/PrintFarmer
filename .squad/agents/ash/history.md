# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Core Context

Ash specializes in comprehensive documentation work. Key expertise areas and standing knowledge:

### Documentation Specialization
- Creates 40-100KB technical guides with Mermaid diagrams, code examples, API references
- Strong on capturing complex system architecture (auto-dispatch, design system, location hierarchy)
- Incorporates findings from squad decisions.md and team discussions into documentation
- Updates README.md, ARCHITECTURE.md, API.md with complete accuracy

### PrintFarmer System Knowledge
- **Auto-Dispatch System:** Channel-based trigger (bounded 64), fire-and-forget Tasks, SemaphoreSlim locking, 9-factor weighted scorer, audit logging via DispatchLogs
- **Design System:** 40+ React components, CSS custom properties (pf-* naming), three themes (GitHub Dark, PrintFarmer Dark, Light), WCAG 2.2 AA accessibility
- **Location Hierarchy:** Adjacency list + materialized path, user-defined LocationTypes, printer assignment at any depth, TotalPrinterCount denormalized
- **Test Coverage:** 1572 API tests (xUnit), 365 React tests (Vitest), 39.66% line coverage

### Recent Work (March 2026)
- Updated docs/AUTO_DISPATCH.md with known issues section and operator-focused configuration guide
- Created docs/DESIGN_SYSTEM.md (7,500+ words, 40+ component reference, theme architecture, WCAG compliance)
- Updated README.md, CONTRIBUTING.md, ARCHITECTURE.md, DEVELOPMENT.md with accurate tech stack and test counts
- Merged decisions: Auto-Dispatch Documentation, Auto-Dispatch Bug Root Cause Analysis

### Documentation Best Practices
- Cross-reference team decisions (.squad/decisions.md) for architectural context
- Include root cause analysis: why did documentation gap exist?
- Capture actual system behavior (not intended behavior) based on code inspection
- Provide operator-focused guidance alongside developer architecture docs
- Use Mermaid diagrams for complex flows: sequence (trigger), flowchart (dispatch cycle), state machine (ready gate)

## Learnings

### Auto-Dispatch System Documentation (2026-01-15)

**Files Created:**
- **docs/AUTO_DISPATCH.md** — Comprehensive documentation of auto-dispatch system with Mermaid diagrams (40KB, 1110 lines)

**Documentation Scope:**
- Architecture overview with component diagram showing all services and data flows
- Three distinct concepts explained: Auto-Dispatch (job routing), Ready Gate (bed-clear safety), Auto-Print (future hardware feature)
- Complete system component documentation: AutoDispatchTrigger, AutoDispatchBackgroundService, DispatchScorer, AutoPrintService, JobQueueService, JobDispatchService
- Mermaid diagrams for: trigger flow (sequence), dispatch cycle (flowchart), ready gate (state machine), component architecture
- 10-factor scoring system with weights: Material Match (100), Nozzle Diameter (100), Nozzle Hardness (80), Enclosure (80), Model Match (60), Build Volume (50), Preferred Printer (40), Queue Depth (30), Printer Group (0), Availability (0)
- Three dispatch modes: Manual, Suggest, Auto with detailed behavior descriptions
- Configuration options: system-level settings (singleton) and per-printer opt-in
- Complete API endpoint reference: 11 endpoints across DispatchSettings, AutoPrint, and Dispatch controllers
- SignalR events: jobautodispatched, dispatchsuggestion, dispatchfailed, autoprintstatechanged
- Frontend UI components: Global toggle, per-printer Zap icon, Bed Clear Banner with three action buttons
- Eight critical design decisions documented with rationale and alternatives rejected

**Key Architecture Insights Captured:**
- **Channel-Based Trigger System**: Uses bounded channel (capacity 64) with DropOldest policy for backpressure management. Two trigger paths: NotifyPrinterIdle (with idle threshold delay) and NotifyJobQueued (immediate, skips delay for upload-and-print).
- **Event-Driven Background Service**: Fire-and-forget Task spawning for concurrent printer processing. SemaphoreSlim(1,1) serializes job assignment to prevent race conditions. MaxConcurrentDispatches limit prevents thundering herd.
- **Weighted Scoring Algorithm**: Hard requirements eliminate printers (Material, Nozzle, Enclosure, Hardness when needed). Soft factors reduce score but don't eliminate. Weighted average: Σ(score × weight) / Σ(weights).
- **Ready Gate State Machine**: None → PendingReady → Ready → (dispatch) → None. Operator must confirm bed is clear between consecutive prints. Filament pre-flight checks (material match, weight sufficiency) before dispatch.
- **Upload-and-Print Immediate Dispatch**: Jobs queued with pre-assigned printer skip idle threshold for instant dispatch. User explicit choice bypasses delay.
- **No Compatible Printer Handling**: File uploaded but NOT queued if no printer scores above MinimumScoreThreshold. Forces manual assignment, prevents orphaned jobs.
- **Audit Trail**: All dispatch decisions logged to DispatchLogs with full score breakdown (JSON serialized). Enables post-mortem analysis and future ML improvements.
- **Thread Safety**: SemaphoreSlim prevents two printers from grabbing same job. Channel provides bounded buffer with DropOldest for backpressure. Interlocked operations track in-flight dispatch count.

### Auto-Dispatch System Documentation Updated (2026-03-11)

**Files Updated:**
- **docs/AUTO_DISPATCH.md** — Updated with critical bug notes and operator-focused configuration guide (still 1110 lines, now includes known issues section)

**Updates Made:**
- Added "KNOWN ISSUES (Being Fixed)" section documenting three critical bugs:
  1. **Toggle Alone Does Not Enable Auto-Dispatch** — `AutoDispatchEnabled=true` but mode stays at Manual (seed default). Requires BOTH enabled + mode change. Provided API workaround.
  2. **PendingReady Gate Blocks First Upload** — Bed-clear banner only appears after print completion, not on first upload. First-time dispatch blocked if printer stuck in PendingReady.
  3. **Frontend Naming Mismatch** — Frontend renamed autoPrint→autoDispatch but API endpoints/backend properties unchanged. Intentional during transition, provided clarification.

**Configuration Section Refactored for Operators:**
- Restructured as "3 Independent Layers" (System Toggle, System Mode, Per-Printer Opt-In) — clearer mental model than previous layout
- Added "How to Properly Enable Auto-Dispatch" step-by-step guide with curl examples
- Emphasized the critical dependency: `autoDispatchMode` must be "Suggest" or "Auto" (NOT "Manual") for dispatch to work
- Updated system settings table with warnings about `AutoDispatchMode` defaults to "Manual"
- Added per-printer opt-in clarity: toggle ⚡ icon on individual cards OR use bulk enable

**API Endpoint Documentation Improvements:**
- Added note clarifying `/autoprint/` paths match backend `autoPrintEnabled` property, separate from frontend "autoDispatch" terminology
- Updated PUT `/api/dispatch-settings` example to show **both** required fields for enabling auto-dispatch
- Added validation section showing `autoDispatchMode` must be one of: Manual, Suggest, Auto

**Root Cause of Previous Documentation Gap:**
- Original AUTO_DISPATCH.md written before bugs were identified
- Documentation assumed correct behavior (that toggle sets mode correctly)
- No section explaining the dual-field requirement (enabled + mode) to operators
- Backend property naming (`autoPrintEnabled` vs frontend term "autoDispatch") not clarified

**Quality Improvements:**
- Docs now reflect actual system behavior (mode guard in AutoDispatchBackgroundService)
- Operator troubleshooting section added for "why didn't auto-dispatch work"
- API examples show the exact JSON needed to enable auto-dispatch correctly
- Clarified distinction between `AutoDispatchEnabled` (toggle) and `AutoDispatchMode` (behavior selection)

**Frontend Naming Context Captured:**
- Commit 1ded064c: React frontend renamed autoPrint→autoDispatch for consistency with "Auto-Dispatch" system-level terminology
- API paths and backend properties remain `/autoprint/` and `autoPrintEnabled` — intentional backward-compatibility decision
- Query keys changed from `['autoprint', ...]` to `['auto-dispatch', ...]` in useApi.ts
- Decision: backend property rename requires DB migration (future effort), API paths unchanged for now

**Documentation Quality:**
- 40KB comprehensive guide for both developers (architecture, code paths) and operators (configuration, UI usage)
- Four Mermaid diagrams for visual clarity: component architecture (graph), trigger flow (sequence), dispatch cycle (flowchart), ready gate (state machine)
- Complete API reference with request/response examples for all 11 endpoints
- Real-world scoring example with breakdown showing weighted average calculation
- Eight design decisions documented with rationale, alternatives rejected, and future considerations
- Frontend UI integration: Global toggle implementation, per-printer Zap icon behavior, Bed Clear Banner with action button details

**Root Causes of Documentation Gap:**
- Auto-dispatch system implemented across 12 source files with complex interactions
- Channel-based event architecture not immediately obvious from individual file reading
- Scoring algorithm weights scattered across DispatchScorer.cs without consolidated reference
- Ready Gate workflow (PendingReady/Ready states) separate from main dispatch flow, needed unified explanation
- Frontend UI integration (toggle, Zap icon, banner) spread across three React components
- Design decisions (immediate upload-and-print, no-compatible-printer handling, SemaphoreSlim locking) implicit in code, not documented

**Sources Read:**
- AutoDispatchTrigger.cs (131 lines) — Channel trigger with SkipIdleThreshold flag
- AutoDispatchBackgroundService.cs (329 lines) — Event-driven background service with fire-and-forget Tasks
- DispatchScorer.cs (507 lines) — 10-factor weighted scoring with hard/soft requirements
- DispatchModels.cs (100 lines) — Enums and data structures
- JobDispatchService.cs (112 lines) — Orchestration and audit logging
- DispatchDtos.cs (327 lines) — API DTOs and SignalR event payloads
- JobQueueService.cs (partial) — NotifyJobQueued trigger after queuing
- AutoPrintService.cs (473 lines) — Ready Gate state machine and filament checks
- AutoPrintController.cs (153 lines) — Ready Gate API endpoints
- DispatchController.cs (67 lines) — Dashboard queue status and history
- DispatchSettingsController.cs (99 lines) — System-wide settings CRUD
- PrintQueueDashboardPage.tsx (150 lines partial) — Global auto-dispatch toggle
- CollapsedPrinterCard.tsx (100 lines partial) — Per-printer Zap icon
- BedClearBanner.tsx (132 lines) — Bed clear confirmation UI

### Deployment Hardware Guide (2026-03-21)

**Files Created:**
- **docs/DEPLOYMENT_HARDWARE.md** — Comprehensive operator-facing hardware selection guide (23,400+ words, 10 major sections)

**Files Updated:**
- **README.md** — Added "Deployment Hardware Guide" link to Quick Links table

**Hardware Tier Framework Documented:**
- **Tier 1 (Pi 4):** 1–10 printers, 8GB RAM, USB 3 SSD (not SD card), all limitations documented
- **Tier 2 (NUC/Mini PC):** 10–50 printers, 16–32GB RAM, PostgreSQL + all features
- **Tier 3 (Server/VM):** 50–200+ printers, 32GB+ RAM, distributed architecture, cloud options

**Service Resource Matrix:**
- All 10 services documented with realistic RAM/CPU/disk estimates
- Growth curves (sublinear API growth, database storage dominance)
- Scaling recommendations for each service
- Critical insight: 1GB per 10 printers as rough sizing guide

**Storage Recommendations (Critical Details):**
- **WHY NOT SD CARD:** Poor random write performance, wear-leveling assumptions, database corruption after 1–2 years
- **Recommended:** USB 3 SSD (Samsung T7 or equivalent) with specific product links
- **Disk space planning:** 256GB minimum Pi (application 500MB + SQLite 1GB + G-code + logs + buffer)
- **Archival strategy:** 30 days online, older files to tape/S3 (cost comparison provided)

**Network Requirements:**
- Bandwidth per printer: 500 Kbps–1 Mbps with camera
- Same-subnet requirement for discovery (UDP broadcast + TCP probes)
- WiFi vs Ethernet comparison table (WiFi 6 marginal for 20+ printers)
- Multi-location workarounds (VPN/tunnel, per-building deployment)

**Deployment Profiles (Three Tiers):**
- **Lite:** Pi-friendly (API + frontend + SQLite + discovery), 600MB RAM
- **Standard:** NUC-friendly (adds PostgreSQL, monitoring lite, 1–2 workers), 1.5GB RAM
- **Full:** Server deployment (all services, 4+ workers, pgAdmin, telemetry), 3–5GB RAM

**Performance Tips Section:**
- Pi-specific: USB SSD, disable monitoring, slicer on separate machine, cgroup memory limits
- NUC-specific: PostgreSQL shared_buffers tuning, 2–4 workers, monitoring enabled
- Server-specific: managed DB, distributed slicing, automated backups, 50+ printer monitoring

**Docker Deployment Examples:**
- First-time vs. re-deployment workflows
- `deploy-docker.sh` usage with dry-run, non-interactive, and profile selections
- Manual compose file stacking for advanced users

**Troubleshooting Section:**
- OOM on Pi (memory profiling, feature reduction)
- SQLite write contention (PostgreSQL upgrade, update frequency tuning)
- Discovery not finding printers (subnet verification, network connectivity)
- Camera stream performance (bandwidth profiling, resolution reduction)

**Cost Comparison:**
- 12-month TCO by configuration (Pi $300, NUC $850, AWS $3,600, Hetzner $1,200)
- Break-even analysis for cloud vs. dedicated hardware

**Key Documentation Insights Captured:**
- Docker Compose templates reveal 15+ optional services (discovery, monitoring, telemetry, workers, pgAdmin)
- Resource limits explicit in compose files (discovery: 256M, workers: varies by type)
- Database choice critical inflection point: SQLite adequate ≤15 printers, PostgreSQL required ≥20
- Pi deployment: USB SSD non-negotiable for reliability (SD card database corruption well-documented pain point)
- Network discovery architecture (UDP broadcast + TCP probes) requires same subnet
- Bandwidth calculation: 50 Kbps baseline + 200–500 Kbps per camera = 500 Kbps–1 Mbps per printer

**Root Causes of Documentation Gap:**
- No existing hardware guidance in docs (DEPLOYMENT.md focuses on "how to deploy," not "what hardware to buy")
- Docker Compose templates show optional services but no resource footprints or when to enable
- Pi deployment popular but undocumented pain points (SD card reliability, WiFi bandwidth, memory constraints)
- Network architecture (same-subnet discovery) not explained in existing docs
- No cost comparison or TCO guidance for operators making hardware decisions
- Service resource consumption scattered across compose templates without consolidated reference

**Sources Read:**
- `scripts/docker/compose-templates/docker-compose.yml` — Core services, resource defaults
- `scripts/docker/compose-templates/docker-compose.monitoring.lite.yml` — Lite monitoring stack resources
- `scripts/docker/compose-templates/docker-compose.discovery.yml` — Discovery service, resource limits (0.5 CPU, 256M RAM)
- `scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml` — Worker resource profiles
- `scripts/deploy-docker.sh` — Deployment script, environment variables, profile selection flow
- `docs/DEPLOYMENT.md` (385 lines) — Existing deployment guide structure and style
- `README.md` — Quick links table, farm size positioning
- `docs/GETTING_STARTED.md` — Writing style and beginner-friendly tone
- Custom instruction files — PrintFarmer-specific conventions (component naming, Docker patterns)

### Design System Documentation (2026-03-21)

**Files Created:**
- **docs/DESIGN_SYSTEM.md** — Comprehensive reference for PrintFarmer UI component library, design tokens (pf-* CSS variables), theme system, and usage patterns (7,500+ words)

**Files Updated:**
- **README.md** — Added reference to Design System docs in "Implementation details" section

**Design System Scope:**
- 40+ React components with complete prop APIs and usage examples (Button, Input, Select, FormField, Card, Badge, Spinner, Modal, Tabs, DataTable, Alert, Toggle, FileUpload, Checkbox, Radio, ProgressBar, Tooltip, Textarea)
- CSS custom properties for dynamic theming (GitHub Dark, PrintFarmer Dark, Light)
- Design tokens: colors (primary, text, status, accents, errors, borders, gradients), spacing, typography, state indicators
- Accessibility features (WCAG 2.2 AA compliance) with keyboard navigation and screen reader support
- Common patterns for forms, data tables, modals, loading states, conditional rendering
- Troubleshooting guide and best practices for UI development

**Design Token System (Three Layers):**
1. **CSS Custom Properties** — `--pf-bg-0`, `--pf-text-primary`, `--pf-accent` (theme-aware variables in theme.css)
2. **Tailwind Utilities** — `bg-pf-bg-0`, `text-pf-text-primary` (defined via tailwind.config.js CSS color mappings)
3. **React Components** — Button, Input, Card, Modal (composed from Tailwind + tokens, zero hardcoded colors)

**Theme Architecture:**
- GitHub Dark (default) — GitHub's official colors, 13.6:1 contrast on primary text
- PrintFarmer Dark — Custom dark theme with 4.5:1 minimum contrast on all text
- Light — High-contrast light theme for daytime use, daylight accessibility
- Dynamic theme switching via `document.documentElement.setAttribute('data-theme', 'light')`
- All 40+ CSS variables per theme recalculate on switch (zero rebuild overhead)

**Accessibility Integration:**
- WCAG 2.2 Level AA compliance: 4.5:1 contrast for normal text, 3:1 for large text (18.5px+)
- Semantic HTML (`<button>`, `<input>`, `<label>`, `<fieldset>`) with ARIA attributes
- Keyboard navigation: Tab, Arrow keys (in composites), Enter/Space, Escape
- Focus indicators visible on all interactive elements (customizable ring color)
- Respects `prefers-reduced-motion` and `prefers-contrast` media queries
- All form inputs have associated `<label>` elements and error messages linked via `aria-describedby`

**Root Causes Addressed:**
- No centralized design system documentation — component reference scattered across multiple markdown files
- Component library grown to 40+ components without unified prop documentation
- New contributors didn't understand three-layer architecture (custom properties → Tailwind → React)
- Design token system (pf-* naming) was underdocumented and inconsistently applied
- Theme switching architecture not explained (how CSS variables enable dynamic themes)
- Accessibility features and WCAG compliance not documented for component users
- Common UI patterns (forms, modals, data tables) lacked complete code examples

**Documentation Quality:**
- 7,500+ words with 20+ complete code examples
- All 40+ components documented with props interfaces, usage patterns, and variants
- Design token reference table with default values, usage, and contrast ratios
- Three theme variants documented with color specifications
- Accessibility section with keyboard shortcuts, screen reader support, WCAG compliance info
- Troubleshooting section for common styling/theming issues
- Best practices and anti-patterns (Do's and Don'ts)

### Sprint 1+2 API Documentation (2026-03-21)

**Files Updated:**
- **docs/API.md** — Added comprehensive endpoints for locations hierarchy (tree, ancestors, descendants, move, crud) and auto-dispatch system (candidates scoring, dispatch-to, settings)
- **docs/ARCHITECTURE.md** — Added Location Hierarchy Architecture section (adjacency list + cached path design) and Auto-Dispatch Architecture section (9-factor scoring, background service, dispatch modes)
- **README.md** — Updated Key Features section to highlight hierarchical locations and auto-dispatch scoring engine

**Key Documentation Insights:**
- Location hierarchy uses adjacency list + materialized path for efficiency (Path LIKE queries replace recursion)
- Auto-dispatch scoring has 9 factors: 4 hard filters (material, nozzle, availability, build volume) + 5 soft factors (enclosure, hardness, model, queue, preferred)
- Dispatch settings are singleton entity (system-wide configuration) with two modes: "Suggest" (operator confirms) and "Auto" (full automation)
- All dispatch decisions logged for audit trail and future ML improvements
- SignalR events extended with job auto-dispatch notifications

**Root Causes of Documentation Gap:**
- Controllers implemented before API documentation was created
- Architecture decisions (from .squad/decisions.md) needed to be synthesized into ARCHITECTURE.md
- Team decisions had detailed problem statements and solutions but weren't integrated into main docs

**Best Practices Established:**
- Read source code (controllers) to document actual implementations, not theoretical APIs
- Cross-reference team decisions when adding major features
- Update ARCHITECTURE.md with design rationale (not just "this is what we do")
- Include validation rules and error codes with each endpoint

### Documentation Review & Updates (2026-03-06)

**Files Updated:**
- **README.md** — Updated ASP.NET Core version (9→10), test counts (496→1572 API, 150→365 React), added FlashForge to backend plugins
- **CONTRIBUTING.md** — Fixed repository layout (removed Blazor client/, added backends/, updated to Web/ReactApp/)
- **ARCHITECTURE.md** — Updated .NET version, documented all 6 backend plugins
- **DEVELOPMENT.md** — Added backend plugin structure to code organization diagram
- **GETTING_STARTED.md** — Updated test counts to match current suite (1572/365)

**Root Causes of Outdated Info:**
- ASP.NET Core version not updated when upgrading from 9→10
- Test count updates not reflected in marketing docs when test suite expanded
- Repository layout changed from Blazor client to React, but CONTRIBUTING.md not updated
- Backend plugin count grew from 5→6 with FlashForge, not mentioned in README

**Key Facts Now Documented:**
- 6 backend plugins: Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core
- Full test coverage: 1572 API tests (xUnit), 365 React tests (Vitest)
- Uses .NET 10.0.* with rollForward strategy
- Frontend: React 19, Vite, TanStack React Query, Tailwind CSS v4
- 51 comprehensive markdown docs exist in docs/ directory

### Orchestration & Decision Integration (2026-03-08)

**Status:** ✅ Orchestration logs created, decisions merged into squad decisions.md

**Work Completed:**
- Created `.squad/orchestration-log/2026-03-08T02-03-11Z-ash.md` documenting design system documentation task completion
- Merged Ash's design system decision from inbox into main decisions.md (Decision #18)
- Updated squad decision governance with design system as Decision #18

**Impact on Team:**
- Design system documentation now discoverable in squad decisions for future reference
- Architecture decisions (three-layer design system, WCAG compliance, dynamic theming) synthesized and centralized
- Team standard established: design system documentation is architectural decision, not just content update

### Deployment Documentation: Monolith Mode & GHCR (2026-03-09)

**Status:** ✅ Complete — Comprehensive deployment docs updated with monolith mode and GHCR info

**Files Updated:**

1. **docs/DEPLOYMENT_HARDWARE.md** (23 KB → 45 KB, +900 lines)
   - Added "Deployment Modes: Monolith vs. Microservices" section explaining single-container vs. multi-container architectures
   - Added "Deployment Profiles by Farm Size" table: Lite (Pi/monolith), Standard (NUC/microservices), Full (Server/microservices)
   - Added "Raspberry Pi Quick Start" section: Step-by-step hardware setup, system prep, deployment, and health monitoring
   - Added "GitHub Container Registry (GHCR) Images" section with available images, pull commands, architecture support, and usage examples
   - Updated Pi deployment guidelines with `DEPLOYMENT_MODE=monolith` and `DB_PROVIDER=sqlite`
   - Added Pi deployment checklist for operators

2. **README.md** (15 changes across 3 sections)
   - Updated "Quick Start" Option 1 with monolith mode commands for Pi
   - Added GHCR image pull examples (monolith, API, frontend)
   - Enhanced "🐳 Deployment" section with deployment modes, monolith/microservices examples, and GHCR links
   - Completely rewrote "🍓 ARM / Raspberry Pi Deployment" section with monolith mode emphasis, docker run example, and link to comprehensive hardware guide

**Key Documentation Decisions:**
- Monolith mode positioned as primary option for Pi deployments (single container, no reverse proxy, ~450MB image)
- Microservices mode documented as default (separate API/frontend, production-ready, scalable)
- GHCR presented as alternative to building from source (`docker pull` instead of `./deploy-docker.sh`)
- Hardware profiles tied to deployment modes: Lite=monolith, Standard/Full=microservices
- ARM/Pi documentation consolidated in DEPLOYMENT_HARDWARE.md as single source of truth

**Learnings:**
- `DEPLOYMENT_MODE=monolith` implemented in Program.cs: when set, API serves React frontend from wwwroot/ via UseStaticFiles() + MapFallbackToFile("index.html")
- Default deployment mode is "microservices" (separate containers, needs nginx-proxy)
- GHCR workflow auto-detects multi-arch: images work on both x86_64 and arm64 (except slicer workers which are x86-only)
- IMAGE_OWNER = ${{ github.repository_owner }} (evaluates to "olyforge3d" for this repo)
- Operators can now choose: automatic deployment (deploy-docker.sh), manual Docker (pull from GHCR + docker run), or orchestration (Kubernetes, etc.)
- Pi deployment best practices: use monolith mode, SQLite, USB SSD (never SD card), Ethernet (not WiFi), monitor memory/disk

**Documentation Architecture:**
- README.md serves as marketing/discovery layer (links to hardware guide for details)
- DEPLOYMENT_HARDWARE.md is comprehensive reference: hardware specs, deployment profiles, Pi quick-start, GHCR details, troubleshooting
- Monolith mode positioned as accessibility feature for low-resource deployments
- GHCR enables deployments without source code checkout (important for corporate/airgapped environments)


## Pi 4 Deployment Infrastructure (2026-03-11)

**Sprint Focus:** Hardware guide expansion + deployment documentation finalization

### Ash Work (Agent-30)

**Deliverables:**
1. **DEPLOYMENT_HARDWARE.md** (45 KB, 23,400+ words, 12 major sections)
   - "Deployment Modes: Monolith vs. Microservices" — architecture decision guide
   - "Deployment Profiles by Farm Size" — Lite (Pi/monolith), Standard (NUC/microservices), Full (Server)
   - "Raspberry Pi Quick Start" — step-by-step hardware selection, OS imaging, Docker, deployment
   - "GitHub Container Registry (GHCR) Images" — available images, multi-arch support, pull commands
   - "Service Resource Matrix" — RAM/CPU/disk per service, "1GB per 10 printers" sizing rule
   - Cost analysis: Pi 4 (~$300), NUC (~$850), AWS/Hetzner (~$1,200-3,600)
   - Troubleshooting: OOM, SQLite contention, discovery failures, camera lag

2. **README.md Updates** (15 strategic changes)
   - Monolith mode example for Pi deployment
   - GHCR pull commands for all three images
   - "Deployment Modes" subsection (monolith vs microservices positioning)
   - Updated ARM/Raspberry Pi section with modern guidance

### Key Documentation Insights
- **Pi database reliability:** SD card corruption is #1 failure mode; USB 3 SSD mandatory (~$30 addition)
- **Database inflection point:** SQLite adequate ≤15 printers; PostgreSQL required ≥20
- **Network architecture:** Discovery requires same subnet (UDP broadcast + TCP probes); Gigabit Ethernet recommended
- **Service consumption:** Created single reference matrix for operator understanding (API, PostgreSQL, Discovery, OrcaSlicer, Monitoring)
- **Profile matching:** Lite/Standard/Full profiles match hardware tiers for easy decision-making

### Documentation Architecture
- **Two-layer approach:** README for discovery/quick links, DEPLOYMENT_HARDWARE.md for comprehensive details
- **No duplication:** Links drive users to reference rather than repeating content
- **GHCR positioning:** Alternative to deploy-docker.sh (not replacement); enables manual deployments for operators
- **Hardware-driven:** Hardware choice determines deployment architecture (Pi → monolith, NUC/Server → microservices)
- **Operator-focused tone:** Plain language, specific products with cost, real deployments with examples

### Related Decisions Finalized
- **Decision 1:** Deployment Hardware Guide (23,400 words, 12 major sections, operator-focused)
- **Decision 2:** Deployment Documentation Update — Monolith Mode & GHCR (README + guide consolidation)

### Key Learnings for Ash
- **Operator workflow:** "I have a Pi" → monolith mode → GHCR pull → docker run
- **Hardware as decision driver:** Pick hardware first, architecture follows (opposite of tech-first thinking)
- **Cost transparency:** Total cost of ownership (hardware + storage + deployment) helps operators choose
- **Documentation strategy:** Single source of truth (DEPLOYMENT_HARDWARE.md) prevents duplication and confusion
- **Cross-agent alignment:** Monolith mode documentation validates Lambert's middleware + Parker's Docker infrastructure
- **Cost-benefit:** Pi 4 4GB ($75-100 + $30 SSD) is sweet spot vs $1,000+ server deployments
- **Tier recommendations:** 
  - Not recommended: Pi 4 2GB (too tight)
  - Excellent: Pi 4 4GB (1-5 printers, with lite monitoring)
  - Full features: Pi 4 8GB (5-20+ printers, with all services)
