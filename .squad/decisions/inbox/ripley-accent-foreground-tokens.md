## Decision: Accent Foreground Tokens For Shared Frontend Controls

| Field | Value |
|-------|-------|
| **Date** | 2026-06-02 |
| **Agent** | Ripley |
| **Status** | Proposed |

## Decision

Use per-theme `--pf-on-accent` and `--pf-on-danger` tokens as the shared
foreground contract whenever text or icons sit on accent-filled or
error-filled controls.

Apply that contract to shared button variants, notification count badges,
settings category highlights, and the selected Preferences theme chip instead
of falling back to generic white text.

## Rationale

The contrast failures in issues #470, #471, and #472 all came from the same
assumption: a single white foreground would stay readable on every theme's
accent/error fill.

That breaks down on bright palettes like Matrix, Blueprint, RatOS, and Farm.
Defining the foreground once per theme keeps contrast decisions in the design
system instead of scattering one-off component overrides.

## Impact

Theme authors now control legible foregrounds centrally for accent-backed and
error-backed controls.

Frontend components can reuse the shared tokens and stay readable across all
supported themes with less per-feature CSS.
