---
name: "external-repo-triage"
description: "Focused method for extracting architecture lessons from an external repository without over-reading it"
domain: "research"
confidence: "medium"
source: "brett"
---

## Context

Use this skill when asked to review an external repository for specific architectural lessons.
The goal is a focused evidence pass, not a full code audit.

## Pattern

1. Read the external repository README and docs first.
2. List the top-level tree to identify service boundaries and technology stacks.
3. Run scoped code searches for the requested lenses only.
4. Read 3-6 representative files that connect the flow end-to-end.
5. Capture exact file paths and line numbers while reading.
6. Compare findings to PrintFarmer in three buckets: adopt, already ahead/equivalent, open questions.

## Useful Search Terms

- For slicers: `orca`, `slicer`, `slice`, `gcode`, `profile`, `preset`, `filament`, `process`, `machine`.
- For 3D viewing: `three`, `STLLoader`, `3MF`, `gcode-preview`, `toolpath`, `model-viewer`, `Canvas`.
- For async flow: `job`, `progress`, `status_url`, `queue`, `dispatcher`, `worker`.

## Anti-Patterns

- Do not read the whole repository.
- Do not generalize from README marketing claims without checking source files.
- Do not propose implementation changes unless the user asks; keep the first pass fact-finding.
