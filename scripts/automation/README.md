## Ralph GitHub Snapshot Scanner

`ralph-scan.mjs` is a standalone, read-only Node 20+ helper for a Ralph
automation runtime. Copy the file outside a checkout if desired: it uses only
the Node standard library and an authenticated `gh` CLI. It never runs Git,
creates labels or comments, claims work, merges, deletes, or writes inside the
repository. GitHub requests are explicitly pinned to the `github.com` host.

### Invocation

```bash
node /opt/printfarmer-ralph/ralph-scan.mjs \
  --repo OlyForge3D/PrintFarmer \
  --workflow-id 5edfe068-4f7c-4734-a078-8ee6fba95918 \
  --state-root /Users/example/.copilot/automation-state
```

Optionally provide an absolute `--sessions-file` containing the native
`list_sessions_and_chats` JSON array. Omitting it reports session enumeration
as unavailable and requires live native enumeration before dispatch or reap.

Stdout is one compact JSON document. Its stable top-level keys are
`schemaVersion`, `complete`, `baseline`, `counts`, `changedItems`, `issues`,
`dependencyOrder`, `blockedEdges`, `graphFlags`, `prs`, `sessions`, `security`,
`artifacts`, and `api`. Diagnostics are written to stderr. Exit zero means
collection completed and the snapshot advanced atomically; any nonzero result
preserves the preceding good snapshot.

`dependencyOrder` is the full topological future sequence. By contrast,
`issues.currentlyUnblocked` and `issues.readyUnresolved` only describe work
without known open native/external blockers; `readyUnresolved` further excludes
assigned, in-progress, needs-analysis, epic, reviewer-owned, and textual-only
blocker candidates. All open blocking edges remain in `blockedEdges`. These
fields are suggestions for an agent to assess, never authority to act.

`api` reports actual collection volume: `rootCalls` is the number of GitHub API
requests, `paginatedRequests` is the subset using pagination, `paginationPages`
is the returned HTTP-page count, and `responseBytes` is the normalized JSON
response size. It distinguishes request count from paginated page count.

### Private state and artifacts

The state namespace is:

```text
<state-root>/github.com/olyforge3d/printfarmer/<workflow-id>/
```

It contains a mode-`0700` directory, an atomic `snapshot.json`, a short-lived
exclusive `scan.lock`, and `artifacts/issues-<hash>.json` plus
`artifacts/prs-<hash>.json`. Files are mode `0600`. The helper does not delete
artifacts or stale state: the deployment owner chooses retention and performs
any targeted cleanup outside a running scan.

The issue artifact accounts for every open non-PR issue, including unlabeled
and mechanically ambiguous entries. Each issue retains assignment and a
mobile-scope `unknown`/agent-decision flag; the scanner never infers mobile
scope from member identity. Open drafts, review/comment changes, status and
check reruns, dependency changes, security-alert availability, and supplied
session fingerprints are observed in the delta. Code-scanning API denial or
absence is emitted as `security.availability: "unknown"`, never success.
