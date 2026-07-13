# Operator native push (F3 / #708) — backend architecture

Status: **implemented (backend/API stage)** on `feature/705-operator-redesign`.
Mobile client wiring is a separate stage; this document covers the server design
that Bishop / Hicks / Vasquez reviewed and gated.

## 1. Constraints and the topology decision

PrintFarmer is self-hosted per customer. OlyForge3D owns the App Store bundle
identifier and the associated APNs `.p8` provider key; that key must never be
distributed to a self-hosted install (Dallas triage on #708). At the same time
the backend must compile, run, and pass tests without any live credentials.

We resolve this with a **provider-abstract sender** chosen by configuration:

| Mode      | `NativePush__Mode` | Where APNs credentials live                    | Intended use                       |
|-----------|--------------------|------------------------------------------------|------------------------------------|
| `disabled`| (default / unset)  | nowhere                                        | fresh install, dev, CI             |
| `relay`   | `relay`            | OlyForge3D-hosted relay (backend never sees)   | production TestFlight / App Store  |
| `direct`  | `direct`           | local backend `.p8` (path or PEM)              | self-signed enterprise / dev-cert  |

The default is **disabled**. The recommended production topology is **relay**;
`direct` exists so an operator who signs their own build can bring their own
provider key without a code change.

`INativePushSender` accepts a typed `NativePushEnvelope` and returns a typed
`NativePushDispatchResult`. There is exactly one production sender wired at any
time (chosen by `Mode`); the disabled sender is a no-op that returns
`Success=false, Reason=notConfigured` and logs at debug level.

### Credential handling

- Secrets are only read from configuration (env / user-secrets / mounted files).
  Nothing is committed to the repository, nothing is echoed in logs.
- Direct APNs mode accepts the `.p8` either as a mounted file path
  (`NativePush__Apns__P8KeyPath`) or an inline PEM
  (`NativePush__Apns__P8KeyPem`). The path form is preferred and validated for
  existence + readability at startup.
- Relay mode uses a bearer token (`NativePush__RelayApiKey`) issued per
  installation by OlyForge3D. The relay endpoint URL is separate
  (`NativePush__RelayEndpoint`).
- Deployment templates and `.env.example` document the keys but never contain
  live values. See `scripts/docker/env.template`.

### JWT / expiry / retry (direct mode)

- Provider JWT is ES256, `iss=TeamId`, `iat=now`, cached and rotated at ≤ 50
  minutes.
- `apns-topic = BundleId`, `apns-priority = 10` for alerts, `apns-expiration`
  is the earlier of `deadlineAt` and `now + 30 min` for critical items,
  `now + 5 min` for informational.
- Retries: exponential backoff `1s → 5s → 15s`, total three attempts, only on
  5xx / network errors. `4xx` responses are terminal.
- Response classification:
  - `200 OK` → success.
  - `410 Gone` OR APNs body reason `BadDeviceToken` / `Unregistered` → token
    is permanently invalidated; the row is hard-deleted from `DeviceTokens`
    and the fleet-side counter is not touched.
  - `408 Request Timeout`, `429 Too Many Requests`, `5xx`, socket errors,
    or client-side timeouts → **transient**. The dispatcher retries per
    backoff and, critically, does **not** increment the per-token
    consecutive-failure counter — so an APNs / relay outage cannot
    soft-deactivate the entire fleet.
  - Other `4xx` (e.g. `400 BadCollapseId`, `403 InvalidProviderToken`,
    `413 PayloadTooLarge`) → **terminal, retain token**. The dispatch is
    counted as a failure for that user's rate-limit accounting but the
    token row is preserved so operator-visible tokens do not disappear on
    a payload bug.
  - HTTP status `404` from the relay is treated as terminal-retain (not
    an invalidation) because `404` is not part of the APNs invalidation
    contract; a relay that wants to invalidate a token must return `410`.

The direct-APNs client sends all requests as **HTTP/2 with
`RequestVersionOrHigher`** — APNs will reject HTTP/1.1 with a stream
error. The named `HttpClient` also sets `DefaultRequestVersion` /
`DefaultVersionPolicy` at DI configuration time as defence-in-depth.

Telemetry note: OpenTelemetry `http.client` spans for the direct-APNs
sender are decorated by `EnrichWithHttpRequestMessage` /
`EnrichWithHttpResponseMessage` callbacks registered on
`AddHttpClientInstrumentation` (see `TelemetryStartup`). Any tag whose
name is `url.full`, `http.url`, `url.path`, or `http.request.path` and
whose value matches `/3/device/<raw-token>` on an APNs host
(`api.push.apple.com`, `api.sandbox.push.apple.com`,
`api.development.push.apple.com`) is rewritten to
`/3/device/<REDACTED>` on the actual HTTP-client activity. The response
enricher re-applies redaction to the same four tags on span end so
processors that read tags at end-of-span (rather than start) also see
redacted values.

In addition, the two named APNs `HttpClient`s (`NativePushDirect` and
`NativePushRelay`) call `.RemoveAllLoggers()` in DI configuration.
`IHttpClientFactory`'s default `LoggingScopeHttpMessageHandler` writes
the outbound request URI at `Information` — the raw device token would
otherwise land in stdout logs even with the OTel span redacted (spans
and logs are separate sinks). Removing the loggers for these two named
clients closes that leak; the redacted OTel span remains the sole
audit trail for these outbound requests.

Together, the enricher and the logger scrub guarantee raw device
tokens never leave the process in traces or logs. A prior
implementation used a `DelegatingHandler`, but that runs above the
primary handler that creates the HTTP-client activity —
`Activity.Current` was null there and the redaction had no effect. The
enricher path is the only one guaranteed to reach the exported span.

### Deduplication and rate-limiting

The delivery service applies two guards in-process before the sender is
invoked:

1. A 60-second LRU dedupe on `(userId, attentionItemId, changeKind)` — a
   burst of `attentionchanged` fires for the same source collapses to one
   push per user.
2. A token bucket per `(printerId, attentionKind)` at 1 push per 30s. Excess
   is dropped (not queued) and counted.

Both guards emit metrics (`native_push_deduplicated`, `native_push_rate_limited`).

## 2. Actionable categories and deep links

The category identifiers and action ids are stable across the mobile app and
the server. String enum wire values are camelCase per the API contract.

| `attentionKind` | APNs `category`     | actions on lock-screen              | primary deep link                                                                     |
|-----------------|---------------------|-------------------------------------|---------------------------------------------------------------------------------------|
| `failure`       | `PRINTER_FAILURE`   | `PAUSE`, `CANCEL`, `SNOOZE_15`      | `printfarmer://attention/{attentionItemId}`                                           |
| `offline`       | `PRINTER_OFFLINE`   | `SNOOZE_15`                         | `printfarmer://printer/{printerId}`                                                   |
| `maintenance`   | `MAINTENANCE_DUE`   | `ACKNOWLEDGE`, `SNOOZE_15`          | `printfarmer://attention/{attentionItemId}`                                           |
| `harvest`       | `HARVEST_READY`     | (tap only)                          | `printfarmer://attention/{attentionItemId}`                                           |
| `runout`        | `FILAMENT_RUNOUT`   | `OPEN_SWAP`, `SNOOZE_15`            | `printfarmer://printer/{printerId}/swap/{toolheadIndex}?jobId={jobId}`                |

Categories and action ids are also exposed at `GET /api/notifications/attention-categories`
so the mobile client can register `UNNotificationCategory`s from server metadata
rather than a hard-coded list. That endpoint is the authoritative contract used
by #716 (React preferences) and by Gorman/Hudson's iOS stages.

APS payload shape (identical across relay and direct modes):

```json
{
  "aps": {
    "alert": { "title": "...", "subtitle": "...", "body": "..." },
    "sound": "default",
    "badge": 1,
    "category": "PRINTER_FAILURE",
    "thread-id": "printer:{printerId}",
    "mutable-content": 1
  },
  "attentionItemId": "failure:{incidentId}",
  "attentionKind": "failure",
  "printerId": "{printerId}",
  "jobId": "{jobId}",
  "toolheadIndex": 0,
  "deepLink": "printfarmer://attention/{attentionItemId}"
}
```

## 3. Double gate on `nativePushEnabled`

The feature is doubly gated on `IOperatorFeatureGate.IsEnabled(OperatorFeature.NativePush)`
per #725:

1. **Registration API** — `POST/DELETE /api/notifications/device-tokens` return
   `404 ProblemDetails` with `code=featureDisabled` and perform no writes when
   the flag is off.
2. **Queue path** — `NativePushDeliveryService.HandleAttentionChangeAsync`
   re-reads the gate before enumerating users; if disabled, returns without
   touching the DB or the sender.
3. **Send path** — the same delivery service re-reads the gate immediately
   before calling `INativePushSender.SendAsync`; if the flag flipped mid-flight,
   the outbound is dropped with a `native_push_dropped_disabled` metric.

`OperatorFeatures__nativePushEnabled=false` (env / hard-disable) is the
emergency rollback per #725. Runtime toggles from the Unified Settings page
take effect on the next request without a restart.

## 4. Registration retention on disable

Disabling the flag never mutates `DeviceTokens`. Rows are only removed when:

- APNs signals a permanently invalid token (`410 Gone` /
  `BadDeviceToken` / `Unregistered`), or
- The user unregisters (`DELETE /api/notifications/device-tokens`), or
- The user is deleted (cascade FK).

Consecutive-failure soft-deactivation (`IsActive=false`) triggers after 5
consecutive send failures for the same token; the row is retained for
diagnostics and can be reactivated on next successful registration upsert.

## 5. Persistence

New entity `DeviceToken` (main app, `AppDbContext`):

| Column                     | Type            | Notes                                                     |
|----------------------------|------------------|-----------------------------------------------------------|
| `Id`                       | `uuid`           | PK.                                                       |
| `UserId`                   | `uuid`           | FK → `Users(Id)`, cascade delete.                         |
| `InstallationId`           | `varchar(64)`    | Per-server installation id from the mobile app.           |
| `Token`                    | `varchar(256)`   | Provider-issued device token (APNs hex today).            |
| `Platform`                 | `varchar(16)`    | `ios` today. `android` reserved.                          |
| `Environment`              | `varchar(16)`    | `development` (sandbox) or `production`.                  |
| `AppBundleId`              | `varchar(128)`   | Bundle id captured on registration.                        |
| `CreatedAt`                | `timestamptz`    | UTC creation.                                              |
| `LastUsedAt`               | `timestamptz?`   | Last successful send (or last upsert).                     |
| `LastFailureAt`            | `timestamptz?`   | Last transient send failure.                               |
| `ConsecutiveFailureCount`  | `int`            | Reset on success; deactivates at 5.                        |
| `IsActive`                 | `bool`           | Soft-deactivated tokens are skipped by fan-out.            |

Indexes:

- Unique `(UserId, InstallationId)` — one token per installation per user; upsert.
- Non-unique `(Token)` for fast reverse lookup on inbound `410 Gone`.

Extension to `NotificationPreferences` (same entity, one new column):

| Column                                   | Type              | Notes                                                                  |
|------------------------------------------|-------------------|------------------------------------------------------------------------|
| `AttentionPushCategoryPreferencesJson`   | `text` / `nvarchar(max)` nullable | JSON opt-in map keyed by `attentionKind` string.       |

Absent / null / malformed JSON = all attention categories opted **in**. The
existing browser-push/email/telegram matrix is untouched; native push has its
own axis and does not corrupt legacy fields.

Migrations land in both `Farm.Migrations.PostgreSQL` and `Farm.Migrations.SqlServer`
against `AppDbContext`. SQLite local dev picks up the entity via `EnsureCreated`.

## 6. Attention pipeline integration

`AttentionBroadcaster` remains the single source of `attentionchanged` events.
After the SignalR broadcast, it invokes `INativePushDispatcher.DispatchAsync`
(resolved via `IServiceScopeFactory` — the pattern documented in
`docs/OPERATOR_FEATURE_GATES.md`). Dispatch failure never breaks the broadcast
path; every exception is logged and swallowed. See
`AttentionBroadcaster.cs` for the invocation site.

The dispatcher:

1. Gates on `NativePush` (queue-side gate).
2. Enumerates users with active device tokens
   (`IDeviceTokenRepository.GetActiveTokenOwnersAsync`).
3. Per user, resolves the item via `IAttentionService.FindItemAsync(userId,
   itemId, ...)`. Users who cannot see the item (snoozed, resolved, or
   maintenance without admin role) get no push.
4. Applies per-user category opt-in from `NotificationPreferences`.
5. Applies dedupe + rate-limit.
6. Re-reads the gate (send-side gate) and calls `INativePushSender.SendAsync`
   for each active token.

For `Resolved` events, the dispatcher sends a minimal content-available payload
that lets the client dismiss the corresponding notification without producing a
new alert.

## 7. Observability

Meter name: `Farm.Infrastructure.Services.Notifications.NativePush`.

| Instrument                       | Kind      | Tags                                       |
|----------------------------------|-----------|--------------------------------------------|
| `native_push_attempts`           | counter   | `mode`, `platform`, `attentionKind`        |
| `native_push_success`            | counter   | `mode`, `platform`, `attentionKind`        |
| `native_push_failure`            | counter   | `mode`, `platform`, `attentionKind`, `reason` |
| `native_push_dropped_disabled`   | counter   | `stage` (`queue` \| `send`)                |
| `native_push_dropped_no_tokens`  | counter   | `attentionKind`                            |
| `native_push_token_invalidated`  | counter   | `platform`, `reason`                       |
| `native_push_retries`            | counter   | `platform`                                 |
| `native_push_rate_limited`       | counter   | `attentionKind`                            |
| `native_push_deduplicated`       | counter   | `attentionKind`                            |
| `native_push_send_ms`            | histogram | `mode`, `platform`                         |

Structured logs use `attentionItemId`, `changeKind`, `installationId`,
`deviceTokenId`. Raw provider tokens are never logged.

## 8. Deployment

`scripts/docker/env.template` documents the new configuration keys:

```dotenv
# Native push (F3 / #708). Default: disabled.
NativePush__Mode=disabled

# Relay mode (production)
# NativePush__Mode=relay
# NativePush__RelayEndpoint=https://push-relay.olyforge3d.com/v1/dispatch
# NativePush__RelayApiKey=<per-install bearer, obtained from OlyForge3D>

# Direct APNs mode (self-signed / enterprise). Prefer the file path form.
# NativePush__Mode=direct
# NativePush__Apns__TeamId=...
# NativePush__Apns__KeyId=...
# NativePush__Apns__BundleId=com.example.printfarmer
# NativePush__Apns__P8KeyPath=/secrets/apns.p8
# NativePush__Apns__Environment=production        # or development

# Emergency hard-disable (wins over the DB flag)
# OperatorFeatures__nativePushEnabled=false
```

Health probing: `GET /api/system/capabilities` continues to expose
`nativePushEnabled` (from #725); no separate healthcheck endpoint is added.

## 9. Rollback

- Runtime: admin flips `nativePushEnabled` off in the Unified Settings page.
  Next request stops queueing and sending; registration returns 404
  `featureDisabled`; existing tokens remain in the DB.
- Emergency: set `OperatorFeatures__nativePushEnabled=false` and reload
  config. Same behavior; wins over any DB value.
- Re-enable: flip the flag back on. No re-registration required; tokens
  resume immediately.

## 10. Shared notification-preference contract (dependency of #716)

The React and mobile clients share one preference matrix — one row per
`NotificationPreferenceEventType` × four channels
(`inApp`, `email`, `push`, `telegram`). Native push is the `push` column
for the attention rows added in this stage.

### Wire enum tokens

Production JSON serialization uses `new JsonStringEnumConverter()` with no
naming policy override (see `ControllerStartup.cs`), so enum members
serialize as their raw PascalCase names. This matches the existing React
type declarations (`src/Web/ReactApp/src/types/api.ts`, `NotificationPreferenceEventType`)
so the frontend does not need to be re-generated to consume the new tokens:

| Enum member         | JSON token         |
| ------------------- | ------------------ |
| `JobStarted`        | `JobStarted`       |
| `JobCompleted`      | `JobCompleted`     |
| `JobFailed`         | `JobFailed`        |
| `JobPaused`         | `JobPaused`        |
| `PrinterFailure`    | `PrinterFailure`   |
| `FilamentRunout`    | `FilamentRunout`   |
| `HarvestReady`      | `HarvestReady`     |
| `MaintenanceDue`    | `MaintenanceDue`   |
| `PrinterOffline`    | `PrinterOffline`   |

DTO shape (unchanged, per-row):
`{ eventType: string, inApp: bool, email: bool, push: bool, telegram: bool }`.

### Capability probe for backward-compatible clients

`GET /api/notifications/preferences/capabilities` — **canonical route**,
nested under `preferences` per existing controller convention (siblings:
`GET /api/notifications/preferences`, `PUT /api/notifications/preferences`,
`GET /api/notifications/attention-push-preferences`).
Anonymous — clients feature-detect before login →

```json
{ "supportedEventTypes": ["JobStarted", "JobCompleted", "JobFailed", "JobPaused",
                          "PrinterFailure", "FilamentRunout", "HarvestReady",
                          "MaintenanceDue", "PrinterOffline"] }
```

**Client contract for old servers:** an HTTP `404` from this endpoint
means the server predates this stage and only supports the legacy four
job tokens (`JobStarted`, `JobCompleted`, `JobFailed`, `JobPaused`).
Clients MUST NOT POST unknown enum values back to a server that returned
404 here. The response is enumerated on demand from the enum via a
`JsonStringEnumConverter` configured identically to the production one,
so the endpoint and the wire tokens cannot drift.

**Server rejects unknown enum values on write:** `PUT /api/notifications/preferences`
uses `JsonStringEnumConverter` which throws on unknown enum tokens; ASP.NET
Core turns that into a 400 ProblemDetails. A newer client MUST NOT send
enum values that were not advertised by `capabilities` for the current
server. Test `UnknownEnumToken_IsRejectedByDeserializer` locks this.

**Server GET always hydrates all 9 rows:** `GET /api/notifications/preferences`
returns 9 `eventChannelPreferences` rows regardless of whether the user
has a persisted preferences row. Legacy users whose row predates this
stage automatically observe the migration's column defaults (opt-in-safe:
`inApp=true`, `push=true`, `email=false`, `telegram=false` for the 5 new
attention rows). Test `LegacyUserWithNoPreferencesRow_HydratesAllNineRowsWithSafeDefaults`
locks this.

## 11. Non-goals for this stage

- No mobile / iOS code changes. Gorman and Hudson will consume this contract in
  a follow-up.
- No React preferences UI changes. #716 will consume the shared enum + the
  `GET /api/notifications/attention-categories` endpoint.
- Provisioning of live APNs / relay credentials is Parker's release/#724 scope.
