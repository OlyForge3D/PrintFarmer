#!/bin/bash
set -euo pipefail

# Release Script
# Merges development → main, strips .squad/ files, tags, and pushes to OlyForge3D.
#
# Usage: ./scripts/release.sh [major|minor|patch]
#   patch (default) — v0.1.0 → v0.1.1
#   minor           — v0.1.0 → v0.2.0
#   major           — v0.1.0 → v1.0.0
#
# Or provide an explicit tag: ./scripts/release.sh v2.0.0

# --- Version bump logic ---
bump_version() {
  local current="$1" level="$2"
  # Strip leading 'v' if present
  local ver="${current#v}"
  local major minor patch
  IFS='.' read -r major minor patch <<< "$ver"
  major="${major:-0}"; minor="${minor:-0}"; patch="${patch:-0}"

  case "$level" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
  esac
  echo "v${major}.${minor}.${patch}"
}

# Ensure we're at the repo root
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

# Determine the new version
ARG="${1:-patch}"
case "$ARG" in
  major|minor|patch)
    CURRENT="$(cat VERSION 2>/dev/null || echo "v0.0.0")"
    TAG="$(bump_version "$CURRENT" "$ARG")"
    ;;
  v*)
    # Explicit version tag provided
    TAG="$ARG"
    ;;
  *)
    echo "❌ Usage: $0 [major|minor|patch|vX.Y.Z]"
    echo "   Default: patch"
    exit 1
    ;;
esac

RELEASE_REMOTE="release"
FORBIDDEN_PATHS=(.squad/ .ai-team/ .ai-team-templates/ team-docs/ docs/proposals/ devnotes/)

echo "🚀 Releasing ${TAG}"
echo ""

# Ensure we're at the repo root
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

# Ensure we're on a clean working tree
if [[ -n "$(git status --porcelain)" ]]; then
  echo "❌ Working tree is dirty. Commit or stash changes first."
  exit 1
fi

# Save current branch to return to it later
ORIGINAL_BRANCH=$(git branch --show-current)

# Verify release remote exists
if ! git remote get-url "$RELEASE_REMOTE" &>/dev/null; then
  echo "❌ Remote '${RELEASE_REMOTE}' not found."
  echo "   Add it with: git remote add release https://github.com/OlyForge3D/PrintFarmer.git"
  exit 1
fi

# 1. Update from remotes
echo "📥 Fetching latest from remotes..."
git fetch origin
git fetch "$RELEASE_REMOTE"

# 2. Checkout main and merge development
echo "🔀 Merging development → main..."
git checkout main
git merge development --no-edit

# 3. Strip forbidden paths (team state, dev notes, etc.)
echo "🧹 Stripping forbidden paths from main..."
STRIPPED=false
for path in "${FORBIDDEN_PATHS[@]}"; do
  if git ls-files --error-unmatch "$path" &>/dev/null 2>&1; then
    git rm --cached -r "$path" 2>/dev/null || true
    STRIPPED=true
    echo "   Removed: ${path}"
  fi
done

if [[ "$STRIPPED" == "true" ]]; then
  git commit -m "chore: strip team state files from main for release"
fi

# 4. Tag (skip if tag already exists)
if git rev-parse "$TAG" &>/dev/null 2>&1; then
  echo "⚠️  Tag ${TAG} already exists — skipping tag creation"
else
  echo "🏷️  Tagging ${TAG}..."
  git tag "$TAG"
fi

# 5. Push to release remote (OlyForge3D)
echo "📤 Pushing main + tag to ${RELEASE_REMOTE}..."
git push "$RELEASE_REMOTE" main
git push "$RELEASE_REMOTE" "$TAG" 2>/dev/null || true

# 6. Also push main to origin so both remotes stay in sync
echo "📤 Pushing main to origin..."
git push origin main

# 7. Return to original branch
echo "↩️  Returning to ${ORIGINAL_BRANCH}..."
git checkout "$ORIGINAL_BRANCH"

echo ""
echo "✅ ${TAG} released to OlyForge3D/PrintFarmer!"
echo "   https://github.com/OlyForge3D/PrintFarmer"
