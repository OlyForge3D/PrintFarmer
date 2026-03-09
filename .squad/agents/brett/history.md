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

