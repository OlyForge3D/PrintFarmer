#!/usr/bin/env bash
# dependency-maintenance-dry-run.sh
# Local helper to simulate (or apply) the consolidated dependency-maintenance.yml logic.
#
# Features:
#  - Lists all open Dependabot PRs
#  - Simulates pruning of the keep-open label after KEEP_OPEN_MAX_DAYS (unless security-reviewed)
#  - Simulates closing stale PRs with > STALE_DAYS inactivity unless exempt
#  - Outputs a summary table
#  - Supports dry-run (default) or apply mode (MODE=apply) for real changes
#
# Requirements: gh CLI (authenticated), jq
# Optional env vars (with defaults):
#   KEEP_OPEN_LABEL=keep-open
#   SECURITY_REVIEWED_LABEL=security-reviewed
#   KEEP_OPEN_MAX_DAYS=30
#   STALE_DAYS=5
#   EXEMPT_STALE_LABELS="no-autoclose,security,keep-open"
#   MODE=dry-run   (set to 'apply' to perform actions)
#   LIMIT=0        (if >0, limits number of PRs processed)
#   VERBOSE=0      (set to 1 for more detail)
#
# Usage examples:
#   bash scripts/dependency-maintenance-dry-run.sh
#   MODE=apply bash scripts/dependency-maintenance-dry-run.sh
#   STALE_DAYS=7 KEEP_OPEN_MAX_DAYS=45 bash scripts/dependency-maintenance-dry-run.sh --limit 25
#
set -euo pipefail

KEEP_OPEN_LABEL=${KEEP_OPEN_LABEL:-keep-open}
SECURITY_REVIEWED_LABEL=${SECURITY_REVIEWED_LABEL:-security-reviewed}
KEEP_OPEN_MAX_DAYS=${KEEP_OPEN_MAX_DAYS:-30}
STALE_DAYS=${STALE_DAYS:-5}
EXEMPT_STALE_LABELS=${EXEMPT_STALE_LABELS:-no-autoclose,security,keep-open}
MODE=${MODE:-dry-run}
LIMIT=${LIMIT:-0}
VERBOSE=${VERBOSE:-0}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --limit)
      LIMIT=$2; shift 2;;
    -h|--help)
      sed -n '1,60p' "$0"; exit 0;;
    *)
      echo "Unknown arg: $1" >&2; exit 1;;
  esac
done

command -v gh >/dev/null 2>&1 || { echo "ERROR: gh CLI not found in PATH"; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "ERROR: jq not found in PATH"; exit 1; }

echo "Mode: $MODE (apply will mutate PRs)"
echo "KEEP_OPEN_MAX_DAYS=$KEEP_OPEN_MAX_DAYS  STALE_DAYS=$STALE_DAYS"
echo "Labels: KEEP_OPEN=$KEEP_OPEN_LABEL  SECURITY_REVIEWED=$SECURITY_REVIEWED_LABEL"
echo "Exempt (stale close) labels: $EXEMPT_STALE_LABELS"

# Fetch PRs
SEARCH_QUERY="is:open is:pr author:dependabot"
if [[ $LIMIT -gt 0 ]]; then
  gh pr list --search "$SEARCH_QUERY" -L "$LIMIT" --json number,title,updatedAt,createdAt,labels,headRefName > /tmp/deps_prs.json
else
  gh pr list --search "$SEARCH_QUERY" --json number,title,updatedAt,createdAt,labels,headRefName > /tmp/deps_prs.json
fi

TOTAL=$(jq 'length' /tmp/deps_prs.json)
echo "Fetched $TOTAL Dependabot PR(s)."

now_epoch=$(date -u +%s)
IFS=',' read -ra EXEMPT_STALE_ARRAY <<< "$EXEMPT_STALE_LABELS"

prune_removed=0
prune_retained=0
stale_closed=0
stale_kept=0

echo
printf '%s\n' "=== Phase 1: Prune keep-open (>$KEEP_OPEN_MAX_DAYS days, unless $SECURITY_REVIEWED_LABEL) ==="

while read -r pr; do
  num=$(echo "$pr" | jq -r '.number')
  title=$(echo "$pr" | jq -r '.title')
  updatedAt=$(echo "$pr" | jq -r '.updatedAt')
  createdAt=$(echo "$pr" | jq -r '.createdAt')
  labels=$(echo "$pr" | jq -r '[.labels[].name]|join(",")')
  last_iso=${updatedAt:-$createdAt}
  last_epoch=$(date -u -d "$last_iso" +%s || echo 0)
  age_days=$(( (now_epoch - last_epoch)/86400 ))

  echo "$labels" | grep -qi "\b${KEEP_OPEN_LABEL}\b" || continue

  if echo "$labels" | grep -qi "\b${SECURITY_REVIEWED_LABEL}\b"; then
    [[ $VERBOSE -eq 1 ]] && echo "[PR #$num] keep-open retained (security-reviewed, age ${age_days}d)"
    prune_retained=$((prune_retained+1))
    continue
  fi

  if (( age_days > KEEP_OPEN_MAX_DAYS )); then
    echo "[PR #$num] Would remove $KEEP_OPEN_LABEL (age ${age_days}d) - $title"
    if [[ $MODE == apply ]]; then
      gh issue edit "$num" --remove-label "$KEEP_OPEN_LABEL" || true
      gh pr comment "$num" --body "Removed '${KEEP_OPEN_LABEL}' label after ${age_days} days without merge. Re-review or relabel if still needed." || true
    fi
    prune_removed=$((prune_removed+1))
  else
    [[ $VERBOSE -eq 1 ]] && echo "[PR #$num] keep-open within age (${age_days}d)"
    prune_retained=$((prune_retained+1))
  fi
done < <(jq -c '.[]' /tmp/deps_prs.json)

echo
printf '%s\n' "=== Phase 2: Close stale (>$STALE_DAYS days inactivity, no exempt labels) ==="

while read -r pr; do
  num=$(echo "$pr" | jq -r '.number')
  title=$(echo "$pr" | jq -r '.title')
  updatedAt=$(echo "$pr" | jq -r '.updatedAt')
  createdAt=$(echo "$pr" | jq -r '.createdAt')
  labels=$(echo "$pr" | jq -r '[.labels[].name]|join(",")')
  branch=$(echo "$pr" | jq -r '.headRefName')
  [[ $branch == dependabot/* ]] || { stale_kept=$((stale_kept+1)); continue; }

  skip=false
  for lbl in "${EXEMPT_STALE_ARRAY[@]}"; do
    if echo "$labels" | grep -qi "\b${lbl}\b"; then skip=true; break; fi
  done
  $skip && { [[ $VERBOSE -eq 1 ]] && echo "[PR #$num] Exempt labels present"; stale_kept=$((stale_kept+1)); continue; }

  last_ts_iso=${updatedAt:-$createdAt}
  last_epoch=$(date -u -d "$last_ts_iso" +%s || echo 0)
  age_seconds=$((now_epoch - last_epoch))
  age_days=$(( age_seconds/86400 ))

  if (( age_days > STALE_DAYS )); then
    echo "[PR #$num] Would close stale (age ${age_days}d) - $title"
    if [[ $MODE == apply ]]; then
      gh pr comment "$num" --body "Closing as stale (no activity > ${STALE_DAYS} days). Reopen or rebase if still needed." || true
      gh pr close "$num" || true
    fi
    stale_closed=$((stale_closed+1))
  else
    [[ $VERBOSE -eq 1 ]] && echo "[PR #$num] Recent activity (${age_days}d)"
    stale_kept=$((stale_kept+1))
  fi
done < <(jq -c '.[]' /tmp/deps_prs.json)

echo
printf '%s\n' "=== Summary ==="
printf '%-30s %5s\n' "Phase" "Count"
printf '%-30s %5s\n' "Prune removed" "$prune_removed"
printf '%-30s %5s\n' "Prune retained" "$prune_retained"
printf '%-30s %5s\n' "Stale closed" "$stale_closed"
printf '%-30s %5s\n' "Stale kept" "$stale_kept"

if [[ $MODE != apply ]]; then
  echo "(dry-run) No mutations were performed. Set MODE=apply to enforce changes."
fi

exit 0
