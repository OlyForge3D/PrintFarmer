---
name: "reviewer-protocol"
description: "Reviewer rejection workflow and strict lockout semantics"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

When a team member has a **Reviewer** role (e.g., Tester, Code Reviewer, Lead), they may approve or reject work from other agents.

**Canonical rule — read this first:** whether a rejected author may self-revise, or is
instead locked out and requires a different agent, is governed by
`.github/copilot-instructions.md` § "Post-Rejection Revision Ownership". That is the
canonical definition — self-revision is the default, and strict lockout activates only
when a reviewer explicitly invokes it. Do not restate that rule here; this skill covers
only the Coordinator-facing mechanics for operationalizing it once lockout has been
invoked.

## Patterns

### Reviewer Rejection Protocol

When a team member has a **Reviewer** role:

- Reviewers may **approve** or **reject** work from other agents.
- On **rejection**, by default the original author self-revises and re-requests review
  (see the canonical rule above).
- The Reviewer may instead explicitly **invoke lockout** by choosing ONE of:
  1. **Reassign:** Require a *different* agent to do the revision (not the original author).
  2. **Escalate:** Require a *new* agent be spawned with specific expertise.
- Once lockout is invoked, the Coordinator MUST enforce it: the original agent does NOT
  get to self-revise for that artifact.
- If the Reviewer approves, work proceeds normally.

### Strict Lockout Mechanics (once invoked)

These rules apply only after a Reviewer has explicitly invoked lockout per the canonical
rule — they do not apply to an ordinary rejection:

1. **The original author is locked out.** They may NOT produce the next version of that artifact.
2. **A different agent MUST own the revision.** The Coordinator selects the revision author based on the Reviewer's recommendation (reassign or escalate).
3. **The Coordinator enforces this mechanically.** Before spawning a revision agent, the Coordinator MUST verify that the selected agent is NOT the locked-out author. If the Reviewer names that author as the fix agent, the Coordinator MUST refuse and ask the Reviewer to name a different agent.
4. **The locked-out author may NOT contribute to the revision** in any form — not as a co-author, advisor, or pair. The revision must be independently produced.
5. **Lockout scope:** The lockout applies to the specific artifact that was rejected. The original author may still work on other unrelated artifacts.
6. **Lockout compounds on repeated rejection:** if the revision is also rejected, the same rule applies again — the revision author is now also locked out, and a further different agent must revise.
7. **Roster-exhaustion escalation:** if all eligible agents have been locked out of an artifact, the Coordinator MUST escalate to the user rather than re-admitting a locked-out author.

## Examples

**Example 1: Reassign after rejection**
1. Fenster writes authentication module
2. Hockney (Tester) reviews → rejects: "Error handling is missing. Verbal should fix this."
3. Coordinator: Fenster is now locked out of this artifact
4. Coordinator spawns Verbal to revise the authentication module
5. Verbal produces v2
6. Hockney reviews v2 → approves
7. Lockout clears for next artifact

**Example 2: Escalate for expertise**
1. Edie writes TypeScript config
2. Keaton (Lead) reviews → rejects: "Need someone with deeper TS knowledge. Escalate."
3. Coordinator: Edie is now locked out
4. Coordinator spawns new agent (or existing TS expert) to revise
5. New agent produces v2
6. Keaton reviews v2

**Example 3: Deadlock handling**
1. Fenster writes module → rejected
2. Verbal revises → rejected
3. Hockney revises → rejected
4. All 3 eligible agents are now locked out
5. Coordinator: "All eligible agents have been locked out. Escalating to user: [artifact details]"

**Example 4: Reviewer accidentally names original author**
1. Fenster writes module → rejected
2. Hockney says: "Fenster should fix the error handling"
3. Coordinator: "Fenster is locked out as the original author. Please name a different agent."
4. Hockney: "Verbal, then"
5. Coordinator spawns Verbal

## Anti-Patterns

- ❌ Allowing self-revision after a Reviewer has explicitly invoked lockout for that artifact
- ❌ Treating the locked-out author as an "advisor" or "co-author" on the revision
- ❌ Re-admitting a locked-out author when roster exhaustion occurs (must escalate to user)
- ❌ Applying lockout across unrelated artifacts (scope is per-artifact)
- ❌ Accepting the Reviewer's assignment when they name the locked-out author (must refuse and ask for a different agent)
- ❌ Clearing lockout before the revision is approved (lockout persists through the revision cycle)
- ❌ Skipping verification that the revision agent is not the locked-out author
