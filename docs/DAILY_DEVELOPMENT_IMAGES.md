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

The immutable tag is `sha-<full-development-commit>`. The workflow does not publish
or consume `latest`. PostgreSQL and the nginx reverse proxy remain official upstream
images and are not republished.

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
test "$(jq -r '.images | length' "$IMAGE_SET")" -eq 5
```

To test a specific commit instead, find its successful workflow run and pass that
run ID to `gh run download`. The manifest's `commit` field is authoritative.

## Export Digest-Pinned Images

Export all five references from the same manifest:

```bash
export PRINTFARMER_API_IMAGE="$(jq -er '.images.api.reference' "$IMAGE_SET")"
export PRINTFARMER_FRONTEND_IMAGE="$(jq -er '.images.frontend.reference' "$IMAGE_SET")"
export PRINTFARMER_SLICER_HOST_IMAGE="$(jq -er '.images["slicer-host"].reference' "$IMAGE_SET")"
export PRINTFARMER_PRINTER_DISCOVERY_IMAGE="$(jq -er '.images["printer-discovery"].reference' "$IMAGE_SET")"
export PRINTFARMER_ORCASLICER_WORKER_IMAGE="$(jq -er '.images["orcaslicer-worker"].reference' "$IMAGE_SET")"
export ORCASLICER_CONTAINER_DIGEST="$(jq -er '.images["orcaslicer-worker"].digest' "$IMAGE_SET")"

printf 'Using PrintFarmer development commit %s\n' "$COMMIT_SHA"
```

These values use `image@sha256:...`, so Compose cannot silently substitute a
different image even if a mutable registry tag changes.

## Start the Local Validation Stack

Generate the existing microservice deployment with PostgreSQL, discovery,
slicer-host, and one OrcaSlicer worker:

```bash
export POSTGRES_PASSWORD="$(openssl rand -base64 24)"
export Jwt__Key="$(openssl rand -base64 48)"
export WORKER_SHARED_API_KEY="$(openssl rand -hex 32)"
export DISCOVERY_SHARED_API_KEY="$(openssl rand -hex 32)"
export ConnectionStrings__Default="Host=database;Port=5432;Database=printfarmer;Username=printfarmer;Password=$POSTGRES_PASSWORD"
export DB_PROVIDER=Postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_ORCA_WORKER=yes
export ORCA_WORKER_COUNT=1

./scripts/docker/compose-generator.sh \
  --architecture microservices \
  --db-provider postgres \
  --enable-orca-worker yes \
  --include-discovery \
  --exclude-monitoring \
  --exclude-telemetry \
  --output-dir "$STACK_DIR"

mkdir -p "$STACK_DIR/deploy"
cp -R deploy/nginx "$STACK_DIR/deploy/nginx"

docker compose \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  pull

docker compose \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  up -d --scale orcaslicer-worker=1
```

The daily validation override runs the API normally in `Development` and explicitly
enables the TestEmulator's three simulated printers. It disables periodic network
discovery so local validation does not probe the physical network. The stack contains
one upstream PostgreSQL container and exactly one OrcaSlicer worker.

Wait for the application images to become healthy before starting local UI
validation:

```bash
docker compose \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  ps

curl --fail --retry 30 --retry-delay 5 http://localhost:5245/healthz
curl --fail --retry 30 --retry-delay 5 http://localhost:8080/
```

This build workflow performs only image-level smoke checks. Browser and Playwright
validation remains a local automation responsibility.

## Cleanup

Use the same Compose file set so all containers, networks, and volumes belong to the
selected stack:

```bash
docker compose \
  -f "$STACK_DIR/docker-compose.yml" \
  -f scripts/docker/compose-templates/docker-compose.daily-registry.yml \
  -f scripts/docker/compose-templates/docker-compose.daily-validation.yml \
  down --volumes --remove-orphans

rm -rf "$STACK_DIR"
unset PRINTFARMER_API_IMAGE PRINTFARMER_FRONTEND_IMAGE
unset PRINTFARMER_SLICER_HOST_IMAGE PRINTFARMER_PRINTER_DISCOVERY_IMAGE
unset PRINTFARMER_ORCASLICER_WORKER_IMAGE POSTGRES_PASSWORD Jwt__Key
unset ORCASLICER_CONTAINER_DIGEST
unset WORKER_SHARED_API_KEY DISCOVERY_SHARED_API_KEY ConnectionStrings__Default
```
