# Parker History Archive (before 2026-03-31)

## 2026-03-25: Monitoring route error / Docker DNS learnings

**Status:** ✅ Documented

- Containerized deployments must use Docker DNS names like `spoolman:8000` and `obico-ml-api:3333` for internal services. Hardcoded LAN IPs caused the same class of `No route to host` failures seen in runtime monitoring.
- Updating `.env` / `.deploy-config` back to DNS-based service names restored internal connectivity for Spoolman and reinforced that similar 3333 errors should be investigated as runtime target-selection or network issues first.

**Role:** Deployment & Infrastructure Engineer  
**Status:** ✅ COMPLETED

### Deployment Action
Executed `./scripts/pfdev redeploy api` to deploy backend fix for slicer UI visibility in microservices mode.

**Rationale:** Used targeted `pfdev` script per user directive (Jeff Papiez preference for canonical script name) rather than full `deploy-docker.sh`:
- Fast iteration (5 min vs full-stack redeploy)
- Minimal disruption to other services
- Appropriate for single-service code change during active development

### Validation
- ✅ API container rebuilt and redeployed
- ✅ `/api/system/capabilities` returns `slicingEnabled=true`
- ✅ Slicer routing working (`/api/slicer/profiles` → 200 OK)
- ✅ All containers healthy (API, slicer-host, nginx-proxy)

### User Directive Captured
Documented Jeff Papiez preference: use `pfdev` (canonical), not `pf-dev` or `pf-dev.sh`. Decision record created for team reference.

### Key Lesson
In microservices deployments, module-loading logic and capability reporting need independent detection paths. Conflating them causes false-negative capability reports when services run as separate containers.

