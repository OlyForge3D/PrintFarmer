## [Unreleased]

### Features

- Add dry-run-first Mac mini Ralph setup with private unverified staging and
  explicit native-app cutover instructions, discovered policy approval and
  receipt reuse/renewal without manually copying IDs or commit pins (#2935).
- Stage a sole mini coordinator with centralized triage and native Mac/Windows
  consumers using an explicitly supplied private GitHub control repository.
  Durable reserve-before-publish transitions, role gates, opaque receipts and
  local native-session journals retain capacity through uncertainty. Setup
  never initializes the queue, enables schedules or migrates live authority.

### Breaking changes

- Ralph shared host profiles no longer pin deployment workflow UUIDs. IDs remain
  in private configuration; filesystem preflight is non-authorizing and every
  round requires fresh current-execution native identity reconciliation. Existing
  deployments need an owner-approved merged policy pin and updated bootstrap;
  no live workflow, schedule or host configuration is changed automatically.
- Ralph macOS eligibility now includes all issues with hard 1-mobile + 4-general
  work quotas (5 total); Windows permits only 5 general. New Windows mobile SSH
  dispatch is disabled while legacy reconciliation remains available. Mac
  general admission remains blocked on the legacy dispatcher. The new native
  role path requires approved policy, attested cutover and pinned private queue
  genesis before its four general slots become operational.

### Fixes

- Honor **OctoPrint/Slicer API → Require API key** for slicer uploads: allow
  anonymous uploads and queue submission when disabled, while preserving
  authenticated users' queue and printer-group permissions (#2779).

## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

- Request the owner-approved `workflows: write` permission explicitly for the
  canonical reusable publisher while retaining its existing pinned action and
  least-privilege read/write permissions. The consumed tag diagnostic workflow
  file is removed from `development`, retiring future dispatches from that ref;
  no GitHub API operation disables or deletes the remote workflow record, and
  existing run history is unaltered.
- Record the source-only assessment for the unsigned sequence-1 reservation:
  the permanent tag is not a release or recovery, the original discarded HTTP
  422 remains unknown, and no safe recovery operation fits the current
  signed-authority invariants without a new owner decision.
- Release GitHub failures preserve bounded, sanitized validation and permission
  diagnostics without changing request or recovery behavior (#2734).
- Publication keeps the release draft until its signed manifest is verified.
- Automatic release qualification runs after read-only admission, with genuine
  same-transaction checks and exact-tree reviewed-PR evidence before publication.
- Release review verification reads individual commit statuses so genuine
  GitHub Actions creator provenance is retained through collection and authorization.

### Breaking changes

- None.
