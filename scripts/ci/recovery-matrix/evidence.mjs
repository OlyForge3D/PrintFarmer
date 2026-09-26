// Evidence contract for the isolated offline update recovery matrix (#3098).
// Every matrix run emits one record per cell; this module is the single
// source of truth for the record shape, approved host, fail-closed cells, and
// the redaction rules the record must satisfy before it is uploaded.

export const evidenceKind = 'printfarmer-recovery-matrix-evidence';
export const evidenceSchema = 1;

export const approvedHost = Object.freeze({
  distribution: 'ubuntu',
  arch: 'x64',
  ltsVersionPattern: /^(22|24|26)\.04$/,
});

export const topologies = Object.freeze(['monolith', 'split']);
export const providers = Object.freeze(['postgres', 'sqlserver']);
export const databaseLayouts = Object.freeze(['shared', 'split']);
export const owners = Object.freeze(['host', 'external']);
export const workerModes = Object.freeze(['managed', 'none', 'remote']);
export const entryPoints = Object.freeze(['bash', 'powershell']);
export const signingRoots = Object.freeze(['fixture', 'published-insider']);
export const outcomes = Object.freeze([
  'Activated',
  'RolledBack',
  'NeedsOperator',
  'RecoveryRequired',
  'FenceReleasePending',
  'Refused',
]);
export const checkpointResults = Object.freeze(['ok', 'failed', 'skipped']);
export const verdicts = Object.freeze(['pass', 'fail']);
export const networkDenialMechanism =
  'docker-internal-network+default-deny-egress-sink';

// Ordered: the first matching rule decides the expected outcome.
export const failClosedCells = Object.freeze([
  Object.freeze({
    field: 'databaseLayout',
    value: 'split',
    outcome: 'Refused',
    reason: 'split_database_not_supported',
  }),
  Object.freeze({
    field: 'workers',
    value: 'remote',
    outcome: 'Refused',
    reason: 'remote_worker_unsupported',
  }),
  Object.freeze({
    field: 'databaseOwner',
    value: 'external',
    outcome: 'NeedsOperator',
    reason: 'database_externally_owned',
  }),
  Object.freeze({
    field: 'storageOwner',
    value: 'external',
    outcome: 'NeedsOperator',
    reason: 'storage_externally_owned',
  }),
]);

export function expectedCellOutcome(cell) {
  for (const rule of failClosedCells) {
    if (cell?.[rule.field] === rule.value) {
      return { failClosed: true, outcome: rule.outcome, reason: rule.reason };
    }
  }
  return { failClosed: false, outcome: null, reason: null };
}

const identityKeys = [
  'tag',
  'version',
  'channel',
  'sourceCommit',
  'buildId',
  'sequence',
];

const shape = {
  schema: 'number',
  kind: 'string',
  run: {
    id: 'string',
    startedAt: 'timestamp',
    finishedAt: 'timestamp',
    harnessCommit: 'sha',
    entryPoint: 'string',
  },
  host: {
    distribution: 'string',
    distributionVersion: 'string',
    arch: 'string',
    kernel: 'string',
  },
  cell: {
    topology: 'string',
    provider: 'string',
    databaseLayout: 'string',
    databaseOwner: 'string',
    storageOwner: 'string',
    workers: 'string',
  },
  identities: {
    source: Object.fromEntries(identityKeys.map((key) => [key, 'identity'])),
    target: Object.fromEntries(identityKeys.map((key) => [key, 'identity'])),
    prior: Object.fromEntries(identityKeys.map((key) => [key, 'identity'])),
    bundleSha256: 'sha256',
    signingRoot: 'string',
  },
  tools: {
    cli: 'string',
    docker: 'string',
    compose: 'string',
    cosign: 'string',
    node: 'string',
    shell: 'string',
  },
  networkDenial: {
    mechanism: 'string',
    egressSinkActive: 'boolean',
    attempts: 'attempts',
  },
  checkpoints: 'checkpoints',
  outcome: {
    expected: 'string',
    actual: 'string',
    reason: 'nullableString',
    exitCode: 'integer',
    journalPhase: 'string',
  },
  timings: {
    activationSeconds: 'nonNegative',
    recoverySeconds: 'nonNegative',
  },
  verdict: 'string',
};

const isPlainObject = (value) =>
  value !== null && typeof value === 'object' && !Array.isArray(value);

function checkScalar(kind, value, path, errors) {
  const fail = (what) => errors.push(`${path}: expected ${what}`);
  switch (kind) {
    case 'string':
      if (typeof value !== 'string' || value.length === 0) fail('non-empty string');
      break;
    case 'nullableString':
      if (value !== null && (typeof value !== 'string' || value.length === 0)) {
        fail('non-empty string or null');
      }
      break;
    case 'number':
      if (typeof value !== 'number' || !Number.isFinite(value)) fail('number');
      break;
    case 'integer':
      if (!Number.isInteger(value)) fail('integer');
      break;
    case 'nonNegative':
      if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
        fail('non-negative number');
      }
      break;
    case 'boolean':
      if (typeof value !== 'boolean') fail('boolean');
      break;
    case 'timestamp':
      if (
        typeof value !== 'string' ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(value) ||
        Number.isNaN(Date.parse(value))
      ) {
        fail('UTC ISO-8601 timestamp');
      }
      break;
    case 'sha':
      if (typeof value !== 'string' || !/^[0-9a-f]{40}$/.test(value)) {
        fail('40-character lowercase commit SHA');
      }
      break;
    case 'sha256':
      if (typeof value !== 'string' || !/^[0-9a-f]{64}$/.test(value)) {
        fail('64-character lowercase SHA-256');
      }
      break;
    case 'identity':
      if (
        !(typeof value === 'string' && value.length > 0) &&
        !(Number.isInteger(value) && value >= 0)
      ) {
        fail('non-empty string or non-negative integer');
      }
      break;
    case 'attempts':
      if (!Array.isArray(value)) {
        fail('array');
        break;
      }
      value.forEach((attempt, index) =>
        checkShape(
          { at: 'timestamp', destination: 'string', protocol: 'string' },
          attempt,
          `${path}[${index}]`,
          errors,
        ),
      );
      break;
    case 'checkpoints':
      if (!Array.isArray(value) || value.length === 0) {
        fail('non-empty array');
        break;
      }
      value.forEach((checkpoint, index) => {
        const itemPath = `${path}[${index}]`;
        checkShape(
          { name: 'string', at: 'timestamp', result: 'string' },
          checkpoint,
          itemPath,
          errors,
        );
        if (isPlainObject(checkpoint) && !checkpointResults.includes(checkpoint.result)) {
          errors.push(`${itemPath}.result: must be one of ${checkpointResults.join(', ')}`);
        }
      });
      break;
    default:
      throw new Error(`unknown shape kind ${kind}`);
  }
}

function checkShape(expected, value, path, errors) {
  if (!isPlainObject(value)) {
    errors.push(`${path || 'record'}: expected object`);
    return;
  }
  for (const key of Object.keys(value)) {
    if (!Object.hasOwn(expected, key)) {
      errors.push(`${path ? `${path}.` : ''}${key}: unexpected field`);
    }
  }
  for (const [key, kind] of Object.entries(expected)) {
    const childPath = path ? `${path}.${key}` : key;
    if (!Object.hasOwn(value, key)) {
      errors.push(`${childPath}: missing field`);
      continue;
    }
    if (typeof kind === 'string') checkScalar(kind, value[key], childPath, errors);
    else checkShape(kind, value[key], childPath, errors);
  }
}

const redactionRules = [
  { name: 'URL userinfo', pattern: /[a-z][a-z0-9+.-]*:\/\/[^\s/@:]+:[^\s/@]*@/i },
  { name: 'PEM block', pattern: /-----BEGIN [A-Z0-9 ]+-----/ },
  { name: 'GitHub token', pattern: /\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b/ },
  { name: 'JWT', pattern: /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/ },
  {
    name: 'secret assignment',
    pattern:
      /\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token|client[_-]?secret)\s*[=:]\s*\S+/i,
  },
];

const secretKeyPattern =
  /(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token|credential|private[_-]?key)/i;

function scanForSecrets(value, path, errors) {
  if (typeof value === 'string') {
    for (const rule of redactionRules) {
      if (rule.pattern.test(value)) {
        errors.push(`${path}: contains unredacted ${rule.name}`);
      }
    }
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => scanForSecrets(item, `${path}[${index}]`, errors));
    return;
  }
  if (isPlainObject(value)) {
    for (const [key, child] of Object.entries(value)) {
      const childPath = path ? `${path}.${key}` : key;
      if (secretKeyPattern.test(key)) {
        errors.push(`${childPath}: secret-bearing field name is not allowed`);
      }
      scanForSecrets(child, childPath, errors);
    }
  }
}

function checkEnum(value, allowed, path, errors) {
  if (typeof value === 'string' && !allowed.includes(value)) {
    errors.push(`${path}: must be one of ${allowed.join(', ')}`);
  }
}

export function validateRecoveryEvidence(record) {
  const errors = [];
  scanForSecrets(record, '', errors);
  checkShape(shape, record, '', errors);
  if (!isPlainObject(record)) return errors;

  if (record.schema !== evidenceSchema) {
    errors.push(`schema: expected ${evidenceSchema}`);
  }
  if (record.kind !== evidenceKind) {
    errors.push(`kind: expected ${evidenceKind}`);
  }

  const { run, host, cell, identities, networkDenial, outcome } = record;
  if (isPlainObject(run)) {
    checkEnum(run.entryPoint, entryPoints, 'run.entryPoint', errors);
    if (
      typeof run.startedAt === 'string' &&
      typeof run.finishedAt === 'string' &&
      Date.parse(run.finishedAt) < Date.parse(run.startedAt)
    ) {
      errors.push('run.finishedAt: must not precede run.startedAt');
    }
  }

  if (isPlainObject(host)) {
    if (host.distribution !== approvedHost.distribution) {
      errors.push('host.distribution: only Ubuntu LTS hosts are supported');
    }
    if (
      typeof host.distributionVersion !== 'string' ||
      !approvedHost.ltsVersionPattern.test(host.distributionVersion)
    ) {
      errors.push('host.distributionVersion: must be a supported Ubuntu LTS release');
    }
    if (host.arch !== approvedHost.arch) {
      errors.push('host.arch: only x64 hosts are supported');
    }
  }

  if (isPlainObject(cell)) {
    checkEnum(cell.topology, topologies, 'cell.topology', errors);
    checkEnum(cell.provider, providers, 'cell.provider', errors);
    checkEnum(cell.databaseLayout, databaseLayouts, 'cell.databaseLayout', errors);
    checkEnum(cell.databaseOwner, owners, 'cell.databaseOwner', errors);
    checkEnum(cell.storageOwner, owners, 'cell.storageOwner', errors);
    checkEnum(cell.workers, workerModes, 'cell.workers', errors);
  }

  if (isPlainObject(identities)) {
    checkEnum(identities.signingRoot, signingRoots, 'identities.signingRoot', errors);
  }

  if (isPlainObject(networkDenial)) {
    if (networkDenial.mechanism !== networkDenialMechanism) {
      errors.push(`networkDenial.mechanism: expected ${networkDenialMechanism}`);
    }
    if (networkDenial.egressSinkActive !== true) {
      errors.push('networkDenial.egressSinkActive: the egress sink must be active');
    }
  }

  if (isPlainObject(outcome)) {
    checkEnum(outcome.expected, outcomes, 'outcome.expected', errors);
    checkEnum(outcome.actual, outcomes, 'outcome.actual', errors);
    const expectation = expectedCellOutcome(cell);
    if (expectation.failClosed) {
      if (outcome.expected !== expectation.outcome) {
        errors.push(
          `outcome.expected: unsupported cell must expect ${expectation.outcome}`,
        );
      }
      if (outcome.reason !== expectation.reason) {
        errors.push(`outcome.reason: unsupported cell must report ${expectation.reason}`);
      }
    } else if (outcome.expected === 'Refused') {
      errors.push('outcome.expected: supported cell must not expect Refused');
    }
  }

  if (record.verdict !== undefined) {
    checkEnum(record.verdict, verdicts, 'verdict', errors);
  }
  if (record.verdict === 'pass') {
    if (Array.isArray(networkDenial?.attempts) && networkDenial.attempts.length > 0) {
      errors.push('verdict: a run with outbound network attempts cannot pass');
    }
    if (isPlainObject(outcome) && outcome.actual !== outcome.expected) {
      errors.push('verdict: a run whose actual outcome differs from expected cannot pass');
    }
    if (
      Array.isArray(record.checkpoints) &&
      record.checkpoints.some((checkpoint) => checkpoint?.result === 'failed')
    ) {
      errors.push('verdict: a run with a failed checkpoint cannot pass');
    }
  }

  return errors;
}
