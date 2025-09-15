#!/usr/bin/env bash
set -euo pipefail
mkdir -p src/coverage
shopt -s nullglob
files=(src/coverage/*.info)
if [ ${#files[@]} -eq 0 ]; then
  echo "No per-project LCOV files in src/coverage; skipping backend coverage merge"
  # communicate to GitHub Actions caller whether coverage was present
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    echo "coverage_present=false" >> "$GITHUB_OUTPUT"
  fi
  exit 0
fi

echo "Found ${#files[@]} LCOV file(s) to merge:"
for f in "${files[@]}"; do echo " - $f"; done

# Use gawk to group per-SF blocks and merge DA/BRDA/FNDA entries.
# The AWK script below groups by SF and emits merged FN/FNDA/DA/BRDA and counters.
gawk '
BEGIN { OFS = "" }
function addda(sf, ln, hits) {
  key = sf SUBSEP ln
  if ((key in da) == 0 || hits+0 > da[key]+0) da[key] = hits+0
  hasda[sf] = 1
}
function addbr(sf, l,b,c,h) {
  key = sf SUBSEP l SUBSEP b SUBSEP c
  if ((key in br) == 0 || h+0 > br[key]+0) br[key] = h+0
  hasbr[sf] = 1
}
function addfn(sf, line, name) {
  fnline[sf SUBSEP name] = line
  fnnames[sf SUBSEP name] = 1
  hasfn[sf] = 1
}
function addfnda(sf, hits, name) {
  key = sf SUBSEP name
  if ((key in fnda) == 0 || hits+0 > fnda[key]+0) fnda[key] = hits+0
  fnnames[sf SUBSEP name] = 1
  hasfn[sf] = 1
}
# Read all input files
{ line = $0 }
/^SF:/ { sf = substr($0,4); files[sf]=1; next }
/^DA:([0-9]+),([0-9]+)/ { match($0,/^DA:([0-9]+),([0-9]+)/,m); addda(sf,m[1],m[2]); next }
/^BRDA:/ { match($0,/^BRDA:([0-9]+),([0-9]+),([0-9]+),([0-9]+)/,m); addbr(sf,m[1],m[2],m[3],m[4]); next }
/^FN:/ { match($0,/^FN:([0-9]+),(.*)/,m); addfn(sf,m[1],m[2]); next }
/^FNDA:/ { match($0,/^FNDA:([0-9]+),(.*)/,m); addfnda(sf,m[1],m[2]); next }
/^TN:/ { tn[substr($0,4)]=1; next }
END {
  for (t in tn) print "TN:" t
  n = asorti(files, sorted)
  for (i=1; i<=n; i++) {
    s = sorted[i]
    print "SF:" s
    fncount = 0
    for (k in fnnames) {
      split(k, parts, SUBSEP)
      if (parts[1]==s) { fncount++; fnlist[fncount]=parts[2] }
    }
    for (j=1; j<=fncount; j++) {
      name = fnlist[j]
      if ((s SUBSEP name) in fnline) print "FN:" fnline[s SUBSEP name] "," name
    }
    for (j=1; j<=fncount; j++) {
      name = fnlist[j]
      h = (s SUBSEP name) in fnda ? fnda[s SUBSEP name] : 0
      print "FNDA:" h "," name
    }
    FNF = fncount; FNH = 0
    for (j=1; j<=fncount; j++) { name = fnlist[j]; if (((s SUBSEP name) in fnda) && fnda[s SUBSEP name] > 0) FNH++ }
    print "FNF:" FNF
    print "FNH:" FNH
    dcount = 0
    for (k in da) { split(k, p, SUBSEP); if (p[1]==s) { dcount++; dlist[dcount]=p[2] } }
    asort(dlist)
    for (j=1; j<=dcount; j++) { ln = dlist[j]; print "DA:" ln "," da[s SUBSEP ln] }
    bcount = 0
    for (k in br) { split(k, p, SUBSEP); if (p[1]==s) { bcount++; brlist[bcount]=p[2] SUBSEP p[3] SUBSEP p[4] } }
    for (j=1; j<=bcount; j++) {
      split(brlist[j], parts, SUBSEP)
      h = br[s SUBSEP parts[1] SUBSEP parts[2] SUBSEP parts[3]]
      print "BRDA:" parts[1] "," parts[2] "," parts[3] "," h
    }
    LF = dcount; LH = 0
    for (j=1; j<=dcount; j++) { ln = dlist[j]; if (da[s SUBSEP ln] > 0) LH++ }
    print "LF:" LF
    print "LH:" LH
    print "end_of_record"
    delete dlist; delete fnlist; delete brlist
  }
}
' src/coverage/*.info > src/coverage/coverage.info

echo "Wrote merged coverage to src/coverage/coverage.info"
# communicate back to workflow that coverage was produced
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  echo "coverage_present=true" >> "$GITHUB_OUTPUT"
fi
