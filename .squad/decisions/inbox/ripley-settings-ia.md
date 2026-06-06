## Decision: Phase 1 Settings IA Query Model

| Field | Value |
|-------|-------|
| **Date** | 2026-06-06T08:58:45.350-07:00 |
| **Agent** | Ripley |
| **Requested by** | Jeff Papiez |
| **Status** | Proposed |
| **Scope** | React settings information architecture |

## Recommendation

Use a normalized query model inside the existing settings shell:

- `scope=user|system|admin`
- `tab=<category>`
- `sub=<sub-page>`

Keep the current `/settings` route alive for Phase 1, but resolve legacy `?tab=` links through shared navigation helpers so the shell can infer the correct scope and destination.

## Why

This lets the frontend ship the new User/System/Admin information architecture without moving page components or changing route ownership yet.
It also keeps existing bookmarks working while the sidebar, command palette, and search all move onto the new scope-aware model.

## Compatibility Notes

- Legacy `tab=notifications` now resolves to User Settings → Profile → Notifications.
- Legacy `tab=users&sub=api-keys` now resolves to User Settings → Profile → API Keys.
- Legacy operational tabs such as `system`, `users`, and `data` continue to resolve into Admin categories.
