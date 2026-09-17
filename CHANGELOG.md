## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

- Request the owner-approved `workflows: write` permission explicitly for the
  canonical reusable publisher while retaining its existing pinned action and
  least-privilege read/write permissions. The consumed tag diagnostic is retired
  from source; its remote workflow and run history are not deleted or altered.
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
