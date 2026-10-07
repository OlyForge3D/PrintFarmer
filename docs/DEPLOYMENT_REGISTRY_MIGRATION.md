# Build-Free Registry Deployment Migration

This guide converts an existing git-checkout deployment created by
`scripts/deploy-docker.sh` into a deployment that runs and updates only from
published GHCR release images. After migration, the host needs no source
checkout and never builds an image.

The controller is `scripts/registry/printfarmer_registry.py` (tracking issue
[#3295](https://github.com/OlyForge3D/PrintFarmer/issues/3295)). It needs only
Python 3 (standard library) and the Docker CLI with Compose v2.

## What Migration Preserves

The migration is in place. It derives a release Compose file from the
deployment's own Compose files, project name, and active profiles, then verifies
the result against the running containers before writing anything.

| Preserved | How |
|---|---|
| Compose project name | Discovered from running containers or `--project` |
| Named volumes and bind sources | Compared with running container mounts; any difference fails closed |
| `.env` and `.deploy-config` | Never rewritten or printed; `.env` is passed only to `docker compose --env-file` |
| Database provider and optional services | Taken from the active profiles and rendered services |
| OrcaSlicer custom-profile volume | Release worker `orcaslicer.version` label must match the deployment |

The controller never runs `docker compose down`, never removes volumes, and
never rotates credentials. Resolved configuration stays in memory, and
differences are reported as field paths, never values.

## Unsupported Configurations

Migration or update fails closed when:

- The Moonraker emulator is enabled. It has no published image; remove it first.
- The previous-version OrcaSlicer worker is enabled. It has no published image.
- The OrcaSlicer worker runs on a non-amd64 host. The worker image is amd64-only.
- A release's OrcaSlicer version differs from the deployment's. The worker
  custom-profile volume name includes the OrcaSlicer version, so accepting a
  different version would silently start on a new, empty profile volume. There
  is no supported adoption path yet; choose releases whose worker matches the
  recorded `orcaslicerVersion` in `.printfarmer/deployment.json`. Do not edit
  `.env`, `deployment.json`, or the release Compose file by hand to work around it.
- An insider release is requested without `--allow-insider`.
- Any selected image cannot be pulled, or its labels, platform, or digest do not
  match the release's `container-images.json`.

## Procedure

### 1. Back up

Take a database backup and copy the data volumes or `EXTERNAL_*_PATH` bind
directories before any update. Database migrations run on API startup and are
not reversed by changing images. The `update` and `rollback` commands require
`--backup-confirmed`.

### 2. Preview the migration

Run from the existing checkout's repository root:

```bash
python3 scripts/registry/printfarmer_registry.py migrate --dry-run --deployed-commit HEAD
```

Locally built images do not carry a source revision label, so pass the full
40-character commit SHA that was deployed, or `HEAD` when the checkout matches
the running build. The plan lists original Compose files, active profiles, each
service's target release image, vendored files, and the OrcaSlicer volume
version. A dry run writes nothing and touches no containers.

Add `--project <name>` when the project cannot be discovered, or
`--allow-new-services` when a configured service has never been created.

### 3. Migrate

```bash
python3 scripts/registry/printfarmer_registry.py migrate --deployed-commit HEAD
```

This writes, without restarting anything:

- `docker-compose.release.yml` — image-only services, no `build:` stanzas.
- `.printfarmer/deployment.json` — project, profiles, services, and origin commit.
- `.printfarmer/files/` — vendored copies of tracked bind-mounted files, such as
  the telemetry collector configuration.
- `.printfarmer/bin/printfarmer-registry` — a copy of the controller.

### 4. Plan and apply a release

```bash
.printfarmer/bin/printfarmer-registry plan --version 1.2.3
.printfarmer/bin/printfarmer-registry update --version 1.2.3 --backup-confirmed
```

`plan` shows old and new images per service, source lineage, and named volume
identity, and changes nothing. `update` then:

1. Fetches `container-images.json` from the GitHub release `v<version>`, or
   reads a reviewed copy from `--manifest-file`.
2. Verifies the target commit descends from the deployed commit through the
   GitHub compare API. This step needs online GitHub API access even with
   `--manifest-file`; an unknown or unreachable lineage is refused, never
   treated as proven. Set `GITHUB_TOKEN` to avoid rate limits. Override with
   `--allow-unsafe-downgrade` only after a deliberate review. The host is
   therefore not air-gapped-capable without that override.
3. Pulls and verifies every selected image by digest before stopping anything.
4. Re-checks storage identity, then runs `docker compose up -d --no-build --pull never`.
5. Waits for container health (`--health-timeout`, default 600 seconds) and only
   then records the release as current.

The OrcaSlicer worker's `ORCASLICER_CONTAINER_DIGEST` is set by the controller
to the release's exact multi-platform index digest and passed through the
process environment, which overrides the value in `.env`. The API compares this
value as an opaque string. Always start the release through the controller: a
manual `docker compose up` would fall back to the stale digest in `.env`.

A failed pull leaves the deployment untouched. A failure or Ctrl-C after start
leaves `.printfarmer/pending.json`; inspect `status`, then rerun with
`--resume-interrupted`. A malformed pending file is reported, not guessed at.

Concurrent runs are blocked by `.printfarmer/lock`, which records the holder's
PID and start time. Locks are never taken over automatically. If `status` shows
no other run is active on the host, delete the lock file and retry.

### 5. Verify

```bash
.printfarmer/bin/printfarmer-registry status
docker compose -p <project> ps
curl -fsS http://localhost:<published-http-port>/healthz
```

### 6. Remove the source checkout (optional)

After a verified update, the source tree is no longer used. The exact host
paths to keep depend on the deployment's profiles and `EXTERNAL_*_PATH`
settings, so do not rely on a static list. `migrate` prints them, derived from
the resolved configuration, and `status` re-checks each one:

```bash
.printfarmer/bin/printfarmer-registry status
```

Keep every listed path, typically `.env`, `.deploy-config`,
`docker-compose.release.yml`, `.printfarmer/`, data directories, and generated
configuration such as `deploy/nginx/` or monitoring directories. `status` exits
non-zero and marks a path `MISSING` if any is removed.

Do not run `scripts/deploy-docker.sh` against a migrated deployment; it would
rebuild from source and replace the release Compose project.

## Rollback

```bash
.printfarmer/bin/printfarmer-registry rollback --backup-confirmed --allow-unsafe-downgrade
```

Rollback switches images to the previous release only. It does not restore
data, and `--backup-confirmed` only attests that a backup exists. Because the
previous release is behind the current one, lineage verification always refuses
it unless `--allow-unsafe-downgrade` acknowledges the schema risk. If the newer
release applied database migrations, restore the backup first; older images
may not start against a newer schema. Rollback to the original git-checkout build is not
automated; restore the checkout and run `scripts/deploy-docker.sh` instead.

## Updating the Controller

The vendored controller is pinned at migration time. To use a newer controller,
download it from the release tag, review it, and replace the vendored copy:

```bash
curl -fsSLo .printfarmer/bin/printfarmer-registry \
  https://raw.githubusercontent.com/OlyForge3D/PrintFarmer/v1.2.3/scripts/registry/printfarmer_registry.py
chmod 755 .printfarmer/bin/printfarmer-registry
```

## Limitations

- `container-images.json` is not signed. Trust rests on GitHub TLS, digest
  pinning, and image label checks.
- Lineage verification needs GitHub API access.
- The procedure has not been exercised against a production host. Run the dry
  run and `plan` first, and keep the backup until the update is verified.

## Testing

```bash
python3 -m unittest discover -s scripts/registry/tests -v
```

The suite uses a fake Docker CLI and, when `docker compose` is available, real
Compose rendering. CI runs it in `.github/workflows/deployment-tests.yml`.
