## Decision: Profile Settings Discoverability

| Field | Value |
|-------|-------|
| **Date** | 2026-06-02 |
| **Agent** | Ripley |
| **Status** | Proposed |

## Decision

Use the authenticated user menu's **Profile** action as a direct entry point to
`/profile/api-keys`.

Expose **API Keys**, **Notifications**, and **Passkeys** as dedicated quick links
inside the Preferences page so all three self-service profile pages are
reachable without manual URL entry.

## Rationale

The three profile routes already existed, but they were effectively hidden
because the user menu's Profile action was a no-op and there were no other
in-app links to those pages.

Making **Profile** open a concrete page preserves the existing menu structure,
while the Preferences quick links surface the full set of account-management
pages in an already discoverable settings area.

## Impact

Authenticated users can now find and open API Keys, Notifications, and
Passkeys from normal navigation flows.

The change avoids adding a new route or expanding sidebar scope while still
solving the discoverability gap.
