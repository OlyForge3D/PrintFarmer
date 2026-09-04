# Copilot Processing

## User request
Fix deploy-docker.sh/templates so host-backed slicer artifact storage is created before startup and writable by slicer-host UID/GID 1001, including fresh root-owned bind mounts. Add focused tests for missing directory and idempotent reruns. Preserve ownership strategy and deployment modes. Run relevant tests and commit with trailer.

## Plan
- [x] Read Docker hierarchy instructions and deployment test guidance.
- [x] Trace artifact storage setup and ownership handling in deployment templates.
- [x] Add narrow directory creation/ownership fix in source templates.
- [x] Add/update tests for missing artifacts directory and idempotent reruns.
- [x] Run focused deployment tests and inspect results.
- [ ] Review diff, commit with required trailer, and summarize.

## Summary
Implemented `prepare_slicer_artifact_directories` in `scripts/docker-utils.sh`.
Deployment and direct validation stacks now create the artifact leaf before startup,
align it to UID/GID 1001 when possible, and use a narrowly scoped writable fallback.
Regression tests cover missing-directory creation and idempotent reruns.

Validation:
- `bash -n` passed for all changed shell scripts.
- `git diff --check` passed.
- Direct helper verification passed, including preserving a sentinel on rerun.
- `bash tests/test-deploy-docker.sh` reaches the existing worker permission test but
  fails on Windows Git Bash because filesystem `chmod 777` reports mode `755`; this
  prevents the suite from reaching the new tests. The same assertion requires `777`
  on Linux CI.
