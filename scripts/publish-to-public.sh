#!/usr/bin/env bash
# Publish the private repo to OlyForge3D/PrintFarmer (public).
#
# Takes a snapshot of HEAD, strips files in .github/public-exclude.txt,
# force-pushes to the public repo main branch as a single squashed commit,
# creates a version tag, and opens a draft GitHub release.
#
# Usage:
#   ./scripts/publish-to-public.sh [micro|minor|major] [--dry-run]
#
# Requirements:
#   - Run with Git Bash, NOT WSL bash (gh.exe is not on the WSL PATH):
#       "C:\Program Files\Git\bin\bash.exe" scripts/publish-to-public.sh micro
#   - gh CLI authenticated with write access to the public repo

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)"
source "$SCRIPT_DIR/common-utils.sh"

# ── Arguments ────────────────────────────────────────────────────────────────
BUMP="${1:-micro}"
DRY_RUN=false
for arg in "$@"; do
    [[ "$arg" == "--dry-run" ]] && DRY_RUN=true
done

PUBLIC_REPO="${PUBLIC_REPO:-OlyForge3D/PrintFarmer}"
EXCLUDE_FILE="$ROOT/.github/public-exclude.txt"

# ── Validate ─────────────────────────────────────────────────────────────────
case "$BUMP" in
    micro|minor|major) ;;
    *) log_error "Unknown bump type '$BUMP'. Use micro, minor, or major."; exit 1 ;;
esac

if [[ ! -f "$EXCLUDE_FILE" ]]; then
    log_error "Missing $EXCLUDE_FILE"; exit 1
fi

# ── Cleanup trap ─────────────────────────────────────────────────────────────
SQUASH_BRANCH="release-squash-$$"
cleanup() {
    git checkout main 2>/dev/null || true
    git branch -D "$SQUASH_BRANCH" 2>/dev/null || true
}
trap cleanup EXIT

log_header "Publish to Public Repo"
log_info "Public repo : $PUBLIC_REPO"
log_info "Bump type   : $BUMP"
log_info "Dry run     : $DRY_RUN"

# ── Calculate new version from local tags ────────────────────────────────────
# Local tags include those fetched from olyforge3d (via previous fetches).
# Use the published/* marker tag if present, otherwise fall back to semver tags.
log_info "Checking latest published version..."
LATEST_TAG=$(git tag --sort=-version:refname | grep "^v[0-9]" | grep -v "^v0\.0\.0$" | head -1 || true)

if [[ -z "$LATEST_TAG" ]]; then
    MAJOR=0; MINOR=1; PATCH=0; PREV_TAG=""
    log_info "No previous version found, starting at v0.1.0"
else
    VERSION="${LATEST_TAG#v}"
    MAJOR=$(echo "$VERSION" | cut -d. -f1)
    MINOR=$(echo "$VERSION" | cut -d. -f2)
    PATCH=$(echo "$VERSION" | cut -d. -f3)
    PREV_TAG="$LATEST_TAG"
    log_info "Current version: $LATEST_TAG"
fi

case "$BUMP" in
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
    micro) PATCH=$((PATCH + 1)) ;;
esac

NEW_VERSION="v${MAJOR}.${MINOR}.${PATCH}"
log_success "New version: $NEW_VERSION"

# ── Generate release notes from private repo history ─────────────────────────
# Use published-<version> marker tag if it exists to scope the log range.
# This tracks what was last published from the private repo.
RELEASE_NOTES_FILE="$(mktemp)"
MARKER_TAG="published-${LATEST_TAG:-none}"
{
    echo "## What's Changed"
    echo ""
    if git rev-parse "$MARKER_TAG" > /dev/null 2>&1; then
        # Show commits since last publish marker on private repo
        git log -50 --pretty=format:"- %s" "${MARKER_TAG}..HEAD" \
            | grep -v "Bump version to" | grep -v "\[skip ci\]" || true
        echo ""
        echo "**Full Changelog**: https://github.com/${PUBLIC_REPO}/compare/${LATEST_TAG}...${NEW_VERSION}"
    else
        # No marker — show last 30 commits as summary
        git log -30 --pretty=format:"- %s" HEAD \
            | grep -v "Bump version to" | grep -v "\[skip ci\]" || true
    fi
} > "$RELEASE_NOTES_FILE"

log_info "Release notes preview:"
cat "$RELEASE_NOTES_FILE"

# ── Create orphan squash branch ───────────────────────────────────────────────
# Always orphan: private and public repos have unrelated git histories.
log_info "Creating orphan squash branch from HEAD..."

git checkout --orphan "$SQUASH_BRANCH"

log_info "Stripping excluded files..."
removed=0
while IFS= read -r pattern || [[ -n "$pattern" ]]; do
    [[ -z "$pattern" || "$pattern" =~ ^# ]] && continue
    clean="${pattern%/}"
    git rm -rf --cached --ignore-unmatch -- "$clean" > /dev/null 2>&1 || true
    removed=$((removed + 1))
done < "$EXCLUDE_FILE"
log_success "Processed $removed exclusion rules"

COMMIT_MSG="${NEW_VERSION}"
[[ -z "$PREV_TAG" ]] && COMMIT_MSG="${NEW_VERSION} - Initial release"
git commit -m "$COMMIT_MSG"
git tag "$NEW_VERSION"

log_success "Squashed commit: $(git log -1 --oneline)"
log_info "Files in release: $(git ls-tree -r --name-only HEAD | wc -l)"

# ── Dry run ───────────────────────────────────────────────────────────────────
if [[ "$DRY_RUN" == "true" ]]; then
    log_header "DRY RUN — no changes pushed"
    log_info "Would push to : $PUBLIC_REPO main"
    log_info "Would tag     : $NEW_VERSION"
    git tag -d "$NEW_VERSION"
    rm -f "$RELEASE_NOTES_FILE"
    exit 0
fi

# ── Push via gh CLI (handles auth natively) ───────────────────────────────────
log_info "Pushing $NEW_VERSION to $PUBLIC_REPO..."
PUBLIC_URL="$(gh repo view "$PUBLIC_REPO" --json url --jq '.url').git"
git push "$PUBLIC_URL" "$SQUASH_BRANCH:main" --force
git push "$PUBLIC_URL" "$NEW_VERSION"
log_success "Pushed $NEW_VERSION to $PUBLIC_REPO"

# ── Tag private repo HEAD as publish marker ───────────────────────────────────
# Allows next run to generate accurate release notes (commits since last publish)
git checkout main
MARKER="published-${NEW_VERSION}"
git tag -f "$MARKER"
log_info "Marked private repo HEAD as $MARKER"

# ── Draft release ─────────────────────────────────────────────────────────────
log_info "Creating draft release..."
gh release create "$NEW_VERSION" \
    --repo "$PUBLIC_REPO" \
    --title "$NEW_VERSION" \
    --notes-file "$RELEASE_NOTES_FILE" \
    --draft
log_success "Draft release: https://github.com/${PUBLIC_REPO}/releases"

rm -f "$RELEASE_NOTES_FILE"

log_header "Done — $NEW_VERSION published to $PUBLIC_REPO"
log_info "Review and publish the draft at:"
log_info "  https://github.com/${PUBLIC_REPO}/releases"