### 2026-03-18T03:54:12Z: User directive — Verify before you use
**By:** Jeff Papiez (via Copilot)
**What:** Before referencing ANY external symbol, route, or API endpoint in code, agents MUST verify it exists:
1. **Imports**: Before importing a symbol (icon, component, hook, type), read the source file and confirm the export exists. Never guess names.
2. **API routes**: Before calling an API endpoint from frontend code, confirm the controller action and route attribute exist in the backend. Grep for the route pattern.
3. **API methods**: Before using an `apiClient.method()`, confirm the method exists in `src/services/api.ts`.
This is not optional. Unverified references cause production build failures, runtime 404s, and wasted debugging time.
**Why:** Two bugs in this session traced to the same root cause — assuming things exist without checking. TestTubeIcon/PencilIcon broke the Docker build. /api/job-queue/{id}/rerun caused a 404 because no controller route existed.
