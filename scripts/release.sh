#!/bin/bash
set -euo pipefail

# =============================================================================
# Release Script — Dual-History Architecture
# =============================================================================
#
# Maintains SEPARATE histories for the private and public repositories:
#
#   origin  (private)  — full merge history, all files including .squad/ etc.
#   release (OlyForge3D) — clean linear history, forbidden files never appear
#                          in ANY commit (not even reachable via parents)
#
# The release remote receives a single flat commit per release whose tree is
# built from main's content with forbidden paths removed. No merge parents
# link back to the development branch, so `git log --all` on OlyForge3D
# will never surface internal team files.
#
# Usage:
#   ./scripts/release.sh [major|minor|patch|vX.Y.Z] [--clean-history]
#
#   patch (default) — v0.1.0 → v0.1.1
#   minor           — v0.1.0 → v0.2.0
#   major           — v0.1.0 → v1.0.0
#
# Options:
#   --clean-history   One-time: force-push a fresh orphan main to the release
#                     remote, erasing ALL prior history that may contain leaked
#                     forbidden files. Safe to run — only affects the release
#                     remote; origin is untouched.

# --- Helpers -----------------------------------------------------------------

bump_version() {
  local current="$1" level="$2"
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

# Remove forbidden paths from the git index and working tree.
strip_forbidden() {
  local path
  for path in "${FORBIDDEN_PATHS[@]}"; do
    if git ls-files --error-unmatch "$path" &>/dev/null 2>&1; then
      git rm --cached -r "$path" 2>/dev/null || true
      rm -rf "$path" 2>/dev/null || true
      echo "   Stripped: ${path}"
    fi
  done
}

# --- Configuration -----------------------------------------------------------

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

RELEASE_REMOTE="release"
readonly FORBIDDEN_PATHS=(.squad/ .ai-team/ .ai-team-templates/ team-docs/ docs/proposals/ devnotes/)

# --- Parse arguments ---------------------------------------------------------

CLEAN_HISTORY=false
ARGS=()
for arg in "$@"; do
  case "$arg" in
    --clean-history) CLEAN_HISTORY=true ;;
    *) ARGS+=("$arg") ;;
  esac
done

CURRENT="$(cat VERSION 2>/dev/null || echo "v0.0.0")"
ARG="${ARGS[0]:-patch}"
case "$ARG" in
  major|minor|patch) TAG="$(bump_version "$CURRENT" "$ARG")" ;;
  v*) TAG="$ARG" ;;
  *)
    echo "❌ Usage: $0 [major|minor|patch|vX.Y.Z] [--clean-history]"
    echo "   Default: patch"
    exit 1
    ;;
esac

echo "🚀 Releasing ${TAG} (was ${CURRENT})"
if $CLEAN_HISTORY; then
  echo "   ⚠️  --clean-history: release remote history will be reset"
fi
echo ""

# --- Pre-flight checks -------------------------------------------------------

if [[ -n "$(git status --porcelain)" ]]; then
  echo "❌ Working tree is dirty. Commit or stash changes first."
  exit 1
fi

ORIGINAL_BRANCH=$(git branch --show-current)

if ! git remote get-url "$RELEASE_REMOTE" &>/dev/null; then
  echo "❌ Remote '${RELEASE_REMOTE}' not found."
  echo "   Add it with: git remote add release https://github.com/OlyForge3D/PrintFarmer.git"
  exit 1
fi

# --- 1. Fetch remotes --------------------------------------------------------

echo "📥 Fetching latest from remotes..."
git fetch origin
git fetch "$RELEASE_REMOTE" 2>/dev/null || true

# --- 2. Merge development → main (full history for origin) -------------------

echo "🔀 Merging development → main (full history)..."
git checkout main
git merge development --no-edit

# --- 3. Bump VERSION on main -------------------------------------------------

echo "📝 Updating VERSION to ${TAG}..."
echo "$TAG" > VERSION
git add VERSION
git commit -m "chore: bump VERSION to ${TAG}"

# --- 4. Push full history to origin (private repo) ---------------------------

echo "📤 Pushing main to origin (full history)..."
git push origin main

# --- 5. Build clean release for OlyForge3D -----------------------------------
#
# Instead of pushing main (which carries development merge parents and thus
# full history), we build a new commit whose tree equals main minus forbidden
# paths. For --clean-history or first release, this is an orphan (root) commit.
# For subsequent releases, it's a child of release/main — producing a clean
# linear history on the public repo.

echo "📤 Preparing clean release for ${RELEASE_REMOTE}..."

USE_ORPHAN=false
if $CLEAN_HISTORY; then
  USE_ORPHAN=true
elif ! git rev-parse "${RELEASE_REMOTE}/main" &>/dev/null 2>&1; then
  USE_ORPHAN=true
  echo "   First release — will create orphan main on ${RELEASE_REMOTE}"
fi

if $USE_ORPHAN; then
  # Orphan branch: index carries main's content from our current checkout
  git checkout --orphan release-staging
else
  # Based on release/main; replace the tree entirely with main's content
  git checkout -B release-staging "${RELEASE_REMOTE}/main"
  git rm -rf . 2>/dev/null || true
  git checkout main -- .
fi

strip_forbidden

# Commit the clean tree (preserve main's author date for consistency)
GIT_AUTHOR_DATE="$(git log -1 --format=%aI main)" \
GIT_COMMITTER_DATE="$(git log -1 --format=%cI main)" \
git commit -m "Release ${TAG}"

# --- 6. Tag and push to release remote ---------------------------------------

# Tag on the clean release commit
if git rev-parse "$TAG" &>/dev/null 2>&1; then
  git tag -d "$TAG" 2>/dev/null || true
fi
git tag "$TAG"

if $USE_ORPHAN; then
  echo "   Force-pushing clean history to ${RELEASE_REMOTE}/main..."
  git push "$RELEASE_REMOTE" release-staging:main --force
else
  git push "$RELEASE_REMOTE" release-staging:main
fi
git push "$RELEASE_REMOTE" "$TAG" --force 2>/dev/null || true

# --- 7. Re-tag on main for origin and clean up -------------------------------

# Origin's tag should point to the full-history commit on main
git checkout main
git tag -f "$TAG"
git push origin "$TAG" --force 2>/dev/null || true

git branch -D release-staging

# --- 8. Back-merge VERSION bump to development --------------------------------

echo "🔀 Back-merging VERSION bump to ${ORIGINAL_BRANCH}..."
git checkout "$ORIGINAL_BRANCH"
git merge main --no-edit
git push origin "$ORIGINAL_BRANCH"

echo ""
echo "✅ ${TAG} released!"
echo "   ${CURRENT} → ${TAG}"
echo ""
echo "   📦 origin/main          — full history (private)"
echo "   📦 ${RELEASE_REMOTE}/main  — clean history (public)"
if $CLEAN_HISTORY; then
  echo "   ⚠️  Release remote history was reset (--clean-history)"
fi
echo ""
echo "   https://github.com/OlyForge3D/PrintFarmer"
