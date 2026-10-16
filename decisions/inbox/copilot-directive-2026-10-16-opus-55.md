### 2026-10-16: Replace Opus 5 with Opus 5.5
**By:** Jeff Papiez (via Copilot)
**What:** User-directed: "we should replace all usages of opus 5 with opus 5.5". Use `claude-opus-5.5` for future dispatches formerly using `claude-opus-5`; update active repository model configuration, agent definitions, templates and documentation. Preserve historical immutable review and log evidence.
**Why:** Opus 5 dispatches failed as unsupported; Opus 5.5 dispatches succeeded. Do not cancel or relaunch already running Opus 5.5 reviews solely for this change.
