# Model Selection

> Determines which LLM model to use for each agent spawn.

## SCOPE

✅ THIS SKILL PRODUCES:
- A resolved `model` parameter for every supported agent spawn
- Persistent model preferences in `.squad/config.json`
- Spawn acknowledgments that include the resolved model

❌ THIS SKILL DOES NOT PRODUCE:
- Code, tests, or documentation
- Model performance benchmarks
- Cost reports or billing artifacts

## Context

Squad supports 18 models across three routing tiers (premium, standard, fast/cheap). These tiers are policy groupings, not verified pricing claims. The coordinator must select the right model for each agent spawn while preserving explicit user preferences.

## 5-Layer Model Resolution Hierarchy

Resolution is **first-match-wins** — the highest layer with a value wins.

| Layer | Name | Source | Persistence |
|-------|------|--------|-------------|
| **0a** | Per-Agent Config | `.squad/config.json` → `agentModelOverrides.{name}` | Persistent (survives sessions) |
| **0b** | Global Config | `.squad/config.json` → `defaultModel` | Persistent (survives sessions) |
| **1** | Session Directive | User said "use X" in current session | Session-only |
| **2** | Charter Preference | Agent's `charter.md` → `## Model` section | Persistent (in charter) |
| **3** | Task-Aware Auto | Select by task output | Computed per-spawn |
| **4** | Default | `gpt-5.6-luna` | Final automatic default |

**Key principle:** Layer 0 (persistent config) beats everything. If the user saved a model preference, honor it regardless of role or task type.

## AGENT WORKFLOW

### On Session Start

1. READ `.squad/config.json`
2. CHECK for `defaultModel` field — if present, this is the Layer 0 override for all spawns
3. CHECK for `agentModelOverrides` field — if present, these are per-agent Layer 0a overrides
4. STORE both values in session context for the duration

### On Every Agent Spawn

1. CHECK Layer 0a: Is there an `agentModelOverrides.{agentName}` in config.json? → Use it.
2. CHECK Layer 0b: Is there a `defaultModel` in config.json? → Use it.
3. CHECK Layer 1: Did the user give a session directive? → Use it.
4. CHECK Layer 2: Does the agent's charter have a `## Model` section? → Use it.
5. CHECK Layer 3: Determine task type:
   - Architecture, security, reviewer gates, complex coordination → `gpt-5.6-sol`
   - Visual/design under the existing Opus convention → `claude-opus-4.8`
   - Code, tests, refactoring, prompt architecture → `claude-sonnet-5`
   - Docs, planning, triage, mechanical work → `gpt-5.6-luna`
   - Heavy code generation → `gpt-5.3-codex`
   - Analytical diversity → `gemini-3.1-pro-preview`
6. FALLBACK Layer 4: `gpt-5.6-luna`
7. INCLUDE model in spawn acknowledgment: `🔧 {Name} ({resolved_model}) — {task}`

### When User Sets a Preference

**Trigger phrases:** "always use X", "use X for everything", "switch to X", "default to X"

1. VALIDATE the model ID against the 18-model catalog
2. WRITE `defaultModel` to `.squad/config.json` (merge, don't overwrite)
3. ACKNOWLEDGE: `✅ Model preference saved: {model} — all future sessions will use this until changed.`

**Per-agent trigger:** "use X for {agent}"

1. VALIDATE model ID
2. WRITE to `agentModelOverrides.{agent}` in `.squad/config.json`
3. ACKNOWLEDGE: `✅ {Agent} will always use {model} — saved to config.`

### When User Clears a Preference

**Trigger phrases:** "switch back to automatic", "clear model preference", "use default models"

1. REMOVE `defaultModel` from `.squad/config.json`
2. ACKNOWLEDGE: `✅ Model preference cleared — returning to automatic selection.`

### STOP

After resolving the model and including it in the spawn template, this skill is done. Do NOT:
- Generate model comparison reports
- Run benchmarks or speed tests
- Create new config files (only modify existing `.squad/config.json`)
- Change the model after spawn (fallback chains handle runtime failures)

## Config Schema

`.squad/config.json` model-related fields:

```json
{
  "version": 1,
  "defaultModel": "claude-opus-4.8",
  "agentModelOverrides": {
    "fenster": "claude-sonnet-5",
    "mcmanus": "gpt-5.6-luna"
  }
}
```

- `defaultModel` — applies to ALL agents unless overridden by `agentModelOverrides`
- `agentModelOverrides` — per-agent overrides that take priority over `defaultModel`
- Both fields are optional. When absent, Layers 1-4 apply normally.

## Valid Model Catalog

Premium: `gpt-5.6-sol`, `claude-opus-4.8`, `claude-opus-4.7`, `claude-opus-4.6`
Standard: `claude-sonnet-5`, `gpt-5.6-terra`, `gpt-5.5`, `gpt-5.4`, `gpt-5.3-codex`, `claude-sonnet-4.6`, `claude-sonnet-4.5`, `gemini-3.1-pro-preview`
Fast/Cheap policy: `gpt-5.6-luna`, `gemini-3.5-flash`, `claude-haiku-4.5`, `gpt-5.4-mini`, `gpt-5-mini`, `mai-code-1-flash-picker`

Runtime rejection overrides this static catalog.

## Fallback Chains

If a model is unavailable, make at most three retries, then omit the model parameter:

```
Premium:  gpt-5.6-sol → claude-opus-4.8 → claude-opus-4.7 → claude-sonnet-5 → (omit model param)
Standard: claude-sonnet-5 → gpt-5.6-terra → gpt-5.5 → claude-sonnet-4.6 → (omit model param)
Fast:     gpt-5.6-luna → gemini-3.5-flash → claude-haiku-4.5 → gpt-5.4-mini → (omit model param)
Visual:   claude-opus-4.8 → claude-opus-4.7 → claude-opus-4.6 → (omit model param)
```

- If the user specified a provider, try compatible models from that provider in chain order, then omit the model parameter.
- Never fall UP in tier. Premium may fall down to Standard; Standard and Fast never move upward.
- Keep reasoning effort and context-window settings separate from model IDs. Never create suffix IDs such as `-xhigh`.
- Drop reasoning-effort or context-window parameters unsupported by the fallback model.
- Runtime rejection overrides the static catalog.
