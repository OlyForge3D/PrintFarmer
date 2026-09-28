import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { defaultCell } from './cell-runtime.mjs';
import { providerFor } from './providers.mjs';
import { topologyFor } from './topologies.mjs';

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
  databaseIp,
  appIp,
  runId,
  cell = defaultCell,
  hostUpdateBackupsRoot,
}) {
  const labels = recoveryRunLabel(runId);
  const provider = providerFor(cell.provider);
  const topology = topologyFor(cell.topology);
  const serviceIds = topology.serviceIds(cell.workers);
  const applicationServices = Object.fromEntries(serviceIds.map((serviceId) => [
    topology.composeServiceName(serviceId),
    applicationService({ serviceId, topology, provider, databaseHost, appIp, network, egressSinkIp, labels, cell }),
  ]));
  const services = {
    database: databaseService({ provider, network, databaseIp, labels, hostUpdateBackupsRoot }),
    ...applicationServices,
    ...(cell.topology === 'split' ? {
      nginx: {
        image: '${NGINX_IMAGE:-nginx:1.27-alpine}',
        labels,
        depends_on: { frontend: { condition: 'service_started' }, api: { condition: 'service_started' } },
        dns: [egressSinkIp],
        ...(appIp ? { extra_hosts: healthHostEntry(topology, appIp) } : {}),
        networks: [network],
      },
    } : {}),
  };
  const volumeNames = [
    provider.id === 'postgres' ? 'postgres-data' : 'mssql-data',
    'app-data',
    'models',
    'gcode',
    'profiles',
    'keys',
  ];
  const compose = {
    name: '${COMPOSE_PROJECT_NAME}',
    services,
    networks: {
      [network]: { external: true },
    },
    volumes: Object.fromEntries(
      volumeNames.map((name) => [name, { labels }]),
    ),
  };
  writeFileSync(join(deploymentRoot, 'docker-compose.recovery.yml'), `${JSON.stringify(compose, undefined, 2)}\n`);
  return compose;
}

function databaseService({ provider, network, databaseIp, labels, hostUpdateBackupsRoot }) {
  const base = {
    image: provider.id === 'postgres'
      ? '${POSTGRES_IMAGE:-postgres:16-alpine}'
      : '${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}',
    labels,
    networks: databaseIp ? { [network]: { ipv4_address: databaseIp } } : [network],
  };
  if (provider.id === 'postgres') {
    return {
      ...base,
      environment: {
        POSTGRES_DB: '${POSTGRES_DB}',
        POSTGRES_USER: '${POSTGRES_USER}',
        POSTGRES_PASSWORD: '${POSTGRES_PASSWORD}',
      },
      ports: ['127.0.0.1:${POSTGRES_PORT}:5432'],
      volumes: ['postgres-data:/var/lib/postgresql/data'],
    };
  }
  const backupsRoot = hostUpdateBackupsRoot ?? '${HOST_UPDATE_BACKUPS_ROOT:-./host-update/backups}';
  return {
    ...base,
    user: '10001',
    environment: {
      ACCEPT_EULA: '${ACCEPT_EULA:-Y}',
      MSSQL_SA_PASSWORD: '${MSSQL_SA_PASSWORD}',
      MSSQL_PID: '${MSSQL_PID:-Developer}',
    },
    ports: ['127.0.0.1:${MSSQL_PORT}:1433'],
    volumes: [
      'mssql-data:/var/opt/mssql',
      `${backupsRoot}:${backupsRoot}`,
    ],
  };
}

function applicationService({ serviceId, topology, provider, databaseHost, appIp, network, egressSinkIp, labels, cell }) {
  const composeService = topology.composeServiceName(serviceId);
  const isHttpHost = ['monolith', 'api', 'slicer-host', 'frontend', 'printer-discovery', 'orcaslicer-worker'].includes(serviceId);
  const service = {
    image: `\${${topology.imageEnvironmentVariable(serviceId)}}`,
    labels,
    depends_on: { database: { condition: 'service_started' } },
    dns: [egressSinkIp],
    environment: commonEnvironment({ serviceId, topology, provider, databaseHost, cell }),
    networks: composeService === topology.healthComposeService && appIp
      ? { [network]: { ipv4_address: appIp } }
      : [network],
    volumes: [
      'app-data:/data',
      'models:/app/models',
      'gcode:/app/gcode',
      'profiles:/app/profiles',
      'keys:/app/data-protection-keys',
    ],
  };
  const isHealthHost = composeService === topology.healthComposeService;
  if (isHttpHost) {
    service.environment.push(`ASPNETCORE_URLS=http://+:${isHealthHost ? topology.healthPort : 5000}`);
  }
  if (isHealthHost) {
    service.ports = [`127.0.0.1:\${PRINTFARMER_PORT:-5245}:${topology.healthPort}`];
  } else if (appIp) {
    service.extra_hosts = healthHostEntry(topology, appIp);
  }
  return service;
}

// Siblings reach the health host by its compose name. While activation recreates it, Docker's embedded
// DNS cannot resolve that name and forwards it to the egress sink, which is recorded as an outbound
// attempt. Pin the name to its static IP; every other lookup still reaches the sink (#3161).
function healthHostEntry(topology, appIp) {
  return [`${topology.healthComposeService}:${appIp}`];
}

function commonEnvironment({ serviceId, topology, provider, databaseHost, cell }) {
  const connection = provider.id === 'postgres'
    ? `Host=${databaseHost};Port=5432;Database=\${POSTGRES_DB};Username=\${POSTGRES_USER};Password=\${POSTGRES_PASSWORD}`
    : `Server=${databaseHost},1433;Database=\${MSSQL_DB};User Id=\${MSSQL_USER};Password=\${MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=True`;
  const slicerConnection = provider.id === 'postgres'
    ? `Host=${databaseHost};Port=5432;Database=\${POSTGRES_DB}_slicer;Username=\${POSTGRES_USER};Password=\${POSTGRES_PASSWORD}`
    : `Server=${databaseHost},1433;Database=\${MSSQL_DB}_slicer;User Id=\${MSSQL_USER};Password=\${MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=True`;
  return [
    `DEPLOYMENT_MODE=${topology.deploymentMode}`,
    `PRINT_FARMER_SERVICE_ID=${serviceId}`,
    `DB_PROVIDER=${provider.dbProvider}`,
    `ConnectionStrings__Default=${connection}`,
    ...(cell.databaseLayout === 'split' ? [`ConnectionStrings__SlicerDatabase=${slicerConnection}`] : []),
    'ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT}',
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
  ];
}
