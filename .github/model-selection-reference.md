# Model Selection Reference

### Per-Agent Model Selection

Before spawning an agent, determine which model to use. Check these layers in order — first match wins:

**Layer 0 — Persistent Config (`.squad/config.json`):** On session start, read `.squad/config.json`. If `agentModelOverrides.{agentName}` exists, use that model for this specific agent. Otherwise, if `defaultModel` exists, use it for ALL agents. This layer survives across sessions — the user set it once and it sticks.

- **When user says "always use X" / "use X for everything" / "default to X":** Write `defaultModel` to `.squad/config.json`. Acknowledge: `✅ Model preference saved: {model} — all future sessions will use this until changed.`
- **When user says "use X for {agent}":** Write to `agentModelOverrides.{agent}` in `.squad/config.json`. Acknowledge: `✅ {Agent} will always use {model} — saved to config.`
- **When user says "switch back to automatic" / "clear model preference":** Remove `defaultModel` (and optionally `agentModelOverrides`) from `.squad/config.json`. Acknowledge: `✅ Model preference cleared — returning to automatic selection.`

**Layer 1 — Session Directive:** Did the user specify a model for this session? ("use opus for this session", "save costs"). If yes, use that model. Session-wide directives persist until the session ends or contradicted.

**Layer 2 — Charter Preference:** Does the agent's charter have a `## Model` section with `Preferred` set to a specific model (not `auto`)? If yes, use that model.

**Layer 3 — Task-Aware Auto-Selection:** Match the agent's task to the output type, then select accordingly:

| Task Output | Model | Tier | Rule |
|-------------|-------|------|------|
| Architecture, security, reviewer gates, complex coordination | `gpt-5.6-sol` | Premium | Use the strongest reasoning default for high-stakes decisions. |
| Visual/design work under the existing Opus convention | `claude-opus-4.8` | Premium | Preserve the established visual/design model convention. |
| Code, tests, refactoring, prompt architecture | `claude-sonnet-5` | Standard | Use the standard implementation model. |
| Docs, planning, triage, mechanical work | `gpt-5.6-luna` | Fast/Cheap | Use the fast policy default for non-code work. |

**Role-to-model mapping:**

| Role | Default Model | Why | Override When |
|------|---------------|-----|---------------|
| Core Dev / Backend / Frontend | `claude-sonnet-5` | Writes code — quality first | Heavy code generation → `gpt-5.3-codex` |
| Tester / QA | `claude-sonnet-5` | Writes test code — quality first | Mechanical test updates → `gpt-5.6-luna` |
| Lead / Architect | auto (per-task) | Mixed architecture and planning work | Architecture/reviewer gates → `gpt-5.6-sol`; planning → `gpt-5.6-luna` |
| Prompt Engineer | auto (per-task) | Prompt architecture functions like code | Prompt architecture → `claude-sonnet-5`; research → `gpt-5.6-luna` |
| Copilot SDK Expert | `claude-sonnet-5` | Technical analysis that often touches code | Pure research → `gpt-5.6-luna` |
| Designer / Visual | `claude-opus-4.8` | Existing Opus visual/design convention | — |
| DevRel / Writer | `gpt-5.6-luna` | Docs and writing — not code | — |
| Scribe / Logger | `gpt-5.6-luna` | Mechanical file operations | — |
| Git / Release | `gpt-5.6-luna` | Mechanical operations | — |

**Task complexity adjustments** (apply at most ONE — no cascading):

- **Use premium (`gpt-5.6-sol`):** architecture proposals, reviewer gates, security audits, complex multi-agent coordination
- **Use fast/cheap (`gpt-5.6-luna`):** typo fixes, renames, boilerplate, scaffolding, changelogs, version bumps
- **Use the heavy code specialist (`gpt-5.3-codex`):** large multi-file refactors, complex implementation from specification, heavy code generation
- **Use analytical diversity (`gemini-3.1-pro-preview`):** reviews where an independent perspective helps, especially after a rejection

**Layer 4 — Default:** If nothing else matched, use `gpt-5.6-luna`.

**Fallback chains — when a model is unavailable:**

If a spawn fails because the selected model is unavailable (plan restriction, org policy, rate limit, deprecation, runtime rejection, or any other reason), silently retry with the next model in the applicable chain. Make at most three retries, then omit the model parameter.

```
Premium:  gpt-5.6-sol → claude-opus-4.8 → claude-opus-4.7 → claude-sonnet-5 → (omit model param)
Standard: claude-sonnet-5 → gpt-5.6-terra → gpt-5.5 → claude-sonnet-4.6 → (omit model param)
Fast:     gpt-5.6-luna → gemini-3.5-flash → claude-haiku-4.5 → gpt-5.4-mini → (omit model param)
Visual:   claude-opus-4.8 → claude-opus-4.7 → claude-opus-4.6 → (omit model param)
```

`(omit model param)` means calling the spawn tool without a `model` parameter so the platform chooses its runtime default. Runtime rejection overrides this static catalog.

**Fallback rules:**

- If the user specified a provider (for example, "use Claude"), try compatible models from that provider in chain order, then omit the model parameter
- Never fall back UP in tier; the Premium chain may fall down to Standard, but Standard and Fast chains never move upward
- Keep the model ID separate from reasoning effort; never create suffix IDs such as `-xhigh`
- Drop unsupported reasoning-effort or context-window parameters when a fallback model does not accept them
- Log fallbacks to the orchestration log for debugging, but do not surface them to the user unless asked

**Passing the model to spawns:**

Pass the resolved model as the `model` parameter on every supported spawn tool call:

```
agent_type: "general-purpose"
model: "{resolved_model}"
mode: "background"
name: "{name}"
description: "{emoji} {Name}: {brief task summary}"
prompt: |
  ...
```

If the fallback chain is exhausted, omit the `model` parameter entirely.

**Spawn output format — show the model choice:**

```
🔧 Fenster (claude-sonnet-5) — refactoring auth module
🎨 Redfoot (claude-opus-4.8 · visual) — designing color system
📋 Scribe (gpt-5.6-luna · fast) — logging session
⚡ Keaton (gpt-5.6-sol · architecture) — reviewing proposal
🧪 Vasquez (gemini-3.1-pro-preview · analytical diversity) — independently reviewing implementation
```

Include a tier annotation only when the model was bumped or a specialist was chosen. Default-tier spawns just show the model name.

### Per-Agent Reasoning Effort

Reasoning effort is resolved independently **after** the model is selected. Check these layers in order:

**Layer 0 — Agent-Specific Override (`.squad/config.json`):** On this machine, specific agents use elevated reasoning effort defined in `agentReasoningEffortOverrides.{agentName}`:

- **Implementation agents** (Lambert, Ripley, Hudson, Gorman, Parker): reasoning effort `max`
- **Code reviewers:**
  - Bishop (`claude-opus-5`): reasoning effort `medium`
  - Hicks (`gpt-5.6-sol`): reasoning effort `medium`
  - Vasquez (`gemini-3.1-pro-preview`): reasoning effort `medium`

These overrides are automatically resolved when spawning. Work continues until verified and mandatory gates pass. Unavoidable platform/provider hard limits still apply.

**Layer 1 — Session Directive:** Did the user specify reasoning effort for this session? If yes, use that effort for this session.

**Layer 2 — Charter Preference:** Does the agent's charter have a `## Reasoning Effort` section with a preference? If yes, use that effort.

**Layer 3 — Platform Default:** If nothing matched, use the platform's default reasoning effort for the selected model.

Model IDs and reasoning effort are separate parameters. Never encode effort into a model ID. When falling back, omit reasoning-effort or context-window parameters unsupported by the fallback model.

**No-artificial-budget directives:** The machine-local policy removes self-imposed time, tool-call, review-round, and iteration budgets. This does **not** disable unavoidable platform, provider, or infrastructure hard limits (rate limits, timeout limits, resource quotas, etc.).

**Valid models (current platform catalog):**

Premium: `gpt-5.6-sol`, `claude-opus-4.8`, `claude-opus-4.7`, `claude-opus-4.6`
Standard: `claude-sonnet-5`, `gpt-5.6-terra`, `gpt-5.5`, `gpt-5.4`, `gpt-5.3-codex`, `claude-sonnet-4.6`, `claude-sonnet-4.5`, `gemini-3.1-pro-preview`
Fast/Cheap policy: `gpt-5.6-luna`, `gemini-3.5-flash`, `claude-haiku-4.5`, `gpt-5.4-mini`, `gpt-5-mini`, `mai-code-1-flash-picker`

These are routing tiers, not verified pricing claims. Runtime rejection overrides the static catalog.

> **Local policy note:** These manually maintained references can be overwritten by a future Squad upgrade. Reapply or reconcile this local policy after upgrading; no upstream package is changed by this repository-local customization.
