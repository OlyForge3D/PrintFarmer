# Dallas History

## Core Context

Dallas is the project lead & product architect. Key contributions:
- Feature prioritization & architecture oversight
- Location hierarchy system design (phase 1 approved)
- Auto-dispatch phase 1 & 2 architecture
- Competitive analysis & market differentiation
- Team coordination & decision governance
- Failure detection & UI polish sessions (2026-03-25)
- Auto-dispatch naming cleanup & consistency (2026-03-25)

Early entries (pre-2026-03-25) summarized for maintainability. See decisions-archive.md for historical context.

---

## Session History Summary (2026-03-25 — 2026-05-12)

**Sessions Archived & Summarized:**
- Failure Detection Badge Placement Review (2026-03-25) — Recommendation to remove camera overlay; keep header badge only
- 11 prior decision reviews and architectural analyses (2026-03-16 — 2026-03-25)

**Themes Across Sessions:**
- UX clarity and visual consistency enforcement
- Backend-agnostic feature scoping (UI-first architecture)
- Competitive analysis and market differentiation
- Team decision governance and conflict resolution

See `.squad/decisions-archive.md` for detailed decision records from archived sessions.

---

## Session: Prusa-StatusBar Camera Integration Research (2026-05-12)

**Role:** Lead/Architect  
**Status:** Research complete; proposal approved for decision registry

### Work Completed
- Analyzed Prusa-Buddy printer camera capabilities and RTSP streaming protocol
- Evaluated integration approaches (direct RTSP URL vs. go2rtc sidecar bridge)
- Assessed Tier 1/2/3 feature breakdown with implementation complexity estimates
- Confirmed no upstream firmware changes needed from Prusa
- Documented auto-discovery and event snapshot strategies

### Key Findings
- Prusa-Buddy printers expose RTSP URLs natively (hardware-built capability)
- PrintFarmer can integrate via: (a) direct RTSP URL config, or (b) go2rtc sidecar for WebRTC fallback
- Event snapshots possible via background timelapse capture from RTSP stream
- Auto-discovery can use Prusa API or manual discovery per printer

### Decision Record
- **File:** `.squad/decisions.md` → Prusa-StatusBar Camera Integration entry
- **Tiers:** MVP (RTSP viewer), Core (auto-bridge), Polish (admin mgmt)
- **Next:** Frontend design (Ripley), backend scoping (Lambert), DevOps planning

### Session Artifacts
- Orchestration Log: `.squad/orchestration-log/2026-05-12T18-18-17Z-dallas.md`
- Session Log: `.squad/log/2026-05-12T18-18-17Z-prusa-camera-research.md`
