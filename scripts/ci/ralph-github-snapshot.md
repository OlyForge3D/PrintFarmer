---
post_title: Ralph GitHub snapshot collector
author1: PrintFarmer
post_slug: ralph-github-snapshot
microsoft_alias: n-a
featured_image: ""
categories: []
tags: [ralph, automation]
ai_note: true
summary: Runnable cache-backed GitHub observation collector contract.
post_date: 2026-09-08
---

## Cache-backed collector

Run the repository-owned collector with the explicitly approved macOS cache
directory:

```bash
RALPH_CACHE_DIR=/Users/jpapiez/Library/Caches/PrintFarmer/ralph-cache \
node scripts/ci/ralph-github-snapshot.mjs \
  --repo OlyForge3D/PrintFarmer \
  --workflow 5edfe068-4f7c-4734-a078-8ee6fba95918 \
  --policy-version 2026-09-08
```

The cache file is derived by `ralph-round-cache.mjs` from the exact scope:

```text
/Users/jpapiez/Library/Caches/PrintFarmer/ralph-cache/
9b6e550c62460179abe4aa8d9cab4fee033f1d1f1c630625afb41a93d01910c0.json
```

The collector uses `gh api --hostname github.com` and mechanical page handling
through `collectPaginated`. It records normalized open issue dependencies,
assignments and labels; open PR heads, comments, reviews, checks and statuses;
and CodeQL alerts. Cache comparisons explicitly retain unavailable sessions,
uncached action authority, uncached linked PRs, uncollected base state, and
uncollected holds. These values cannot be mistaken for approval or a complete
delta: until a caller supplies those live observations, the result remains
`deep-scan-required`.

Output is one compact JSON object with `complete`, `baseline`, and
`conclusions`. `conclusions.changed` identifies changed top-level comparison
groups and `conclusions.metrics` provides actual request, page, and response
byte counts. `conclusions.coverage` reports every comparison group's
availability. CodeQL permission denial produces explicit unknown evidence; all
other API, pagination, shape, and parse failures exit nonzero before the
helper writes a new cache record. Unknown CodeQL or any uncollected comparison
group forces `deep-scan-required`, even when its value is unchanged.

The collector is observation-only. It never claims, comments, labels, opens
sessions, reviews, merges, deletes, or treats any cache field as action
authority. A Ralph action must re-fetch its issue or PR live before acting.
