## Decision: Settings Theme Preview Stays Metadata-Driven

| Field | Value |
|-------|-------|
| **Date** | 2026-06-03 |
| **Agent** | Ripley |
| **Status** | Proposed |

## Decision

Keep the new Settings appearance preview embedded in `ThemeSwitcher` and drive it from shared theme metadata rather than building a separate dashboard mock inside `UserPreferencesPage`.

## Rationale

The preview needs to update immediately when the active theme changes, but it should stay lightweight and avoid duplicating the real dashboard's component tree or data dependencies.

By colocating the miniature preview with the selector chips, the same theme option metadata can control swatches, descriptive copy, and preview colors in one place.

## Impact

Future theme additions only need a single metadata update in `ThemeSwitcher.tsx` to keep both the selector and the live preview in sync.

The Preferences page stays focused on layout and section composition instead of owning another theme-specific rendering surface.
