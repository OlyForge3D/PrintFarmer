#!/usr/bin/env bash
set -euo pipefail

WORKFLOW_DIR=".github/workflows"
TMP_DIR="$(mktemp -d)"
SUMMARY_MD="$TMP_DIR/summary.md"

echo "# GitHub Actions Version Check" > "$SUMMARY_MD"
echo >> "$SUMMARY_MD"

if [[ ! -d $WORKFLOW_DIR ]]; then
  echo "No workflows directory present." >> "$SUMMARY_MD"
  echo "OUTDATED_COUNT=0"; cat "$SUMMARY_MD"; exit 0
fi
command -v jq >/dev/null 2>&1 || { echo "jq required" >&2; exit 1; }

GITHUB_HEADER=()
[[ -n "${GITHUB_TOKEN:-}" ]] && GITHUB_HEADER=(-H "Authorization: Bearer ${GITHUB_TOKEN}")

extract_actions(){ grep -hE "^[[:space:]]*-? ?uses:" "$1" | sed 's/#.*//' | awk '{print $2}' | sed 's/"//g'; }
is_semver(){ [[ $1 =~ ^v?[0-9]+(\.[0-9]+){0,2}$ ]]; }
norm(){ echo "$1" | sed 's/^v//'; }

list_repos_file="$TMP_DIR/repos.txt"
> "$list_repos_file"

while IFS= read -r wf; do
  while IFS= read -r use; do
    [[ -z $use ]] && continue
    [[ $use == docker://* || $use == ./* || $use == ./ ]] && continue
    repo="${use%@*}"; ref="${use##*@}"
    [[ $ref == *'${{'* ]] && continue
    echo -e "$repo\t$ref\t$wf" >> "$list_repos_file"
  done < <(extract_actions "$wf")
done < <(find "$WORKFLOW_DIR" -maxdepth 1 -name '*.yml' -o -name '*.yaml')

sort -u "$list_repos_file" -o "$list_repos_file"

outdated_file="$TMP_DIR/outdated.txt"
> "$outdated_file"

while IFS=$'\t' read -r repo ref wf; do
  if ! is_semver "$ref"; then
    [[ $ref =~ ^v[0-9]+$ ]] || continue
  fi
  cur_norm=$(norm "$ref")
  cache="$TMP_DIR/${repo//\//__}.json"
  if [[ ! -f $cache ]]; then
    curl -sS "https://api.github.com/repos/$repo/tags?per_page=50" "${GITHUB_HEADER[@]}" > "$cache" || echo '[]' > "$cache"
  fi
  latest=$(jq -r '.[].name' "$cache" | grep -E '^v?[0-9]+(\.[0-9]+){0,2}$' | sed 's/^v//' | sort -Vr | head -n1 || true)
  [[ -z $latest ]] && continue
  if [[ $latest != "$cur_norm" ]]; then
    echo -e "$repo\t$ref\tv$latest" >> "$outdated_file"
  fi
done < "$list_repos_file"

COUNT=$(wc -l < "$outdated_file" | tr -d ' ')
if [[ $COUNT -eq 0 ]]; then
  echo "All referenced GitHub Actions are up-to-date." >> "$SUMMARY_MD"
  echo "OUTDATED_COUNT=0"; cat "$SUMMARY_MD"; exit 0
fi

echo "Found $COUNT outdated action version(s):" >> "$SUMMARY_MD"
echo >> "$SUMMARY_MD"
while IFS=$'\t' read -r repo cur latest; do
  echo "- \`$repo\`: current \`$cur\`, latest \`$latest\`" >> "$SUMMARY_MD"
done < "$outdated_file"
echo >> "$SUMMARY_MD"
echo "Recommendation: update to latest patch/minor where compatible." >> "$SUMMARY_MD"
echo "OUTDATED_COUNT=$COUNT"
cat "$SUMMARY_MD"
