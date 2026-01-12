#!/usr/bin/env bash
set -euo pipefail

# select-devcontainer.sh
# If the GHCR devcontainer image exists and is pullable, generate a simple devcontainer.json
# that uses the image. Otherwise generate a devcontainer.json that builds from the local Dockerfile.

OWNER="jpapiez"
IMAGE="ghcr.io/${OWNER}/printfarmer-devcontainer:latest"
OUTFILE=".devcontainer/devcontainer.json"

echo "[devcontainer] Checking for GHCR image: $IMAGE"
if docker manifest inspect "$IMAGE" >/dev/null 2>&1; then
  echo "[devcontainer] Found GHCR image; using image in generated devcontainer.json"
  cat > "$OUTFILE" <<EOF
{
  "name": "PrintFarmer (GHCR image)",
  "image": "$IMAGE",
  "postCreateCommand": ".devcontainer/post-create.sh --verify",
  "forwardPorts": [5245,3000]
}
EOF
else
  echo "[devcontainer] GHCR image not found (or docker not running). Falling back to build from Dockerfile"
  cat > "$OUTFILE" <<EOF
{
  "name": "PrintFarmer (local build)",
  "build": { "dockerfile": "Dockerfile" },
  "postCreateCommand": ".devcontainer/post-create.sh --verify",
  "forwardPorts": [5245,3000]
}
EOF
fi

echo "[devcontainer] Generated $OUTFILE"
