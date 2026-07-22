# Anvil — Code Review & Revision History

## Core Context

- Evidence-first code reviewer on PrintFarmer project
- Adversarial multi-model review, IDE diagnostics, SQL-tracked verification
- Authorized for independent revision cycles when consensus review determines re-authoring needed
- Project: C# .NET 10 API + React 19 TypeScript frontend for 3D printer management
- Owner: Jeff Papiez

## Learnings

_(append new learnings below this line)_

### 2026-07-19 — Hudson #785 Independent Third Revision (authorized)

- **Issue:** #785
- **Candidate SHA:** 536bce0650d24c186b8c12a939046212bd8fc5b6 (exact, clean)
- **Session ID:** 691f8ba6-6037-4624-9b56-4885f0ce2ce2
- **Worktree:** `/Users/jpapiez/s/copilot-worktrees/PFarm1/jpapiez-laughing-fiesta`
- **Branch:** `jpapiez-laughing-fiesta`
- **Verification:** Clean HEAD exact 536bce065; merge-base exact; zero patch artifacts
- **Authorization Basis:** Bishop & Hicks REQUEST_CHANGES identified 6 blocker concerns; Vasquez final review invalidated by contamination incident; Hudson locked out; independent revision required
- **Scope (Address All 6 Blockers):**
  1. Tokenless Dashboard publish/activation order
  2. Server-switch auth strand and stale continuation
  3. Activation nonce/currentLease race
  4. Fire-and-forget namespace revoke ordering
  5. Offline cached authority
  6. Direct auth/runtime-switch proof
- **Push Status:** None yet (awaiting work completion)
- **Orchestration:** `.squad/orchestration-log/2026-07-19T22-29-26Z-scribe-hudson-785-review-cycle.md`

### 2026-07-20 — Ripley #782 Operator Redesign Independent Reconstruction (authorized)

- **Issue:** #782 (Operator Redesign)
- **Previous Author:** Ripley
- **Rejected Candidate SHA:** 680515c94d3f806b7e14351657b230c063f2c7ad
- **Feature Base SHA:** 967474c1bc2d4b44aa9bbbf1c3730d0df8fb5019 (clean, verified)
- **Session ID:** 193b2bc9-939a-4aad-8eee-748e4d2f7e21
- **Authorization Basis:** 
  - Bishop & Hicks REQUEST_CHANGES (two explicit blockers)
  - Vasquez conditional APPROVE (contingent on blocker resolution) 
  - Two blockers override single conditional per team policy → formal rejection
  - Ripley locked due to worktree boundary incident (external detach without Ripley command)
- **Scope (P1-P10 Reconstruction Contract):**
  - **P1:** Epoch-safe mutation/refetch semantics
  - **P2:** Queued callback fencing (prevent interleaving)
  - **P3:** Raw SignalR FIFO parser matrix (state isolation)
  - **P4:** Causal Retry/Dismiss action ordering
  - **P5:** Single a11y action entry point (consolidation)
  - **P6:** Shared locale/timezone formatting (i18n)
  - **P7:** No Release hooks in operator lifecycle
  - **P8:** Exact-SHA evidence capture (SRI/attestation)
  - **P9–P10:** Reserved for trio blocker resolution
- **Mandate:** 
  - Rebuild from clean origin/feature/705-operator-redesign @967474c1
  - Do NOT reference Ripley's 680515c9 candidate
  - Address all P1-P10 contract requirements explicitly
  - Prepare fresh diff for Bishop/Hicks/Vasquez unified re-review
  - Target unanimous APPROVE before PR open
- **Locked Agents:** Ripley, Bishop, Hicks, Gorman, Hudson, Kane (no further work until Anvil submits)
- **Public Actions:** 
  - Issue comment posted to #782 (live)
  - Owner label changed: squad:⚩ ripley → squad:🔨 anvil
- **Push Status:** None yet (awaiting work completion)
- **Orchestration:** `.squad/orchestration-log/2026-07-20T10-49-02Z-scribe-782-ripley-rejection-anvil-handoff.md`

---

## 2026-07-20T10:51:27Z — #782 Lockout Scope Clarification (Append-Only)

**Scope:** Clarified #782 agent-lock enumeration in prior rejection log.

**Authoritative Lock Status for #782:**
- **Locked Authors (No Further Work):** Ripley, Gorman, Hudson, Kane
- **Not Locked (Mandatory Reviewers Required):** Bishop, Hicks, Vasquez
- **Anvil Status:** NOT locked; authorized and current independent revision author for #782

**Clarification Detail:**
Team policy restricts reviewer-rejection lockout to artifact/revision authors only. Reviewers (Bishop, Hicks, Vasquez) must remain independent and unbiased for all subsequent review cycles, including Anvil's exact-SHA unified re-review.

**Anvil Mandate Remains Unchanged:**
- Reconstruct from clean origin/feature/705-operator-redesign @967474c1
- Address P1-P10 contract requirements explicitly
- Prepare fresh diff for Bishop/Hicks/Vasquez unified re-review (all three required reviewers remain eligible and independent)
- Target unanimous APPROVE before PR open

**Orchestration Reference:**  
`.squad/orchestration-log/2026-07-20T10-51-27Z-scribe-782-lockout-correction.md`

**Status:** ✅ Clarification recorded (append-only; prior erroneous log preserved); Anvil's mandate and authorization unchanged
