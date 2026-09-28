import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { cells, cellIds, resolveCell, resolveCellList } from '../recovery-matrix/cells.mjs';
import { writeHostUpdateConfig } from '../recovery-matrix/cell-runtime.mjs';
import { writeRecoveryCompose } from '../recovery-matrix/compose-config.mjs';
import { expectedCellOutcome } from '../recovery-matrix/evidence.mjs';
import { providerFor } from '../recovery-matrix/providers.mjs';
import { requiredInfrastructureIds, serviceMappingsFor, topologyFor } from '../recovery-matrix/topologies.mjs';

const scratchRoot = path.resolve('.recovery-matrix-test-work');

test('catalog exposes every issue #3100 cell with evidence-compatible expectations', () => {
  assert.deepEqual(cellIds, [
    'c2',
    'monolith-sqlserver',
    'split-postgres',
    'split-postgres-no-worker',
    'split-sqlserver',
    'external-database',
    'external-storage',
    'remote-worker',
    'split-database',
  ]);
  assert.equal(resolveCellList('all').length, cellIds.length);
  assert.throws(() => resolveCell('missing'), /unknown recovery matrix cell/);

  for (const entry of cells) {
    assert.deepEqual(entry.expected, expectedCellOutcome(entry.cell).failClosed
      ? expectedCellOutcome(entry.cell)
      : { failClosed: false, outcome: 'RolledBack', reason: undefined });
  }
});

test('providers expose production provider names, connection strings, readiness, and shims', () => {
  const postgres = providerFor('postgres');
  assert.equal(postgres.supportedProviderName, 'Npgsql.EntityFrameworkCore.PostgreSQL');
  assert.match(postgres.connectionString({
    host: '172.30.1.11',
    env: { POSTGRES_DB: 'printfarmer', POSTGRES_USER: 'pf', POSTGRES_PASSWORD: 'secret' },
  }), /Host=172\.30\.1\.11;Port=5432/);
  assert.deepEqual(postgres.readinessArgs({ POSTGRES_USER: 'pf', POSTGRES_DB: 'printfarmer' }).slice(0, 3), ['pg_isready', '-U', 'pf']);

  const sqlserver = providerFor('sqlserver');
  assert.equal(sqlserver.supportedProviderName, 'Microsoft.EntityFrameworkCore.SqlServer');
  assert.match(sqlserver.connectionString({
    host: '172.30.1.11',
    env: { MSSQL_DB: 'printfarmer', MSSQL_USER: 'sa', MSSQL_SA_PASSWORD: 'Secret1!' },
  }), /TrustServerCertificate=True/);
  assert.equal(sqlserver.readinessArgs({ MSSQL_USER: 'sa', MSSQL_SA_PASSWORD: 'Secret1!' })[0], '/opt/mssql-tools18/bin/sqlcmd');
});

test('topologies map active services and infrastructure requirements', () => {
  assert.deepEqual(topologyFor('monolith').activeServiceIds(), ['monolith']);
  assert.deepEqual(serviceMappingsFor(resolveCell('c2').cell).map((mapping) => mapping.composeServiceName), [
    'printfarmer',
    'printfarmer',
    'printfarmer',
    'printfarmer',
    'printfarmer',
    'printfarmer',
  ]);
  assert.deepEqual(topologyFor('split').activeServiceIds('none'), ['api', 'frontend', 'slicer-host', 'printer-discovery']);
  assert.deepEqual(serviceMappingsFor(resolveCell('split-postgres-no-worker').cell).map((mapping) => mapping.serviceId), [
    'api',
    'frontend',
    'slicer-host',
    'printer-discovery',
    'orcaslicer-worker',
    'monolith',
  ]);
  assert.deepEqual(requiredInfrastructureIds(resolveCell('split-sqlserver').cell).sort(), ['mssql', 'nginx']);
});

test('compose generation uses selected provider, split services, static IPs, and pull-never-ready image variables', () => {
  const scratch = path.join(scratchRoot, `catalog-compose-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const cell = resolveCell('split-sqlserver').cell;
    const compose = writeRecoveryCompose({
      deploymentRoot: scratch,
      network: 'matrix-net',
      egressSinkIp: '172.30.55.10',
      databaseHost: '172.30.55.11',
      databaseIp: '172.30.55.11',
      appIp: '172.30.55.20',
      runId: 'split-sqlserver-test',
      cell,
      hostUpdateBackupsRoot: '/work/host-update/backups',
    });
    assert.equal(compose.services.database.image, '${MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}');
    assert.equal(compose.services.database.user, '10001');
    assert.equal(compose.services.database.networks['matrix-net'].ipv4_address, '172.30.55.11');
    assert.equal(compose.services.api.networks['matrix-net'].ipv4_address, '172.30.55.20');
    assert.equal(compose.services.api.environment.find((value) => value.startsWith('DB_PROVIDER=')), 'DB_PROVIDER=SqlServer');
    assert.ok(compose.services.nginx);
    assert.ok(!compose.services.printfarmer);

    const splitDbCompose = writeRecoveryCompose({
      deploymentRoot: scratch,
      network: 'matrix-net',
      egressSinkIp: '172.30.55.10',
      databaseHost: '172.30.55.11',
      databaseIp: '172.30.55.11',
      appIp: '172.30.55.20',
      runId: 'split-database-test',
      cell: resolveCell('split-database').cell,
    });
    assert.match(
      splitDbCompose.services.api.environment.find((value) => value.startsWith('ConnectionStrings__SlicerDatabase=')),
      /Database=\$\{POSTGRES_DB\}_slicer/,
    );
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('host-update config follows selected provider, active services, split DB and external DB flags', () => {
  const scratch = path.join(scratchRoot, `catalog-config-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const cell = resolveCell('external-database').cell;
    const configPath = path.join(scratch, 'host-update.json');
    writeHostUpdateConfig(configPath, {
      rootDirectory: path.join(scratch, 'host-update'),
      deploymentRoot: scratch,
      projectName: 'external-database-test',
      databaseConnectionString: 'Host=172.30.55.11;Database=printfarmer',
      cell,
      databaseProvider: providerFor(cell.provider),
    });
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    assert.equal(config.HostUpdateExecution.DatabaseExternallyOwned, true);
    assert.deepEqual(config.HostUpdateExecution.SupportedProviderNames, ['Npgsql.EntityFrameworkCore.PostgreSQL']);

    const splitCell = resolveCell('split-database').cell;
    writeHostUpdateConfig(configPath, {
      rootDirectory: path.join(scratch, 'host-update-2'),
      deploymentRoot: scratch,
      projectName: 'split-database-test',
      databaseConnectionString: 'Host=172.30.55.11;Database=printfarmer',
      slicerConnectionString: 'Host=172.30.55.11;Database=printfarmer_slicer',
      cell: splitCell,
      databaseProvider: providerFor(splitCell.provider),
    });
    const splitConfig = JSON.parse(readFileSync(configPath, 'utf8'));
    assert.equal(splitConfig.ConnectionStrings.SlicerDatabase, 'Host=172.30.55.11;Database=printfarmer_slicer');
    assert.deepEqual(splitConfig.HostUpdateExecution.ActiveServiceIds, ['api', 'frontend', 'slicer-host', 'printer-discovery', 'orcaslicer-worker']);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('sqlserver sqlcmd shim forwards SQLCMDPASSWORD by name so the product password reaches sqlcmd without argv', () => {
  const runRoot = path.join(scratchRoot, `sqlcmd-shim-${process.pid}`);
  mkdirSync(runRoot, { recursive: true });
  try {
    const { sqlcmd } = providerFor('sqlserver').writeToolShims({ runRoot, databaseContainer: 'pf-db-1' });
    const shim = readFileSync(sqlcmd, 'utf8');
    const execLine = shim.split('\n').find((line) => line.includes('/opt/mssql-tools18/bin/sqlcmd'));
    assert.ok(execLine, 'shim must exec sqlcmd in the database container');
    assert.match(execLine, /docker exec -e SQLCMDPASSWORD pf-db-1 /);
    assert.doesNotMatch(execLine, /SQLCMDPASSWORD=/, 'password value must never be expanded into docker argv');
    assert.doesNotMatch(execLine, / -P /, 'password must not be passed on the sqlcmd command line');
  } finally {
    rmSync(runRoot, { recursive: true, force: true });
  }
});
