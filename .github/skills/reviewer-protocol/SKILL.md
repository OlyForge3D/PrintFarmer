---
name: "reviewer-protocol"
description: "Reviewer rejection workflow — the author fixes their own work"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

When a team member has a **Reviewer** role (e.g., Tester, Code Reviewer, Lead), they may approve
or reject work from other agents.

**On rejection, the original author fixes their own work.** Reviewers report defects; they do not
assign who fixes them.

> **This skill previously mandated a strict author lockout. That rule has been removed at the
> repo owner's direction.** It was rescinded more than once and kept resurfacing because it was
> duplicated across a dozen files. If you find lockout language anywhere else, it is stale — this
> file and `.github/agents/squad.agent.md` are authoritative.

## Patterns

### Reviewer Rejection Protocol

- Reviewers may **approve** or **reject** work from other agents.
- On **rejection**, the **original author owns the revision.** They hold the most context on the
  artifact; handing the fix to a stranger throws that context away and invites new defects.
- Reviewers state *what* is wrong and *why*. Staffing is a coordinator decision, not a reviewer one.
- If the Reviewer approves, work proceeds normally.

### There Is No Author Lockout

1. **The original author owns the revision by default**, including after a second or third
   rejection of the same artifact. Repeated rejection means the author needs better information —
   the specific finding, a failing test, a clearer spec — not a different author.
2. **Do not track "locked out" agents.** There is no such state. Never exclude an agent from an
   artifact because they authored a rejected version of it.
3. **Do not add a team member to work around a rejection.** Grow the roster when the team is
   genuinely missing a skill or capacity — never to sidestep this.
4. **Reviewers may not dictate the fix agent.** "Someone else should fix this" is an opinion about
   the code, not an instruction about staffing. Take the finding; ignore the staffing suggestion.

### When reassignment IS legitimate

Reassign only for a real reason, and say what it is:

- The author is genuinely blocked on a skill or area they do not cover (e.g. a frontend fix turns
  out to need a database migration).
- Capacity or parallelism — the author is occupied and the fix is urgent.
- The user explicitly asks for a different agent.

Reassignment is a routing decision like any other. It is never a consequence of being rejected.

## Examples

**Example 1: Standard rejection — author fixes it**
1. Fenster writes the authentication module
2. Hockney (Tester) reviews → rejects: "Error handling is missing on the refresh path"
3. Coordinator re-spawns **Fenster** with Hockney's specific finding attached
4. Fenster produces v2
5. Hockney reviews v2 → approves

**Example 2: Repeated rejection — still the same author**
1. Fenster writes the module → rejected
2. Fenster revises → rejected again, for a different reason
3. Coordinator re-spawns **Fenster** again, this time with both findings and a failing test
4. The second rejection is a signal the brief was unclear, not that Fenster is the problem

**Example 3: Legitimate reassignment**
1. Edie writes a TypeScript config → rejected: "This needs a change to the build pipeline"
2. Coordinator reassigns to the DevOps agent — **because the work moved outside Edie's area**,
   not because Edie was rejected
3. Coordinator states the reason explicitly when spawning

**Example 4: Reviewer oversteps**
1. Fenster writes a module → rejected
2. Hockney says: "Verbal should fix this instead"
3. Coordinator takes Hockney's technical finding and **ignores the staffing suggestion**.
   Fenster gets the fix.

## Anti-Patterns

- ❌ Locking an author out of their own artifact after a rejection
- ❌ Tracking who is "locked out" of what
- ❌ Rotating authors on repeated rejection instead of improving the brief
- ❌ Adding a new team member to work around a rejection
- ❌ Letting a reviewer decide who does the fix
- ❌ Escalating a "deadlock" that only exists because of an artificial lockout
- ❌ Reassigning without stating a concrete reason unrelated to the rejection itself
