# Settings Tab Assignment Decisions (ST-2, #358)

**Date:** 2026-05-31  
**Author:** Ripley  
**PR:** #376 (stacked on #367)

## Tab Assignments

These assignments follow the issue's mapping table. Deviations:

- **Quotas** → Data tab (not explicitly listed in issue but semantically fits with Tags and Data Management)
- **Login Audit** → Users tab (grouped with user account management rather than keeping a separate Security nav section)
- **Notifications** → Placeholder only (no NotificationsSettingsPage exists yet)

## Sidebar Simplification

Reduced from 5 nav sections (Operations, Hardware, Management, Admin, Security) to 3 (Operations, Management, Admin). The "Hardware" section was renamed to "Management" after its items moved to Settings. "Security" section was eliminated entirely (Login Audit moved to Users tab).

## Redirect Strategy

All old routes use `<Navigate replace />` for immediate client-side redirect. No server-side redirects needed. These should be removed after 30 days per the issue's acceptance criteria.
