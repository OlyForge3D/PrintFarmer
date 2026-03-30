# Ash History

## Core Context

Ash is a frontend/infra specialist. Key contributions:
- Camera integration framework (2026-03-15 onwards)
- Tailwind v4 CSS-First adoption (2026-03-18)
- Design system architecture & WCAG compliance
- Deployment documentation for Pi/monolith modes (2026-03-09-11)
- Markdown tooling & documentation styling

Early entries (pre-2026-03-09) summarized to reduce file size. See decisions-archive.md for historical context.

---

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

---

## 2026-03-15 Camera Phase A Backend — Frontend Impact

**Related Work:** Lambert completed Camera Phase A backend unification (2026-03-15T01-57-00Z)

**Impact:** Frontend should expect unified Camera API in Phase D:
- `GET /api/cameras/by-printer/{printerId}` — cameras for specific printer
- New Camera DTO fields: PrinterId, Source, CameraType, HealthStatus, LastHealthCheck
- Support multi-camera per printer UI
- Camera type filters (General, Bed, Nozzle, Wide, Timelapse)
- Health status indicators

**Decision:** `.squad/decisions.md` #17 — Camera Management Phase A  
**Next Phase:** Phase D — Frontend multi-camera UI

## 2026-01-XX Tailwind v4 CSS-First Documentation Update

**Task:** Update developer documentation to reflect Tailwind v4 CSS-first migration (requested by Jeff Papiez, executed by Ripley).

**Files Updated:**
1. `.github/instructions/printfarmer-react-components.instructions.md` — Updated Styling section to reference `@theme` block in `index.css` instead of `tailwind.config.js`
2. `docs/DESIGN_SYSTEM.md` — Removed reference to `tailwind.config.js` from Key Files; updated troubleshooting guidance
3. `docs/FRONTEND_UI_COMPONENTS.md` — Updated component overview and dependencies to reference CSS-first configuration
4. `docs/TROUBLESHOOTING.md` — Updated Tailwind CSS troubleshooting steps to reflect `@theme` block approach

**Key Changes Documented:**
- Tailwind v4 uses CSS-first configuration: no `tailwind.config.js` file
- Design tokens defined via `@theme { }` block in `src/Web/ReactApp/src/index.css` with CSS custom properties (`--color-pf-*`, `--font-family-*`)
- Custom utilities defined with `@utility` blocks (not JS plugins)
- No manual `content` array needed (v4 auto-detects)
- PostCSS config unchanged (`@tailwindcss/postcss`)

**Architecture Pattern Reinforced:**
- Layer 1: CSS Custom Properties (`--pf-*`) defined in `@theme` block
- Layer 2: Tailwind utilities (flex, gap, rounded, etc.)
- Layer 3: React components consuming tokens

**No New Files Created** — integrated all updates into existing documentation following Ash charter.

### Key Learnings
- Instruction files (`.github/instructions/`) directly impact code generation quality via Copilot; updates to these take priority
- Tailwind v4 CSS-first migration is architecture-wide, affecting design system docs, troubleshooting guides, and component patterns
- Design token management shifts from JS config layer to CSS layer, enabling better runtime flexibility

---

## Tailwind v4 CSS-First Documentation Updates — Complete (2026-03-18)

**Coordination:** Multi-agent sprint (Ripley + Ash + Kane)  
**Status:** ✅ DELIVERED  
**Mode:** Background

### Documentation Scope

**Priority 1: Copilot Instruction Files** (Impact: Code generation quality)
- `.github/instructions/printfarmer-react-components.instructions.md` — Updated Styling section
  - Added explicit reference to `@theme` block in `index.css`
  - Documented `@utility` blocks for custom utilities
  - Removed references to `tailwind.config.js`

**Priority 2: Design System Documentation** (Impact: Architecture clarity)
- `docs/DESIGN_SYSTEM.md` — Key Files + troubleshooting
  - Removed: `src/Web/ReactApp/tailwind.config.js` from project file structure
  - Added: Clarification that design tokens live in CSS `@theme` block
  - Updated: Troubleshooting section (v4 auto-detects, no manual safelist)

**Priority 3: Component Documentation** (Impact: Developer onboarding)
- `docs/FRONTEND_UI_COMPONENTS.md` — Overview + dependencies
  - Updated: Component overview references tokens in `index.css` `@theme`
  - Updated: Dependencies listed CSS-first approach

**Priority 4: Troubleshooting Guides** (Impact: Problem resolution)
- `docs/TROUBLESHOOTING.md` — Styles not applying section
  - Updated: Verification of `@theme` block in `index.css`
  - Updated: Tailwind CSS checks for v4 auto-detection

### Validation

- ✅ All 4 docs files verified readable and consistent
- ✅ No broken links or formatting issues
- ✅ Search of all `.md` and `.instructions.md` files: no orphaned v3 references
- ✅ All updates integrated into existing docs (no new files created)

### Architecture Pattern Reinforced

Documentation now consistently reflects three-layer design system:
```
Layer 3: React Components (Button, Input, Card, etc.)
        ↓ composed of
Layer 2: Tailwind Utilities (flex, gap-4, rounded-lg, etc.)
        ↓ powered by
Layer 1: CSS Custom Properties (@theme block in index.css)
```

### Team Coordination

**Ripley (Frontend Dev)** — CSS-first implementation:
- Migrated `index.css` to `@theme` block
- Converted 3 plugin utilities to `@utility` blocks
- Deleted `tailwind.config.js`
- All 1480 tests pass ✅

**Kane (Tester)** — Independent verification:
- Verified all documentation changes align with Ripley's implementation
- Confirmed no references to deleted files in docs
- All tests passing ✅

### Ready for

✅ Code review  
✅ Merge to main  
✅ Production deployment

---

## January 2026 — Auto-Dispatch Rename Completion

### Work Completed

Completed comprehensive documentation update for auto-dispatch rename:

1. **AUTO_DISPATCH.md** (34 lines changed, 18 insertions, 16 deletions):
   - Updated component diagram: `AutoPrintController` → `AutoDispatchController`
   - Updated all API endpoint references: `/api/auto-dispatch/` (documented legacy `/api/auto-print` support)
   - Clarified state names: `AutoDispatchState = PendingReady` (not `AutoPrintState`)
   - Clarified enabled flag: "Auto-Dispatch Enabled" with database schema annotation
   - Marked internal `AutoPrintService` as "Internal Implementation Detail"
   - Updated bed-clear workflow docs for consistency

2. **Verification completed**:
   - README.md: ✅ Already uses "Auto-Dispatch with 9-Factor Scoring"
   - API.md: ✅ Already using "auto-dispatch" terminology correctly
   - COMPETITIVE_ANALYSIS.md: ✅ Correctly references competitor's "AutoPrint™" (trademark noted)
   - All docs now consistent on user-facing terminology

3. **Decision record created**: `.squad/decisions/inbox/ash-complete-auto-dispatch-rename.md`

### Key Learnings

**Documentation Naming Strategy:**
- User-facing features use kebab-case: "auto-dispatch"
- Internal implementation details clearly marked (e.g., "Internal Implementation Detail")
- Database schema names documented with context
- Legacy API routes supported for backwards compatibility — no need to update client code if working

**API Route Design Pattern:**
- Primary route: `/api/auto-dispatch/` (new, forward-looking)
- Legacy route: `/api/auto-print/` (maintained for compatibility)
- Controller accepts both, so no forced client migration
- Client can use either route without issue

### Architecture Notes

- AutoPrintService/DTO naming stays internal — would require significant refactoring across layers
- Renaming internal names provides minimal user benefit but high code change cost
- User-facing docs are the customer interface; internal naming is implementation detail

### Files Modified

- `docs/AUTO_DISPATCH.md` — Core auto-dispatch documentation

### Future Considerations

If a major refactor occurs, consider renaming internal services from AutoPrint* to AutoDispatch* for full codebase consistency, but this is not urgent given current dual-route strategy and clear documentation.
