# Decision: Bambuddy Feature Adoption — Phased Rollout Plan

**Author:** Dallas
**Date:** 2026-05-31
**Status:** Proposed (awaiting Brady approval on decision points)

## Decision

Adopt a subset of bambuddy features into PrintFarmer across 4 phases, prioritizing G-code preview, Quick Slice UX, notifications, and per-print cost tracking. Each phase ships independently.

## Architectural Calls

1. **Client-side 3MF parsing DEFERRED** — bambuddy's main-thread JSZip approach is a known performance risk. We will not copy it. When 3MF client-side parsing is needed (Phase 2 multi-plate picker), it will use a Web Worker-based design. Until then, server-side 3MF metadata extraction (already in `Model3DFileService`) is sufficient.

2. **gcode-preview v2 (stable) over v3 (alpha)** — v3 has API churn and isn't production-ready. We ship on v2.18.x. Migration to v3 happens when it stabilizes.

3. **No worker built into gcode-preview** — We accept main-thread parsing for v1 (files <10MB). Large-file guardrails (file-size warning, chunked loading) are Phase 1b follow-up work, not blockers.

4. **Notification system uses IProvider pattern** — matches bambuddy's `ProviderType` enum + interface approach. Phased: webhook + Discord + Telegram first; remaining providers are separate PRs.

5. **Quick Slice does NOT replace NewSliceJobPage** — it's an alternative entry point for simple jobs. Raw-param SlicerConfigModal is hidden behind "Advanced" but not removed.

## Don't Chase List

| Feature | Reason |
|---------|--------|
| Virtual printer emulation (MQTT/FTP/RTSP proxy) | Bambu-specific protocol debt; PrintFarmer is backend-agnostic |
| SpoolBuddy NFC hardware (ESP32 + firmware) | Out of software-only scope |
| MakerWorld direct import | Depends on Bambu Cloud token; not applicable to our multi-vendor users |
| LDAP/OIDC/TOTP auth | PrintFarmer auth is out of scope for this round |
| Multi-language i18n | Large effort, orthogonal to feature work |
| Smart plug integration | Hardware dependency; can revisit when energy tracking demand is proven |
| GitHub backup | Not relevant to PrintFarmer's deployment model |
| Layer timelapse → MP4 | Deferred to post-camera-infrastructure (go2rtc sidecar must land first) |

## Scope Boundary

This plan covers Phases 1-4 only. Layer timelapse, print queue scheduler with SJF, and multi-plate 3MF picker are explicitly future work beyond this round.
