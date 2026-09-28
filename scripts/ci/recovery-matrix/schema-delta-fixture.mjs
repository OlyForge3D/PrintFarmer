// N -> N+1 schema-delta fixture for the recovery matrix (#3167).
//
// A cell whose `cell.schemaDelta` is 'changed' gets api, monolith and slicer-host target
// images that carry exactly one extra real EF migration per DbContext over the prior image. The prior
// image is unchanged. The fixture is test-only: the migration sources live beside this
// module and are injected into rebuilt migrations assemblies through MSBuild's
// CustomAfterMicrosoftCommonTargets hook (see fixture-migrations/), so no shipped
// migrations project, model snapshot or Dockerfile is touched.
import { join } from 'node:path';

import { schemaDeltas } from './evidence.mjs';

export const schemaDeltaFixtureDirectory = 'scripts/ci/recovery-matrix/fixture-migrations';
export const schemaDeltaFixtureDockerfile = `${schemaDeltaFixtureDirectory}/Dockerfile.target-schema-delta`;

export const schemaDeltaFixtureMigrations = Object.freeze({
  AppDbContext: Object.freeze({
    migrationId: '29990601000000_RecoveryMatrixFixtureSchemaDelta',
    table: 'RecoveryMatrixFixtureMarkers',
    source: 'RecoveryMatrixFixtureSchemaDelta.cs',
    assemblies: Object.freeze(['Farm.Migrations.PostgreSQL', 'Farm.Migrations.SqlServer']),
  }),
  SlicerDbContext: Object.freeze({
    migrationId: '29990601000001_RecoveryMatrixFixtureSlicerSchemaDelta',
    table: 'RecoveryMatrixFixtureSlicerMarkers',
    source: 'RecoveryMatrixFixtureSlicerSchemaDelta.cs',
    assemblies: Object.freeze(['Farm.Slicer.Migrations.PostgreSQL', 'Farm.Slicer.Migrations.SqlServer']),
  }),
});

// The executor runs each context's migration from exactly one service image
// (HostUpdateTargetImageMigrationRunner.ContextServices: AppDbContext -> api,
// SlicerDbContext -> slicer-host). The monolith image is built FROM the api image, so it
// carries the same assemblies and is overlaid too, keeping the running application's
// migrations assembly in step with the database. Paths are relative to /app; api and
// monolith load the slicer migrations from plugins/slicer. Other services (frontend,
// printer-discovery, orcaslicer-worker) carry no migrations and keep an identical target.
const appMigrationPaths = Object.freeze([
  'Farm.Migrations.PostgreSQL.dll',
  'Farm.Migrations.SqlServer.dll',
  'plugins/slicer/Farm.Slicer.Migrations.PostgreSQL.dll',
  'plugins/slicer/Farm.Slicer.Migrations.SqlServer.dll',
]);
export const schemaDeltaRequiredAssemblies = Object.freeze({
  api: appMigrationPaths,
  monolith: appMigrationPaths,
  'slicer-host': Object.freeze(['Farm.Slicer.Migrations.PostgreSQL.dll', 'Farm.Slicer.Migrations.SqlServer.dll']),
});

export function schemaDeltaAppliesTo(serviceId) {
  return Object.hasOwn(schemaDeltaRequiredAssemblies, serviceId);
}

export function resolveSchemaDelta(cell, override) {
  const value = override ?? cell?.schemaDelta ?? 'identical';
  if (!schemaDeltas.includes(value)) {
    throw new Error(`unknown schemaDelta '${value}'. Expected one of: ${schemaDeltas.join(', ')}`);
  }
  return value;
}

export function schemaDeltaFixtureSummary(schemaDelta) {
  return schemaDelta === 'changed' ? schemaDeltaFixtureMigrations : null;
}

export function schemaDeltaTargetBuildArgs({ repo, priorTag, targetTag, targetVersion, sourceCommit, serviceId }) {
  if (!schemaDeltaAppliesTo(serviceId)) {
    throw new Error(`service '${serviceId}' carries no migrations assemblies; build an identical target instead`);
  }
  const required = schemaDeltaRequiredAssemblies[serviceId];
  return [
    'build',
    repo,
    '--file', join(repo, schemaDeltaFixtureDockerfile),
    '--tag', targetTag,
    '--build-arg', `PRIOR_IMAGE=${priorTag}`,
    '--build-arg', `TARGET_VERSION=${targetVersion}`,
    '--build-arg', `GIT_SHA=${sourceCommit}`,
    '--build-arg', `REQUIRED_ASSEMBLIES=${required.join(' ')}`,
  ];
}

const quoteIdentifier = (provider, name) => (provider === 'sqlserver' ? `[${name}]` : `"${name}"`);
const quoteLiteral = value => `'${String(value).replaceAll("'", "''")}'`;

// SQL returning one row with the history-row count and table presence for a context's
// fixture migration, so a cell can compare the database against its recorded outcome.
export function schemaDeltaFixtureStateSql(provider, contextName) {
  const fixture = schemaDeltaFixtureMigrations[contextName];
  if (!fixture) {
    throw new Error(`no schema-delta fixture for context '${contextName}'`);
  }
  if (provider !== 'postgres' && provider !== 'sqlserver') {
    throw new Error(`schema-delta fixture supports postgres and sqlserver, not '${provider}'`);
  }
  const history = quoteIdentifier(provider, '__EFMigrationsHistory');
  const migrationId = quoteIdentifier(provider, 'MigrationId');
  const historyCount = `(SELECT COUNT(*) FROM ${history} WHERE ${migrationId} = ${quoteLiteral(fixture.migrationId)})`;
  const tableExists = provider === 'sqlserver'
    ? `(CASE WHEN OBJECT_ID(N${quoteLiteral(`dbo.${fixture.table}`)}, N'U') IS NULL THEN 0 ELSE 1 END)`
    : `(CASE WHEN to_regclass(${quoteLiteral(`public."${fixture.table}"`)}) IS NULL THEN 0 ELSE 1 END)`;
  return `SELECT ${historyCount} AS history_rows, ${tableExists} AS table_exists;`;
}

// Expected fixture state for a durable outcome: Activated applied the N+1 migration exactly
// once; RolledBack restored the prior heads, so neither the history row nor the table exist.
export function expectedSchemaDeltaFixtureState(outcome) {
  if (outcome === 'Activated') {
    return Object.freeze({ historyRows: 1, tableExists: 1 });
  }
  if (outcome === 'RolledBack') {
    return Object.freeze({ historyRows: 0, tableExists: 0 });
  }
  return null;
}
