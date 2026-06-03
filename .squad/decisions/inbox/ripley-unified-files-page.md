## 2026-06-03T10:36:27.015-07:00

- Decision: keep `/files` as the single library route and drive file-type lenses with `?type=` filters instead of child tab routes.
- Why: the page now shows one shared file browser, preserves source-specific actions, and still normalizes legacy `/files/gcode` and `/files/3d-models` entry points without keeping tab-only route structure alive.
- Impact: new navigation should point to `/files` or `/files?type=<filter>`; harvest remains an action on the page, not a separate content tab.
