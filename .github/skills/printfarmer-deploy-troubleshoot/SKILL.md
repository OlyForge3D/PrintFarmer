---
name: "printfarmer-deploy-troubleshoot"
description: "Diagnose PrintFarmer Docker redeploy, nginx 502, connection-refused, and GHCR migration failures. Use before changing or resetting a deployment."
---

## Preserve deployment state

- Do not run `docker compose down -v`, delete generated Compose files, remove
  `deploy/nginx/`, or delete TLS certificates as a first-line repair. These can
  destroy data or overwrite operator configuration.
- Keep valid TLS certificates. `pfdev` checks and refreshes certificates when
  deploying `nginx` or `frontend`; investigate missing/invalid files before
  allowing regeneration.
- Do not print or paste `.env`, `.deploy-config`, or command output containing
  credentials.

## Diagnose 502 or connection refused

1. Run from the deployment repository root and capture service state:

   ```bash
   docker compose ps
   docker compose logs --tail 100 api frontend nginx-proxy
   docker compose exec -T nginx-proxy nginx -t
   docker compose exec -T nginx-proxy nginx -T
   docker compose exec -T api curl -fsS localhost:5245/healthz
   ```

   If a service name is absent from the active Compose profile, omit it.
   Redact sensitive values before sharing diagnostics.
2. Check whether the API is healthy and whether nginx points to the current
   service names/ports. A recreated upstream can receive a new container IP;
   nginx must validate and reload to resolve it again.
3. For a single service, use the repository helper:

   ```bash
   pfdev redeploy <service>
   ```

   `pfdev` rebuilds build-backed services with `--no-cache`; after deployment
   it validates and reloads a running `nginx-proxy`. The `nginx` alias maps to
   `nginx-proxy`. A failed nginx validation or reload is a real command failure;
   inspect its output instead of claiming success.
4. For a full installer redeploy or cache issue, inspect the exact
   [deployment recovery guidance](../../../docs/DOCKER_DEPLOYMENT.md#troubleshooting)
   before using `./scripts/deploy-docker.sh --redeploy` or
   `./scripts/deploy-docker.sh --no-cache --non-interactive`. The script
   automatically retries once with `--no-cache` only for its recognized
   BuildKit snapshot-corruption signature. Do not loop full redeploys or jump
   to global cache pruning.

## GHCR build-free deployments

The in-place GHCR migration is not the installer's `--use-registry` switch.
Follow the [build-free registry migration guide](../../../docs/DEPLOYMENT_REGISTRY_MIGRATION.md):
back up the database and configured storage, review a dry-run plan, then use
the registry controller. It verifies storage identity and image lineage and
does not run `docker compose down` or remove volumes. After migration, do not
run `scripts/deploy-docker.sh`; it would replace the release Compose project.
