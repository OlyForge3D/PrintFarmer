---
name: review
description: Run three parallel code reviews (Bishop/Opus, Hicks/GPT-5.6 Sol, Vasquez/Gemini 3.8 Flash) and synthesize findings into a prioritized fix list
---
Run a 3-way multi-model code review across the adversarial review panel:

1. Dispatch Bishop, Hicks, and Vasquez as three parallel read-only task agents using canonical `agent_type: "code-review"` with explicit model overrides. `agent_type` must NEVER be set to a custom agent display name like `"Code Review (Opus)"` since that string is not a valid `agent_type` enum value and resolves unreliably across backends:
   - **Bishop**: `name: "bishop"`, `agent_type: "code-review"`, `model: "claude-opus-5"`, `reasoning_effort: "medium"`
   - **Hicks**: `name: "hicks"`, `agent_type: "code-review"`, `model: "gpt-5.6-sol"`, `reasoning_effort: "medium"`
   - **Vasquez**: `name: "vasquez"`, `agent_type: "code-review"`, `model: "gemini-3.8-flash"`, `reasoning_effort: "medium"`
   Because generic `agent_type: "code-review"` does not automatically load custom agent markdown files, each reviewer's dispatch prompt MUST inline the full persona, read-only contract, PrintFarmer standards checklist, and canonical verdict output template directly from `.github/agents/code-review-opus.agent.md` (Bishop), `.github/agents/code-review-codex.agent.md` (Hicks), and `.github/agents/code-review-gemini.agent.md` (Vasquez).
2. Pass complete context in each dispatch prompt: repository worktree root path (`cwd`), current branch name, base branch (`origin/development`), and exact head commit SHA to review.
3. Reviewers are strictly read-only: verify `git rev-parse HEAD` matches the target SHA and inspect diffs via `git diff origin/development...<target-head-sha>` or read-only tools (`view`, `grep`, `glob`, `lsp`). Do NOT modify code or files, do NOT create, edit, or delete `Copilot-Processing.md` or tracking files, and do NOT run builds, package installations, or test suites.
4. Each reviewer outputs a structured verdict starting with the canonical record block as plain, UNFENCED, and unquoted text (never wrapped in code fences ``` or blockquotes >):
   <!-- squad-verdict -->
   Squad-Reviewer: <bishop|hicks|vasquez>
   Squad-Verdict: APPROVE | REQUEST_CHANGES
   Squad-Head-SHA: <exact-40-character-sha>
5. Synthesize findings ordered by severity (Critical > Major > Minor > Nit).
6. Report consensus verdict: unanimous APPROVE (3/3) to proceed to PR creation; REQUEST_CHANGES if any reviewer rejects.

