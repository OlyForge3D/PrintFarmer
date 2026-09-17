## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

- Prepare the diagnostic-only Workflows-write hypothesis test for #2741,
  admitting only run 3 / attempt 1 with both prior runs preserved. Parent-approved
  App/installation grants and one invocation remain separate; this is not a
  verified fix, release publication, or unsigned-reservation recovery.
- Add the owner-authorized, fixed-target one-shot tag diagnostic for #2736;
  success creates only the tag and leaves the release reservation incomplete.
- Release GitHub failures preserve bounded, sanitized validation and permission
  diagnostics without changing request or recovery behavior (#2734).
- Publication keeps the release draft until its signed manifest is verified.
- Automatic release qualification runs after read-only admission, with genuine
  same-transaction checks and exact-tree reviewed-PR evidence before publication.
- Release review verification reads individual commit statuses so genuine
  GitHub Actions creator provenance is retained through collection and authorization.

### Breaking changes

- None.
