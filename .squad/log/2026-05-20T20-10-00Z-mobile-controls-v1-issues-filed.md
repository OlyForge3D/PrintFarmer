# Session Log: Mobile Controls v1 Issues Filed

**Timestamp:** 2026-05-20T20:10:00-07:00 (UTC: 2026-05-21T03:10:00Z)
**User:** Jeff Papiez
**Topic:** Mobile API Drift + Basic Printer Controls v1 — issue filing

## Summary

Dallas decomposed the approved "Mobile API Drift + Basic Printer Controls v1" plan into 16 GitHub issues (#274–#289) on OlyForge3D/PrintFarmer, with all v1 design questions locked per Jeff's approval.

## Locked design (v1)

- Fixed preheat presets: PLA 200/60, PETG 240/80, ABS 240/100, Cool Down 0/0
- Fixed jog feedrates: XY 3000, Z 600 mm/min
- Jog steps: 0.1 / 1 / 10 / 100 mm
- Trust backend `supportsTemperatureControl` capability flag (no client probing)
- Cooldown = both hotend and bed to zero
- Match backend auth — no extra mobile role checks beyond `farm_admin` for maintenance toggle
- No optimistic UI — wait for next `printerupdated` SignalR event
- Hide controls when printer offline; block when printing/paused
- Human squad only (no `squad:copilot`)

## Issues filed

| # | Epic | Assignee |
|---|---|---|
| 274–278 | A — API drift cleanup | hudson, gorman |
| 279 | Spike — backend print-state enforcement | ripley |
| 280–283 | B foundation — services + viewmodel | gorman |
| 284–286 | UI build — preheat/jog/home | hudson, newt |
| 287 | Integration — E2E + SignalR re-sync | gorman |
| 288–289 | Polish + testing | hudson, gorman |

## Decisions promoted

- 2026-05-20: Mobile controls v1 locked design (this session)
- 2026-05-12: Lambert lockout override for camera review pass 2+3 (carry-over inbox)
- 2026-05-12: go2rtc deployment integration — opt-in flag (carry-over inbox)

## Next

Ralph picks up the board. Hudson / Gorman / Newt / Ripley start triage on their assigned issues.
