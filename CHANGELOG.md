## [0.2.3]

### Features

- Release publication emits a signed complete immutable release set.

### Fixes

- Publication keeps the release draft until its signed manifest is verified.
- Automatic release qualification runs after read-only admission, with genuine
  same-transaction checks and exact-tree reviewed-PR evidence before publication.

### Breaking changes

- None.
