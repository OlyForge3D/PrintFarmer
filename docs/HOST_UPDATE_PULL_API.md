---
post_title: "Host-update daemon pull API contract"
author1: "Parker"
post_slug: "host-update-pull-api"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "security", "updates", "api"]
ai_note: "AI-assisted contract definition for issue #3115."
summary: "Request/response models, authentication, status codes, redaction and fail-closed rules for the enrolled host-update daemon pull API."
post_date: "2026-09-27"
---

## Status and scope

This is the API contract for issue
[#3115](https://github.com/OlyForge3D/PrintFarmer/issues/3115), a child of epic
[#2658](https://github.com/OlyForge3D/PrintFarmer/issues/2658). It implements the
readiness, approval and status surfaces designed in the
[host-update daemon security model](HOST_UPDATE_DAEMON_SECURITY.md) (#3113).

- **Defined, not served.** Route constants, DTOs, signing profiles and pure
  evaluators live in `src/infra`. No controller maps these routes. They stay
  unmapped until the daemon (#3114), the protected enrollment store and #2982
  recovery evidence exist and the owner authorizes enablement.
- **Automatic updates stay disabled.** `HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled`
  is hard-coded `false`. Every `Automatic` approval is denied with
  `automatic_updates_runtime_disabled`, and readiness reports
  `automaticUpdatesRuntimeEnabled: false`.
- **No schema change.** Nothing here adds tables or migrations.

Source files:

| Concern | File |
|---|---|
| DTOs and enums | `src/infra/Dtos/HostUpdatePullApprovalDtos.cs` |
| Routes, protocol constants, reason codes, identifiers | `src/infra/Services/HostUpdates/PullApproval/HostUpdateDaemonApiContract.cs` |
| Request signing and verification | `.../PullApproval/HostUpdateDaemonRequestSignature.cs` |
| Response signing and daemon-side verification | `.../PullApproval/HostUpdateDaemonResponseSigning.cs` |
| Approval evaluation and re-confirmation | `.../PullApproval/HostUpdatePullApprovalEvaluator.cs` |
| Readiness and status-report validation | `.../PullApproval/HostUpdatePullReadinessEvaluator.cs` |
| Strict installation identity read | `HostUpdateInstallationIdentity.ReadExistingStrict` in `src/infra/Services/HostUpdates/HostStateStorage.cs` |

## Endpoints

All daemon routes are under `/api/host-updates/daemon/v1`.

| Method | Route | Caller | Signed response payload |
|---|---|---|---|
| `GET` | `/enrollment` | Daemon (any enrolled key, including `Pending`) | `HostUpdateDaemonEnrollmentStatusDto` |
| `GET` | `/readiness` | Daemon (`Active` or `Rotating`) | `HostUpdateReadinessDto` |
| `GET` | `/approval` | Daemon (`Active` or `Rotating`) | `HostUpdatePullApprovalResponseDto` |
| `POST` | `/approvals/{approvalId}/confirm` | Daemon (`Active` or `Rotating`) | `HostUpdateApprovalConfirmationDto` |
| `POST` | `/status` | Daemon (`Active` or `Rotating`) | `HostUpdateDaemonStatusReportAckDto` |
| `GET` | `/api/admin/host-updates/daemon/status` | Operator, `farm_admin` JWT | `HostUpdateDaemonOperatorStatusDto` (unsigned JSON) |

## Serialization

- camelCase property names and string enums, exactly like every other API
  contract. `HostUpdateDaemonJson.Options` is the web default plus
  `JsonStringEnumConverter`, and each enum also carries
  `[JsonConverter(typeof(JsonStringEnumConverter))]`.
- Request bodies are capped at 64 KiB and lists at 16 items.
- Timestamps are ISO 8601 `DateTimeOffset` values.

## Authentication

### Daemon requests

Daemon routes do **not** use JWT or cookies. Each request carries a fixed
RFC 9421 profile signed with the daemon's ECDSA P-256 key:

- Label `pf`, tag `printfarmer-host-update-v1`, algorithm `ecdsa-p256-sha256`.
- Covered components, in order: `@method`, `@path`, `@query`,
  `content-digest`, `printfarmer-request-counter`, plus `created`, `nonce` and
  `keyid` parameters.
- Any other label, component list, algorithm or tag is rejected as
  unauthenticated.

Verification runs in a fixed order, so a forged or replayed request can never
advance state:

1. Signature and body digest against the enrolled public key (unknown key or
   bad signature: unauthenticated).
2. `created` within ±60 seconds.
3. Nonce not already seen for the key (replay).
4. Enrollment state: `Revoked`, `Quarantined` and `Pending` (except the
   enrollment poll) are authenticated rejections.
5. Key expiry.
6. Counter against the durable high-water mark. A lower counter with an
   earlier timestamp is stale; a lower counter with a later timestamp, or the
   same counter with a different digest, is fork evidence and quarantines the
   key.

Only an accepted request may commit its counter, timestamp, digest and nonce.

### Signed responses

Every daemon response is a `HostUpdateDaemonSignedResponseDto`:

```json
{
  "signedContent": "{\"protocolVersion\":1,\"responseType\":\"Approval\",...}",
  "responseKeyId": "api-response-key-0001",
  "algorithm": "EcdsaP256Sha256",
  "signature": "<base64url IEEE P1363 r||s>"
}
```

The signature covers `printfarmer-host-update-response-v1\n` plus the exact
UTF-8 bytes of `signedContent`, so no JSON canonicalization is needed. The
envelope echoes `installationId`, `keyId`, `enrollmentEpoch`, `requestNonce`,
`acknowledgedCounter` and `enrollmentStateRevision`.

The daemon discards unsigned, mis-signed, unpinned-key, wrong-nonce or stale
responses. It holds all work (`NeedsOperator`) when an authentic response shows
a changed installation or epoch, an acknowledged counter below its last value or
at or above the request counter, or a regressed enrollment revision. These
indicate API state replacement or database rollback.

### Operator endpoint

The operator status route uses normal JWT authentication and requires the
`farm_admin` role. It contains key fingerprints and bounded codes only, never
key material or enrollment codes.

## Status codes

Every response to an authenticated request is signed, including rejections.
Only a request that cannot be attributed to an enrolled key gets an unsigned
response, and the daemon discards unsigned responses anyway.

| Condition | Status | Body |
|---|---|---|
| Accepted request | `200` | Signed response |
| Unsigned, unknown key, bad signature, non-profile input | `401` | Unsigned problem details; no state change |
| Outside time window or replayed nonce | `401` | Signed `EnrollmentStatus` payload with the reason code; no state change |
| Revoked, quarantined, expired or pending-scope key | `403` | Signed `EnrollmentStatus` payload |
| Stale counter | `409` | Signed `EnrollmentStatus`; no state change |
| Fork evidence | `409` | Signed `EnrollmentStatus` with state `Quarantined` |
| Invalid status report | `400` | Signed `StatusReportAck` with `accepted: false` and `rejectedFields` (field names only; values are never echoed or stored) |
| Operator route without `farm_admin` | `401` / `403` | Standard API responses |

## Approvals carry identities, not instructions

`HostUpdatePullApprovalDto` contains only:

- `approvalId`, `targetKind` (`SignedRelease` or `SignedRecoveryPlan`) and
  `origin` (`ManualOneTime` or `Automatic`).
- Immutable signed identities: `releaseId` (`vX.Y.Z` or `vX.Y.Z-insider.N`),
  `manifestDigest` and, for recovery plans only, `planDigest`
  (`sha256:<64 hex>`).
- Bindings: `channel`, `installationId`, `enrollmentEpoch`, `keyId`,
  `policyRevision`, `trustRevision`, `hostPolicyRevision`, and topology,
  configuration and schema fingerprints.
- `issuedAt`, `expiresAt` (at most five minutes apart) and redacted
  `authorization` evidence (revision, time, reauthenticated flag).

There are no image, command, Compose, path, URL, script, environment or
credential members. A contract test enforces this by reflection. The daemon
resolves everything else from its own verified manifest and host allowlist
(#3116).

## Readiness never grants execution

`HostUpdateReadinessDto.grantsExecution` is always `false`. Readiness is
advisory: the daemon re-checks its own host policy, maintenance window and kill
switch, and still needs an approval plus signed re-confirmation. `Unknown` is
treated exactly like `NotSatisfied`, so `eligible` is `true` only when every
condition is `Satisfied` and the kill switch is off.

## Fail-closed rules

`GET approval` returns `Denied` when any of these hold. Identity failures
short-circuit: when the host identity fails, only identity reasons are reported
and the candidate approval is not inspected. Otherwise every failing reason is
reported:

| Category | Reason codes |
|---|---|
| Identity | `unknown_host_identity`, `enrollment_pending`, `enrollment_revoked`, `enrollment_quarantined`, `enrollment_expired`, `installation_identity_unavailable`, `installation_mismatch` |
| Kill switch | `kill_switch_active` |
| Approval state | `approval_revoked`, `approval_consumed`, `approval_expired`, `approval_not_yet_valid`, `approval_lifetime_exceeded`, `approval_binding_mismatch`, `approval_identity_invalid` |
| Drift | `policy_changed`, `trust_changed`, `host_policy_changed`, `topology_drift`, `configuration_drift`, `schema_drift`, `channel_mismatch` |
| Version | `downgrade_rejected`, `installed_release_unknown` |
| Authorization | `authorization_missing`, `automatic_policy_disabled`, `automatic_updates_runtime_disabled` |
| Recovery | `recovery_prerequisites_missing` |

The installation identity is read with `ReadExistingStrict`, which never
creates the file and never falls back to a transient identity. A missing or
non-canonical identity blocks approval.

`POST approvals/{approvalId}/confirm` re-runs every check against current state
and additionally requires the approval ID, manifest digest, plan digest and host
policy revision to match exactly (`confirmation_mismatch`).

## Status report redaction

`HostUpdateDaemonStatusReportDto` is redacted by shape. Every free-form member
(`currentCheckpoint`, `deferReasons`, `recoveryHints`, `lastResult.reasonCode`)
must match `^[a-z0-9_]{1,64}$`; `lastResult.approvalId` and `releaseId` must be
canonical identifiers. Paths, URLs, tokens, commands and raw error text cannot
be represented, so the validator rejects the report instead of sanitizing it.
Reports outside the ±60 second window, with undefined enum values or with more
than 16 list items, are also rejected.

## Validation

Contract tests live in
`src/tests/Farm.Infrastructure.Tests/Services/HostUpdates/PullApproval/`:

```bash
cd src
dotnet test tests/Farm.Infrastructure.Tests -c Debug --filter "FullyQualifiedName~PullApproval"
```

They cover request verification order, response signing and rollback
detection, each approval denial, automatic denial, readiness, redaction,
camelCase/string-enum serialization and the strict identity read.
