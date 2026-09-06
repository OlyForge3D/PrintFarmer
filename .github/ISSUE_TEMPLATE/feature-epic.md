---
name: "Feature Epic"
about: "Create a parent feature issue that groups child implementation tasks"
title: "feat(epic): <feature name>"
labels: ["type:epic"]
assignees: []
---

## Outcome

Describe the user/business outcome this feature should deliver.

## Scope

- In scope:
- Out of scope:

## Labeling

- Feature label: `feature:<name>`
- Area labels: `area:frontend`, `area:backend`, `area:api`, `area:devops`
- Priority label: `priority:p0|priority:p1|priority:p2`

## Child Issues

<!-- epic-child-plan: draft -->

Add issue numbers as children are created. Before treating this epic as ready,
replace the draft marker with a finalized child list (or explicitly empty plan)
using the [epic author guidance](https://github.com/OlyForge3D/PrintFarmer/blob/development/.github/copilot-instructions.md#epic-dependency-definition-of-done).
Link every child as a native GitHub sub-issue and read back its dependency edges;
this checklist alone does not establish the graph.

- [ ] #<issue-number> - <task title>
- [ ] #<issue-number> - <task title>
- [ ] #<issue-number> - <task title>

## Acceptance Criteria

- [ ] Feature behavior is implemented end-to-end
- [ ] Feature behavior is covered by tests appropriate to risk
- [ ] Docs are updated where user-visible behavior changed

## Rollout / Risk

- Rollout plan:
- Risks:
- Mitigation:
