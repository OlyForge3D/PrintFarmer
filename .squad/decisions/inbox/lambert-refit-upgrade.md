# Keep Refit 11 as the active certificate-fix target

**Author:** Lambert
**Date:** 2026-06-03T10:26:17.641-07:00
**Status:** Proposed
**Related issues:** #497

## Problem

`Refit` and `Refit.HttpClientFactory` were pinned to `10.1.6`, which now has a revoked signing certificate and causes NU3012 restore failures in CI.

There was also uncertainty about package layout in the current Refit line: some notes suggested `Refit.HttpClientFactory` might have been folded into the core package.

## Decision

Upgrade the live repository references to `Refit` `11.0.0` and `Refit.HttpClientFactory` `11.0.0`.

Keep `Refit.HttpClientFactory` as an explicit package reference where it already exists, because Refit 11 still publishes and supports it as a separate package.

## Rationale

Refit 11.0.0 is the latest stable release, so it satisfies the dependency-upgrade goal and removes the revoked-certificate package from restore.

Repository validation showed no compile-time breaks after the version bump, which means this codebase is not currently depending on the Refit 10 error-model behaviors called out in the 11.0.0 release notes.

## Impact

CI restore/build paths stop consuming the revoked `10.1.6` package.

If a future hotfix needs the certificate fix without the Refit 11 behavior changes, `10.2.0` is the documented re-signed fallback, but that is no longer the mainline choice for this repo.
