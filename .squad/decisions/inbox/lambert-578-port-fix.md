### 2026-07-13: Drain-gate engines-endpoint must cover both deployment modes
**By:** Lambert
**What:** Reworded the drain-gate validation step in `docs/ORCASLICER_INTEGRATION.md`
(step 5 of the previous-engine retirement procedure, around line 166) so the
`GET /api/slicers/engines` check explicitly covers both deployment modes:
monolithic/single-container on the main API port `5245`, and microservices
where nginx routes `/api/slicers` to `slicer-host` on `5246`. The curl now
uses a `SLICER_ENGINES_URL` env variable defaulting to `5245`, plus a short
inline note about the `5246` slicer-host route. No other `5245` references
in the doc were touched (lines near 205, 706, 722, 971 preserved).
**Why:** Hicks r9 flagged that the original single-URL example silently
misdirected microservices operators to the wrong port, hiding drain
regressions where the retired engine still appears on `slicer-host:5246`.
Routing evidence: `deploy/nginx/nginx-proxy-split.conf` L81 sets
`$slicer_upstream http://slicer-host:5246`, L118–L135 defines
`location /api/slicers/ { proxy_pass $slicer_upstream$request_uri; ... }`;
`scripts/docker/compose-generator.sh` L759–L761 switches nginx to
`nginx-proxy-split.conf` when the slicer-host addon is merged; and
`.github/copilot-instructions.md` L113 lists `/api/slicers` under the
Slicer host route-ownership row.
