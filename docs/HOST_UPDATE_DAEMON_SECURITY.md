---
post_title: "Enrolled host-update daemon identity and security model"
author1: "Parker"
post_slug: "host-update-daemon-security"
microsoft_alias: ""
featured_image: ""
categories: []
tags: ["deployment", "security", "updates", "design"]
ai_note: "AI-assisted design; the owner approved the accepted-risk positions in #3124."
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

- **Not implemented:** enrollment endpoints, key generation, installer or
  service wrappers, and UI. Those belong to #3115 to #3120. The daemon
  **service core** is implemented by #3114 (see
  [Daemon service core (#3114)](HOST_UPDATE_RUNBOOK.md#daemon-service-core-3114)):
  it validates identity storage, reads the existing journal and lock, and
  publishes redacted status, but its execution gate is hard-wired disabled.
  **Independent signed-release verification** is implemented by #3116 (see
  [Daemon signed-release verification (#3116)](HOST_UPDATE_RUNBOOK.md#daemon-signed-release-verification-3116)).
  It runs behind the same disabled gate and enables nothing.
- **Not enabled:** nothing in this document, a configuration value, an
  environment variable or a saved setting enables enrollment, background
  polling or automatic updates. Auto-update and pilot rollout stay disabled
  until the [#2982](https://github.com/OlyForge3D/PrintFarmer/issues/2982)
  recovery evidence passes and the owner separately authorizes enablement.
- **Accepted risks:** the repository owner approved the positions in
  [Accepted runtime risks](#accepted-runtime-risks) (recorded on
  [#3124](https://github.com/OlyForge3D/PrintFarmer/issues/3124)). A merged
  design still enables nothing; see the points above.

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

**Decision (proposed): a host-generated, host-held asymmetric key pair —
a file-permission-protected software key by default, or a non-exportable
platform key where one is readily available (Windows CNG, or optionally a
TPM) —
registered with the API through an owner-approved enrollment ceremony, with
every request signed end to end. This is the "signed enrollment key" option.
Mutual TLS (mTLS) is not selected.**

### How it works

- The daemon generates its own key pair on the host during enrollment. The
  private key is never sent to the API. On Linux it is a software key that
  only root and the daemon service account can read; file permissions are
  sufficient (R7). Windows CNG can mark it non-exportable. A TPM-backed key is
  optional best effort and not in scope for #2658.
- The API stores only the public key, a key ID, the enrollment epoch and the
  enrollment state. These records live in the protected `HostUpdates:HostState`
  store, not the application database (see [Storage](#storage)).
- Each daemon request carries an HTTP message signature
  ([RFC 9421](https://www.rfc-editor.org/rfc/rfc9421)) covering the method,
  target path, a body digest, the key ID, a timestamp, a random nonce and a
  per-key monotonic counter.
- During enrollment, the API also issues a **per-enrollment response-signing
  public key**, which the daemon pins. Every API response to the daemon is
  signed with that key and echoes the request nonce, the counter of the
  previously accepted request and a monotonic enrollment-state revision. The
  daemon rejects unsigned, mis-signed or non-matching responses, and detects API
  state rollback from the echoed values (see
  [Clone and rollback detection](#clone-and-rollback-detection)).
- The daemon sends **one request at a time** per key. It never has two signed
  requests in flight, and a retry is a new request with a new nonce and
  counter, never a resend of the same signed bytes.
- Proposed algorithm: ECDSA P-256 with SHA-256. It works with Linux software
  keys and the Windows CNG machine key store, and with an optional TPM.
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
| Certificate authority lifecycle | None. Key records are protected API host-state data | The app would run a private CA with issuance, CRL or OCSP, and CA key protection |
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

**What the operator does.** The ceremony comes down to three things:

1. **A one-time code.** The farm administrator creates it in the app and types
   it into the daemon's `enroll` prompt on the host.
2. **Proof that the host holds its key.** The daemon signs the enrollment
   request with the key it just generated. This is automatic; the operator
   does nothing extra.
3. **A short fingerprint.** The host prompt and the app show the same short
   string. The administrator checks they match, then approves.

Everything else below (installation ID, topology and policy binding, rate
limits) is done by the software, not the operator.

**Prerequisites, all fail closed:**

- The API's `HostUpdates:HostState` root is enabled, provisioned and passes its
  filesystem-security validation. Enrollment records are stored there, so an
  unprovisioned API refuses to create invitations.
- The durable installation ID (`installation.id`) already exists in that root
  and is read strictly. The existing `HostUpdateInstallationIdentity.GetOrCreate`
  helper returns a transient random value when storage is unavailable. That
  fallback must never be used for enrollment; #3115 needs a strict read that
  fails with an error instead.
- The daemon reaches the API over a transport it can authenticate: loopback on
  the same host, or TLS with a certificate that validates against the host's
  trust store or a fingerprint the host administrator pins at the prompt.
  Enrollment over plain HTTP to a non-loopback address is refused.

**Ceremony:**

1. **Invitation (app).** A `farm_admin` who holds the proposed
   `updates:manage-policy` permission opens host enrollment, reauthenticates
   interactively (with origin/CSRF protection, per the #2665 contract) and
   creates one invitation. The API returns a one-time enrollment code once.
   - The code carries at least 128 bits from a cryptographic random generator,
     shown as grouped base32 text, so offline or online guessing is
     infeasible.
   - At most one pending invitation per installation.
   - It expires after 10 minutes (proposed).
   - The API stores only a salted SHA-256 hash of the code, never the code.
   - Creating a new invitation invalidates any older one.
2. **Key generation (host).** The host administrator runs the daemon's
   `enroll` command as the OS administrator (root or an elevated
   Administrator). The daemon generates a provisional key pair in protected
   storage (see [Storage](#storage)). It reads the durable installation ID
   strictly and records the Compose project and topology fingerprint and the
   host-policy revision, which the API binds into the enrollment automatically.
3. **Code entry (host).** The code is typed at an interactive prompt on
   standard input. It is **never** accepted from `argv`, an environment
   variable, a file, a Compose value or a pipe, and is never logged. A
   non-interactive session refuses to enroll.
4. **Request (host to API).** Over the authenticated transport above, the
   daemon sends the code, its public key, key ID, installation ID,
   fingerprints and a fresh nonce, all signed with the provisional private key
   to prove possession. The API hashes the submitted code and compares it in
   constant time with the stored hash. On a match, it binds this public key to
   the invitation atomically, and the invitation cannot bind another key.
   - A wrong code counts as a failed attempt. Five failed attempts, or expiry,
     consume the invitation.
   - The API rate-limits enrollment requests per source address, per
     installation and in total.
   - An unauthenticated caller can burn an invitation by failing five times.
     That affects availability only (T10); the administrator creates a new
     one.
5. **Pending state (API and host).** The API records a *pending* enrollment. It
   generates the per-enrollment response-signing key pair, stores the private
   key in the `HostUpdates:HostState` store protected by ASP.NET Core Data
   Protection (never in environment variables), and returns the public key in
   a signed response. The daemon stores the provisional key and the provisional
   pinned response key and enters its own `pending` state. In `pending` it may
   only poll the signed enrollment status; it requests no approvals and
   reports no status. If the enrollment is not approved within 30 minutes
   (proposed), both sides discard it and the daemon destroys the provisional
   key.
6. **Fingerprint comparison.** The host CLI shows a short authentication string
   derived from both public keys and the invitation. The app shows the same
   string prominently. The administrator checks that the two match and rejects
   the request if they don't. The app may also show the installation ID and
   other details as context, but matching the short string is the only
   comparison the operator is asked to make.
7. **Approval (app).** The administrator reauthenticates again and approves.
   The API moves the enrollment to *active* with epoch `N+1`. It also revokes
   any previous active enrollment for this installation (see
   [Single active enrollment](#single-active-enrollment)).
8. **Activation (host).** On its next status poll, the daemon receives the
   approval in a response signed by the provisional pinned key. Only then does
   it record the enrollment, key and pinned response key as *active*.

**What the ceremony proves.** It authenticates the host to the API against an
unsolicited or substituted enrollment: the invitation needs a reauthenticated
administrator, the code is bound to exactly one key, and the fingerprint
comparison catches a key substituted by a network attacker. It authenticates
the **API to the host only as strongly as the enrollment transport**. The
reverse proxy and the API sit inside the same trust boundary: an attacker who
controls nginx or the API during enrollment can present its own response key
and also rewrite the page the administrator compares. That is equivalent to
API compromise, which host policy bounds afterwards (R2). Enrolling through
loopback or a directly reachable API port on the same host is preferred where
possible, because it keeps the proxy out of the path.
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
| Linux (the only platform currently qualified by the [recovery matrix](OFFLINE_UPDATE_RECOVERY.md#isolated-recovery-matrix-scope-3098)) | A `0600` file owned by the daemon service account, in a `0700` directory under `/etc/printfarmer-host/daemon/`, never world- or group-writable, without symlink traversal. A TPM-backed key is optional best effort, not required | `/var/lib/printfarmer-host/daemon/`, which holds the daemon state (`unenrolled`, `pending`, `active`, `rotating`, `revoked`), key ID, epoch, the last counter the API acknowledged, the last enrollment-state revision seen, the pinned installation ID and response key, and consumed approval IDs |
| Windows (future, per #3118) | CNG machine key store, marked non-exportable, with the ACL restricted to the daemon service account and Administrators | `%ProgramData%\PrintFarmer\host\daemon\`, restricted to the same principals |

These locations are proposals; #3114 and #3118 finalize them. #3114 finalized
the Linux key path as `/etc/printfarmer-host/daemon/enrollment-key.pem`,
configured through `HostUpdateDaemon:IdentityDirectory` (an absolute path
outside `HostUpdateExecution:RootDirectory`). Startup validation fails closed
with a bounded code on any group/other permission bit, owner mismatch,
symlink or reparse component, or non-Linux platform. The following
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

- Enrollment records live in the protected `HostUpdates:HostState` store,
  next to the replay store, anchor and policy fence. They are **not** in the
  application database. The [runbook](HOST_UPDATE_RUNBOOK.md#protected-state-and-configuration)
  already requires that store to be outside application-database restore
  scope, so restoring the application database cannot reactivate a revoked
  enrollment or roll back its counter.
- Stored: public key, key ID, algorithm, epoch, state (`pending`, `active`,
  `rotating`, `revoked`, `quarantined`), a monotonic enrollment-state
  revision, the counter high-water mark and the timestamp and request digest
  of the request that set it, installation ID, fingerprints, and the approving
  actor and time. Revoked and quarantined records are retained as tombstones,
  never deleted.
- The response-signing private key is protected by Data Protection in the
  same store, never in environment variables or plain configuration.
- The one-time code is stored only as a salted hash.
- Enrollment events are also written to the audit trail outside restored
  application state, as required by the #2665 contract.
- The rules for that store already apply: never reset it to clear an error,
  and treat missing or rolled-back state as blocking, not as first-time
  provisioning.

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
- **Response-key lifecycle.** The response-signing key has the same maximum
  lifetime as the enrollment key and is rotated in the same rotation
  transaction. The API sends the new response public key in a message signed
  by the currently pinned key; the daemon accepts only such a chained rotation,
  never a replacement key from an unsigned or independently signed message.
  The old response key stays valid for the same 24-hour overlap at most, then
  the API destroys it. The daemon rejects any response key past its expiry.
  Revocation destroys the enrollment's response key, and every new enrollment
  generates a fresh one.
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
  obtain no new approvals. The enrollment's response-signing key is destroyed
  after that final signed response, or at once if the daemon is unreachable.
  The revoked record stays as a tombstone in the protected host-state store.
- **In the daemon.** On a verified `revoked` response, or on local
  `unenroll`, the daemon:
  1. stops admitting new work;
  2. lets any running operation continue only to the next safe journal
     checkpoint, following the kill-switch rules in the #2665 contract and the
     checkpoint matrix of #3117;
  3. destroys its private key;
  4. marks itself unenrolled in host state.
- **Automatic and manual execution.** Revocation resets the daemon's local
  execution mode to `none` (see [Three separate grants](#three-separate-grants)).
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
| Host stolen, or key material suspected exposed | Revoke immediately in the app. Treat everything the host could reach as compromised, including the database and data-protection keys, under accepted risk R1. Re-enroll only on a rebuilt host. |
| Host restored from a backup or snapshot | The key should be absent because it is excluded from backups. If it is present, the fork check quarantines it on first use (see [Clone and rollback detection](#clone-and-rollback-detection)). Re-enroll. |
| Application database restored to an older state | Enrollment records are not in the application database, so this changes nothing about identity. The #2665 replay and continuity rules still apply to update state. |
| API host-state store restored or rolled back | This is prohibited by the runbook. If it happens anyway, the daemon detects it only when the echoed counter or state revision regresses below the state the daemon has already observed, and then holds all work as `NeedsOperator`. A rollback to a point after the daemon's last acknowledged request is undetectable by the daemon; see accepted risk R4. Recovery is revocation and re-enrollment, never resetting the daemon to match. **After any host-state restore, revoke the enrollment again**, whether or not the daemon noticed. |
| Daemon replaced on the same host | Unenroll the old daemon, then enroll the new one. No key transfer between installations is supported. |

Recovery never uses trust on first use, never copies keys between hosts and
never restores grants implicitly. Manual and offline recovery through the
[runbook](HOST_UPDATE_RUNBOOK.md) and [offline bundles](OFFLINE_UPDATE_RECOVERY.md)
stays available without an enrolled daemon.

### Clone and rollback detection

- **Verification order (API).** For each request the API checks, in order:
  the signature against an active key; the timestamp window; nonce
  uniqueness; then the counter. A failure at any step rejects the request
  without changing state. A duplicate nonce is a **replay**: it is rejected,
  not treated as a clone, and never quarantines the key.
- **Counter.** Each signed request carries a counter that increases by one per
  request for that key. The daemon persists the new value before sending and
  keeps only one request in flight, and its signing timestamps never decrease
  (after a backward clock step it waits rather than sign an earlier time). The
  API accepts only a counter above its high-water mark. A counter at or below
  the mark is rejected as stale, which covers a delayed request arriving after
  a newer one.
- **Fork evidence and quarantine.** The API quarantines the key only when it
  sees what a single serialized signer cannot produce: a validly signed
  request whose counter is at or below the high-water mark **and** whose
  timestamp is later than the timestamp recorded for that mark, or two
  different request digests with the same counter. Only the key holder can
  create either, so a network attacker cannot trigger quarantine. Quarantine
  invalidates the key's approvals, and only an administrator can resolve it by
  revoking and re-enrolling.
- **Rollback of API state.** Every signed response echoes the counter of the
  previous request the API accepted and the current enrollment-state revision.
  The daemon compares them with its own records. An echoed counter lower than
  the last one the API acknowledged, or a lower state revision, means the API
  state was rolled back. The daemon then holds all work as `NeedsOperator`.
  This also detects a rollback within one epoch, but only below state the
  daemon has already observed; a rollback to a point after its last
  acknowledged request is not detectable this way (R4).
- **Epoch.** The daemon pins its enrollment epoch. A signed response for a
  different epoch means the API's state changed underneath it, so the daemon
  holds all work as `NeedsOperator`.
- **Installation ID.** The daemon pins the installation ID read at
  enrollment. A later mismatch in the API's records or the host-state root is
  `NeedsOperator`.
- **Revocation surviving a restore.** Enrollment records are in the
  protected host-state store, not the application database, so restoring the
  application database does not affect them. A verified revocation also makes
  the daemon destroy its key. The remaining gap is a prohibited restore of
  the host-state store itself to a point before a revocation the daemon never
  saw, taken after the daemon's last acknowledged request. The daemon cannot
  detect that. It is accepted risk R4: revoke again after any host-state
  restore.

## Three separate grants

Discovery consent, enrollment identity and automatic-update permission are
**independent grants**. Each is stored, audited and revoked on its own. None
implies, creates or widens another.

| Grant | What it permits | Who grants it | Where it is stored | Explicitly does not permit |
| --- | --- | --- | --- | --- |
| **Outbound discovery consent** | The API contacts the release source to check for and verify release metadata | `farm_admin` in app settings | API policy | Enrollment, daemon installation, channel change, execution |
| **Enrollment identity** | The daemon authenticates, reads readiness, approvals and revocation, and reports status | Owner ceremony: `farm_admin` invitation and approval plus the host administrator on the host | API host-state key record and host identity state | Any execution; automatic updates; consent to checks; channel change |
| **Automatic-update permission** | Standing permission for eligible updates in the selected channel and maintenance window, as defined by #2666 | **Both** a `farm_admin` enabling automatic policy in the app **and** the host administrator setting host execution mode `automatic` | API automation policy and a root-owned host policy file | Widening channel, window, downtime or backup policy; skipping signed verification, recovery prerequisites or active-print drain |

### Host execution mode

The API authenticates the administrator who clicks **Update now**; the daemon
cannot. A compromised API holds the response-signing key and could forge a
"manual" approval. Enrollment therefore admits no execution by itself. The
host administrator sets a host-local **execution mode** in the root-owned host
policy file:

| Mode | Pull-delivered operations the daemon accepts |
| --- | --- |
| `none` (default, and the state after any revocation) | None. Status and readiness only. Manual updates keep using the host-local CLI in the [runbook](HOST_UPDATE_RUNBOOK.md) |
| `manual` | One-time Update now requests (#2666) |
| `automatic` | One-time requests and, when the app-side automatic policy is also on, eligible automatic updates. This is the host half of the automatic-update permission grant |

In every mode, **every pull-delivered operation, manual or automatic, is bound
by host policy**: the host maintenance window, allowed channels, maximum
downtime, backup and recovery class, active-print drain, signed-release
verification and high-water marks. An approval labelled "manual" gets no
exception from the host window. An update outside the window uses the
host-local CLI, where the host administrator is present. Mode `manual` is an
option inside the enrollment's scope, set only on the host; it is not a fourth
consent that the app can grant.

Additional rules:

- **Installing the service is not a grant.** Installing the daemon (#3118)
  gives it no rights until enrollment completes. It must not install enabled
  or enrolled by default.
- **Manual Update now** through the daemon needs enrollment, host execution
  mode `manual` or `automatic`, and a fresh one-time administrator request, as
  in #2666. It never needs automatic-update permission, and it never grants
  it.
- **Channel changes** stay privileged policy actions under the #2665 contract.
  Choosing insider grants none of the three grants.
- **Default for every installation:** no discovery consent, no enrollment,
  host execution mode `none`, no automatic updates. The daemon enforces its
  host execution mode itself. An API response claiming "auto enabled" never
  turns it on.
- **Order of withdrawal.** Withdrawing automatic permission on either side
  leaves the enrollment in place. Revoking the enrollment resets host
  execution mode to `none`. Withdrawing discovery consent stops checks, so no
  new offers appear, but does not revoke the enrollment.

## Owner and administrator authorization points

| Action | App authorization | Host authorization | Notes |
| --- | --- | --- | --- |
| Grant or withdraw outbound discovery consent | `farm_admin`, reauthentication, origin/CSRF protection | None | Audited. Separate from the release channel |
| Create an enrollment invitation | `farm_admin` with `updates:manage-policy`, reauthentication | None | One pending, 10 minutes, five attempts, 128-bit code |
| Submit an enrollment | The invitation code | Interactive OS administrator on the host | Code on standard input only |
| Approve or reject an enrollment | `farm_admin` with `updates:manage-policy`, reauthentication, matching fingerprint | None | Revokes any earlier enrollment |
| Rotate a key | None; routine and signed by both keys | Daemon service identity | Cannot change grants or scope |
| Revoke or unenroll | `farm_admin` reauthentication **or** the host administrator | Either side is sufficient | Revocation always succeeds |
| Set host execution mode (`none`, `manual`, `automatic`) | None; the app cannot set it | Host administrator edits the root-owned host policy | Default `none`; reset to `none` by revocation |
| Grant automatic-update permission | `farm_admin` with `updates:manage-policy`, reauthentication, explicit confirmation | Host administrator sets execution mode `automatic` | Both are required. Off by default |
| Manual Update now through the daemon | `farm_admin` with `updates:execute`, reauthentication, one-time request | Host execution mode `manual` or `automatic`, inside the host window and policy | Per #2666. Outside the window, use the host-local CLI |
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
  authentic responses. During the enrollment ceremony only, a reverse proxy
  that terminates the daemon's TLS sits inside this same boundary; see
  [What the ceremony proves](#bootstrap-enrollment-ceremony).
- The network, the reverse proxy, containers, the application database and
  the Compose environment are **untrusted** for identity purposes.

| # | Threat | Attack | Control | Residual |
| --- | --- | --- | --- | --- |
| T1 | Host impersonation | An attacker calls the pull API as the enrolled host to read approvals or submit fake status | Every request is signed with the host-held key; no shared secret exists to steal from the environment; enrollment needs both owner and host administrator; IP and hostname are ignored | Theft of the private key through host compromise (R1, R7) |
| T2 | API impersonation / MITM | A proxy or network attacker injects approvals or a fake "revoked" or "active" status | Responses are signed by the pinned per-enrollment key and bound to the request nonce; enrollment only over loopback or validated TLS; fingerprint comparison; chained response-key rotation with bounded lifetime | A compromised API holds the response key, and a compromised proxy during enrollment can substitute it (both R2) |
| T3 | Replay | Captured requests, responses or approvals are resent | Verification order signature, timestamp window of ±60 seconds, nonce cache, then counter, so replays are rejected without quarantine; one request in flight; nonce echo in responses; approvals carry ID, expiry, installation, epoch and immutable target; consumed approval IDs are journaled; #2665 replay high-water marks | Host clock tampering by a host administrator (part of R1) |
| T4 | Release downgrade | An approval or cached metadata points to an older or cross-channel release | The daemon verifies the signed manifest and enforces per-trust-root and per-channel high-water marks (#3116); approvals cannot lower the sequence; recovery is a separate verified plan | None beyond an authorized-signer compromise (R3) |
| T5 | Stale approval | An approval is used after policy, topology, schema, window or preflight changed, or after revocation | Short expiry (the #2665 contract proposes 5 minutes at most); binding to policy and topology fingerprints; a signed re-confirmation before the first side effect; revocation invalidates pending approvals | An operation already past its first side effect finishes to a safe checkpoint (by design, #3117) |
| T6 | Compromised environment or configuration | An attacker reads or edits the Compose `.env`, container environment, generated Compose or daemon configuration | No secrets in environment variables at all; the daemon configuration holds no secrets; the API URL cannot redirect because of the pinned response key; host policy is root-owned and journaled; configuration fingerprints are bound into approvals; API environment settings cannot create enrollment or automatic permission | An attacker with host root can change anything (R1) |
| T7 | Unauthorized consent expansion | An API change, settings write, restore or channel switch turns discovery consent or enrollment into automatic updates, or widens window, channel or scope | Three separate grants; host execution mode set only on the host, default `none`; automatic permission needs both sides; every pull-delivered operation bound by host window and policy; rotation cannot change grants; revocation resets the mode; enrollment records outside application-database restore; echoed counter and state revision detect rollback; new enrollments inherit nothing | A prohibited restore of the host-state store can re-show a revoked enrollment the daemon never saw revoked (R4) |
| T8 | Confused deputy | The API is tricked into issuing a valid approval for another installation or target | Approvals are bound to installation ID, enrollment epoch, key ID and immutable signed target; the daemon checks all of them against its own state and host policy; a "manual" label grants no exception from host policy | A compromised API can still pick a target, and label it manual, within what host policy and execution mode allow (R2) |
| T9 | Stolen enrollment code | The code is shoulder-surfed or intercepted | The code has at least 128 bits of entropy, expires in 10 minutes, binds exactly one key, allows five attempts, travels only over loopback or validated TLS, and the administrator compares fingerprints before approval | A distracted administrator approves a mismatching fingerprint (R6) |
| T10 | Enrollment flooding / denial of service | Mass enrollment or pull requests | One pending invitation; five-attempt limit; per-source, per-installation and global rate limits; the #2665 polling bounds (at least 60 seconds between polls, backoff up to 15 minutes); bounded request sizes | Availability only; no authority gained |
| T11 | Credential leakage through logs or status | Keys, codes or signed material appear in logs, status, support bundles or UI | Bounded reason codes; fingerprints and IDs only; redaction tests (#3114, #3119) | None beyond the #2665 redaction limits |
| T12 | Cloned host (backup or snapshot) | A restored or copied host runs with the same key | Keys are excluded from backups; fork evidence (a lower counter with a later timestamp, or one counter with two digests) quarantines the key; the epoch and installation ID are pinned | Until the original or the clone sends its next request, the API cannot tell them apart (R1 if the clone came from host access) |

### Fail-closed decision table

| Condition observed by the daemon | Result |
| --- | --- |
| Not enrolled, revoked, quarantined or expired | No approvals requested and no status reported |
| Pending | Signed enrollment-status polling only |
| Response unsigned, signature invalid, nonce mismatch, unexpected epoch, lower echoed counter or lower state revision | Discard the response; no admission; `NeedsOperator` for epoch changes and rollback |
| Installation ID missing, transient or different from the pinned value | Refuse to enroll; `NeedsOperator` when enrolled |
| API unreachable or timed out | No new admission; running work follows #3117 checkpoints |
| Clock skew beyond the window, or clock source uncertain | No admission |
| Key storage permissions invalid, or key unreadable | Refuse to run enrolled |
| Host policy missing, unparseable, or its revision changed after approval | Reject the approval |
| Host execution mode `none` | Ignore all approvals |
| Host execution mode `manual` | Ignore automatic approvals; one-time requests only, inside the host window and policy |
| Any approval outside the host window, channel, downtime or backup policy | Reject, whatever its claimed origin |
| Approval expired, consumed, for another installation or target, or a downgrade | Reject and journal the outcome |
| Replay or high-water state missing or rolled back | Hold for trusted recovery; never reset |

## Audit

The API audit trail, kept outside restored application state, and the host
journal both record:

- invitation created, expired or used;
- enrollment requested, approved or rejected (with actor and fingerprints);
- key rotated, revoked or quarantined, and suspected clones;
- host execution mode changes, and automatic-update permission granted or withdrawn on each side;
- approvals issued, consumed and rejected.

Each record carries bounded reason codes. No record contains codes, private
keys, signatures over secret payloads, tokens or raw remote errors, as required
by the #2665 contract.

## Accepted runtime risks

The repository owner accepted these risks for the bounded single-installation
model (recorded on
[#3124](https://github.com/OlyForge3D/PrintFarmer/issues/3124)). They are
listed so nobody mistakes them for things the daemon prevents. IDs are kept
stable for cross-references; R5 and R9 are folded into R1 and R2, and R8 moves
to [Process notes](#process-notes).

| ID | Risk | Why it remains | Position |
| --- | --- | --- | --- |
| R1 | Host root or administrator compromise, including clock manipulation | The daemon controls Docker and is root-equivalent. Someone with host root can read the key, edit host policy, forge journals and skew the clock to stretch expiry windows | Accept. The daemon cannot defend against its own host's administrator. It rejects clocks it cannot trust. Exporting or backing up the audit log off the host is optional guidance that helps after an incident; it is not a required control |
| R2 | API compromise with a valid response-signing key, including a compromised proxy or API during enrollment | The API holds the response-signing key, so a compromised API can sign authentic approvals. During enrollment, a compromised proxy or API can substitute the response key and rewrite the fingerprint the administrator sees | Accept with the current bounded controls: the daemon independently checks release signatures, host policy, the host window and high-water marks, for manual and automatic approvals alike. In execution mode `none` a compromised API can only withhold updates or misreport status. In `manual` or `automatic` it can at most trigger a signed, policy-allowed update inside the host window. Loopback or direct same-host enrollment is preferred because it keeps the proxy out of the path |
| R3 | A compromised authorized release signer or workflow | A malicious release signed by the trusted workflow passes verification | Accept. This is the existing #2665 authorized-signer risk; the daemon does not introduce it |
| R4 | A host-state restore brings back a revoked enrollment | Application-database restores cannot do this, and the echoed counter catches most host-state rollbacks. A restore of the protected host-state store to a point after the daemon's last acknowledged request, but before a revocation it never saw, is undetectable | Accept. Runbook rule: **revoke the enrollment again after any host-state restore.** Off-host audit is not required |
| R6 | The administrator approves a mismatched fingerprint | People make comparison mistakes | Accept. Prominent fingerprint UI copy (#3120) is enough |
| R7 | Software key storage on most hosts | The daemon's private key proves to the API that approval pulls come from the real enrolled host. On Linux it is a software key that root and the daemon service account can read, so anyone who steals it can impersonate the host to the API | Accept. File permissions (`0600` file in a `0700` directory, owned by the daemon service account) are sufficient. A stolen key lets its holder read approvals and report false status as the host, but it runs nothing on the host, and revocation cuts it off. A TPM-backed key is optional best effort and not in scope |

## Process notes

These are not runtime risks of the daemon, but readers should know them.

- **Self-attested review (formerly R8).** Every squad agent acts with the
  owner's authority, so the agent review panel on this design is not
  independent and is not separation of duties. See
  [Repository verdict evidence](../.github/copilot-instructions.md#repository-verdict-evidence)
  for what the verdict gate does and does not prove.

## Validation expectations for implementation children

This design is prose. It proves nothing about enforcement. The children must
turn it into tests:

- **#3114:** Key storage permission validation. No daemon secret in canonical
  or generated Compose configurations. Logs and status redacted. Disabled by
  default. Covered by `HostUpdateDaemonIdentityStorageTests`,
  `HostUpdateDaemonComposeTests`, `HostUpdateDaemonTests` and
  `HostUpdateCliDaemonTests`.
- **#3115:** Signed-request verification, nonce, counter and timestamp
  rejection. Signed responses. Revoked and quarantined states. Approvals bound
  to installation and epoch. camelCase and string-enum contracts.
- **#3116:** Downgrade, cross-channel and replay rejection independent of the
  API. `HostUpdateDaemonReleaseVerifier` checks the pinned trust root, a
  bounded approval lifetime, the operator trusted root's validity, the
  manifest digest and cosign signature, the manifest's binding, channel and
  complete per-platform image set, and the read-only replay high-water mark.
  It records hash-chained evidence before accepting, and the dispatcher
  refuses expired or mismatched verification. Covered by
  `HostUpdateDaemonReleaseVerifierTests` and `HostUpdateDaemonTests`.
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
environment variables. Also cover: verification order (a forged or replayed
request never advances the counter or triggers quarantine), quarantine only on
fork evidence, host execution mode `none` blocking every pull operation
including manual approvals, a missing `installation.id` failing enrollment
instead of generating a fallback, a response echoing a stale counter or state
revision after a protected `HostUpdates:HostState` rollback to a point older
than the daemon's last acknowledged counter and revision, an
application-database restore that leaves enrollment state unchanged, and invitation-attempt
exhaustion.
