import { chmodSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export const providerCatalog = Object.freeze({
  postgres: Object.freeze({
    id: 'postgres',
    dbProvider: 'Postgres',
    supportedProviderName: 'Npgsql.EntityFrameworkCore.PostgreSQL',
    composeService: 'database',
    infrastructureIds: Object.freeze(['postgres']),
    imageEnv: 'POSTGRES_IMAGE',
    defaultImage: 'docker.io/library/postgres:16-alpine',
    internalPort: 5432,
    env() {
      return {
        POSTGRES_DB: 'printfarmer',
        POSTGRES_USER: 'printfarmer',
      };
    },
    connectionString({ host, env }) {
      return `Host=${host};Port=5432;Database=${env.POSTGRES_DB};Username=${env.POSTGRES_USER};Password=${env.POSTGRES_PASSWORD}`;
    },
    slicerConnectionString({ host, env }) {
      return `Host=${host};Port=5432;Database=${env.POSTGRES_DB}_slicer;Username=${env.POSTGRES_USER};Password=${env.POSTGRES_PASSWORD}`;
    },
    migrationHeadsSql: 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";',
    readinessArgs(env) {
      return ['pg_isready', '-U', env.POSTGRES_USER, '-d', env.POSTGRES_DB];
    },
    queryArgs(env, sql) {
      return ['psql', '-U', env.POSTGRES_USER, '-d', env.POSTGRES_DB, '-tAc', sql];
    },
    writeToolShims({ runRoot, databaseContainer }) {
      return writePostgresToolShims(runRoot, databaseContainer);
    },
  }),
  sqlserver: Object.freeze({
    id: 'sqlserver',
    dbProvider: 'SqlServer',
    supportedProviderName: 'Microsoft.EntityFrameworkCore.SqlServer',
    composeService: 'database',
    infrastructureIds: Object.freeze(['mssql']),
    imageEnv: 'MSSQL_IMAGE',
    defaultImage: 'mcr.microsoft.com/mssql/server:2022-latest',
    internalPort: 1433,
    env() {
      return {
        MSSQL_DB: 'printfarmer',
        MSSQL_USER: 'sa',
        MSSQL_PID: 'Developer',
        ACCEPT_EULA: 'Y',
      };
    },
    connectionString({ host, env }) {
      return `Server=${host},1433;Database=${env.MSSQL_DB};User Id=${env.MSSQL_USER};Password=${env.MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=True`;
    },
    slicerConnectionString({ host, env }) {
      return `Server=${host},1433;Database=${env.MSSQL_DB}_slicer;User Id=${env.MSSQL_USER};Password=${env.MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=True`;
    },
    migrationHeadsSql: 'SET NOCOUNT ON; SELECT [MigrationId] FROM [__EFMigrationsHistory] ORDER BY [MigrationId];',
    readinessArgs(env) {
      return ['/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', env.MSSQL_USER, '-P', env.MSSQL_SA_PASSWORD, '-Q', 'SELECT 1'];
    },
    queryArgs(env, sql, { database } = {}) {
      return ['/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', env.MSSQL_USER, '-P', env.MSSQL_SA_PASSWORD, '-d', database ?? env.MSSQL_DB, '-b', '-h', '-1', '-W', '-Q', sql];
    },
    writeToolShims({ runRoot, databaseContainer }) {
      return writeSqlServerToolShims(runRoot, databaseContainer);
    },
  }),
});

export function providerFor(id) {
  const provider = providerCatalog[id];
  if (!provider) {
    throw new Error(`unknown provider '${id}'`);
  }
  return provider;
}

function writePostgresToolShims(runRoot, databaseContainer) {
  const pgDump = join(runRoot, 'pg_dump');
  const pgRestore = join(runRoot, 'pg_restore');
  writeFileSync(pgDump, `#!/usr/bin/env bash
set -euo pipefail
out=""
args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -f)
      out="$2"
      shift 2
      ;;
    *)
      args+=("$1")
      shift
      ;;
  esac
done
if [[ -z "$out" ]]; then
  echo "pg_dump shim requires -f <output>" >&2
  exit 64
fi
mkdir -p "$(dirname "$out")"
/usr/bin/docker exec -e "PGPASSWORD=\${PGPASSWORD:-}" ${databaseContainer} pg_dump "\${args[@]}" > "$out"
`);
  chmodSync(pgDump, 0o755);
  writeFileSync(pgRestore, `#!/usr/bin/env bash
set -euo pipefail
args=("$@")
last_index=$((\${#args[@]} - 1))
input="\${args[$last_index]}"
unset "args[$last_index]"
/usr/bin/docker exec -i -e "PGPASSWORD=\${PGPASSWORD:-}" ${databaseContainer} pg_restore "\${args[@]}" < "$input"
`);
  chmodSync(pgRestore, 0o755);
  return { pgDump, pgRestore };
}

function writeSqlServerToolShims(runRoot, databaseContainer) {
  const sqlcmd = join(runRoot, 'sqlcmd');
  writeFileSync(sqlcmd, `#!/usr/bin/env bash
set -euo pipefail
query=""
for ((i=1; i<=$#; i++)); do
  if [[ "\${!i}" == "-Q" ]]; then
    next=$((i + 1))
    query="\${!next:-}"
    break
  fi
done
if [[ "$query" =~ TO[[:space:]]+DISK[[:space:]]*=[[:space:]]*N\\'([^\\']+)\\' ]]; then
  backup_file="\${BASH_REMATCH[1]}"
  backup_dir="$(dirname "$backup_file")"
  /usr/bin/docker exec -u 0 ${databaseContainer} sh -c 'mkdir -p "$1" && chown 10001:0 "$1"' sh "$backup_dir"
fi
/usr/bin/docker exec -e SQLCMDPASSWORD ${databaseContainer} /opt/mssql-tools18/bin/sqlcmd -C "$@"
`);
  chmodSync(sqlcmd, 0o755);
  return { sqlcmd };
}
