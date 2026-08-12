# Slicer Configuration for PrintFarmer

This guide explains how to configure popular slicers (PrusaSlicer, OrcaSlicer) to upload G-code files directly to PrintFarmer using the OctoPrint-compatible API.

## Overview

PrintFarmer implements OctoPrint-compatible API endpoints that allow slicers to upload files directly to the print farm management system. Files uploaded through slicers are automatically added to the print queue and require approval before printing begins.

## Supported Slicers

- **PrusaSlicer** (2.0+)
- **OrcaSlicer**
- **SuperSlicer**
- Any slicer that supports OctoPrint integration

## API Endpoints

PrintFarmer provides the following OctoPrint-compatible endpoints required for slicer integration:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/octoprint/version` | GET | Returns server version information (required by slicers for compatibility check) |
| `/api/octoprint/server` | GET | Returns server status |
| `/api/octoprint/files/local` | POST | Uploads a new G-code file with optional auto-dispatch |

These are the minimal endpoints required for PrusaSlicer, OrcaSlicer, and SuperSlicer to upload files to PrintFarmer.

## Configuration Steps

### 1. Generate API Key

Before configuring your slicer, you need to generate an API key in PrintFarmer:

1. Log into PrintFarmer web interface
2. Navigate to **Settings** → **API Keys**
3. Click **Generate New API Key**
4. Give the key a descriptive name (e.g., "PrusaSlicer on Workstation")
5. Copy the generated API key (you won't be able to see it again)

> **Note:** API keys created here default to the **OctoPrint** purpose, which is what slicers need
> for OctoPrint-compatible uploads. A separate **Desktop** purpose exists for the PrintFarmer
> Desktop app; Desktop-purpose keys require explicit scopes and receive a 90-day expiry by
> default, and are never valid for slicer uploads. See
> [Desktop API-Key Exchange](#desktop-api-key-exchange) below for how Desktop-purpose keys are
> exchanged for a short-lived token. Provider migrations and the reserved authentication,
> audit, expiry, scope, and redaction test tranche remain in #839.

### 2. Configure PrusaSlicer

1. Open PrusaSlicer
2. Go to **Configuration** → **Preferences** → **OctoPrint/PrusaLink**
3. Click **Add** to create a new printer connection
4. Fill in the following fields:
   - **Name**: `PrintFarmer` (or any descriptive name)
   - **Hostname/IP**: `your-printfarmer-server.local` or IP address
   - **Port**: `5245` (default PrintFarmer API port)
   - **API Key**: Paste the API key you generated earlier
   - **HTTPS**: Uncheck (unless you've configured HTTPS)
5. Click **Test** to verify the connection
6. Click **OK** to save

### 3. Configure OrcaSlicer

1. Open OrcaSlicer
2. Go to **Preferences** → **Network**
3. In the **OctoPrint** section, click **Add**
4. Fill in the following fields:
   - **Printer Name**: `PrintFarmer`
   - **Host**: `http://your-printfarmer-server.local:5245` or `http://IP:5245`
   - **API Key**: Paste the API key you generated earlier
5. Click **Test** to verify the connection
6. Click **OK** to save

### 4. Configure SuperSlicer

Configuration is identical to PrusaSlicer:

1. Go to **Configuration** → **Preferences** → **Physical Printers**
2. Add a new OctoPrint printer with PrintFarmer details
3. Test and save the connection

## Usage

### Uploading Files

After configuring your slicer:

1. Slice your 3D model as usual
2. Instead of **Export G-code**, click **Send to OctoPrint** (or similar option depending on slicer)
3. Select your PrintFarmer connection
4. Optionally check **Start print after upload** (file will be queued for approval)
5. Click **Send**

The file will be uploaded to PrintFarmer and appear in the G-code library.

### Print Approval Workflow

When you upload a file with "Start print after upload" enabled:

1. File is uploaded to PrintFarmer
2. A print job is created in **Pending Approval** state
3. Navigate to **Print Approvals** in PrintFarmer web interface
4. Review the uploaded file details
5. Click **Approve** to add it to the print queue
6. Optionally assign a specific printer when approving
7. The job will be scheduled for printing once a printer becomes available

Without "Start print after upload", files are simply added to the library and can be queued manually later.

## Troubleshooting

### Connection Test Fails

**Error**: "Could not connect to OctoPrint"

**Solutions**:
- Verify PrintFarmer API server is running: `curl http://your-server:5245/api/octoprint/version`
- Check firewall rules allow connections to port 5245
- Ensure you're using `http://` not `https://` unless HTTPS is configured
- Verify the API key is correct and hasn't been revoked

### Upload Fails

**Error**: "Unauthorized" or "Invalid API key"

**Solutions**:
- Regenerate your API key and update slicer configuration
- Check that the API key hasn't expired or been deleted
- Verify the API key has upload permissions

### Files Not Appearing

**Solutions**:
- Check the **G-code Library** in PrintFarmer web interface
- Verify you have sufficient storage quota
- Check server logs for upload errors: `docker logs printfarmer-api`

### Rate Limiting

If you're uploading many files quickly, you may hit rate limits.

**Error**: "Rate limit exceeded" (HTTP 429)

**Solutions**:
- Wait 1 minute before retrying
- Adjust rate limits in PrintFarmer settings (admin only)
- Reduce concurrent uploads

## API Reference

### Upload Endpoint

```http
POST /api/octoprint/files/local?print=true&printerId=<guid>
Headers:
  X-Api-Key: your-api-key-here
  Content-Type: multipart/form-data
Body:
  file: <binary G-code file>
```

**Parameters**:
- `print` (optional, default: false) - If true, creates a print job immediately
- `printerId` (optional) - Specific printer GUID to assign the job to

**Response** (with print=true):
```json
{
  "file": {
    "fileName": "model.gcode",
    "fileSize": 1234567,
    "gcodeFileId": "guid-here"
  },
  "jobId": "job-guid",
  "approvalId": "approval-guid",
  "status": "PendingApproval"
}
```

**Note**: File management (listing, deleting) should be done through the PrintFarmer web interface, not through the OctoPrint API.

## Security Notes

- **API keys are sensitive**: Treat them like passwords. Don't share or commit them to version control.
- **Use HTTPS in production**: Configure HTTPS for PrintFarmer in production environments to encrypt API keys in transit.
- **Rate limiting**: PrintFarmer enforces rate limits to prevent abuse. Default is 60 uploads per minute per API key.
- **Audit logging**: All uploads via API are logged with API key identifier for traceability.

## Advanced Configuration

### Custom Ports

If PrintFarmer is running on a non-standard port, update the hostname/IP in your slicer configuration:
- Instead of `your-server.local`, use `your-server.local:8080`
- Ensure the port number matches your PrintFarmer deployment

### Reverse Proxy Setup

When using a reverse proxy (Nginx, Traefik):
- Configure the proxy to forward `/api/octoprint/*` to the PrintFarmer API server
- Ensure WebSocket support is enabled for SignalR
- Set appropriate timeouts for large file uploads
- Update slicer configuration to use the proxy hostname/port

### Multiple Printer Farms

To manage multiple PrintFarmer instances:
1. Generate separate API keys for each farm
2. Create separate printer connections in your slicer for each farm
3. Give each connection a descriptive name (e.g., "PrintFarmer - Office", "PrintFarmer - Workshop")
4. Select the appropriate connection when uploading

## See Also

- [API Documentation](API.md) - Complete PrintFarmer API reference
- [Print Approval Workflow](#print-approval-workflow) - Upload and approval behavior
- [OctoPrint API Specification](https://docs.octoprint.org/en/master/api/) - Original OctoPrint API

## Desktop API-Key Exchange

The PrintFarmer Desktop app authenticates with a **Desktop**-purpose API key (see the note under
[Generate API Key](#1-generate-api-key)), but never sends that key on every request. Instead it
exchanges the key once for a short-lived JWT, which is what the main API and the standalone
slicer host both accept for model/library requests.

### Exchange Endpoint

```http
POST /api/auth/api-key/exchange
Content-Type: application/json

{
  "apiKey": "your-desktop-api-key-here"
}
```

**Response** (200 OK):

```json
{
  "token": "eyJhbGciOi...",
  "expiresAt": "2026-01-20T12:15:00Z",
  "scopes": ["ModelRead", "CalibrationRead"]
}
```

`scopes` lists the **effective** scopes granted to this token, which may be a subset of the key's
stored scopes if the owner has since lost a permission — see
[Anti-self-escalation](#anti-self-escalation).

**Response** (401 Unauthorized) - returned uniformly for a missing, malformed, unknown, revoked,
expired, wrong-purpose (non-Desktop), under-scoped (no scopes granted), or fully-revoked (no scope
survives the owner-authorization intersection) key, and never reveals which of these applies:

```json
{
  "error": "Invalid API key"
}
```

The endpoint is rate-limited per client IP address (`RateLimiting:Authentication:MaxApiKeyExchangeAttemptsPerMinute`,
default 5/minute) to resist brute-force and enumeration attempts, and every exchange attempt
(success or failure) is recorded in the authentication audit log. The raw API key, its hash, and
the issued JWT are never written to logs or audit records.

### 401 vs 403, and recovering from either

The distinction is load-bearing for a desktop client:

| Condition | Status | What the client should do |
|---|---|---|
| Exchange token expired (≤15 min lifetime) | `401` | Exchange the API key again. |
| All the owner's tokens force-revoked (`ALL_TOKENS_` marker) | `401` | Exchange again; the key itself is unaffected. |
| Token valid but the key lacks the required scope/permission | `403` | Stop retrying — the key was never granted that authority. |

The `ALL_TOKENS_` row describes the main API. The standalone slicer host does not check the
revocation marker ([#1469](https://github.com/OlyForge3D/PrintFarmer/issues/1469)), so in a split
deployment a revoked token keeps working on slicer-host routes until it expires — at most 15
minutes for an exchange token.

Re-exchange succeeds only while both the key and its owner remain valid: a deactivated key, an
expired key, a deactivated owner, or an owner who has lost every mapped permission all return the
uniform `401 Invalid API key` from the exchange endpoint itself.

"Client IP" is `HttpContext.Connection.RemoteIpAddress`. When PrintFarmer runs behind a reverse
proxy (nginx, Traefik, IIS, etc.) you must enable and configure `ForwardedHeaders` so the framework
rewrites the connection address from `X-Forwarded-For` — otherwise the rate-limit key will be the
proxy address (or `unknown`) and every request will collide in the same bucket. `X-Forwarded-For`
sent directly by an untrusted caller is ignored (see [`docs/DEPLOYMENT.md`](DEPLOYMENT.md) for the
`ForwardedHeaders` configuration surface).

### Token Claims and Lifetime

The issued token is a normal JWT signed with the same `Jwt:Key`/`Jwt:Issuer`/`Jwt:Audience`
configuration as login tokens, so it validates identically wherever those settings match, but it
carries a deliberately minimal claim set: the owning user's identity, a `token_use=desktop_exchange`
marker, the API key's ID, one `scope` claim per **effective** scope, and one `permission` claim per
permission-backed effective scope (see [Scopes and permissions](#scopes-and-permissions)).

**A Desktop-exchange token never carries a role claim** — not even when its owner is a
`farm_admin`. There is therefore no admin bypass on an exchanged token: an admin-owned key is
limited to exactly the scopes it was created with.

Its lifetime is configurable via `Jwt:DesktopExchangeLifetimeMinutes` (default 15 minutes) and is
**clamped to a hard ceiling of 15 minutes** — a larger configured value is reduced and the clamp is
logged. The lifetime is independent of, and much shorter than, the API key's own 90-day expiry.

### Scopes and permissions

A Desktop key carries explicitly selected scopes. They fall into four groups:

| Group | Scopes | How they authorize |
|---|---|---|
| Model/library | `ModelRead`, `ModelWrite`, `LibrarySync` | Scope policies only. **Never** become permission claims. |
| Calibration | `CalibrationRead`, `CalibrationCreate`, `CalibrationUpdate`, `CalibrationDelete`, `CalibrationGenerate`, `CalibrationPublish` | Each maps to exactly one `calibration:*` permission claim. |
| Slicing | `SlicingSubmit`, `SlicingReadArtifact` | `slicing:submit`, `slicing:read-artifact`. |
| Print queue | `QueueRead`, `QueueWrite`, `QueueStart`, `QueueCancel`, `QueueAcknowledgeBedClear` | The matching `queue:*` permission claim. |

`queue:reconcile`, `slicing:promote`, `dispatch-settings:manage`, and `obico:manage` are
deliberately **not** reachable from any Desktop scope.

> **What `SlicingSubmit` actually reaches.** `slicing:submit` is a broad, pre-existing permission:
> the slicer host's `ProfilesController` is gated by it at the class level, so a key holding this
> scope can **read and enumerate the slicer profile catalog** (list, hierarchy, schemas, per-machine
> process/filament queries), and it also authorizes **uploading the G-code artifact for a slice job
> the caller owns** (`POST /api/artifacts/{jobId}`, additionally constrained by per-job ownership).
> That reach is required for submission and is not narrowed here.
> Profile-state **mutation** (`POST /api/slicer/profiles/upload`, `POST .../clone`,
> `PUT .../custom/{id}`) additionally requires an interactive session, so a Desktop key can never
> modify profile state — see
> [Credential management requires an interactive session](#credential-management-requires-an-interactive-session).
> Every other mutating route on that controller was already `farm_admin`-only.

The mapping lives in one place — `DesktopScopePermissionMap` — which both key creation and token
exchange consume, so the two can never drift.

**Selecting scopes.** Prefer the canonical `scopeNames` array on
`POST /api/users/{userId}/apikeys`:

```json
{
  "name": "Desktop calibration client",
  "purpose": "Desktop",
  "scopeNames": ["ModelRead", "CalibrationRead", "SlicingSubmit"]
}
```

The legacy `scopes` flags field still works for existing clients, but supplying both is rejected.
Composite aliases (`"All"`) are not accepted in `scopeNames`. Responses include both `scopes`
(legacy) and `scopeNames` (canonical); **prefer `scopeNames`** — the flags field renders the exact
value `7` as the single name `"All"`, which reads like "every privilege" but actually means only
the three model/library scopes.

**Dependencies.** Some scopes are useless alone and are rejected at creation with a `400`:
`CalibrationGenerate` also requires `CalibrationRead` and `SlicingSubmit`; the other calibration
scopes require `CalibrationRead`; the queue mutation scopes require `QueueRead`; and
`QueueAcknowledgeBedClear` additionally requires `QueueStart`, because the bed-clear routes check
both. `SlicingReadArtifact` is deliberately **not** a generation prerequisite — generation submits a
slice job and polls calibration orchestration, and promotion is server-side, so a generating client
never downloads artifact bytes. Select that scope only for a client that genuinely does.

### Anti-self-escalation

A key can never grant more authority than its owner has.

- **At creation**, every permission-backed scope is checked against the **target owner's** live
  database roles and grants — never against the caller's JWT claims. A `farm_admin` caller cannot
  mint a calibration key for an unprivileged user. Unauthorized scopes are rejected with a `400`
  naming the missing permissions.
- **At exchange**, the owner's authorization is re-resolved and intersected with the key's stored
  flags to produce a single **effective mask**. The `scope` claims, the `permission` claims, and
  the `scopes` array in the response all derive from that one mask, so a scope can never appear
  without its permission.
- **Revocation downgrades rather than breaks.** If the owner has lost a permission, only the
  affected scopes are dropped; unrelated model/library scopes are retained, so a revoked
  calibration role does not break desktop model sync. The exchange fails only when nothing
  survives. The requested, effective, and dropped scope names and the granted permissions are
  recorded in the audit log (never the key, its hash, or the token). Revocation therefore takes
  effect on the next exchange, bounded by the ≤15-minute token lifetime.

> **Who can own a calibration-scoped key.** The owner-authority intersection above is mandatory and
> unconditional. Which users satisfy it depends on how roles are provisioned:
>
> - **`farm_admin` members** always do.
> - **A custom role** carrying the exact mapped permission (e.g. `calibration:read`) also
>   satisfies it, as does a same-resource **`{resource}:admin`** grant — `calibration:admin`
>   implies every `calibration:*` action, exactly as it does at the enforcement points. The
>   implication never crosses resources: `calibration:admin` grants no queue or slicing scope.
>   Roles and their permission grants are managed through the role and role-permission APIs.
> - **An explicit deny wins.** If any of the owner's active roles denies a `resource:action` pair,
>   the corresponding scope is refused at creation and dropped at exchange, even when another role
>   grants it and even when the owner holds `{resource}:admin`. See
>   [`docs/ROLE_PERMISSION_PRECEDENCE.md`](ROLE_PERMISSION_PRECEDENCE.md). The `farm_admin` role
>   bypass is deliberately not subject to this.
> - **A stock `farm_user`** holds the calibration, slicing, and queue permissions these
>   scopes map to, seeded by `DatabaseInitializer` as of
>   [#1473](https://github.com/OlyForge3D/PrintFarmer/pull/1473), so an ordinary user can
>   own a working Desktop calibration key. A user whose role lacks a permission — or who is
>   explicitly denied it — still gets `400` at creation and has that scope dropped at
>   exchange. `queue:reconcile` and `dispatch-settings:manage` are not seeded to `farm_user`
>   and no scope maps to them, so a Desktop key can never reach those routes; an
>   administrator may still grant them to a custom role for use through a normal session.
>
> An admin-owned exchanged token still carries no role claim and only the scopes explicitly
> selected on that key.
>
> **Revocation latency differs by token type.** An exchanged Desktop token is capped at 15 minutes,
> so a permission change takes effect on the next exchange. An ordinary login JWT carries its
> permission claims for up to 7 days, but it is not left stale: changing a role's permission grants
> ([#1471](https://github.com/OlyForge3D/PrintFarmer/pull/1471)) or a user's role assignments
> ([#1475](https://github.com/OlyForge3D/PrintFarmer/pull/1475)) revokes the affected users' live
> tokens through the shared `ALL_TOKENS_` path. **That revocation is enforced by the main API
> only.** The standalone slicer host does not check the revocation marker, so in a split deployment
> a revoked token keeps working on slicer-host routes until it expires — tracked as
> [#1469](https://github.com/OlyForge3D/PrintFarmer/issues/1469). Capping the exchange token at 15
> minutes bounds that window for Desktop clients; a login JWT reaching those routes is bounded only
> by its own expiry. Nothing in this feature extends the exchange token's lifetime.

### Credential management requires an interactive session

Credential-management endpoints reject Desktop-exchange tokens with a `403`. An exchange token is
a short-lived bearer credential held on an end-user machine and carries the owner's identity, so
plain `[Authorize]` would let a stolen token bootstrap a **durable** credential — the same
laundering pattern in two places:

| Endpoint | Why it is covered |
|---|---|
| `/api/users/{userId}/apikeys` (all verbs) and `/api/apikeys/settings` | Minting a replacement API key valid for up to a year, with scopes of the attacker's choosing. |
| `POST /api/auth/passkey/register/begin` and `.../complete` | Registering an attacker-controlled passkey, then using it to obtain a full interactive login. |
| `GET`/`PATCH`/`DELETE /api/auth/passkey/credentials[/{id}]` | Enumerating, renaming, or deleting the owner's existing passkeys. |
| `POST /api/slicer/profiles/upload`, `POST /api/slicer/profiles/clone`, `PUT /api/slicer/profiles/custom/{id}` | `ProfilesController` is class-gated by the broad `slicing:submit` permission, which a calibration-generation key legitimately holds. Mutating profile state is not part of that intent. |

These are the complete set of non-`farm_admin` mutating routes on `ProfilesController`; every other
mutating route there was already `farm_admin`-only. The policy is registered in **both** hosts (the
main API in `AuthenticationStartup`, and the standalone slicer host in `Farm.Slicer.Host/Program.cs`)
so the deny holds in monolithic and microservices deployments alike.

Normal login sessions — including the admin UI — are unaffected: the rule denies only principals
carrying `token_use=desktop_exchange`, and every other principal passes through untouched. The
handler calls `Fail()` rather than merely declining to succeed, so the Development-only
`Security:DevModeBypassAuth` GET bypass cannot re-open these endpoints. The passkey **login**
ceremony (`passkey/login/begin` and `.../complete`) stays anonymous, since the signed assertion is
itself the credential being verified. Password change is not affected because it already requires
the current password.

### Authorization Policies

`ModelRead`, `ModelWrite`, and `LibrarySync` authorization policies gate the corresponding
model/library endpoints on both the main API (`GcodeLibraryController`) and the slicer host
(`Model3DFilesController`). A normal login/session token is unaffected by these policies (they
only constrain principals carrying the `token_use=desktop_exchange` claim), and legacy/unscoped
**OctoPrint**-purpose keys can never obtain a Desktop-exchange token, since the exchange endpoint
rejects any key whose `Purpose` is not `Desktop`.

Calibration, slicing, and queue endpoints are gated by ordinary `permission` claims on both hosts
(`RequirePermissionAttribute` on the main API, `ClaimsPermissionValidator` on the slicer host), so
an exchanged token reaches them through exactly the same check as a login session.

### Database Migrations

The `ApiKeys` table's `Purpose` and `Scopes` columns are provisioned by the initial EF Core
migration for both the PostgreSQL and SQL Server providers (`src/migrations/Farm.Migrations.PostgreSQL`
and `src/migrations/Farm.Migrations.SqlServer`). Both columns default to `0`
(`ApiKeyPurpose.OctoPrint` / `ApiKeyScope.None`), so every pre-existing key upgrades in place as an
unscoped, OctoPrint-purpose key — it keeps working for slicer uploads exactly as before and is
never implicitly granted Desktop access.

**Adding calibration/slicing/queue scopes requires no migration.** `Scopes` is already an `int`
column and the new values are additional bits within it. Every key stored as `1`, `2`, `4`, or the
frozen aggregate `7` continues to mean exactly what it meant when issued and yields **zero**
calibration, slicing, or queue permissions.

**Scopes are immutable.** There is no endpoint to change an existing key's scopes, and rotation
preserves them exactly. To use the new scopes you must **issue a new key**. The exchange request
body is unchanged (`{ "apiKey": "..." }`), so no desktop client change is required to exchange one.

### Slicer Host Configuration

To accept Desktop-exchanged tokens, the standalone slicer host needs the same JWT signing
configuration as the main API - see `Jwt__Key`, `Jwt__Issuer`, and `Jwt__Audience` in
`scripts/docker/compose-templates/docker-compose.slicer-host.yml`. **`Jwt__Key` is required.**
`Farm.Slicer.Host/Program.cs` reads `Jwt:Key` and throws at startup when it is absent, then
registers JWT bearer authentication unconditionally. There is no length-based fallback and no
auto-admin standalone principal - earlier revisions of this guide described one, but no such code
path exists. The compose template enforces this with Docker Compose's required-variable
interpolation (`${Jwt__Key:?...}`), so rendering the compose file with `Jwt__Key` unset fails fast
with a clear error instead of silently starting the container without a signing key; set it
explicitly to the same value as the `api` service.

**Security note:** never set `Jwt__Key` in either service to the well-known placeholder value that
appears anywhere in this repository's committed example configuration - a shared or known signing
key lets anyone mint their own valid tokens. Always generate a real random secret (e.g.
`openssl rand -base64 48`) and set the identical value for both the `api` and `slicer-host`
services via environment variables or a secrets manager, never by committing it to source
control.

### Calibration Profile Resolution (split deployments)

The slicer host owns the calibration profile store, so in split/microservices deployments the main
API resolves the explicitly selected machine/process/filament profiles over an authenticated
internal hop:

- **Endpoint:** `POST /api/slicer/calibration/resolved-profiles` on the slicer host. The body is
  exactly `machineProfileId`, `processProfileId` and `filamentProfileId`; any additional member
  (including `userId` or `bypassOwnership`) is rejected with `400 invalid_profile_resolution_request`.
- **Authorization:** `[Authorize]` plus `calibration:read`. The ownership scope — and the audited
  farm-admin bypass — is derived from the slicer host's own validated JWT, never from the request.
- **Availability probe:** `GET /healthz/calibration-resolver`, anonymous and data-free. It reports
  `Healthy` only when a resolver is registered and its store answers.
- **API side:** set `SlicerHost__BaseUrl` (compose default `http://slicer-host:5246`) on the `api`
  service and use the *same* `Jwt__Key`/`Jwt__Issuer`/`Jwt__Audience` as the slicer host. The API
  forwards the caller's own bearer token; it mints no service credential and logs neither the token
  nor the internal address.

A Desktop API-key exchange token satisfies `calibration:read` here **only** when its key was
explicitly provisioned with the `CalibrationRead` scope *and* the key's owner still holds
`calibration:read` at exchange time — the exchange then emits the mapped `permission` claim
alongside the `scope` claim. A key without that scope, and every legacy or model/library-only key
(including any stored as the frozen aggregate `All`/`7`), carries **no** permission claims and
cannot reach this endpoint; such clients must use a normal login/session token. As of
[#1473](https://github.com/OlyForge3D/PrintFarmer/pull/1473) a stock `farm_user` is seeded
with `calibration:read`, so the owner need not be a `farm_admin` member — see
[Scopes and permissions](#scopes-and-permissions). See
`docs/MICROSERVICES_DEPLOYMENT_GUIDE.md` for the full rollout and verification steps.

## Support

For issues or questions:
- Check PrintFarmer logs: `docker logs printfarmer-api`
- Open an issue on GitHub
- Check existing documentation in the `docs/` folder
