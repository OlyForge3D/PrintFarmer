# AppImage Uploader Runbook

This runbook explains how to manually dispatch the AppImage preseed uploader workflow to cache a slicer AppImage as a GitHub Actions artifact. Use this when you want to add a new Prusa or Orca version to the CI cache.

Workflow: `.github/workflows/appimage-preseed-uploader.yml`

UI (GitHub Actions) manual run

1. Go to the repository on GitHub.
2. Click the `Actions` tab.
3. Search or select the workflow titled "AppImage Preseed Uploader".
4. Click on the workflow, then click the "Run workflow" button.
5. Choose the inputs:
   - `slicer`: `prusa` or `orca`
   - `version`: the version string (e.g., `2.8.0` for Prusa, or `1.8.1` for Orca). Leave empty to attempt 'latest'.
6. Click the green "Run workflow" button.
7. After the job finishes, open the workflow run logs. The uploader will save an artifact named like `prusa-appimage-2.8.0.AppImage` which you can download from the run's "Artifacts" panel.

CLI (`gh`) manual run

Prerequisite: Install the GitHub CLI and authenticate (`gh auth login`).

Run the workflow with:

```bash
# Replace slicer and version as needed
gh workflow run appimage-preseed-uploader.yml -f slicer=prusa -f version=2.8.0
```

Wait for the workflow to complete (watch via `gh run watch` or check the Actions UI). To view the runs and download the artifact via CLI:

```bash
# List recent runs of this workflow
gh run list --workflow=appimage-preseed-uploader.yml

# Watch the latest run
gh run watch <run-id>

# Download artifact (if you know the run-id and artifact name)
gh run download <run-id> --name prusa-appimage-2.8.0.AppImage -D ./preseed
```

Notes and best-practices

- The uploader uses `GITHUB_TOKEN` for authenticated API requests. You do not need to provide extra secrets for the manual run.
- Downloaded artifacts in GitHub Actions have a retention period (repo setting defaults to 90 days). If you need long-term stable storage, upload the AppImage to external object storage (S3/GCS/Azure) and update your strict-build workflows to download from that URL.
- If your strict-build job needs to automatically fetch artifacts, I can implement a small REST-based step to locate and download the uploader's artifact by matching artifact name and run created_at timestamp.
- If you want me to remove the uploader's schedule (already removed) or implement S3 upload, say so and I'll implement the next step.

Troubleshooting

- Uploader fails to find asset: check that the `version` input matches the release tag name on the upstream project. For PrusaSlicer, tags are usually `version_X.Y.Z` or sometimes just the version string; the uploader attempts reasonable fallbacks.
- Artifact not found in Actions UI: open the uploader run and check "Artifacts" — the artifact will be visible there if upload succeeded.

Contact me if you want the uploader to also publish to an external bucket (S3/Azure/GCS) for permanent caching.