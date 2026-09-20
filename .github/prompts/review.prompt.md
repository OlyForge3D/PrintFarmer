---
name: review
description: Route risk-based reviews with distinct lenses, stable findings, and continuation-first checkpoints
mode: agent
tools: ['*']
---
Run a risk-routed review under [Risk-Based Review Scope](../copilot-instructions.md#risk-based-review-scope).
Draft PRs may open early; review, CI, and current-head evidence gate readiness and merge.

1. Select the required reviewers, not the entire eligible roster. Low-risk work gets
   one qualified non-author reviewer from a different model family than the author.
   High-risk work gets two differentiated reviewers. Assign distinct primary lenses:
   Bishop: integration/architecture; Hicks: behavior/contracts/tests;
   Vasquez: trust/failure/concurrency. Invoke the third only for disagreement,
   a critical finding, or unresolved cross-domain risk; record why.
   Dispatch selected reviewers as parallel read-only task agents using canonical
   `agent_type: "code-review"` with explicit model overrides:
   - **Bishop**: `name: "bishop"`, `agent_type: "code-review"`, `model: "claude-opus-5"`, `reasoning_effort: "medium"`
   - **Hicks**: `name: "hicks"`, `agent_type: "code-review"`, `model: "gpt-5.6-sol"`, `reasoning_effort: "medium"`
   - **Vasquez**: `name: "vasquez"`, `agent_type: "code-review"`, `model: "gemini-3.8-flash"`, `reasoning_effort: "medium"`
   Generic `agent_type: "code-review"` does not load custom agent markdown.
   On initial dispatch, supply the selected reviewer's persona, read-only contract,
   applicable standards, and verdict template from `.github/agents/code-review-opus.agent.md`
   (Bishop), `.github/agents/code-review-codex.agent.md` (Hicks), or
   `.github/agents/code-review-gemini.agent.md` (Vasquez). Do not load other personas.
2. Pass complete context in each dispatch prompt: repository worktree root path (`cwd`), current branch name, base branch (`origin/development`), and exact head commit SHA to review. For follow-on rounds, also supply the prior reviewed head, findings, and correction summary per [Delta-Only Panel Rereview](../copilot-instructions.md#delta-only-panel-rereview).
3. Reviewers are strictly read-only: verify `git rev-parse HEAD` matches the target SHA. The initial round inspects `git diff origin/development...<target-head-sha>`; follow-on rounds use the canonical delta-only scope linked above. Use only read-only tools (`view`, `grep`, `glob`, `lsp`). Do NOT modify code or files, do NOT create, edit, or delete `Copilot-Processing.md` or tracking files, and do NOT run builds, package installations, or test suites.
4. Each reviewer outputs a structured verdict starting with the canonical record block as plain, UNFENCED, and unquoted text (never wrapped in code fences ``` or blockquotes >):
   <!-- squad-verdict -->
   Squad-Reviewer: <bishop|hicks|vasquez>
   Squad-Verdict: APPROVE | REQUEST_CHANGES
   Squad-Head-SHA: <exact-40-character-sha>
5. Deduplicate findings using stable IDs, severity, owner, failure scenario, evidence,
   and closure criteria under [Findings and Reviewer Checkpoints](../copilot-instructions.md#findings-and-reviewer-checkpoints).
   Append compact immutable per-reviewer checkpoints. Use continuation-first follow-on
   review: resume a supported session; otherwise hand off its checkpoint, applicable
   read-only contract, complete per-reviewer prior-SHA -> new-head delta, all unresolved
   findings, and correction summary. Never reload full transcripts by default.
6. Report readiness only when required reviews, CI, and current-head evidence pass.
   A third approval cannot erase another reviewer's active rejection; that reviewer
   must clear/update their verdict or the authenticated owner must explicitly override.
   Preserve overridden dissent in the ledger; authorization is not review.
