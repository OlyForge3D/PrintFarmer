---
post_title: "Enrolled host-update daemon identity and security model"
author1: "Parker"
post_slug: "host-update-daemon-security"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "security", "updates", "design"]
ai_note: "AI-assisted design; maintainer and security acceptance are pending."
summary: "Host-held identity, enrollment lifecycle, grants and threat model for the enrolled host-update pull reconciler."
post_date: "2026-09-26"
---

## Status and scope

This is the design for issue
[#3113](https://github.com/OlyForge3D/PrintFarmer/issues/3113), a child of epic
[#2658](https://github.com/OlyForge3D/PrintFarmer/issues/2658). It covers the
identity and security model of the **enrolled host-update daemon**: a
host-side pull reconciler that fetches bounded approvals from the PrintFarmer
API and drives the existing host-update executor. It is **design only**.

- **Not implemented:** the daemon, enrollment endpoints, key storage, installer
  or service wrappers, and UI. Those belong to #3114 to #3120.
- **Not enabled:** nothing in this document, a configuration value, an
  environment variable or a saved setting enables enrollment, background
  polling or automatic updates. Auto-update and pilot rollout stay disabled
  until the [#2982](https://github.com/OlyForge3D/PrintFarmer/issues/2982)
  recovery evidence passes and the owner separately authorizes enablement.
- **Not an approval:** a merged design is not security acceptance. The residual
  risks in [Residual risks requiring acceptance](#residual-risks-requiring-acceptance)
  must be accepted by the repository owner before #3114 or #3115 starts.

This design builds on, and does not repeat, the bounded single-installation
contract recorded for
[#2665](https://github.com/OlyForge3D/PrintFarmer/issues/2665) in the
[host enrollment security boundary](HOST_ENROLLMENT_SECURITY.md): socket-free
discovery, independent host policy, fixed operations, approval bindings,
replay high-water marks, the kill switch, audit outside restored application
state and the abuse-case fixtures. Where this document says "the #2665
contract", it means that document. The updater architecture and increment I5
are in the [deployment update strategy](DEPLOYMENT_UPDATE_STRATEGY.md#i5--opt-in-external-pull-reconciliation).

### Fixed architectural constraints

These were decided before this design and are not reopened here:

1. The containerized API never receives the Docker socket, a socket proxy or a
   host-command channel, and never updates itself.
2. The daemon **pulls**. It opens outbound connections to the API; there is no
   inbound privileged listener on the host.
3. The API approves only an **immutable signed release or plan identity** plus
   policy metadata. It never supplies images, commands, Compose fragments, paths
   or URLs (#3115).
4. The daemon independently verifies the signed release (#3116) and its own
   host policy. An API approval is necessary but never sufficient.
5. Every execution goes through the existing executor, journal and lock
   (#2662, #2663, #3114). The daemon is not a second update engine.
6. Scope is one PrintFarmer installation on one host. There is no fleet
   management and no multi-tenant enrollment product.

## Selected identity mechanism

**Decision (proposed): a host-generated, non-exportable asymmetric key pair,
registered with the API through an owner-approved enrollment ceremony, with
every request signed end to end. This is the "signed enrollment key" option.
Mutual TLS (mTLS) is not selected.**

### How it works

- The daemon generates its own key pair on the host during enrollment. The
  private key never leaves the host and is never sent to the API.
- The API stores only the public key, a key ID, the enrollment epoch and the
  enrollment state.
- Each daemon request carries an HTTP message signature
  ([RFC 9421](https://www.rfc-editor.org/rfc/rfc9421)) covering the method,
  target path, a body digest, the key ID, a timestamp, a random nonce and a
  per-key monotonic counter.
- During enrollment, the API also issues a **per-enrollment response-signing
  public key**, which the daemon pins. Every API response to the daemon is
  signed with that key and echoes the request nonce. The daemon rejects
  unsigned, mis-signed or non-matching responses.
- Proposed algorithm: ECDSA P-256 with SHA-256. It works with the Windows CNG
  machine key store, Linux software keys, and TPM-backed keys where present.
  Ed25519 is acceptable only if every target key store supports it.
- TLS still protects confidentiality. It is required for any non-loopback
  connection, with normal server-certificate validation. Request and response
  signatures supply authentication, and they do not depend on where TLS
  terminates.

### Why this is acceptable for the bounded #2658 model

| Concern | Signed enrollment key | mTLS |
| --- | --- | --- |
| TLS terminates at nginx in split deployments | The signature reaches the API unchanged; nginx needs no configuration | nginx must verify the client certificate and forward identity in headers. A misconfigured proxy, or a request that bypasses it, lets a caller forge those headers |
| Loopback or plain-HTTP single-host installs | Authentication still holds | Either TLS is added everywhere or authentication is lost |
| Certificate authority lifecycle | None. Key records are ordinary API data | The app would run a private CA with issuance, CRL or OCSP, and CA key protection |
| Revocation | Change the key record state; takes effect on the next request | Needs CRL/OCSP distribution or short-lived certificates |
| Replay | Signed nonce, timestamp and counter per request | Only per session; application approvals still need their own replay controls |
| Response authenticity | Pinned response key covers the payload itself | Covers the transport only, not payloads relayed through a proxy |

mTLS would be the better choice for a multi-host fleet with an existing PKI.
That is out of scope for #2658. A single installation gains nothing from a
private CA except more key material to protect.

### Explicitly rejected identity options

- **Shared bearer secrets, API keys or HMAC keys** in Compose environment
  variables, `.env` files, command-line arguments or the application database.
  See [Secrets in Compose environment variables are prohibited](#secrets-in-compose-environment-variables-are-prohibited).
- **Reusing a farm service key**, for example the discovery registration key,
  or any credential already held by a container.
- **Browser or administrator session tokens** used by the daemon. An
  interactive identity cannot stand in for a host identity, and the reverse is
  also true.
- **Identity derived from** hostname, IP or MAC address, the executor's
  `installation.id`, image tags, or anything in the application database. The
  existing `HostUpdates:HostState` installation ID is a non-secret *identifier*
  bound into enrollment. It is never a credential.
- **Trust on first use.** A first request that "claims" an installation is
  never auto-approved.

## Enrollment lifecycle

Every step below fails closed: uncertainty produces a rejection or
`NeedsOperator`, never a default approval.

### Bootstrap (enrollment ceremony)

Enrollment needs **both** an authenticated farm administrator in the app and an
authenticated host administrator on the host. Neither can enroll a host alone.

1. **Invitation (app).** A `farm_admin` who holds the proposed
   `updates:manage-policy` permission opens host enrollment, reauthenticates
   interactively (with origin/CSRF protection, per the #2665 contract) and
   creates one invitation. The API returns a one-time enrollment code once.
   - At most one pending invitation per installation.
   - It expires after 10 minutes (proposed) and is single use.
   - The API stores only a salted hash of the code, never the code itself.
   - Creating a new invitation invalidates any older one.
2. **Key generation (host).** The host administrator runs the daemon's
   `enroll` command as the OS administrator (root or an elevated
   Administrator). The daemon generates the key pair in protected storage
   (see [Storage](#storage)). It also computes its installation ID, a
   canonical Compose project and topology fingerprint, and the host-policy
   revision.
3. **Code entry (host).** The code is typed at an interactive prompt on
   standard input. It is **never** accepted from `argv`, an environment
   variable, a file, a Compose value or a pipe, and is never logged. A
   non-interactive session refuses to enroll.
4. **Request (host to API).** The daemon sends its public key, key ID,
   installation ID, fingerprints and a fresh nonce. The request is signed with
   the new private key, which proves possession. It also carries a MAC over
   that payload, keyed by a hash derived from the code, which binds this key to
   this invitation. A wrong or expired code rejects the request and consumes
   the invitation. The API rate-limits requests per installation and in total.
5. **Pending state (API).** The API records a *pending* enrollment. It
   generates the per-enrollment response-signing key pair, stores the private
   key under ASP.NET Core Data Protection in the API's protected key storage
   (never in environment variables), and returns the public key.
6. **Fingerprint comparison.** The host CLI shows a short authentication string
   derived from both public keys and the invitation. The app shows the same
   string to the administrator, together with the installation ID, the
   topology fingerprint and the requesting source address. If they differ,
   or anything is unexpected, the administrator rejects the request.
7. **Approval (app).** The administrator reauthenticates again and approves.
   The API moves the enrollment to *active* with epoch `N+1`. It also revokes
   any previous active enrollment for this installation (see
   [Single active enrollment](#single-active-enrollment)).
8. **Pinning (host).** On its next signed call, the daemon receives the
   approval in a response signed by the pinned key. Only then does it record
   the enrollment as active in its host state. Until that point the daemon
   treats itself as unenrolled.

Enrollment grants **only** identity: the ability to authenticate, read the
readiness/approval/revocation contract (#3115) and report redacted status. It
grants no update execution, no automatic-update permission and no channel
change. See [Three separate grants](#three-separate-grants).

### Single active enrollment

An installation has at most one active daemon enrollment. A new enrollment
revokes the old one when it is approved, and the API records that as a
revocation event. Remote workers, extra hosts and additional daemons are not
enrolled by this design. Topology membership grants nothing.

### Storage

**On the host (daemon):**

| Platform | Private key | Daemon identity state |
| --- | --- | --- |
| Linux (the only platform currently qualified by the [recovery matrix](OFFLINE_UPDATE_RECOVERY.md#isolated-recovery-matrix-scope-3098)) | TPM-backed non-exportable key where available. Otherwise a `0600` file owned by the daemon service account, in a `0700` directory under `/etc/printfarmer-host/daemon/`, never world- or group-writable, without symlink traversal | `/var/lib/printfarmer-host/daemon/`, which holds the key ID, epoch, counter high-water mark, pinned response key and consumed approval IDs |
| Windows (future, per #3118) | CNG machine key store, marked non-exportable, with the ACL restricted to the daemon service account and Administrators | `%ProgramData%\PrintFarmer\host\daemon\`, restricted to the same principals |

These locations are proposals; #3114 and #3118 finalize them. The following
rules are not optional:

- Host identity paths are never bind-mounted into any container, including
  the API, frontend, workers and discovery. They are outside every application
  volume and outside the executor's `HostUpdateExecution:RootDirectory`
  backups.
- The private key is **excluded from all backups** and offline bundles. If a
  restore brings back a copy of a key, the result is treated as a possible
  clone (see [Clone and rollback detection](#clone-and-rollback-detection)).
- The daemon's own journal records extend the existing host-update journal
  (#3114). There is no separate daemon-only store for execution state.
  Identity records contain fingerprints and IDs only, never private material.
- Startup validates ownership and permissions. A missing, unreadable,
  over-permissive or reparse/symlinked path makes the daemon refuse to run
  as enrolled.

**In the API:**

- Stored: public key, key ID, algorithm, epoch, state (`pending`, `active`,
  `rotating`, `revoked`, `quarantined`), counter high-water mark, installation
  ID, fingerprints, and the approving actor and time.
- The response-signing private key is protected by Data Protection in the
  API's key storage, never in environment variables or plain configuration.
- The one-time code is stored only as a salted hash.
- Enrollment events are also written to the audit trail outside restored
  application state, as required by the #2665 contract.

### Secrets in Compose environment variables are prohibited

This is a **hard requirement** for #3114 to #3120 and for any future change to
the installer or Compose templates:

- No daemon credential, enrollment code, private key, response-signing key,
  shared API or daemon secret, or symmetric token may appear in a Compose
  `environment:` entry, a `.env` file, a generated Compose copy, container
  labels, image build arguments, command-line arguments or Docker secrets
  consumed by application containers.
- The daemon's own configuration file may contain only non-secret values: the
  API base URL, the key *reference* (a path or key-store name), the pinned
  response-key fingerprint and policy references.
- The API authenticates the daemon by signature verification against a stored
  public key. No shared secret is needed on either side, so none should be
  added "for convenience".
- Reviews of #3114, #3115 and #3118 must include a check that no such value
  exists. Tests should inspect canonical and generated Compose configurations
  for daemon or enrollment secrets, as the discovery boundary tests already do
  for the Docker socket.

Rationale: Compose environment values can be read through `docker inspect`, by
anyone with access to the Docker API, from process environments, from support
bundles and from copied `.env` files. A secret there is shared with every
actor who can see the container. It also cannot be revoked independently of
redeploying the whole stack.

### Renewal and rotation

- **Validity.** An enrollment key has a maximum lifetime of 90 days (proposed).
  The daemon starts rotation automatically after 60 days.
- **Daemon key rotation.** The daemon generates a new key pair and sends a
  rotation request signed by **both** the current key and the new key. The API
  accepts it only if the current key is active, not quarantined, and its
  counter is current. The enrollment then enters `rotating`, with a bounded
  overlap of 24 hours at most. The old key is revoked after the first
  successfully verified request under the new key, or when the overlap ends,
  whichever comes first.
- **What rotation cannot change.** It keeps the installation ID, the grants
  and the epoch lineage. It cannot widen scope, change channel, add
  automatic-update permission or reset counters or high-water marks. A rotation
  that changes the topology or installation fingerprint is rejected and needs
  a new enrollment.
- **Response-key rotation.** The API sends the new response public key in a
  message signed by the currently pinned key. The daemon accepts only such a
  chained rotation, never a replacement key from an unsigned or independently
  signed message.
- **Failure.** If rotation fails, the current key keeps working until it
  expires. After expiry the daemon is unenrolled, fails closed, and needs a new
  owner-approved enrollment. There is no grace period and no automatic
  re-enrollment.
- **Audit.** Each rotation is recorded with old and new key IDs, epoch, time
  and outcome, but no key material.

### Revocation

Any of the following revokes an enrollment:

- A `farm_admin` revokes it in the app, after reauthentication.
- A new enrollment for the installation is approved.
- The host administrator runs a local `unenroll` command. The daemon sends a
  signed revocation request first. It destroys the key even if the API cannot
  be reached, in which case the administrator must also revoke in the app.
- The API detects a clone or replay and quarantines the key.

Effects:

- **In the API.** The key's next request is rejected with a signed `revoked`
  response. Pending approvals for that enrollment become invalid, and it can
  obtain no new approvals.
- **In the daemon.** On a verified `revoked` response, or on local
  `unenroll`, the daemon:
  1. stops admitting new work;
  2. lets any running operation continue only to the next safe journal
     checkpoint, following the kill-switch rules in the #2665 contract and the
     checkpoint matrix of #3117;
  3. destroys its private key;
  4. marks itself unenrolled in host state.
- **Automatic updates.** Revocation also clears the daemon's local
  automatic-update grant (see [Three separate grants](#three-separate-grants)).
  A later enrollment never inherits it.
- **Uncertain revocation state.** An unreachable API, an unsigned or
  unverifiable response, clock uncertainty or a response for an unexpected
  epoch all mean **no new admission**. Before the first side effect of any
  operation, the daemon must obtain a fresh signed confirmation that the
  enrollment is active and the approval is still valid. Without that
  confirmation the operation does not start. What happens to work already
  running during an outage is decided by #3117 from durable checkpoints. It
  never widens authorization.

### Lost-host recovery

| Situation | Required procedure |
| --- | --- |
| Host disk lost or rebuilt | The administrator revokes the old enrollment in the app. The host administrator re-enrolls, which creates a new key and a new epoch. Automatic-update permission must be granted again. Replay and high-water host state must be restored through the executor's continuity procedure. If that is not possible, the result is `NeedsOperator`, never a reset. |
| Host stolen, or key material suspected exposed | Revoke immediately in the app. Treat everything the host could reach as compromised, including the database and data-protection keys, under the host-compromise residual risk. Re-enroll only on a rebuilt host. |
| Host restored from a backup or snapshot | The key should be absent because it is excluded from backups. If it is present, the counter and epoch checks quarantine it on first use. Re-enroll. |
| Application database restored to an older state | See [Clone and rollback detection](#clone-and-rollback-detection). The daemon's pinned epoch and counter take precedence. A mismatch holds all work for operator review. |
| Daemon replaced on the same host | Unenroll the old daemon, then enroll the new one. No key transfer between installations is supported. |

Recovery never uses trust on first use, never copies keys between hosts and
never restores grants implicitly. Manual and offline recovery through the
[runbook](HOST_UPDATE_RUNBOOK.md) and [offline bundles](OFFLINE_UPDATE_RECOVERY.md)
stays available without an enrolled daemon.

### Clone and rollback detection

- **Counter.** Each signed request carries a counter that increases by one per
  request for that key. The daemon persists it before sending. The API rejects
  any value at or below its recorded high-water mark. A lower counter together
  with a valid signature points to a duplicated key. The API quarantines the
  key and invalidates its approvals, and only an administrator can resolve it
  by re-enrolling.
- **Epoch.** The daemon pins its enrollment epoch. A signed response for a
  different epoch means the API's state has changed underneath it, for example
  through a database restore, so the daemon holds all work and reports
  `NeedsOperator`.
- **Revocation surviving a restore.** A verified revocation makes the daemon
  destroy its key, so restoring the API database cannot bring that enrollment
  back. If the daemon never received the revocation before the restore, the
  enrollment can reappear. That is residual risk R4.

## Three separate grants

Discovery consent, enrollment identity and automatic-update permission are
**independent grants**. Each is stored, audited and revoked on its own. None
implies, creates or widens another.

| Grant | What it permits | Who grants it | Where it is stored | Explicitly does not permit |
| --- | --- | --- | --- | --- |
| **Outbound discovery consent** | The API contacts the release source to check for and verify release metadata | `farm_admin` in app settings | API policy | Enrollment, daemon installation, channel change, execution |
| **Enrollment identity** | The daemon authenticates, reads readiness, approvals and revocation, and reports status | Owner ceremony: `farm_admin` invitation and approval plus the host administrator on the host | API key record and host identity state | Any execution; automatic updates; consent to checks; channel change |
| **Automatic-update permission** | Standing permission for eligible updates in the selected channel and maintenance window, as defined by #2666 | **Both** a `farm_admin` enabling automatic policy in the app **and** the host administrator enabling it in host-local policy | API automation policy and a root-owned host policy file | Widening channel, window, downtime or backup policy; skipping signed verification, recovery prerequisites or active-print drain |

Additional rules:

- **Installing the service is not a grant.** Installing the daemon (#3118)
  gives it no rights until enrollment completes. It must not install enabled
  or enrolled by default.
- **Manual Update now** still needs enrollment plus a fresh one-time
  administrator request, as in #2666. It never needs automatic-update
  permission, and it never grants it.
- **Channel changes** stay privileged policy actions under the #2665 contract.
  Choosing insider grants none of the three grants.
- **Default for every installation:** no discovery consent, no enrollment, no
  automatic updates. The daemon enforces its local automatic-update grant
  itself. An API response claiming "auto enabled" never turns it on.
- **Order of withdrawal.** Withdrawing automatic permission leaves the
  enrollment in place. Revoking the enrollment also clears the local automatic
  permission. Withdrawing discovery consent stops checks, so no new offers
  appear, but does not revoke the enrollment.

## Owner and administrator authorization points

| Action | App authorization | Host authorization | Notes |
| --- | --- | --- | --- |
| Grant or withdraw outbound discovery consent | `farm_admin`, reauthentication, origin/CSRF protection | None | Audited. Separate from the release channel |
| Create an enrollment invitation | `farm_admin` with `updates:manage-policy`, reauthentication | None | One pending, 10 minutes, single use |
| Submit an enrollment | The invitation code | Interactive OS administrator on the host | Code on standard input only |
| Approve or reject an enrollment | `farm_admin` with `updates:manage-policy`, reauthentication, matching fingerprint | None | Revokes any earlier enrollment |
| Rotate a key | None; routine and signed by both keys | Daemon service identity | Cannot change grants or scope |
| Revoke or unenroll | `farm_admin` reauthentication **or** the host administrator | Either side is sufficient | Revocation always succeeds |
| Grant automatic-update permission | `farm_admin` with `updates:manage-policy`, reauthentication, explicit confirmation | Host administrator edits host policy | Both are required. Off by default |
| Manual Update now | `farm_admin` with `updates:execute`, reauthentication, one-time request | The existing host policy must allow the operation | Per #2666 |
| Change the channel or trust policy | Per the #2665 contract | Host policy agreement | Not a daemon identity action |
| Resolve a quarantined or clone-suspected key | `farm_admin` revokes and invites again | Host administrator re-enrolls | Never an "unquarantine" button |

Any service identity, API key, settings writer or daemon request is refused for
these actions if it tries to act in place of a human `farm_admin`.

## Threat model

Assets, actors and the general updater threat model are those of the
[#2665 contract](HOST_ENROLLMENT_SECURITY.md#threat-model-and-observation-choice)
and the [strategy security boundary](DEPLOYMENT_UPDATE_STRATEGY.md#execution-choices-and-security-boundary).
This section adds only the threats specific to the daemon's identity and pull
channel.

Trust boundaries:

- The host administrator and the daemon's protected storage are trusted.
- The API is **authenticated but not trusted**. A compromised API can sign
  authentic responses.
- The network, the reverse proxy, containers, the application database and
  the Compose environment are **untrusted** for identity purposes.

| # | Threat | Attack | Control | Residual |
| --- | --- | --- | --- | --- |
| T1 | Host impersonation | An attacker calls the pull API as the enrolled host to read approvals or submit fake status | Every request is signed with the host-held non-exportable key; no shared secret exists to steal from the environment; enrollment needs both owner and host administrator; IP and hostname are ignored | Theft of the private key through host compromise (R1) |
| T2 | API impersonation / MITM | A proxy or network attacker injects approvals or a fake "revoked" or "active" status | Responses are signed by the pinned per-enrollment key and bound to the request nonce; TLS off loopback; chained response-key rotation | A compromised API holds the response key (R2) |
| T3 | Replay | Captured requests, responses or approvals are resent | Timestamp window of ±60 seconds, nonce cache, monotonic counter, nonce echo in responses; approvals carry ID, expiry, installation, epoch and immutable target; consumed approval IDs are journaled; #2665 replay high-water marks | Host clock tampering by a host administrator (R5) |
| T4 | Release downgrade | An approval or cached metadata points to an older or cross-channel release | The daemon verifies the signed manifest and enforces per-trust-root and per-channel high-water marks (#3116); approvals cannot lower the sequence; recovery is a separate verified plan | None beyond an authorized-signer compromise (R3) |
| T5 | Stale approval | An approval is used after policy, topology, schema, window or preflight changed, or after revocation | Short expiry (the #2665 contract proposes 5 minutes at most); binding to policy and topology fingerprints; a signed re-confirmation before the first side effect; revocation invalidates pending approvals | An operation already past its first side effect finishes to a safe checkpoint (by design, #3117) |
| T6 | Compromised environment or configuration | An attacker reads or edits the Compose `.env`, container environment, generated Compose or daemon configuration | No secrets in environment variables at all; the daemon configuration holds no secrets; the API URL cannot redirect because of the pinned response key; host policy is root-owned and journaled; configuration fingerprints are bound into approvals; API environment settings cannot create enrollment or automatic permission | An attacker with host root can change anything (R1) |
| T7 | Unauthorized consent expansion | An API change, settings write, restore or channel switch turns discovery consent or enrollment into automatic updates, or widens window, channel or scope | Three separate grants; automatic permission also needs the host-local grant; rotation cannot change grants; revocation clears the local automatic grant; new enrollments inherit nothing; daemon-side policy checks | A restored API database can re-show a revoked enrollment the daemon never saw revoked (R4) |
| T8 | Confused deputy | The API is tricked into issuing a valid approval for another installation or target | Approvals are bound to installation ID, enrollment epoch, key ID and immutable signed target; the daemon checks all of them against its own state and host policy | A compromised API can still pick a target that host policy allows (R2) |
| T9 | Stolen enrollment code | The code is shoulder-surfed or intercepted | The code is short-lived and single use, bound to a key by a MAC, and the administrator compares fingerprints before approval | A distracted administrator approves a mismatching fingerprint (R6) |
| T10 | Enrollment flooding / denial of service | Mass enrollment or pull requests | One pending invitation; per-installation and global rate limits; the #2665 polling bounds (at least 60 seconds between polls, backoff up to 15 minutes); bounded request sizes | Availability only; no authority gained |
| T11 | Credential leakage through logs or status | Keys, codes or signed material appear in logs, status, support bundles or UI | Bounded reason codes; fingerprints and IDs only; redaction tests (#3114, #3119) | None beyond the #2665 redaction limits |
| T12 | Cloned host (backup or snapshot) | A restored or copied host runs with the same key | Keys are excluded from backups; the counter regression quarantines the key; the epoch is pinned | A clone used before the original sends another request (R4 window) |

### Fail-closed decision table

| Condition observed by the daemon | Result |
| --- | --- |
| Not enrolled, pending, revoked, quarantined or expired | No approvals requested; status only, if authenticated |
| Response unsigned, signature invalid, nonce mismatch or unexpected epoch | Discard the response; no admission; `NeedsOperator` for epoch changes |
| API unreachable or timed out | No new admission; running work follows #3117 checkpoints |
| Clock skew beyond the window, or clock source uncertain | No admission |
| Key storage permissions invalid, or key unreadable | Refuse to run enrolled |
| Host policy missing, unparseable, or its revision changed after approval | Reject the approval |
| No local automatic-update grant | Ignore automatic approvals; manual one-time requests only |
| Approval expired, consumed, for another installation or target, or a downgrade | Reject and journal the outcome |
| Replay or high-water state missing or rolled back | Hold for trusted recovery; never reset |

## Audit

The API audit trail, kept outside restored application state, and the host
journal both record:

- invitation created, expired or used;
- enrollment requested, approved or rejected (with actor and fingerprints);
- key rotated, revoked or quarantined, and suspected clones;
- automatic-update permission granted or withdrawn on each side;
- approvals issued, consumed and rejected.

Each record carries bounded reason codes. No record contains codes, private
keys, signatures over secret payloads, tokens or raw remote errors, as required
by the #2665 contract.

## Residual risks requiring acceptance

The repository owner must record explicit acceptance, or a required change,
for each item below on #3113 or #2658 before implementation begins. Reviewer
approval of this document is self-attested and is **not** that acceptance.

| ID | Residual risk | Why it remains | Proposed acceptance position |
| --- | --- | --- | --- |
| R1 | Host root or administrator compromise | The daemon controls Docker and is root-equivalent. An attacker with host root can read keys, edit policy and forge journals | Accept. Mitigate with off-host audit replication, which the #2665 contract already names |
| R2 | API compromise with valid response signing | The API holds the response-signing key, so a compromised API can issue authentic approvals | Accept, but only because the daemon independently verifies signatures, host policy, window and high-water marks. A compromised API can at most trigger a signed, policy-allowed update in an allowed window, or withhold updates |
| R3 | Compromise of an authorized release signer or workflow | A malicious release signed by the trusted workflow passes verification | Accept as the #2665 authorized-signer risk. This design does not change it |
| R4 | A database restore re-shows a revoked enrollment | If the daemon never received the revocation before the restore, the API can re-show the key as active | Accept, with the requirement that the off-host audit shows the revocation and the runbook tells operators to revoke again after any API database restore |
| R5 | Host clock manipulation | A host administrator can skew the clock to stretch expiry windows | Accept under R1. Daemons reject clocks they cannot trust |
| R6 | Human fingerprint-comparison error | The administrator approves a mismatched enrollment | Accept, with UI copy (#3120) that makes the comparison mandatory and prominent |
| R7 | No hardware-backed key on most Linux hosts | Software keys are only protected by file permissions | Accept for the bounded model. Prefer a TPM where present |
| R8 | Self-attested review | Every squad agent acts with the owner's authority, so the review panel is not independent | Accept, as recorded for the repository verdict gate. It is not separation of duties |

## Validation expectations for implementation children

This design is prose. It proves nothing about enforcement. The children must
turn it into tests:

- **#3114:** Key storage permission validation. No daemon secret in canonical
  or generated Compose configurations. Logs and status redacted. Disabled by
  default.
- **#3115:** Signed-request verification, nonce, counter and timestamp
  rejection. Signed responses. Revoked and quarantined states. Approvals bound
  to installation and epoch. camelCase and string-enum contracts.
- **#3116:** Downgrade, cross-channel and replay rejection independent of the
  API.
- **#3117:** Signed re-confirmation before the first side effect.
  API-unavailable and revoked-during-operation cells.
- **#3118:** Installation grants nothing. Enrollment and automatic permission
  are separate, persisted choices. No environment secrets.
- **#3119:** End-to-end impersonation, replay, stale approval, clone and
  database-restore scenarios.
- **#3120:** User-facing copy for the three grants, fingerprint comparison and
  the #2982 gate.

The #2665 abuse-case fixtures still apply. Add to them: a forged host
signature, a replayed signed request, a counter regression or clone, an
unsigned or mis-signed response, an unapproved response-key change, a leaked
invitation code with a fingerprint mismatch, an attempt to add automatic
permission by rotation or restore, and a daemon secret added to Compose
environment variables.
