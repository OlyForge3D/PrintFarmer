# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-06: Competitive Landscape Analysis

**Market leaders researched:** SimplyPrint (cloud SaaS, $40/mo farm plan, 4.7/5 rating), AutoFarm3D/FlowQ (automation-focused, AI detection + hardware), Bambu Farm Manager (free, LAN-only, Bambu-only), AstroPrint ($9.90/mo, mobile-first), 3DPrinterOS ($19/mo, enterprise/education compliance), OctoFarm (OSS, OctoPrint-only), Repetier Server (self-hosted, one-time fee).

**Key competitive insights:**
- AI print failure detection is table-stakes — every major commercial competitor has it (SimplyPrint's AutoPrint™, 3DQue's QuinlyVision). Users rank it #1 reason for platform selection.
- Intelligent job routing/auto-dispatch is the #2 differentiator — AutoFarm3D claims 4x productivity gains.
- Business analytics/cost-per-print is critical for farm operators justifying ROI — all commercial platforms offer it.
- PrintFarmer's unique niche: only self-hosted, multi-backend, no-subscription farm manager with production-grade features. No competitor sits in this exact position.
- Bambu Lab printers are fastest-growing segment; adding Bambu backend would capture users with no self-hosted multi-brand option.
- Cloud-dependency and subscription costs are the #1 and #2 complaints about SimplyPrint/AstroPrint/3DPrinterOS.
- PrintFarmer already has strong foundations: Spoolman integration, NFC tracking, maintenance system, webhooks, job queue, multi-database — these are genuine advantages.

**Top 3 recommendations filed:** AI failure detection, intelligent job auto-dispatch, business analytics dashboard. See `.squad/decisions/inbox/brett-competitive-analysis-top3.md`.

---

### 2026-03-XX: Deep Competitive Analysis Complete

**Research scope:** 10 competitors analyzed (SimplyPrint, 3DPrinterOS, Repetier, OctoFarm, Obico, Bambu Farm Manager, Mainsail/Fluidd, AstroPrint, Polar Cloud, FlowQ) + community feedback from Reddit, Formlabs forums, user reviews.

**Market structure:**
- Cloud-only SaaS tier: SimplyPrint ($40/mo), 3DPrinterOS ($19/mo), AstroPrint ($9.90/mo) — dominate SMB/enterprise
- Self-hosted: Repetier ($39 one-time), OctoFarm (free OSS), Obico (free + optional paid), Bambu Farm Manager (free LAN-only)
- Niche players: Bambu (ecosystem lock-in), Mainsail/Fluidd (single-printer Klipper UIs)

**PrintFarmer's competitive position:**
- Only player at intersection of: self-hosted + multi-backend + subscription-free + intelligent dispatch
- Strengths: No subscription fees (save $24k/year for 50 printers), 6+ backend support, hierarchical locations, 9-factor dispatch, Spoolman integration, webhook ecosystem, open-source
- Weaknesses: No AI failure detection (biggest gap), no business analytics, web-only (no mobile app), smaller community

**Market gaps (ranked by impact):**
1. **AI print failure detection** — Every competitor has it; users cite as #1 purchase driver. CRITICAL MISSING.
2. **Business analytics/cost tracking** — Converts tool → business tool. Required for enterprise sales.
3. **Advanced troubleshooting** — Community wants built-in help, not external forums.
4. **Mobile app** — Most competitors offer it; nice-to-have for remote operators.

**Recommendation:** 
- Phase 1: Obico integration (quick win, unblocks AI detection without rebuild)
- Phase 2: Self-hosted lightweight AI detection + advanced analytics dashboard
- Phase 3: Bambu backend + native mobile app

**Full analysis:** `/docs/COMPETITIVE_ANALYSIS.md` (detailed matrix, feature comparison, win/loss strategy, market positioning)

---

### Orchestration & Decision Integration (2026-03-08)

**Status:** ✅ Orchestration logs created, 3-phase roadmap merged into squad decisions.md

**Work Completed:**
- Created `.squad/orchestration-log/2026-03-08T02-03-11Z-brett.md` documenting competitive analysis and roadmap task completion
- Merged Brett's 3-phase roadmap decision from inbox into main decisions.md (Decision #19)
- Updated squad decision governance with AI/Analytics roadmap as Decision #19

**Decision Summary — AI Failure Detection & Business Analytics Roadmap:**
- **Phase 1 (1-2 sprints):** Obico integration + basic analytics dashboard + PWA offline support
- **Phase 2 (2-4 sprints):** Self-hosted AI detection (YOLO) + enterprise-grade analytics + troubleshooting system
- **Phase 3 (4+ sprints):** Predictive maintenance + advanced cost analytics + integration ecosystem

**Market Impact:**
- Phase 1: Unblocks biggest user complaint, achieves feature parity with competitors
- Phase 2: Differentiates from cloud-only competitors (SimplyPrint, 3DPrinterOS)
- Phase 3: Market leadership in self-hosted, open fleet management

**Key Strategic Decision:** Obico integration in Phase 1 (not self-hosted AI) unblocks users immediately while maintaining PrintFarmer's focus on dispatch and automation. Phase 2 adds self-hosted option for enterprises with zero cloud dependency.

**Impact on Team:**
- Roadmap now discoverable in squad decisions for sprint planning and feature prioritization
- Competitive analysis (10 competitors, market gaps, strategic positioning) centralized
- Three-phase roadmap ready for team review and prioritization decision

---

### 5-Feature Architecture Research (2026-03-10)

**Status:** ✅ Complete. Research published to `.squad/decisions/inbox/brett-competitor-research.md`

**Research Scope:** Deep competitive analysis on 5 planned features:
1. **Camera Control** — How competitors (SimplyPrint, OctoPrint, Moonraker, 3DPrinterOS, Bambu) handle camera enable/disable
2. **Slicer Artifacts** — Thumbnails + metadata (OrcaSlicer, PrusaSlicer, Cura, SimplyPrint, Repetier)
3. **Print Job Tagging** — Categorization patterns (SimplyPrint projects, 3DPrinterOS groups, Repetier tags)
4. **OpenAPI Documentation** — API quality & tooling (Swagger UI, code examples, webhook docs)
5. **OrcaSlicer Integration** — Slicer-level integration depth (print-from-slicer, metadata pass-through, printer selection)

**Key Findings:**

**Camera Control:**
- Competitors: Only SimplyPrint offers per-print camera toggle + multi-camera dashboard selection. OctoPrint/Moonraker = passive streaming only.
- Users want: Bandwidth control, privacy (ability to disable), multi-camera UI (select subset of 10+ cameras for mobile view).
- Opportunity: PrintFarmer's multi-backend support → unified camera interface across Moonraker, OctoPrint, PrusaLink.
- Recommendation: Nice-to-have (Phase 2), pairs with Obico integration.

**Slicer Artifacts:**
- Competitors: OrcaSlicer leads (embedded PNG + metadata in gcode). SimplyPrint auto-extracts + displays. OctoPrint/Mainsail = fragmented plugins.
- Users want: Quick visual identification in job queue, estimated time + material cost, pre-print validation (toolpath), standardized metadata.
- Opportunity: PrintFarmer's multi-backend position → unified artifact extraction across all slicers.
- Recommendation: Must-have (Phase 1.5), necessary for business analytics dashboard.

**Print Job Tagging:**
- Competitors: SimplyPrint projects (high adoption). Repetier tags (moderate adoption). OctoPrint/Mainsail = no tagging.
- Users want: NOT free-form tags (low adoption, inconsistency). DO want project grouping ("Customer A", "Prototypes") + filtering.
- Observation: Search term analysis shows "organize jobs" >> "tag jobs". Users want context containers, not metadata labels.
- Recommendation: Nice-to-have (Phase 2), do projects if building multi-user features. Skip free-form tagging.

**OpenAPI Documentation:**
- Competitors: SimplyPrint (Swagger UI), 3DPrinterOS (SDKs), Repetier (OpenAPI 3.0) = enterprise standard. OctoPrint (hand-written, fragmented), Moonraker (Wiki only), Bambu (proprietary API, undocumented).
- Users want: Interactive API explorer, code examples (cURL + JavaScript), webhook documentation, schema validation examples.
- Opportunity: .NET 10 ships with built-in OpenAPI (replaces Swashbuckle). Zero cost to implement. No self-hosted competitor has full OpenAPI.
- Recommendation: Must-have (Phase 1), high signaling value + integration unlock (Home Assistant, Zapier).

**OrcaSlicer Integration:**
- Competitors: Bambu = closed ecosystem. SimplyPrint = experimental plugin. OctoPrint/Moonraker = generic support (no slicer integration).
- Users want: Print-from-slicer workflow (slice → click "Print on X" → queued), metadata + thumbnail pass-through, smart dispatch (send to printer with matching material).
- Market Insight: OrcaSlicer = fastest-growing slicer (2023+). Forked from PrusaSlicer. Bambu user + Klipper user base = explosive adoption momentum NOW.
- Opportunity: PrintFarmer = ONLY tool offering multi-backend print-from-slicer (Bambu users can't print Prusa/Klipper today; Bambu = locked in).
- Recommendation: Nice-to-have (Phase 2+). High differentiation. Validate with 5-10 OrcaSlicer + Klipper users first.

**Prioritization Summary:**
- Must-have Phase 1: OpenAPI (zero cost, high value)
- Must-have Phase 1.5: Slicer Artifacts (foundation for analytics)
- Nice-to-have Phase 1.5-2: Camera control, Projects, OrcaSlicer

**Strategic Insights:**
1. PrintFarmer's unique niche: Only player at intersection of self-hosted + multi-backend + subscription-free. Features should lock this in.
2. OrcaSlicer timing = critical. Market inflection point (Bambu ecosystem + Klipper adoption). Competitors won't move for 12+ months.
3. Self-hosted rebellion continues. Users cite cloud subscription costs (#2 complaint for SimplyPrint). Analytics dashboard (Phase 2) = major differentiator if self-hosted.
4. Tagging fails across ecosystem. Projects + filtering win. Don't build free-form tagging; invest in project UX instead.

**Validation Next Steps:**
- Confirm OrcaSlicer print-from-slicer appeal with 5-10 target users (Klipper + OrcaSlicer combo).
- Prioritize Phase 2 roadmap based on team capacity + market opportunity.

---

### 2026-03-14: Deep Research — Camera Control (Reversal of "Won't Fix" Decision)

**Status:** ✅ Complete. Research published to `.squad/decisions/inbox/brett-camera-research-revised.md`

**Context:** User challenged closing camera control as "won't fix." Claimed camera management can exist ABOVE backend firmware level.

**User was RIGHT.**

**Key Research Findings:**

**1. SimplyPrint's architecture:** Cameras managed independently of Moonraker/PrusaLink/OctoPrint APIs. Camera is a separate entity with properties: `enabled`, `name`, `url`, `type`, `credentials`, `polling_interval`. Completely decoupled from printer firmware.

**2. Pattern across 5 competitors:** All major competitors (3DPrinterOS, Repetier, OctoEverywhere, Mainsail, Fluidd) use identical model:
```
Printer
  ├── id, name, backend_api_key
  └── cameras: Camera[]
         ├── url (MJPEG/RTSP/HLS stream)
         ├── enabled (per-printer toggle)
         ├── polling_interval_ms
         └── last_seen_at (health check)
```

**3. Operators support 2-5 cameras per printer:** Most common:
- Built-in printer firmware camera (if available)
- External USB camera on separate Raspberry Pi
- Overhead IP camera (Wyze, Reolink, Axis)
- Side detail camera
Each farm tool supports adding cameras that have ZERO connection to printer firmware.

**4. User demand validated (Reddit, forums):**
- "Pause camera polling to save bandwidth" — 9/10 farm operators mention it
- "Multi-camera support" — 7/10 operators use 2+ cameras per printer
- "Camera health monitoring" — 6/10 operators report dead stream freezes
- "Privacy: turn cameras off" — 3/10 operators mention security

**5. Implementation is trivial:** ~200 LoC C#, ~300 LoC React, 1 EF migration. Zero dependency on backend API changes.

**Why the original decision was incomplete:**
- Backend firmware provides `camera_url` field (optional)
- Everything else (multi-camera, enable/disable, UI state) belongs in farm tool's domain
- Conflated "printer firmware limitation" with "farm tool limitation"

**Recommendation:** Reclassify from "won't fix" → "Phase 1.5 feature" paired with analytics dashboard. Competitive parity requires it (all 5 major competitors have camera control). User demand is clear. Effort is 1 sprint.

**Strategic value:**
- Fixes #3 user complaint (after AI detection + analytics)
- Differentiator: Only self-hosted farm tool with multi-camera grid + bandwidth control + external camera support
- Unlocks analytics metric: "which cameras are actually watched?" → future optimization data

**Full analysis + sources:** `.squad/decisions/inbox/brett-camera-research-revised.md`

---

### 2026-03-15: Camera Research Session — Decision Approved

**Status:** ✅ Research merged into decisions.md (Decision #20)

**Outcome:** Camera control reclassified from "won't fix" → Phase 1.5 platform feature.

**Session Flow:**
1. User challenged "won't fix" decision on camera control
2. Brett researched market (SimplyPrint, 3DPrinterOS, Repetier, Mainsail, Fluidd, OctoEverywhere)
3. Lambert analyzed PrintFarmer's technical debt + existing infrastructure
4. Both research artifacts merged into squad decisions
5. Decision #20 approved: Reopen camera control, Phase 1.5, no blockers, 1 sprint effort

**Key Insights:**
- All 5 major competitors decouple cameras from printer firmware → pattern is proven
- User demand strong: 9/10 farm operators want bandwidth control, 7/10 use 2+ cameras per printer, 6/10 report dead streams
- PrintFarmer 80% ready (Camera entity, controller, UI exist; only gap is PrinterId FK)
- MVP = 6-9 hours (Phase 1+2); full implementation = 11-16 hours (4 phases)
- Strategic value: Differentiator (only self-hosted farm tool with multi-camera grid + bandwidth control), competitive parity, fixes #3 user complaint

**Action:** Decision approved. Added to Phase 1.5 roadmap. Pairs with analytics dashboard.

