---
name: epic-dependencies
description: Create and verify GitHub epic sub-issue links and dependency edges so Squad scheduling and blocked-label automation use a machine-readable graph.
---

## Epic Dependency Graph Skill

Use this skill whenever creating or revising an issue labelled `type:epic`.
Prose dependency lists are not sufficient: GitHub sub-issue and dependency API
relationships are the source consumed by automation.

## Definition of done

An epic is ready only when:

- Its child plan is explicitly finalized (or explicitly empty).
- Every child is linked as a GitHub sub-issue, so the epic renders a real
  progress bar.
- Every blocking relationship exists as a GitHub dependency edge.
- Every sub-issue and dependency edge has been read back from the API.
- The verifier returns `PASS`:

  ```bash
  node scripts/ci/verify-epic-dependencies.mjs \
    --repo OWNER/REPO \
    --issue <EPIC> \
    --json
  ```

The existing `.github/workflows/squad-blocked-label-sync.yml` reads these
dependency edges to add and remove `status:blocked`. Creating the edges is what
makes that automation work.

## Declare the child plan

Every epic must contain exactly one child-plan HTML comment outside fenced code
blocks. Use one of these exact forms:

```text
<!-- epic-child-plan: draft -->
<!-- epic-child-plan: empty -->
<!-- epic-child-plan: finalized #123 #124 -->
```

These are alternatives, not three markers to paste together:

- `draft`: planning is in progress. Proposed children may remain in prose;
  a `PASS` verifies only the currently linked graph, not plan completeness.
- `empty`: no implementation children are intended. Any native child link
  contradicts this declaration and fails verification.
- `finalized`: list every intended implementation child by repository-local
  issue number, separated by spaces or commas. At least one number is required.
  Every declared child must be a native sub-issue, including when zero children
  are currently linked. Missing links fail even if the linked subset has a valid
  dependency graph or the epic declares a flat graph.

Contextual `#N` references elsewhere in the body are not child declarations.
All native children still undergo dependency checks, including any additional
linked children not in the declaration. Draft status never waives those checks.
Malformed, repeated, conflicting, or missing child-plan markers fail closed.
Existing epics without a marker must add one; they are not implicitly drafts.
Replace `draft` with `finalized` and the complete list when the plan is ready,
and keep the declaration current when revising the plan.

## Create dependency edges

The dependency mutation endpoint takes the blocker's global issue `id`, not its
issue number. Use uppercase `-F`: lowercase `-f` serializes `issue_id` as a
string and GitHub returns HTTP 422.

```bash
id=$(gh api repos/OWNER/REPO/issues/<BLOCKER> --jq .id)
gh api -X POST repos/OWNER/REPO/issues/<BLOCKED>/dependencies/blocked_by -F issue_id="$id"
gh api --paginate 'repos/OWNER/REPO/issues/<BLOCKED>/dependencies/blocked_by?per_page=100' \
  --jq '.[] | "\(.number) \(.title)"'
```

Repeat the read-back command after every write and confirm the expected blocker
appears.

## Link sub-issues

Resolve the child's global issue `id`, then attach it to the epic.

```bash
id=$(gh api repos/OWNER/REPO/issues/<CHILD> --jq .id)
gh api -X POST repos/OWNER/REPO/issues/<EPIC>/sub_issues -F sub_issue_id="$id"
gh api repos/OWNER/REPO/issues/<EPIC> --jq '.sub_issues_summary'
gh api --paginate repos/OWNER/REPO/issues/<EPIC>/sub_issues --jq '.[] | "\(.number) \(.title)"'
```

Confirm every intended child appears and the summary total matches the planned
child count.

## Declare graph shape

Most epics need dependency edges. A genuinely flat epic may opt out with this
exact body marker:

```text
<!-- epic-dependencies: flat -->
```

Do not use the marker to avoid modeling a real dependency.

In a non-flat graph, a child with no blockers and no dependents must be declared
as intentionally startable in the first wave:

```text
<!-- epic-first-wave: #123 #124 -->
```

The declaration accepts issue references separated by spaces or commas. Every
referenced issue must also be a linked sub-issue. A first-wave declaration does
not waive the requirement for at least one dependency edge across the child
set.

## Verify the complete graph

```bash
node scripts/ci/verify-epic-dependencies.mjs \
  --repo OWNER/REPO \
  --issue <EPIC> \
  --json

gh workflow run epic-dependency-gate.yml -f issue_number=<EPIC>
```

The verifier fails closed when declarations are missing or malformed, declared
children lack native links, graph API reads are incomplete, a non-flat epic has
zero internal edges, or a linked child is isolated outside the declared first
wave.

Verification remains issue-level feedback: the workflow updates its canonical
issue comment and fails its own run on violations. It does not publish a PR
commit status or introduce a merge gate.
