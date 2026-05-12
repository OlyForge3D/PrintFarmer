# Decision: go2rtc Deployment Integration

**Author:** Dallas (Lead / Architect)  
**Date:** 2026-05-12  
**Status:** APPROVED  
**Impact:** Low (deployment tooling addition, no runtime behavior change unless opted in)

---

## Question

Does `deploy-docker.sh` need modification to include the go2rtc container, or will it always be deployed?

## Answer: Yes, Both Scripts Need Modification

The go2rtc compose template (`docker-compose.go2rtc.yml`) exists in `scripts/docker/compose-templates/` but **neither `deploy-docker.sh` nor `compose-generator.sh` reference it**. The template is inert — it will never be included in a generated `docker-compose.yml` without code changes.

## Analysis

### How Compose Assembly Works

1. `deploy-docker.sh` calls `compose-generator.sh` with `--include-*` flags based on user choices
2. `compose-generator.sh` maintains `INCLUDE_*` boolean variables (default false for opt-in services)
3. Each optional service has a corresponding `merge_addon_services` call gated by its `INCLUDE_*` flag
4. The template comment in `docker-compose.go2rtc.yml` line 3 already says _"conditionally included via compose-generator.sh when --include-go2rtc is passed"_ — but that flag doesn't exist yet

### Existing Opt-In Service Pattern (Spoolman, Obico ML)

The established pattern for opt-in services is:

**In `compose-generator.sh`:**
- Add `INCLUDE_GO2RTC="false"` to defaults (~line 221)
- Add `--include-go2rtc)` case to argument parser (~line 256)
- Add merge block after Obico ML (~line 795):
  ```bash
  if [[ "$INCLUDE_GO2RTC" == "true" ]]; then
      if merge_addon_services "$compose_file" "go2rtc"; then
          log_info "Merged go2rtc RTSP-to-WebRTC bridge service"
          addons_merged=true
      else
          log_warning "Failed to merge go2rtc service, continuing without it"
      fi
  fi
  ```
- Add `--include-go2rtc` to usage help text (~line 150)

**In `deploy-docker.sh`:**
- Add `DEPLOY_GO2RTC` variable or `ENABLE_GO2RTC` env var handling
- Pass `--include-go2rtc` to generator when enabled (~line 857)
- Add `--include-go2rtc` to CLI flags and help text (~line 2323)
- Optionally: add interactive prompt during deployment wizard

## Recommendation: Opt-In Flag (Not Always-Deploy)

**Use `--include-go2rtc` opt-in flag**, matching the Spoolman/Obico pattern. Rationale:

1. **go2rtc defaults to disabled** — `Go2Rtc:Enabled = false` in backend config. Deploying the container without enabling the feature wastes resources.
2. **Not all farms have cameras** — many deployments are print-only with no camera infrastructure.
3. **Consistency** — every other optional sidecar (Spoolman, Obico ML, pgAdmin, registry) follows the opt-in pattern. go2rtc should not be the exception.
4. **~30MB is lightweight but not zero** — on resource-constrained single-board computers (common in print farms), every container counts.

### When to Always-Deploy Instead

If camera support becomes a core PrintFarmer differentiator and most users expect it, we could switch to always-on (like monitoring/telemetry). But that's a future decision, not today's.

## Effort Estimate

~30 minutes of scripting work. Both files follow clear, repeatable patterns. No testing infrastructure changes needed — just compose generation verification.

## Dependencies

- `docker-compose.go2rtc.yml` template: ✅ Already exists (Lambert created it)
- Backend `Go2Rtc:Enabled` config: ✅ Already exists
- `merge_addon_services` function: ✅ Already handles arbitrary template names
