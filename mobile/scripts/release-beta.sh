#!/bin/bash
set -euo pipefail

# Release Beta Script
# Merges development → main, strips .squad/ files, tags, and pushes to origin.
#
# Usage: ./scripts/release-beta.sh <beta-number>
# Example: ./scripts/release-beta.sh 8

BETA_NUM="${1:-}"
if [[ -z "$BETA_NUM" ]]; then
  echo "❌ Usage: $0 <beta-number>"
  echo "   Example: $0 8"
  exit 1
fi

ROOT="$(git rev-parse --show-toplevel)"
VERSION_FILE="$ROOT/VERSION"
SYNC_SCRIPT="$ROOT/scripts/sync-monorepo-version.sh"

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "❌ VERSION file not found at $VERSION_FILE"
  exit 1
fi

BASE_VERSION_RAW="$(tr -d '[:space:]' < "$VERSION_FILE")"
BASE_VERSION="${BASE_VERSION_RAW#v}"

if [[ ! "$BASE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "❌ VERSION must be semantic (vX.Y.Z or X.Y.Z). Found: $BASE_VERSION_RAW"
  exit 1
fi

if [[ -x "$SYNC_SCRIPT" ]]; then
  "$SYNC_SCRIPT" --check
fi

TAG="v${BASE_VERSION}-beta.${BETA_NUM}"
RELEASE_REMOTE="ios-release"
FORBIDDEN_PATHS=(.squad/ .ai-team/ .ai-team-templates/ team-docs/ docs/proposals/)

echo "🚀 Releasing ${TAG}"
echo ""

# Ensure we're on a clean working tree
if [[ -n "$(git status --porcelain)" ]]; then
  echo "❌ Working tree is dirty. Commit or stash changes first."
  exit 1
fi

# Save current branch to return to it later
ORIGINAL_BRANCH=$(git branch --show-current)

# 1. Update development from remote
echo "📥 Fetching latest from remotes..."
git fetch origin

# Safety: ensure local main exactly matches origin/main before release.
# This prevents accidentally pushing a large unrelated local commit stack.
LOCAL_MAIN_SHA=$(git rev-parse main)
REMOTE_MAIN_SHA=$(git rev-parse origin/main)
if [[ "$LOCAL_MAIN_SHA" != "$REMOTE_MAIN_SHA" ]]; then
  echo "❌ main is not synced with origin/main."
  echo "   local main:  $LOCAL_MAIN_SHA"
  echo "   origin/main: $REMOTE_MAIN_SHA"
  echo "   Sync/reset main before running release-beta.sh to avoid unintended pushes."
  exit 1
fi

# 2. Checkout main and merge development
echo "🔀 Merging development → main..."
git checkout main
git merge development --no-edit

# 3. Strip forbidden paths (guard workflow enforces these)
echo "🧹 Stripping forbidden paths from main..."
STRIPPED=false
for path in "${FORBIDDEN_PATHS[@]}"; do
  if git ls-files --error-unmatch "$path" &>/dev/null; then
    git rm --cached -r "$path" 2>/dev/null || true
    STRIPPED=true
    echo "   Removed: ${path}"
  fi
done

if [[ "$STRIPPED" == "true" ]]; then
  git commit -m "chore: strip team state files from main for release"
fi

# 4. Tag
echo "🏷️  Tagging ${TAG}..."

if git rev-parse "$TAG" >/dev/null 2>&1; then
  echo "❌ Tag ${TAG} already exists locally."
  exit 1
fi

if git ls-remote --tags "$RELEASE_REMOTE" | grep -q "refs/tags/${TAG}$"; then
  echo "❌ Tag ${TAG} already exists on ${RELEASE_REMOTE}."
  exit 1
fi

git tag "$TAG"

# 5. Show build number for verification
BUILD_NUM=$(git rev-list --count HEAD)
echo ""
echo "📦 Version: ${BASE_VERSION} | Build: ${BUILD_NUM} | Tag: ${TAG}"
echo ""

# 6. Push to release remote (OlyForge3D)
echo "📤 Pushing main + tag to ${RELEASE_REMOTE}..."
git push "$RELEASE_REMOTE" main
git push "$RELEASE_REMOTE" "$TAG"

# 7. Return to original branch
echo "↩️  Returning to ${ORIGINAL_BRANCH}..."
git checkout "$ORIGINAL_BRANCH"

echo ""
echo "✅ ${TAG} released! TestFlight build should start shortly."
echo "   Monitor at: https://github.com/OlyForge3D/PrintFarmer/actions/workflows/testflight-beta.yml"
