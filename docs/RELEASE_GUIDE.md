# Release Guide

This document explains how to create and configure the repository Personal Access Token (PAT) used by the release workflow (`.github/workflows/release.yml`) to push tags so downstream workflows (for example container builds) are triggered.

Why a PAT is required

- GitHub Actions workflows that run using the automatically-provided `GITHUB_TOKEN` cannot create events that trigger other workflows in some scenarios (for example, creating a tag will not trigger workflows that listen for tag pushes). To ensure a tag push triggers the containers/image-publish workflow, the release job must push the tag using a repository Personal Access Token (PAT).

Create a repository PAT (recommended minimal scopes)

1. In GitHub (user account) go to Settings → Developer settings → Personal access tokens → Tokens (classic) (or the new fine-grained token flow if preferred).
2. Create a new token with a descriptive name like `printfarmer-release-pat`.

Minimum required scopes (classic PAT):

- repo (Full control of private repositories)
  - This is required to push commits/tags to the repository.

Optional scopes if you plan to extend the PAT to publish packages or images directly from workflows:

- packages: write (if workflows need to push to GitHub Packages / GHCR directly)

If you create a fine-grained token, grant it the following permissions on the specific repository:

- Repository access: select the PrintFarmer repository
- Permissions:
  - Contents: Read & write
  - Packages: Read & write (optional)

Security recommendations

- Use the smallest scope that meets your needs. If you only need to push tags, `repo` (or repository Contents write in fine-grained) is sufficient.
- Create the PAT from a machine / account that is long-lived and managed (avoid ephemeral accounts).
- Store the PAT in the repository Secrets (see below) and do not commit it anywhere in plaintext.
- Rotate the PAT periodically (for example every 90 days) and document the rotation steps in your team runbook.

Add `REPO_PAT` to repository secrets

1. In the repository, go to Settings → Secrets and variables → Actions → New repository secret.
2. Name: `REPO_PAT`
3. Value: paste the PAT from the previous step.
4. Save.

How the release workflow uses `REPO_PAT`

- The release workflow uses `actions/checkout` with `token: ${{ secrets.REPO_PAT }}` to perform a checkout that is authenticated with the PAT. It then creates a tag and pushes it using the authenticated origin. This push will trigger downstream workflows that listen for tag pushes (for example your container image build & publish workflow).

Troubleshooting

- Linter/validator warnings about `Context access might be invalid: REPO_PAT`:
  - You may see static lint warnings in certain editors or CI checks that attempt to statically validate the workflow YAML where secret usage is flagged. These are warnings about static analysis, not runtime failures. If the secret is present in GitHub Secrets and the workflow is permitted to use it, the runtime will accept the expression `${{ secrets.REPO_PAT }}`.

- Tag push fails with authentication errors:
  - Ensure the PAT has `repo` (or repository contents write) permission and the token was created by a user with push access to the repository.
  - Verify that `REPO_PAT` was added as an Actions secret under the correct repository (not the organization or user settings).

- Downstream workflows do not trigger on tag push:
  - Ensure the downstream workflow triggers include `on: push` with tags or the branches/tags you use (for example `on: push: tags: - 'v*'`).
  - Confirm the tag was pushed by the PAT, not by a run that used `GITHUB_TOKEN` (pushing with GITHUB_TOKEN will sometimes not trigger other workflows).

- Avoid accidental overwrites of existing tags:
  - The `scripts/bump-version.sh` used by release jobs has safety checks: it fetches tags, refuses to run with an unclean working tree, and will abort if the computed new tag already exists locally or remotely.

Maintenance notes

- If you rotate the PAT, update the `REPO_PAT` secret to the new token value.
- If you narrow permissions (for example switch to a fine-grained token), ensure the token has Contents: Read & Write on the repository and any additional package write permissions if you rely on GHCR publishing with the same token.

Contact

- If you need help creating or rotating the PAT or want me to perform a test-run of the release workflow after you add the secret, tell me and I will kick off a test dispatch and verify the tag push and downstream workflow trigger.
