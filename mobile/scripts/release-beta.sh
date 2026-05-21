#!/bin/bash
set -euo pipefail

# Release Beta Script
# Merges development → main, strips .squad/ files, tags, and pushes to OlyForge3D.
#
# Usage: ./scripts/release-beta.sh <beta-number>
# Example: ./scripts/release-beta.sh 8

BETA_NUM="${1:-}"
if [[ -z "$BETA_NUM" ]]; then
  echo "❌ Usage: $0 <beta-number>"
  echo "   Example: $0 8"
  exit 1
fi

TAG="v1.0-beta.${BETA_NUM}"
RELEASE_REMOTE="release"
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
git fetch "$RELEASE_REMOTE"

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
git tag "$TAG"

# 5. Show build number for verification
BUILD_NUM=$(git rev-list --count HEAD)
echo ""
echo "📦 Version: 1.0 | Build: ${BUILD_NUM} | Tag: ${TAG}"
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
echo "   Monitor at: https://github.com/OlyForge3D/PrintFarmerMobile/actions"
