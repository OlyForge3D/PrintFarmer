# Offline update recovery

This document records the operational constraints for recovery tooling that
imports fixture images and runs Docker Compose against a live daemon.

## Recovery-matrix daemon isolation

The recovery matrix uses canonical image names while it builds and loads
fixtures. Two runs on the same Docker daemon can retag or remove the image
digest used by the other run. The matrix therefore requires **one live run per
Docker daemon**.

Use `scripts/ci/recovery-matrix/run-cell.sh` as the outer entrypoint for a
matrix command:

```bash
scripts/ci/recovery-matrix/run-cell.sh -- ./run-recovery-cell.sh c2
```

The entrypoint atomically creates the named internal Docker-network lock
`printfarmer-recovery-matrix-daemon-lock`, labels it with the run owner, and
holds it until the command exits. A competing run fails before it creates
containers, networks, or images, with exit status `75` and the current lock
owner in the error message. Cleanup removes only a lock that is still owned by
the current run.

If a runner is interrupted after its resources have already been removed, an
operator can inspect and release the stale lock:

```bash
scripts/ci/recovery-matrix/run-cell.sh --release-lock
```

Stale-lock release is fail-closed: it refuses to remove the lock while any
container, volume, or network labeled for the recorded run remains. Do not
delete the lock manually while an active run may still own resources.

The current development branch intentionally does not enable a recovery-matrix
workflow or restore the removed HostUpdate CLI packaging. This guard is kept
as the supported boundary for the future matrix runtime; reintroducing a
workflow requires first restoring or redesigning that runtime and adding live
validation.
