# Ceremonies

> Team meetings that happen before or after work. Each squad configures their own.

## Design Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | multi-agent task involving 2+ agents modifying shared systems |
| **Facilitator** | lead |
| **Participants** | all-relevant |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Review the task and requirements
2. Agree on interfaces and contracts between components
3. Identify risks and edge cases
4. Assign action items

---

## Code Review Gate

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | any commit to the repository |
| **Facilitator** | coordinator |
| **Participants** | Bishop (GPT-5.4), Hicks (Gemini 3 Pro), Vasquez (Opus 4.6) |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Protocol:**
1. All three reviewers are spawned in parallel with the staged diff
2. Each reviewer uses their assigned model for analytical diversity
3. Each outputs: APPROVE or REQUEST_CHANGES with ranked issues
4. If ANY reviewer flags 🔴 Critical issues → commit is blocked until addressed
5. 🟡 Warning issues should be addressed; may proceed with justification
6. 🔵 Info issues are advisory only
7. Top issues from all three reviewers are consolidated and addressed before committing

**Models:**
- **Bishop** → `gpt-5.4`
- **Hicks** → `gemini-3-pro-preview`
- **Vasquez** → `claude-opus-4.6`

---

## Retrospective

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | build failure, test failure, or reviewer rejection |
| **Facilitator** | lead |
| **Participants** | all-involved |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. What happened? (facts only)
2. Root cause analysis
3. What should change?
4. Action items for next iteration
