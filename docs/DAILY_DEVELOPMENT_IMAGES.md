## Daily Development Images

The **Daily development microservice images** workflow builds the complete
repository-owned microservice image set from the exact `development` branch HEAD.
It runs every day at `09:17 UTC` and can also be started manually.

A push to `development` does not trigger this expensive image workflow. The normal
CI workflows already validate each push; the daily schedule and manual dispatch
provide the deployment image cadence without duplicating every development build.

## Published Images

Every successful run publishes these Linux AMD64 images:

| Service | GHCR image |
|---|---|
| API | `ghcr.io/olyforge3d/printfarmer-api` |
| Frontend | `ghcr.io/olyforge3d/printfarmer-frontend` |
| Slicer host | `ghcr.io/olyforge3d/printfarmer-slicer-host` |
| Printer discovery | `ghcr.io/olyforge3d/printfarmer-printer-discovery` |
| OrcaSlicer worker | `ghcr.io/olyforge3d/printfarmer-orcaslicer-worker` |
| Moonraker emulator | `ghcr.io/olyforge3d/printfarmer-moonraker-emulator` |

Each publication gets a never-reused tag:
`sha-<full-development-commit>-run-<workflow-run-id>-attempt-<attempt>`. GHCR does not
provide server-enforced tag immutability, so the workflow avoids tag reuse and verifies
the published image identity. It does not publish or consume `latest`. PostgreSQL and
the nginx reverse proxy remain official upstream images and are not republished.

Each image is built once, smoke checked, and saved as a short-lived workflow artifact
before publication starts. The workflow publishes those exact tested image archives,
records their registry digests, and uploads one
`daily-development-image-set` artifact. Its `image-set.json` contains the tested
commit and the digest-pinned reference for every image. Local automation must treat
that file as the atomic release unit and must not combine references from different
runs.

## Authentication

Public GHCR packages can be pulled anonymously. If the package visibility requires
authentication, use a GitHub token that can read packages:

```bash
gh auth status
gh auth token | docker login ghcr.io -u "$(gh api user --jq .login)" --password-stdin
```

Do not store the token in the repository or a Compose environment file.

### Publishing access

The workflow publishes with its repository-scoped `GITHUB_TOKEN` and grants
`packages: write` only to the publication job. It does not use a personal access
token.

Each existing `printfarmer-*` package must be connected to
`OlyForge3D/PrintFarmer`. In the package settings, either enable **Inherit access
from source repository** or add `OlyForge3D/PrintFarmer` with **Write** under
**Manage Actions access**. Package settings use this URL pattern:

```text
https://github.com/orgs/OlyForge3D/packages/container/<package>/settings
```

The publication job authenticates to GHCR before downloading the validated image
archives. GitHub does not provide a non-mutating API that proves package write
access, so the workflow classifies the actual push failure instead of creating a
throwaway tag. A `permission_denied: write_package` failure reports the exact
package settings URL and access correction; unrelated registry or network failures
remain distinct. The validated images also must carry
`org.opencontainers.image.source=https://github.com/OlyForge3D/PrintFarmer`, which
allows newly created packages to link to this repository.

## Select the Latest Successful Set

Run these commands from the repository root. They deliberately select a successful
run on `development`, then download the artifact from that exact run:

```bash
STACK_DIR=".daily-validation"
rm -rf "$STACK_DIR"
mkdir -p "$STACK_DIR"

RUN_ID="$(
  gh run list \
    --workflow daily-development-images.yml \
    --branch development \
    --status success \
    --limit 1 \
    --json databaseId \
    --jq '.[0].databaseId'
)"
test -n "$RUN_ID"

gh run download "$RUN_ID" \
  --name daily-development-image-set \
  --dir "$STACK_DIR/release"

IMAGE_SET="$STACK_DIR/release/image-set.json"
COMMIT_SHA="$(jq -er '.commit' "$IMAGE_SET")"
test "$(jq -r '.images | length' "$IMAGE_SET")" -eq 6
```

To test a specific commit instead, find its successful workflow run and pass that
run ID to `gh run download`. The manifest's `commit` field is authoritative.

## Export Digest-Pinned Images

Export all six references from the same manifest:

```bash
export PRINTFARMER_API_IMAGE="$(jq -er '.images.api.reference' "$IMAGE_SET")"
export PRINTFARMER_FRONTEND_IMAGE="$(jq -er '.images.frontend.reference' "$IMAGE_SET")"
export PRINTFARMER_SLICER_HOST_IMAGE="$(jq -er '.images["slicer-host"].reference' "$IMAGE_SET")"
export PRINTFARMER_PRINTER_DISCOVERY_IMAGE="$(jq -er '.images["printer-discovery"].reference' "$IMAGE_SET")"
export PRINTFARMER_ORCASLICER_WORKER_IMAGE="$(jq -er '.images["orcaslicer-worker"].reference' "$IMAGE_SET")"
export PRINTFARMER_MOONRAKER_EMULATOR_IMAGE="$(jq -er '.images["moonraker-emulator"].reference' "$IMAGE_SET")"
export ORCASLICER_CONTAINER_DIGEST="$(jq -er '.images["orcaslicer-worker"].digest' "$IMAGE_SET")"

printf 'Using PrintFarmer development commit %s\n' "$COMMIT_SHA"
```

These values use `image@sha256:...`, so Compose cannot silently substitute a
different image even if a mutable registry tag changes.

## Start the Local Validation Stack

Generate the existing microservice deployment with PostgreSQL, discovery,
slicer-host, one OrcaSlicer worker, and the Moonraker protocol emulator:

```bash
export POSTGRES_PASSWORD="$(openssl rand -base64 24)"
export POSTGRES_USER=printfarmer
export Jwt__Key="$(openssl rand -base64 48)"
export WORKER_SHARED_API_KEY="$(openssl rand -hex 32)"
export DISCOVERY_SHARED_API_KEY="$(openssl rand -hex 32)"
export ConnectionStrings__Default="Host=database;Port=5432;Database=printfarmer;Username=printfarmer;Password=$POSTGRES_PASSWORD"
export DB_PROVIDER=Postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_ORCA_WORKER=yes
export ORCA_WORKER_COUNT=1
export COMPOSE_PROJECT_NAME=printfarmer-daily-validation
export API_PORT=5245
export SLICER_HOST_PORT=15246
export HTTP_PORT=3000
export HTTPS_PORT=18443
export POSTGRES_PORT=15432
export MOONRAKER_EMULATOR_PORT=17125
export MOONRAKER_EMULATOR_PRINTING_PORT=17126
export MOONRAKER_EMULATOR_PAUSED_PORT=17127
export MOONRAKER_EMULATOR_SHUTDOWN_PORT=17128

./scripts/docker/compose-generator.sh \
  --architecture microservices \
  --db-provider postgres \
  --enable-orca-worker yes \
  --include-discovery \
  --include-moonraker-emulator \
  --exclude-monitoring \
  --exclude-telemetry \
  --output-dir "$STACK_DIR"

mkdir -p "$STACK_DIR/deploy"
cp -R deploy/nginx "$STACK_DIR/deploy/nginx"
./scripts/generate-certs.sh "$STACK_DIR/deploy/nginx/certs"

docker compose \
  --project-name "$COMPOSE_PROJECT_NAME" \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  pull

docker compose \
  --project-name "$COMPOSE_PROJECT_NAME" \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  up -d --scale orcaslicer-worker=1
```

The daily validation override runs the API normally in `Development` and enables the
API's own deterministic Moonraker-backed seed fixtures
(`MoonrakerEmulatorSeed__Enabled`), plus the printer-discovery service's matching
deterministic discovery fixtures (`Discovery__DeterministicFixtures__Enabled`). The
seed fixtures point at four isolated instances of the same digest-pinned emulator
image — `moonraker-ready`, `moonraker-printing`, `moonraker-paused`, and
`moonraker-shutdown` — plus a fifth, `moonraker-offline`, that is deliberately **not**
a running service, so the seeded "Moonraker Offline" printer exercises a real
connection failure. None of this uses the in-process TestEmulator plugin. It disables
periodic network discovery so local validation does not probe the physical network.
The stack contains one upstream PostgreSQL container, exactly one OrcaSlicer worker,
and four Moonraker emulator instances (the repository's "exactly one" rule applies
only to the OrcaSlicer worker, not to these emulator replicas of a single image).
The dedicated project name, reset container names, and isolated network prevent
cleanup from targeting an existing PrintFarmer deployment. The API defaults to
`http://127.0.0.1:5245` and the frontend (through nginx-proxy) to
`http://127.0.0.1:3000`, matching the deterministic validation harness. Container
ports remain `5245` for the API and `80` for nginx-proxy; internal service URLs
are unchanged. Stop any local development servers occupying these host ports
before starting validation. For a custom mapping, set `API_PORT` and `HTTP_PORT`
and match the harness's `API_BASE_URL` and `BASE_URL` to them. Clear stale port
exports or update your validation environment when regenerating an older stack.
Every published port binds to `127.0.0.1`; the authentication-bypass validation stack is
therefore reachable only from the local machine and must not be exposed externally.
The validation override also removes the discovery service's host Docker socket mount.

Every emulator instance has no Docker socket mount and no external network or service
dependencies. Each instance is internal-only by default (reachable only on the
internal `printfarmer-network`, e.g. `http://moonraker-ready:7125`); the validation
overlay is the only place that publishes any of them, and only to loopback, one
distinct port per instance. Its control API (`Emulator__EnableControlApi`) is off in
the base template and on only in this validation overlay, so scenario/fault-injection
endpoints are never reachable from the production compose template. See
[`MOONRAKER_EMULATOR_VALIDATION.md`](./MOONRAKER_EMULATOR_VALIDATION.md) for the
emulator's health/control surface, seeded scenarios, and supported/unsupported protocol
fidelity boundary.

Wait for the application images to become healthy before starting local UI
validation:

```bash
docker compose \
  --project-name "$COMPOSE_PROJECT_NAME" \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  ps

curl --fail --retry 30 --retry-delay 5 http://localhost:5245/healthz
curl --fail --retry 30 --retry-delay 5 http://localhost:3000/
curl --fail --retry 30 --retry-delay 5 http://localhost:17125/healthz
curl --fail --retry 30 --retry-delay 5 http://localhost:17126/healthz
curl --fail --retry 30 --retry-delay 5 http://localhost:17127/healthz
curl --fail --retry 30 --retry-delay 5 http://localhost:17128/healthz
```

This build workflow performs only image-level smoke checks. Browser and Playwright
validation remains a local automation responsibility. A separate Compose-level smoke
script (`scripts/ci/smoke-daily-validation-stack.sh`) boots this exact stack when
Docker is available and asserts the seeded Moonraker-backend printers and the
single-worker topology; see
[`MOONRAKER_EMULATOR_VALIDATION.md`](./MOONRAKER_EMULATOR_VALIDATION.md) for details.

## Cleanup

Use the same Compose file set so all containers, networks, and volumes belong to the
selected stack:

```bash
docker compose \
  --project-name "$COMPOSE_PROJECT_NAME" \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  down --volumes --remove-orphans

rm -rf "$STACK_DIR"
unset PRINTFARMER_API_IMAGE PRINTFARMER_FRONTEND_IMAGE
unset PRINTFARMER_SLICER_HOST_IMAGE PRINTFARMER_PRINTER_DISCOVERY_IMAGE
unset PRINTFARMER_ORCASLICER_WORKER_IMAGE PRINTFARMER_MOONRAKER_EMULATOR_IMAGE
unset POSTGRES_PASSWORD Jwt__Key
unset POSTGRES_USER
unset ORCASLICER_CONTAINER_DIGEST
unset WORKER_SHARED_API_KEY DISCOVERY_SHARED_API_KEY ConnectionStrings__Default
unset COMPOSE_PROJECT_NAME API_PORT SLICER_HOST_PORT HTTP_PORT HTTPS_PORT POSTGRES_PORT
unset MOONRAKER_EMULATOR_PORT MOONRAKER_EMULATOR_PRINTING_PORT MOONRAKER_EMULATOR_PAUSED_PORT
unset MOONRAKER_EMULATOR_SHUTDOWN_PORT
unset DB_PROVIDER ENABLE_DISTRIBUTED_SLICING ENABLE_ORCA_WORKER ORCA_WORKER_COUNT
```
