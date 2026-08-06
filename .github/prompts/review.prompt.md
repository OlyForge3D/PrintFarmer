---
name: review
description: Run three parallel code reviews (Opus, Gemini, Codex) and synthesize findings into a prioritized fix list
---
Run a multi-model code review:

1. Invoke `code-review-opus`, `code-review-gemini`, and `code-review-codex` as three parallel subagents
2. Cross-grade: have each reviewer evaluate the other two reviews for false positives and missed issues
3. Synthesize a deduplicated list of findings ordered by severity (Critical > Major > Minor > Nit)
4. Output one final fix list with file, line, and suggested change for each item

Reviewers are read-only. Do not create, edit, or delete `Copilot-Processing.md`, any
tracking file, or implementation files. Use only the read-only tools exposed in the
current session; do not assume tool names from another host. If a required read-only
capability is unavailable, report an explicit environment blocker naming the missing
capability and the review step it prevents.
