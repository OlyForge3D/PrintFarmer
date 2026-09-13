## Printer discovery troubleshooting

The supported deployment runs `printer-discovery` and `api` on
`printfarmer-network`, a Docker bridge. Discovery sends TCP/HTTP probes to
configured printer subnets and connects to **`http://api:5245`** using Docker
DNS. The API reaches discovery at
`http://printer-discovery:5247/api/discovery/health`. Port 5247 is internal;
5246 belongs to slicer-host, not discovery.

Discovery needs no Docker control access, shared host namespaces, elevated
execution, or additional capabilities. Do not add these when diagnosing an outage.
Keep the non-root image user, read-only root, dropped capabilities,
no-new-privileges, and bounded scratch/resources from the canonical template.

### Regenerate and recreate an older deployment

Run from the repository root using the deployment's existing saved settings.
Review local overrides and remove legacy networking, socket mounts, engine
proxies, added capabilities, and stale `DISCOVERY__API_BASE_URL` overrides.
The supported API URL is `http://api:5245`, regardless of the published host port.
Do not edit templates or generated YAML as a repair; do not run Compose directly
against unmerged template fragments.

```bash
# Default deployment directory, saved settings and environment:
./scripts/docker/fix-discovery-heartbeat.sh

# Explicit deployment directory, environment file and saved configuration:
./scripts/docker/fix-discovery-heartbeat.sh /path/to/deployment \
  /path/to/deployment/runtime.env /path/to/deployment/saved-config
```

The repair requires an existing saved configuration, runs `deploy-docker.sh`
with discovery enabled to regenerate from current canonical templates, then
force-recreates discovery and waits up to 120 seconds for health.
This is a deployment operation and can update other services; schedule it
accordingly. A restart alone cannot remove old mounts or change networking.
`fix-discovery-simple.sh` delegates to the same repair with identical arguments.
Custom Compose overlays must be reviewed and regenerated through their owning
deployment process before verification; these helpers select one generated file.

### Verify the actual bridge paths

```bash
./scripts/docker/verify-discovery-service.sh
# Or select the same deployment directory and environment file:
./scripts/docker/verify-discovery-service.sh /path/to/deployment \
  /path/to/deployment/runtime.env
```

The verifier fails nonzero if containers are missing/stopped, discovery's live
isolation is wrong, the services do not share a bridge, or a bounded HTTP probe
fails. It checks discovery's own health, discovery → API `/healthz`, and
API → discovery `/api/discovery/health`, inside the actual containers.
It does not dump container environments or automatically print logs.

Equivalent HTTP checks, using the selected generated Compose and environment:

```bash
docker compose --env-file .env -f docker-compose.yml exec -T printer-discovery \
  curl --fail --silent --show-error --connect-timeout 5 --max-time 15 \
  http://api:5245/healthz
docker compose --env-file .env -f docker-compose.yml exec -T api \
  curl --fail --silent --show-error --connect-timeout 5 --max-time 15 \
  http://printer-discovery:5247/api/discovery/health
```

Health success proves connectivity, **not** authenticated heartbeat delivery or
printer discovery. Sign in as a farm administrator, check the discovery status
and recent heartbeat in the UI, and perform a manual scan against a known,
reachable printer. Do not send fabricated heartbeats to make status appear healthy.

### Diagnose failures without elevation

- **DNS or connection refused:** Check that both selected services are running
  on the same deployment bridge and that the API listens on internal port 5245.
  Regenerate/recreate stale deployments; do not replace Docker DNS with a host alias.
- **Health succeeds but heartbeat is stale:** Verify discovery is enabled and
  both services receive the same `DISCOVERY_SHARED_API_KEY`. Rotate them together
  and recreate both services. The key is only for internal discovery-event
  ingestion, not the administrator settings API. Never print it or paste it into
  an issue. Review relevant logs locally and redact before sharing.
- **Heartbeat succeeds but no printers appear:** Set `DISCOVERY_SUBNETS` to the
  actual reachable CIDRs (the template fallback is `10.0.0.0/24`). Check routing,
  egress firewall policy and the printer's TCP/HTTP endpoint from the discovery
  container. ICMP/ping is not a required discovery capability or a reliable test.
- **VLAN, Wi-Fi isolation, cloud or VPN:** Routing and network ACLs must permit
  the configured printer connections and return traffic. Broadcast/multicast
  does not cross routed VLANs automatically. Granting container privileges cannot
  fix routing or access-point isolation.
- **Docker Desktop on macOS/Windows:** The Linux VM's routes may differ from the
  host's, and LAN broadcast discovery is not guaranteed. Use reachable addresses
  and explicit subnets; add printers manually when automatic scanning cannot
  enumerate them. Manual addition still requires API-to-printer connectivity.
  If no permitted route exists, use a deployment on an approved reachable network.

### Related guidance

- [Deployment and socket-free discovery](DEPLOYMENT.md#socket-free-printer-discovery)
- [Microservices deployment](MICROSERVICES_DEPLOYMENT_GUIDE.md)
- [Deployment regression checks](DEPLOYMENT_TESTING_CHECKLIST.md#discovery-security-boundary)
- [Proposed host enrollment boundary and pending approval gates](HOST_ENROLLMENT_SECURITY.md)
