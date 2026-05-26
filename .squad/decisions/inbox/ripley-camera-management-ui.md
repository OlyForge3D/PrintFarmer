---
post_title: Camera Management UI
post_slug: ripley-camera-management-ui
author1: Ripley
microsoft_alias: n/a
featured_image: n/a
categories: [decisions]
tags: [camera-management, frontend]
ai_note: AI-assisted implementation note
summary: Camera management page UX updates for edit/delete controls, printer association, and endpoint detection.
post_date: 2026-05-26
---

## Camera Management UI

Ripley updated the camera management page so operators can manage cameras from the camera cards and table without leaving the Cameras page.

## UX Decisions

- Camera cards expose Edit and Delete buttons for farm admins.
- Edit uses the shared `Modal` component; delete uses the shared `ConfirmationModal`.
- The Edit Camera modal includes an Associated Printer dropdown so linked cameras can be reassigned or detached.
- The camera management table includes a Printer column so associations are visible in list view.
- The Edit Camera modal includes a Detect Endpoints button that probes the selected printer and populates Stream URL and Snapshot URL.
- Endpoint detection uses `POST /api/cameras/detect-endpoints` with `{ printerId }` and expects camelCase `{ streamUrl, snapshotUrl, source?, cameraType?, message? }`.
- Camera preview media uses `object-contain bg-black` to avoid cropping/zooming stream frames inside fixed aspect-ratio cards.
