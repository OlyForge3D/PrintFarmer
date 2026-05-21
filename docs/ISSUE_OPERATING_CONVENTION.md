## Goal

Make features visible in GitHub by combining labels + epics + project views.

Issues remain task-level. Features are represented by a shared `feature:*` label
and one parent epic issue.

## Canonical Labels

Use these four dimensions on open work:

- Feature: `feature:*` (for example `feature:slicer`, `feature:saas`)
- Area: `area:*` (frontend/backend/api/devops)
- Type: `type:*` (bug/feature/chore/docs/spike/epic)
- Priority: `priority:p0|priority:p1|priority:p2`

## Required Conventions

1. Every feature starts with one epic issue labeled `type:epic` + one
   `feature:*` label.
2. Every child task must link to exactly one parent epic.
3. Every child task must carry the same `feature:*` label as the parent epic.
4. Child tasks should also include one `type:*` and one `area:*` label.
5. Epic progress is tracked through a checklist of child issue links.

## Issue Creation Workflow

1. Create epic from `.github/ISSUE_TEMPLATE/feature-epic.md`.
2. Add `feature:*`, `priority:*`, and area labels to epic.
3. Create child tasks from `.github/ISSUE_TEMPLATE/feature-task.md`.
4. Add each child issue number to the epic checklist.
5. During implementation, update task status and keep labels current.
6. Close epic only after all child tasks are complete.

## Suggested GitHub Views

Use these search filters in Issues or Project views.

### 1) Feature Breakdown (Open)

```text
is:issue is:open label:type:feature
```

Group by `Label` and prioritize `feature:*` labels.

### 2) Epics In Progress

```text
is:issue is:open label:type:epic
```

### 3) One Feature Drilldown (Example: Slicer)

```text
is:issue is:open label:feature:slicer
```

### 4) Delivery Queue (P0/P1)

```text
is:issue is:open (label:priority:p0 OR label:priority:p1)
```

### 5) By Area (Example: Backend)

```text
is:issue is:open label:area:backend
```

## Weekly Operating Cadence

1. Monday planning: review `label:type:epic` and confirm child task coverage.
2. Mid-week: review P0/P1 queue and rebalance area ownership.
3. Friday: close completed child tasks and update epic checklists.

## Hygiene Rules

1. Do not create new synonyms if a canonical label exists.
2. Keep exactly one `feature:*` label per issue unless explicitly cross-cutting.
3. Keep exactly one `priority:*` label on delivery issues.
4. Use `type:spike` for research that does not deliver production behavior.
