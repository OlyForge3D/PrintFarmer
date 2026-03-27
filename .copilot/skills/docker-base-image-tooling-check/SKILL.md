---
name: "docker-base-image-tooling-check"
description: "Verify a derived Dockerfile only uses tools guaranteed by its base image, especially for download steps"
domain: "deployment"
confidence: "high"
source: "observed"
tools:
  - name: "view"
    description: "Read the runtime Dockerfile and the base-image Dockerfiles together"
    when: "When a container build fails because a command is missing"
  - name: "bash"
    description: "Validate the fixed image with docker build, docker compose build, and docker run"
    when: "After patching the Dockerfile"
---

## Context

Use this when a Docker build fails in a derived image because a command such as `curl` is missing. The right fix is often to align the derived image with the actual tooling contract of the base image instead of adding more packages to the runtime layer.

## Patterns

- Read the failing runtime Dockerfile and the base-image Dockerfiles together before changing anything.
- Prefer the fetch/download tool already installed by the base image (`wget` in this case) over adding a fresh `apt install curl` layer.
- Keep the fix narrow: replace the command, preserve the artifact paths, and update docs if they describe the build path.
- Validate both `docker build` and the real operator path (`docker compose build <service>`).
- Inspect the built image to confirm the expected files landed where runtime code expects them.

## Examples

- Obico server `ml_api/Dockerfile` failed on `/bin/sh: 1: curl: not found` while downloading model weights.
- `ml_api/Dockerfile.base_amd64` and `ml_api/Dockerfile.base_arm64` both install `wget`.
- Safe fix:

```dockerfile
RUN mkdir -p /model_cache/ml_api/darknet /model_cache/ml_api/onnx \
 && wget -O /model_cache/ml_api/darknet/model-weights.darknet "$(tr -d '\r' < model/model-weights.darknet.url)" \
 && wget -O /model_cache/ml_api/onnx/model-weights.onnx "$(tr -d '\r' < model/model-weights.onnx.url)"
```

## Anti-Patterns

- Assuming common tools like `curl` exist just because the image is Ubuntu-based.
- Fixing a missing-command failure by adding more runtime packages without first checking what the base image already guarantees.
- Declaring success after a raw `docker build` without testing the actual compose/service rebuild path operators use.
