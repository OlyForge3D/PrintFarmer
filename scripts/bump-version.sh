#!/usr/bin/env bash
set -euo pipefail

# Bump a semantic version stored in the repository `VERSION` file.
# Usage: bump-version.sh <major|minor|patch>

ROOT=$(git rev-parse --show-toplevel)
VERSION_FILE="$ROOT/VERSION"

if [ $# -lt 1 ]; then
  echo "Usage: $0 <major|minor|patch>" >&2
  exit 2
fi
BUMP="$1"

if [ ! -f "$VERSION_FILE" ]; then
  echo "v0.0.0" > "$VERSION_FILE"
fi

CURRENT_RAW=$(cat "$VERSION_FILE" | tr -d ' \n\r\t')
# allow leading v
CURRENT=${CURRENT_RAW#v}

if [[ ! "$CURRENT" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "VERSION file has invalid format: '$CURRENT_RAW'" >&2
  exit 3
fi

IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT"

case "$BUMP" in
  major)
    MAJOR=$((MAJOR + 1))
    MINOR=0
    PATCH=0
    ;;
  minor)
    MINOR=$((MINOR + 1))
    PATCH=0
    ;;
  patch)
    PATCH=$((PATCH + 1))
    ;;
  *)
    echo "Unknown bump type: $BUMP. Use major, minor or patch." >&2
    exit 4
    ;;
esac

NEW_VERSION="v${MAJOR}.${MINOR}.${PATCH}"

echo "Bumping version: ${CURRENT_RAW} -> ${NEW_VERSION}"

printf "%s\n" "$NEW_VERSION" > "$VERSION_FILE"

# Ensure we have tags and full history (works even if checkout was shallow)
git fetch --prune --unshallow --tags || true

# Require a clean working tree to avoid accidental commits of unrelated files
if [ -n "$(git status --porcelain)" ]; then
  echo "Working tree is not clean. Please commit or stash changes before running this script." >&2
  exit 5
fi

# Commit only if VERSION changed
git add "$VERSION_FILE"
if git diff --cached --quiet -- "$VERSION_FILE"; then
  echo "No change to $VERSION_FILE; nothing to commit."
else
  git commit -m "chore(release): bump version to ${NEW_VERSION}"
fi

# Check for existing tag locally or on origin to avoid overwriting
if git rev-parse "$NEW_VERSION" >/dev/null 2>&1; then
  echo "Tag $NEW_VERSION already exists locally; aborting to avoid overwrite." >&2
  exit 6
fi

if git ls-remote --tags origin | grep -q "refs/tags/${NEW_VERSION}$"; then
  echo "Tag $NEW_VERSION already exists on remote 'origin'; aborting to avoid overwrite." >&2
  exit 7
fi

# Configure remote to use token if provided (CI-safe). GITHUB_REPOSITORY should be set in CI.
if [ -n "${GITHUB_TOKEN:-}" ]; then
  if [ -z "${GITHUB_REPOSITORY:-}" ]; then
    echo "GITHUB_REPOSITORY not set; cannot configure remote URL with token." >&2
    exit 8
  fi
  REMOTE_URL="https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"
  git remote set-url origin "$REMOTE_URL"
fi

# Push commit and tag
git push origin HEAD
git tag -a "$NEW_VERSION" -m "Release ${NEW_VERSION}"
git push origin "$NEW_VERSION"

echo "New version ${NEW_VERSION} written, committed, tagged and pushed."
