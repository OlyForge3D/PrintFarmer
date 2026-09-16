## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

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
