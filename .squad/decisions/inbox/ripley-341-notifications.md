## Notification Preferences — Architecture Decisions

**Issue:** #341  
**Author:** Ripley (Frontend)  
**Date:** 2025-05-31

### Context

Farm operators need notification delivery (email, web push, in-app) with per-user preferences.

### Decisions

1. **Backend already existed.** The `NotificationPreferences` entity, `NotificationService`, and `GET/PUT /api/notifications/preferences` were already implemented. No changes to the existing preference logic were needed.

2. **Push subscription model.** Added `PushSubscription` entity with `(UserId, Endpoint)` unique index. Supports multiple subscriptions per user (different browsers/devices). VAPID public key served from `GET /api/notifications/push-subscription/vapid-key` (reads from `VAPID_PUBLIC_KEY` env var).

3. **Service Worker.** Extended existing `sw.js` with `push` and `notificationclick` event handlers rather than creating a separate file. Keeps a single SW registration.

4. **Frontend pattern.** New `features/notifications/` module with TanStack Query hooks (`useNotificationPreferences`, `usePushSubscription`). Page at `/profile/notifications` — user-level, not admin-restricted.

5. **No email/push delivery wiring yet.** The `NotificationService.BroadcastJobNotificationAsync` currently only creates in-app DB records and fires SignalR. Actual email sending (SMTP) and web push dispatch (via WebPush library) are deferred to phase 2. The infrastructure (subscriptions, preferences) is ready.

### What's NOT included

- SMS, Slack, Discord channels
- Actual SMTP email sending
- Actual web push payload dispatch (needs WebPush NuGet + VAPID private key)
- `farm_alert` / low filament event types (only job events covered)

### Migration

- `AddPushSubscriptions` migration for both PostgreSQL and SqlServer
- Creates `PushSubscriptions` table with FK to `Users`
