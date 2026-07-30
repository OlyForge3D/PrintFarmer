---
name: "economy-mode"
description: "Shifts Layer 3 model selection to fast/cheap policy alternatives when economy mode is active."
domain: "model-selection"
confidence: "low"
source: "manual"
---

## SCOPE

✅ THIS SKILL PRODUCES:
- A modified Layer 3 model selection table applied when economy mode is active
- `economyMode: true` written to `.squad/config.json` when activated persistently
- Spawn acknowledgments with `💰` indicator when economy mode is active

❌ THIS SKILL DOES NOT PRODUCE:
- Code, tests, or documentation
- Cost reports or billing artifacts
- Changes to Layer 0, Layer 1, or Layer 2 resolution (user intent always wins)

## Context

Economy mode shifts Layer 3 (Task-Aware Auto-Selection) to fast/cheap policy alternatives. These are routing tiers, not verified pricing claims. Economy mode does NOT override persistent config (`defaultModel`, `agentModelOverrides`) or per-agent charter preferences — those represent explicit user intent and always take priority.

Use this skill when the user wants the fast/cheap routing policy across an entire session or permanently, without manually specifying models for each agent.

## Activation Methods

| Method | How |
|--------|-----|
| Session phrase | "use economy mode", "save costs", "go cheap", "reduce costs" |
| Persistent config | `"economyMode": true` in `.squad/config.json` |
| CLI flag | `squad --economy` |

**Deactivation:** "turn off economy mode", "disable economy mode", or remove `economyMode` from `config.json`.

## Economy Model Selection Table

When economy mode is **active**, Layer 3 auto-selection uses this table instead of the normal defaults:

| Task Output | Normal Mode | Economy Mode |
|-------------|-------------|--------------|
| Architecture, security, reviewer gates, complex coordination | `gpt-5.6-sol` | `claude-sonnet-5` |
| Visual/design under the existing Opus convention | `claude-opus-4.8` | `claude-opus-4.8` |
| Code, tests, refactoring, prompt architecture | `claude-sonnet-5` | `gpt-5.6-luna` or `gemini-3.5-flash` |
| Docs, planning, triage, mechanical work | `gpt-5.6-luna` | `gpt-5.6-luna` |
| Scribe / logger / mechanical file operations | `gpt-5.6-luna` | `gpt-5.6-luna` |

Prefer `gpt-5.6-luna` for structured output and agentic tool use. Use `gemini-3.5-flash` when an alternate fast-policy model is useful.

## AGENT WORKFLOW

### On Session Start

1. READ `.squad/config.json`
2. CHECK for `economyMode: true` — if present, activate economy mode for the session
3. STORE economy mode state in session context

### On User Phrase Trigger

**Session-only (no config change):** "use economy mode", "save costs", "go cheap"

1. SET economy mode active for this session
2. ACKNOWLEDGE: `✅ Economy mode active — using fast/cheap policy models this session. (Layer 0 and Layer 2 preferences still apply)`

**Persistent:** "always use economy mode", "save economy mode"

1. WRITE `economyMode: true` to `.squad/config.json` (merge, don't overwrite other fields)
2. ACKNOWLEDGE: `✅ Economy mode saved — fast/cheap policy routing will be used until disabled.`

### On Every Agent Spawn (Economy Mode Active)

1. CHECK Layer 0a/0b first (agentModelOverrides, defaultModel) — if set, use that. Economy mode does NOT override Layer 0.
2. CHECK Layer 1 (session directive for a specific model) — if set, use that. Economy mode does NOT override explicit session directives.
3. CHECK Layer 2 (charter preference) — if set, use that. Economy mode does NOT override charter preferences.
4. APPLY economy table at Layer 3 instead of normal table.
5. INCLUDE `💰` in spawn acknowledgment: `🔧 {Name} ({model} · 💰 economy) — {task}`

### On Deactivation

**Trigger phrases:** "turn off economy mode", "disable economy mode", "use normal models"

1. REMOVE `economyMode` from `.squad/config.json` (if it was persisted)
2. CLEAR session economy mode state
3. ACKNOWLEDGE: `✅ Economy mode disabled — returning to standard model selection.`

### STOP

After updating economy mode state and including the `💰` indicator in spawn acknowledgments, this skill is done. Do NOT:
- Change Layer 0, Layer 1, or Layer 2 model choices
- Override charter-specified models
- Generate cost reports or comparisons
- Fall back to premium models via economy mode (economy mode never bumps UP)

## Config Schema

`.squad/config.json` economy-related fields:

```json
{
  "version": 1,
  "economyMode": true
}
```

- `economyMode` — when `true`, Layer 3 uses the economy table. Optional; absent = economy mode off.
- Combines with `defaultModel` and `agentModelOverrides` — Layer 0 always wins.

## Anti-Patterns

- **Don't override Layer 0 in economy mode.** If the user set `defaultModel: "claude-opus-4.8"`, honor that explicit quality preference. Economy mode only affects Layer 3 auto-selection.
- **Don't silently apply economy mode.** Always acknowledge when activated or deactivated.
- **Don't treat economy mode as permanent by default.** Session phrases activate session-only; only "always" or `config.json` persist it.
- **Don't over-downgrade high-stakes tasks.** Architecture and security reviews shift from `gpt-5.6-sol` to `claude-sonnet-5`, not directly to the fast tier.
- **Don't invent pricing claims.** The fast/cheap tier is routing policy, not verified billing data.
