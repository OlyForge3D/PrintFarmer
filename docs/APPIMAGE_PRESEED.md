# AppImage Pre-seed (CI caching) Guide

This repository supports a pre-seeding pattern for the PrusaSlicer and OrcaSlicer AppImages so CI builds don't repeatedly download release assets from GitHub during every Docker build. The strict-build workflows include logic to accept a preseeded AppImage and pass it into the Docker build as a BuildKit secret.

How it works

- The Dockerfiles (`Dockerfile.prusaslicer`, `Dockerfile.orcaslicer`) accept a buildkit secret (default ids: `prusa`, `orca`). If a file is mounted at `/run/secrets/prusa` or `/run/secrets/orca` inside the asset stage, the Dockerfile will copy that file and treat it as the AppImage; it will skip the remote download and extraction steps if present.

- The strict-build workflows (`.github/workflows/prusaslicer-strict-build.yml`, `.github/workflows/orcaslicer-strict-build.yml`) attempt to discover a preseeded artifact and, when available, mount it into the Docker build with `--secret id=<id>,src=<path>` so BuildKit makes it available at `/run/secrets/<id>` during the build.

How to prepare and upload a preseed AppImage for CI

Option A: Upload as a GitHub Actions artifact from a separate job

1. Create a short workflow that runs on your schedule or manually and downloads the desired AppImage (from an official release URL). Name the artifact `prusa-appimage-<version>` or `orca-appimage-<version>`.
2. In the strict-build job, the preseed step will look for that artifact. The current implementation makes a best-effort detection but does not automatically download artifacts due to cross-run access limitations; you can extend this by adding a step to the build workflow that uses the `actions/download-artifact` action when you know which run contains the artifact.

Option B: Store artifacts in an external object storage (S3, blob) and download in the strict-build job

1. Upload the AppImage to an object store with a stable URL.
2. Modify the strict-build workflow's "Attempt to download preseeded ..." step to curl the object store URL and save it to `preseed/prusa.AppImage` or `preseed/orca.AppImage` before the build step.
3. Ensure credentials for the object store are added as repository secrets.

Option C: Use the GitHub Actions build secret feature directly (manual / ephemeral)

1. Locally or from a different job use `docker buildx build --secret id=prusa,src=path/to/PrusaSlicer.AppImage ...` to pass the AppImage during build. This is similar to Option A but done at build invocation time.

Notes and recommendations

- Make sure the AppImage filename is correct and the file is the full AppImage binary (not a small placeholder). The Dockerfiles validate size > ~5MB before trusting the binary.
- If you want strict CI that fails when the binary is missing, set `ALLOW_STUB=false` in the matrix for the strict-build workflow (currently the matrix sets `allow_stub: [false]` which already enforces real binaries for strict builds).
- For reliability and reproducibility, prefer Option B (external object store + stable URL) if you have a shared storage bucket available. That avoids cross-run artifact access complexity.

Flathub / Flatpak note (PrusaSlicer)

- The `Dockerfile.prusaslicer` now contains a fallback that will attempt to install PrusaSlicer from Flathub via Flatpak when AppImage extraction fails. The build stage will:
	- try preseeded AppImage -> try GitHub AppImage download+extraction -> fallback to Flatpak install from Flathub
	- if Flatpak installation succeeds, the Dockerfile attempts to copy relevant installed files into `/prusaslicer-dist/opt/prusaslicer` and a wrapper `/prusaslicer-dist/prusa-slicer` for the runtime stage to use
- Notes and caveats:
	- Installing Flatpak inside a Debian-based container increases image size and requires `ostree`/`flatpak` runtimes. The Dockerfile installs these conditionally during the assets stage when needed.
	- Running apps installed via Flatpak inside containers can require extra runtime dependencies and sandbox handling; the extraction approach (AppImage) remains preferred for lightweight runtime images.
	- If you prefer to cache Flathub artifacts, the uploader can be extended to download the Flathub `.flatpakref` or bundle and upload it as an artifact; strict-build can then pass it into the build similarly to the AppImage pre-seed.

Example snippet to download from S3 in a workflow step

```bash
mkdir -p preseed
aws s3 cp s3://my-bucket/printfarmer/prusa/PrusaSlicer-2.8.0.AppImage preseed/prusa.AppImage
```

After the preseed file is present at `preseed/prusa.AppImage` or `preseed/orca.AppImage`, the strict-build step will pass it into `docker build` with BuildKit `--secret id=prusa,src=preseed/prusa.AppImage` and the Dockerfile will use it.

If you want, I can add an example scheduled workflow that downloads the official AppImage and uploads it as an artifact or to an S3 bucket. Tell me which storage approach you prefer (Actions artifact, S3, or other) and I will implement the scheduled uploader workflow.