### 2026-03-18T03:53:10Z: User directive
**By:** Jeff Papiez (via Copilot)
**What:** Agents must verify imports exist before using them. Never guess at export names — read the source file first. Specifically: before importing from a barrel export or icon library, check what's actually exported. This applies to all agents writing code.
**Why:** User request — ObicoServersSection.tsx used `TestTubeIcon` and `PencilIcon` which don't exist in MdiIcons.tsx. This broke the production build and wasn't caught until Docker deployment failed. Verify, don't assume.
