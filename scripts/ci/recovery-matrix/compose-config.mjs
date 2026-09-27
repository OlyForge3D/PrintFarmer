import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

export function recoveryRunLabel(runId) {
  return {
    'printfarmer.recovery-matrix.run': runId,
  };
}

export function writeRecoveryCompose({
  deploymentRoot,
  network,
  egressSinkIp,
  databaseHost = 'database',
  appIp,
  runId,
}) {
  const labels = recoveryRunLabel(runId);
  const compose = {
    name: '${COMPOSE_PROJECT_NAME}',
    services: {
      database: {
        image: '${POSTGRES_IMAGE:-postgres:16-alpine}',
        labels,
        environment: {
          POSTGRES_DB: '${POSTGRES_DB}',
          POSTGRES_USER: '${POSTGRES_USER}',
          POSTGRES_PASSWORD: '${POSTGRES_PASSWORD}',
        },
        ports: ['127.0.0.1:${POSTGRES_PORT}:5432'],
        networks: [network],
        volumes: ['postgres-data:/var/lib/postgresql/data'],
      },
      printfarmer: {
        image: '${PRINTFARMER_IMAGE}',
        labels,
        depends_on: { database: { condition: 'service_started' } },
        dns: [egressSinkIp],
        ports: ['127.0.0.1:${PRINTFARMER_PORT:-5245}:5000'],
        environment: [
          'DEPLOYMENT_MODE=monolith',
          'DB_PROVIDER=Postgres',
          `ConnectionStrings__Default=Host=${databaseHost};Port=5432;Database=\${POSTGRES_DB};Username=\${POSTGRES_USER};Password=\${POSTGRES_PASSWORD}`,
          'ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT}',
          'ASPNETCORE_URLS=http://+:5000',
          'Jwt__Key=${Jwt__Key}',
          'Jwt__Issuer=${Jwt__Issuer}',
          'Jwt__Audience=${Jwt__Audience}',
          'WorkerAuth__SharedKey=${WORKER_SHARED_API_KEY}',
          'SlicerPromotion__SharedKey=${PROMOTION_SHARED_API_KEY}',
          'DiscoveryAuth__SharedKey=${DISCOVERY_SHARED_API_KEY}',
          'HostUpdates__VerifiedReleaseDiscovery__Enabled=false',
          'WebAuthn__RelyingPartyId=${WebAuthn__RelyingPartyId}',
          'WebAuthn__RelyingPartyName=${WebAuthn__RelyingPartyName}',
          'WebAuthn__Origin=${WebAuthn__Origin}',
          'GCODE_STORAGE_PATH=/app/gcode',
          'MODEL_UPLOAD_PATH=/app/models',
          'SLICER_PROFILES_PATH=/app/profiles',
          'DATAPROTECTION_KEYS_PATH=/app/data-protection-keys',
        ],
        networks: appIp ? { [network]: { ipv4_address: appIp } } : [network],
        volumes: [
          'app-data:/data',
          'models:/app/models',
          'gcode:/app/gcode',
          'profiles:/app/profiles',
          'keys:/app/data-protection-keys',
        ],
      },
    },
    networks: {
      [network]: { external: true },
    },
    volumes: Object.fromEntries(
      ['postgres-data', 'app-data', 'models', 'gcode', 'profiles', 'keys']
        .map((name) => [name, { labels }]),
    ),
  };
  writeFileSync(join(deploymentRoot, 'docker-compose.recovery.yml'), `${JSON.stringify(compose, undefined, 2)}\n`);
  return compose;
}
