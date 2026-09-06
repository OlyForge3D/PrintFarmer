#!/bin/bash
set -euo pipefail

# Rotate Match Passphrase Script
#
# Use this ONLY when the current MATCH_PASSWORD is lost/unrecoverable and
# nobody can decrypt the existing certs repo. This script:
#   1. Destroys the existing App Store certs/profiles (Apple-side revoke +
#      certs-repo delete) via `fastlane match nuke`.
#   2. Re-creates fresh certs/profiles for BOTH the main app and the new
#      widget extension, encrypted under a NEW passphrase you choose.
#   3. Updates the GitHub Actions secret MATCH_PASSWORD to the new value
#      so CI can keep working without any further manual steps.
#
# This does NOT touch anything already installed on a live/shipped app -
# existing installed provisioning profiles keep working until they expire.
# Only CI's ability to sign NEW builds depends on this.
#
# Usage:
#   ./scripts/rotate-match-password.sh

MATCH_GIT_URL_DEFAULT="https://github.com/OlyForge3D/PrintFarmerApp-certificates.git"
BUNDLE_IDS="com.olyforge3d.printfarmer.ios,com.olyforge3d.printfarmer.ios.scan-widgets"
REPO="OlyForge3D/PrintFarmer"

# This script needs a git credential for the certs repo, but must NEVER
# leave that credential behind in ~/.gitconfig once it exits - a stale
# global `url.*.insteadOf` rewrite with an embedded token silently hijacks
# every future `https://github.com/` git operation on this machine and
# overrides even a correctly configured `gh auth git-credential` helper.
# (This exact bug already cost a multi-hour debugging session once.)
#
# Cleanup is trap-based so it always runs, on success, failure, or Ctrl-C -
# and it also clears the clipboard so the new passphrase doesn't linger
# there if something fails partway through.
CREDENTIAL_CONFIGURED=0
cleanup() {
  if [[ "$CREDENTIAL_CONFIGURED" -eq 1 ]]; then
    git config --global --unset-all "url.https://git:${MATCH_GIT_TOKEN}@github.com/.insteadOf" 2>/dev/null || true
    echo "-> Cleaned up temporary git credential from ~/.gitconfig."
  fi
  printf '' | pbcopy 2>/dev/null || true
}
trap cleanup EXIT

echo "==============================================================="
echo " Rotating fastlane-match passphrase (App Store certs)"
echo " Bundle IDs: $BUNDLE_IDS"
echo "==============================================================="
echo
echo "⚠️  This will REVOKE and RECREATE the existing App Store certificate"
echo "   and profiles. This is safe for CI builds, but make sure nobody"
echo "   else is mid-way through a local signing operation right now."
echo
read -r -p "Type YES to continue: " CONFIRM
if [[ "$CONFIRM" != "YES" ]]; then
  echo "Aborted."
  exit 1
fi

read -r -s -p "OLD match passphrase (if you have any guess, else press Enter to skip): " OLD_MATCH_PASSWORD
echo

# NOTE: macOS's `security` CLI has no flag to mark an item as iCloud-syncable
# (kSecAttrSynchronizable) - that attribute can only be set by the Passwords
# app / Safari / apps using the Keychain Services API directly. So this
# script cannot silently deposit the new passphrase into iCloud Keychain for
# you. Instead, it generates a strong passphrase, puts it on your clipboard,
# and opens the Passwords app so you can paste it into a new entry yourself
# (~10 seconds). This keeps the claim honest: nothing here pretends to write
# to iCloud Keychain when it structurally cannot.
echo "-> Generating a strong random passphrase..."
NEW_MATCH_PASSWORD="$(openssl rand -base64 24 | tr -d '=+/' | cut -c1-32)"
echo "$NEW_MATCH_PASSWORD" | pbcopy
echo "✅ New passphrase generated and copied to your clipboard."
echo
echo "-> Opening the Passwords app - please create a new entry now:"
echo "     Title:    PrintFarmer CI - MATCH_PASSWORD"
echo "     Username: ci"
echo "     Password: (Cmd+V to paste what's on your clipboard)"
open -a Passwords 2>/dev/null || open "x-apple.systempreferences:com.apple.Passwords-Settings.extension" 2>/dev/null || true
read -r -p "Press Enter once you've saved it in Passwords (so it syncs via iCloud Keychain): " _

read -r -s -p "GitHub token for certs repo (MATCH_GIT_TOKEN): " MATCH_GIT_TOKEN
echo
read -r -p "Certs repo URL [$MATCH_GIT_URL_DEFAULT]: " MATCH_GIT_URL_INPUT
MATCH_GIT_URL="${MATCH_GIT_URL_INPUT:-$MATCH_GIT_URL_DEFAULT}"

echo
echo "-> Configuring temporary git credential helper for the certs repo..."
git config --global url."https://git:${MATCH_GIT_TOKEN}@github.com/".insteadOf "https://github.com/"
CREDENTIAL_CONFIGURED=1

export MATCH_GIT_URL

if [[ -n "$OLD_MATCH_PASSWORD" ]]; then
  echo "-> Attempting nuke with the OLD passphrase (needed to decrypt+delete)..."
  MATCH_PASSWORD="$OLD_MATCH_PASSWORD" fastlane match nuke appstore \
    --git_url "$MATCH_GIT_URL" \
    --app_identifier "$BUNDLE_IDS" \
    --skip_confirmation
else
  echo "-> No old passphrase given - skipping nuke step."
  echo "   NOTE: if the old certs repo cannot be decrypted at all, you may"
  echo "   need to manually delete/re-create the PrintFarmerApp-certificates"
  echo "   repo contents, and manually revoke the old certificate at"
  echo "   https://developer.apple.com/account/resources/certificates/list"
  echo "   before continuing, otherwise Apple will reject creating a"
  echo "   duplicate certificate."
  read -r -p "Press Enter once any manual cleanup above is done (or if not needed): " _
fi

echo
echo "-> Creating fresh certs/profiles under the NEW passphrase..."
# set -e is active, so a failure here exits immediately via the trap above -
# there is no reachable code after a non-zero exit, so we don't pretend to
# check $? afterward (that pattern is dead code under `set -e`).
MATCH_PASSWORD="$NEW_MATCH_PASSWORD" fastlane match appstore \
  --git_url "$MATCH_GIT_URL" \
  --app_identifier "$BUNDLE_IDS"

echo
echo "-> Updating GitHub Actions secret MATCH_PASSWORD on $REPO..."
printf '%s' "$NEW_MATCH_PASSWORD" | gh secret set MATCH_PASSWORD --repo "$REPO"

echo
echo "✅ Done. New certs/profiles exist for both bundle IDs, and the"
echo "   MATCH_PASSWORD GitHub secret has been updated to match."
echo
echo "   Confirm you actually saved 'PrintFarmer CI - MATCH_PASSWORD' in the"
echo "   Passwords app earlier in this run - that's now the only readable"
echo "   copy anywhere (GitHub secrets are write-only and cannot be read back)."
echo
echo "   Next: dispatch a FRESH TestFlight beta run:"
echo "     gh workflow run testflight-beta.yml --ref development -f environment=internal"
