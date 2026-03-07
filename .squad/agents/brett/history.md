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
