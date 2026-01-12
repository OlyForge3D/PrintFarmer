## Soft Freeze (MVP Stabilization)

The project is in a soft freeze to stabilize for the MVP release.

Active when the file `.soft-freeze` exists at repo root. Remove it in a PR (with approval) to end the freeze.

### Allowed (No Exception Needed)
- Feature code in `src/api`, `src/Web/ReactApp`, `src/shared`
- Tests, docs, assets, comments

### Restricted (Need label `allow-freeze-exception` OR commit marker `[freeze-exception]`)
- Dependency & build manifests: `package.json`, `package-lock.json`, `*.csproj`, `Directory.Build.props`, `global.json`
- Tool/build configs: `vite.config.*`, `vitest.config.*`, `tsconfig.*`
- CI / workflows: `.github/workflows/*`
- Deployment & container: `Dockerfile*`, `docker-compose*.yml`
- Scripts affecting build/deploy/security in `scripts/`

### Strongly Discouraged During Freeze
- Adding new major dependencies
- Upgrading frameworks/toolchain versions
- Large refactors that churn many files

### Exception Process
1. Open PR touching restricted files.
2. Add label `allow-freeze-exception` (maintainer) OR include `[freeze-exception]` in a commit message.
3. Justify risk + mitigation in PR description.
4. Ensure CI green; no coverage regression.

### Local Check
Run:
```bash
./scripts/check-soft-freeze.sh
```
Exits non‑zero if restricted files changed without exception marker.

### Rationale
Reduces late-stage build instability and shortens release candidate cycle.

### Exit Criteria
- All `mvp` issues closed
- No open P1 bugs
- Release candidate passes full CI and smoke tests

---
See `scripts/check-soft-freeze.sh` and workflow `soft-freeze-guard.yml` for enforcement details.