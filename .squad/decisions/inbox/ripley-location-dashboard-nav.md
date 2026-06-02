## Decision

Add a top-level `Locations` sidebar item in the Hardware section of `Layout.tsx`.
Point the nav link directly to `/locations/dashboard`.

## Rationale

`LocationDashboardPage` is already a complete dashboard with its own tree, stats,
and printer list, so it needs first-class navigation rather than being stranded
behind an unrelated settings redirect.
Keeping the existing `/locations` redirect pointed at Settings > Hardware avoids
changing the behavior of any existing settings-oriented links or bookmarks.

## Impact

Users get a clear, persistent entry point to the location dashboard from the main
sidebar.
The dashboard becomes discoverable without widening the scope into route or settings-shell changes.
