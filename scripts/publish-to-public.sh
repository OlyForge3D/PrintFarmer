#!/usr/bin/env bash
# Publish the private repo to OlyForge3D/PrintFarmer (public).
#
# Squashes all commits since the last version tag into a single commit,
# strips files listed in .github/public-exclude.txt, force-pushes to the
# public repo's main branch, creates a version tag, and opens a draft
# GitHub release.
#
# Usage:
#   ./scripts/publish-to-public.sh [micro|minor|major] [--dry-run]
#
# Requirements:
#   - git, gh (GitHub CLI, authenticated)
#   - A PAT stored in the env var PUBLIC_REPO_PAT  OR  gh must have write
#     access to the public repo via its stored credentials.

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

# ── Configure public remote ───────────────────────────────────────────────────
PUBLIC_REMOTE_URL="https://github.com/${PUBLIC_REPO}.git"

# Temporary remote so we don't pollute .git/config permanently
REMOTE_NAME="public-publish-$$"
git remote add "$REMOTE_NAME" "$PUBLIC_REMOTE_URL"
cleanup() { git remote remove "$REMOTE_NAME" 2>/dev/null || true; }
trap cleanup EXIT

log_header "Publish to Public Repo"
log_info "Public repo : $PUBLIC_REPO"
log_info "Bump type   : $BUMP"
log_info "Dry run     : $DRY_RUN"

# ── Fetch tags from public repo ───────────────────────────────────────────────
log_info "Fetching tags from public repo..."
git fetch "$REMOTE_NAME" --tags 2>/dev/null || log_warn "No tags in public repo yet"

# ── Calculate new version ────────────────────────────────────────────────────
LATEST_TAG=$(git tag --sort=-version:refname | grep "^v[0-9]" | head -1 || true)

if [[ -z "$LATEST_TAG" ]]; then
    MAJOR=0; MINOR=1; PATCH=0
    PREV_TAG=""
    FIRST_RELEASE=true
    log_info "No previous version found, starting at v0.1.0"
else
    VERSION="${LATEST_TAG#v}"
    MAJOR=$(echo "$VERSION" | cut -d. -f1)
    MINOR=$(echo "$VERSION" | cut -d. -f2)
    PATCH=$(echo "$VERSION" | cut -d. -f3)
    PREV_TAG="$LATEST_TAG"
    FIRST_RELEASE=false
    log_info "Current version: $LATEST_TAG"
fi

case "$BUMP" in
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
    micro) PATCH=$((PATCH + 1)) ;;
esac

NEW_VERSION="v${MAJOR}.${MINOR}.${PATCH}"
log_success "New version: $NEW_VERSION"

# ── Generate release notes ────────────────────────────────────────────────────
RELEASE_NOTES_FILE="$(mktemp)"
{
    echo "## What's Changed"
    echo ""
    if [[ "$FIRST_RELEASE" == "true" ]]; then
        git log --pretty=format:"- %s" HEAD \
            | grep -v "Bump version to" | grep -v "\[skip ci\]" | head -50
    else
        git log --pretty=format:"- %s" "${PREV_TAG}..HEAD" \
            | grep -v "Bump version to" | grep -v "\[skip ci\]"
        echo ""
        echo "**Full Changelog**: https://github.com/${PUBLIC_REPO}/compare/${PREV_TAG}...${NEW_VERSION}"
    fi
} > "$RELEASE_NOTES_FILE"

log_info "Release notes:"
cat "$RELEASE_NOTES_FILE"

# ── Remove excluded files from index ─────────────────────────────────────────
remove_excluded() {
    log_info "Stripping excluded files..."
    local removed=0
    while IFS= read -r pattern || [[ -n "$pattern" ]]; do
        [[ -z "$pattern" || "$pattern" =~ ^# ]] && continue
        local clean="${pattern%/}"
        if git rm -rf --cached --ignore-unmatch -- "$clean" > /dev/null 2>&1; then
            if ! git diff --cached --quiet -- "$clean" 2>/dev/null; then
                log_info "  Removed: $pattern"
                removed=$((removed + 1))
            fi
        fi
    done < "$EXCLUDE_FILE"
    log_success "Stripped $removed excluded entries"
}

# ── Create squashed release branch ───────────────────────────────────────────
SQUASH_BRANCH="release-squash-$$"
CURRENT_HEAD="$(git rev-parse HEAD)"

log_info "Creating squashed commit on branch $SQUASH_BRANCH..."

if [[ "$FIRST_RELEASE" == "true" ]]; then
    git checkout --orphan "$SQUASH_BRANCH"
    remove_excluded
    git commit -m "$NEW_VERSION - Initial release"
else
    git checkout -b "$SQUASH_BRANCH" "$PREV_TAG"
    git merge --squash "$CURRENT_HEAD"
    remove_excluded
    git commit -m "$NEW_VERSION"
fi

git tag "$NEW_VERSION"

log_success "Squashed commit:"
git log -1 --oneline

# ── Dry run summary ───────────────────────────────────────────────────────────
if [[ "$DRY_RUN" == "true" ]]; then
    log_header "DRY RUN — no changes pushed"
    log_info "Would push: $SQUASH_BRANCH -> $PUBLIC_REPO main"
    log_info "Would tag : $NEW_VERSION"
    log_info "Files in squashed commit: $(git ls-tree -r --name-only HEAD | wc -l)"
    git checkout -
    git branch -D "$SQUASH_BRANCH"
    git tag -d "$NEW_VERSION"
    rm -f "$RELEASE_NOTES_FILE"
    exit 0
fi

# ── Push ─────────────────────────────────────────────────────────────────────
log_info "Pushing $NEW_VERSION to $PUBLIC_REPO..."
git push "$REMOTE_NAME" "$SQUASH_BRANCH:main" --force
git push "$REMOTE_NAME" "$NEW_VERSION"
log_success "Pushed $NEW_VERSION to $PUBLIC_REPO"

# ── Create draft release via gh CLI ──────────────────────────────────────────
log_info "Creating draft release on GitHub..."
gh release create "$NEW_VERSION" \
    --repo "$PUBLIC_REPO" \
    --title "$NEW_VERSION" \
    --notes-file "$RELEASE_NOTES_FILE" \
    --draft
log_success "Draft release: https://github.com/${PUBLIC_REPO}/releases"

# ── Clean up local squash branch (tag stays) ─────────────────────────────────
git checkout -
git branch -D "$SQUASH_BRANCH"
rm -f "$RELEASE_NOTES_FILE"

log_header "Done — $NEW_VERSION published to $PUBLIC_REPO"
log_info "Review and publish the draft release at:"
log_info "  https://github.com/${PUBLIC_REPO}/releases"
