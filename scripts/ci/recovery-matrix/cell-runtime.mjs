import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

import {
  evidenceKind,
  evidenceSchema,
  networkDenialMechanism,
  validateRecoveryEvidence,
} from './evidence.mjs';

export const defaultCell = Object.freeze({
  topology: 'monolith',
  provider: 'postgres',
  databaseLayout: 'shared',
  databaseOwner: 'host',
  storageOwner: 'host',
  workers: 'managed',
});

export const defaultFaultHooks = Object.freeze({
  'before-activate': 'noop',
  'during-activate': 'noop',
  'before-recover': 'noop',
});

export function createCheckpoints(now = () => new Date()) {
  const checkpoints = [];
  return {
    checkpoints,
    ok(name) {
      checkpoints.push({ name, at: now().toISOString(), result: 'ok' });
    },
    failed(name) {
      checkpoints.push({ name, at: now().toISOString(), result: 'failed' });
    },
    skipped(name) {
      checkpoints.push({ name, at: now().toISOString(), result: 'skipped' });
    },
  };
}

export function networkDenial(attempts = []) {
  return {
    mechanism: networkDenialMechanism,
    egressSinkActive: true,
    attempts,
  };
}

export function readJournalActivities(journalPath) {
  if (!existsSync(journalPath)) return [];
  return readFileSync(journalPath, 'utf8')
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      const record = JSON.parse(line);
      return typeof record.Payload === 'string' ? JSON.parse(record.Payload) : record.Activity;
    });
}

export function lastJournalPhase(journalPath) {
  const activities = readJournalActivities(journalPath);
  return activities.at(-1)?.Phase ?? activities.at(-1)?.phase ?? 'unknown';
}

export function sha256File(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

export function directorySnapshot(paths) {
  const snapshot = {};
  for (const [name, path] of Object.entries(paths)) {
    if (!existsSync(path)) {
      snapshot[name] = { exists: false, sha256: undefined };
      continue;
    }
    snapshot[name] = { exists: true, sha256: sha256Tree(path) };
  }
  return snapshot;
}

export function sha256Tree(path) {
  const script = [
    "set -euo pipefail",
    `cd ${JSON.stringify(path)}`,
    "find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | awk '{print $1}'",
  ].join('\n');
  return execFileSync('bash', ['-lc', script], { encoding: 'utf8' }).trim();
}

export function writeValidatedEvidence(path, evidence) {
  const errors = validateRecoveryEvidence(evidence);
  if (errors.length > 0) {
    throw new Error(`Recovery evidence is invalid:\n${errors.join('\n')}`);
  }
  mkdirSync(dirname(resolve(path)), { recursive: true });
  writeFileSync(path, `${JSON.stringify(evidence, undefined, 2)}\n`);
  return evidence;
}

export function baseEvidence({
  run,
  host,
  identities,
  tools,
  checkpoints,
  outcome,
  timings,
  verdict,
  networkAttempts = [],
  cell = defaultCell,
}) {
  return {
    schema: evidenceSchema,
    kind: evidenceKind,
    run,
    host,
    cell,
    identities: {
      ...identities,
      signingRoot: 'fixture-ephemeral',
      schemaDelta: identities.schemaDelta ?? 'identical',
    },
    tools,
    networkDenial: networkDenial(networkAttempts),
    checkpoints,
    outcome,
    timings,
    verdict,
  };
}

export function parseToolVersions({ cosign }) {
  const read = (command, args) => {
    try {
      return execFileSync(command, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
    } catch {
      return 'unavailable';
    }
  };
  const cosignVersion = cosign ? read(cosign, ['version', '--json']) : read('cosign', ['version', '--json']);
  return {
    cli: 'not-installed',
    docker: read('docker', ['version', '--format', '{{.Server.Version}}']),
    compose: read('docker', ['compose', 'version', '--short']),
    cosign: parseCosignGitVersion(cosignVersion),
    node: process.version.replace(/^v/, ''),
    shell: read('bash', ['--version']).split('\n')[0] || 'bash',
  };
}

export function parseCosignGitVersion(output) {
  if (typeof output !== 'string' || output.length === 0 || output === 'unavailable') {
    return 'unavailable';
  }
  try {
    const parsed = JSON.parse(output);
    return typeof parsed.gitVersion === 'string' && parsed.gitVersion.length > 0
      ? parsed.gitVersion
      : 'unavailable';
  } catch {
    const match = /gitVersion["':=\s]+(v?\d+\.\d+\.\d+)/i.exec(output);
    return match?.[1] ?? output.split(/\r?\n/)[0].trim();
  }
}

export function detectUbuntuHost() {
  const osRelease = existsSync('/etc/os-release') ? readFileSync('/etc/os-release', 'utf8') : '';
  const fields = Object.fromEntries(osRelease
    .split(/\r?\n/)
    .filter((line) => line.includes('='))
    .map((line) => {
      const [key, ...rest] = line.split('=');
      return [key, rest.join('=').replace(/^"|"$/g, '')];
    }));
  return {
    distribution: (fields.ID ?? 'ubuntu').toLowerCase(),
    distributionVersion: fields.VERSION_ID ?? '24.04',
    arch: process.arch === 'x64' ? 'x64' : process.arch,
    kernel: execFileSync('uname', ['-r'], { encoding: 'utf8' }).trim(),
  };
}

export function writeThrowawayEnv(path, values = {}) {
  const defaults = {
    COMPOSE_PROJECT_NAME: `pf-recovery-${process.pid}`,
    DB_PROVIDER: 'Postgres',
    POSTGRES_DB: 'printfarmer',
    POSTGRES_USER: 'printfarmer',
    POSTGRES_PASSWORD: createHash('sha256').update(`postgres-${process.pid}-${Date.now()}`).digest('hex'),
    Jwt__Key: createHash('sha256').update(`jwt-${process.pid}-${Date.now()}`).digest('base64url'),
    Jwt__Issuer: 'PrintFarmerRecoveryHarness',
    Jwt__Audience: 'PrintFarmerRecoveryHarness',
    WORKER_SHARED_API_KEY: createHash('sha256').update(`worker-${process.pid}-${Date.now()}`).digest('base64url'),
    PROMOTION_SHARED_API_KEY: createHash('sha256').update(`promotion-${process.pid}-${Date.now()}`).digest('base64url'),
    DISCOVERY_SHARED_API_KEY: createHash('sha256').update(`discovery-${process.pid}-${Date.now()}`).digest('base64url'),
    WebAuthn__RelyingPartyId: 'localhost',
    WebAuthn__RelyingPartyName: 'PrintFarmer Recovery Harness',
    WebAuthn__Origin: 'http://localhost',
    ASPNETCORE_ENVIRONMENT: 'Production',
    PRINTFARMER_PORT: '5245',
  };
  const merged = { ...defaults, ...values };
  const text = Object.entries(merged).map(([key, value]) => `${key}=${String(value).replace(/\r?\n/g, '')}`).join('\n');
  writeFileSync(path, `${text}\n`, { mode: 0o600 });
  return merged;
}

export function writeHostUpdateConfig(path, {
  rootDirectory,
  deploymentRoot,
  projectName,
  hostStateRoot = join(rootDirectory, 'host-state'),
  databaseConnectionString,
  jwtKey = 'recovery-harness-configured',
  jwtIssuer = 'PrintFarmerRecoveryHarness',
  jwtAudience = 'PrintFarmerRecoveryHarness',
  docker = '/usr/bin/docker',
  pgDump = 'pg_dump',
  pgRestore = 'pg_restore',
  healthBaseUrl = 'http://127.0.0.1:5245',
  activeServiceIds = ['monolith'],
} = {}) {
  const compose = (name) => join(deploymentRoot, name);
  const ownedDirectories = {
    'app-data': join(deploymentRoot, 'volumes', 'app-data'),
    'model-uploads': join(deploymentRoot, 'volumes', 'models'),
    'gcode-storage': join(deploymentRoot, 'volumes', 'gcode'),
    'slicer-profiles': join(deploymentRoot, 'volumes', 'profiles'),
    'data-protection-keys': join(deploymentRoot, 'volumes', 'keys'),
  };
  for (const directory of [
    rootDirectory,
    join(rootDirectory, 'state'),
    join(rootDirectory, 'backups'),
    hostStateRoot,
    ...Object.values(ownedDirectories),
  ]) {
    mkdirSync(directory, { recursive: true });
  }
  const serviceMappings = [
    'api',
    'frontend',
    'slicer-host',
    'printer-discovery',
    'orcaslicer-worker',
    'monolith',
  ].map((serviceId) => ({
    serviceId,
    composeServiceName: 'printfarmer',
    imageEnvironmentVariable: 'PRINTFARMER_IMAGE',
    imageRepository: `ghcr.io/olyforge3d/printfarmer-${serviceId}`,
  }));
  const config = {
    DB_PROVIDER: 'Postgres',
    ConnectionStrings: {
      Default: databaseConnectionString,
    },
    Jwt: {
      Key: jwtKey,
      Issuer: jwtIssuer,
      Audience: jwtAudience,
    },
    HostUpdates: {
      HostState: {
        Enabled: true,
        RootPath: hostStateRoot,
      },
    },
    HostUpdateExecution: {
      RootDirectory: rootDirectory,
      HostExecutablePaths: {
        docker,
        'docker-compose': docker,
        pg_dump: pgDump,
        pg_restore: pgRestore,
      },
      MinimumFreeBytes: 1,
      SupportedProviderNames: ['Npgsql.EntityFrameworkCore.PostgreSQL'],
      ActiveServiceIds: activeServiceIds,
      RequiredFencedWriterNames: [],
      OwnedDirectories: ownedDirectories,
      ComposeFiles: [compose('docker-compose.recovery.yml')],
      ComposeProjectName: projectName,
      HealthCheckBaseUrl: healthBaseUrl,
      ServiceMappings: serviceMappings,
    },
  };
  writeFileSync(path, `${JSON.stringify(config, undefined, 2)}\n`, { mode: 0o600 });
  return config;
}

export function provisionFixtureHostState(rootPath, { channel = 'insider' } = {}) {
  mkdirSync(rootPath, { recursive: true, mode: 0o700 });
  chmodSync(rootPath, 0o700);

  const policy = {
    Enabled: false,
    KillSwitch: false,
    Channel: channel,
    InsiderAcknowledged: channel === 'insider',
    PollIntervalSeconds: 3600,
    InsiderPollIntervalSeconds: null,
    MaintenanceWindowStartHour: 0,
    MaintenanceWindowEndHour: 24,
    Revision: 0,
    Fingerprint: '',
  };
  policy.Fingerprint = sha256Json({
    Enabled: policy.Enabled,
    KillSwitch: policy.KillSwitch,
    Channel: policy.Channel,
    InsiderAcknowledged: policy.InsiderAcknowledged,
    PollIntervalSeconds: policy.PollIntervalSeconds,
    InsiderPollIntervalSeconds: policy.InsiderPollIntervalSeconds,
    MaintenanceWindowStartHour: policy.MaintenanceWindowStartHour,
    MaintenanceWindowEndHour: policy.MaintenanceWindowEndHour,
    Revision: policy.Revision,
  });
  writeFileSync(join(rootPath, 'update-automation-policy.json'), JSON.stringify({
    Version: 1,
    Policy: policy,
    Checksum: policy.Fingerprint,
  }));

  const replayChecksum = sha256Json({
    Version: 1,
    Epoch: 0,
    HighWater: [],
    Identities: [],
  });
  const replay = JSON.stringify({
    Version: 1,
    Epoch: 0,
    Checksum: replayChecksum,
    HighWaterByNamespace: {},
    Identities: {},
  });
  const replayPath = join(rootPath, 'host-update-replay.json');
  writeFileSync(replayPath, replay);
  const stateHash = createHash('sha256').update(readFileSync(replayPath)).digest('hex');
  const anchorHash = createHash('sha256').update(`1|0||${stateHash}`, 'utf8').digest('hex');
  const anchor = {
    Version: 1,
    Epoch: 0,
    PreviousHash: '',
    StateHash: stateHash,
    Hash: anchorHash,
  };
  writeFileSync(join(rootPath, 'replay-anchor.journal'), `${JSON.stringify(anchor)}\n`);
  writeFileSync(join(rootPath, 'replay-anchor.json'), JSON.stringify({
    Version: 1,
    Epoch: 0,
    StateHash: stateHash,
    Hash: anchorHash,
  }));
}

function sha256Json(value) {
  return `sha256:${createHash('sha256').update(JSON.stringify(value), 'utf8').digest('hex')}`;
}
