#!/usr/bin/env bash
# Collapse a deployed PrintFarmer PostgreSQL "__EFMigrationsHistory" table down to
# one baseline row per DbContext, after the repo-side migration squash has landed.
#
# WHY A WHOLESALE REPLACE IS SAFE HERE
# ------------------------------------
# AppDbContext and SlicerDbContext resolve the same connection string and neither
# customises MigrationsHistoryTable, so they SHARE one database and one default
# "__EFMigrationsHistory" table. Because both contexts are being squashed together,
# every existing row is superseded and the table can be replaced wholesale with
# exactly two baseline rows. If you ever squash only ONE context, this script is
# the wrong tool -- a blanket delete would strand the other context's history and
# it would try to re-apply its migrations against tables that already exist.
#
# PRECONDITIONS (enforced below where possible)
#   1. The deployment has already applied every migration up to the squash point.
#      Enforced via --require-applied, which defaults to the harvest backfill so
#      you cannot collapse history before that data migration has actually run.
#   2. The repo-side squash is merged and you know both new baseline migration IDs.
#   3. The API and slicer-host containers are STOPPED. Both call Database.Migrate()
#      on startup and would race this surgery.
#
# Usage:
#   ./scripts/squash-migrations-postgres.sh \
#       --app-migration-id    20260801120000_InitialV2 \
#       --slicer-migration-id 20260801120500_SlicerInitialV2
#
#   Add --yes to actually apply. Without it the script only inspects and reports.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# shellcheck source=./common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

CONTAINER="${PF_DB_CONTAINER:-printfarmer-database-postgres}"
DB_NAME="${PF_DB_NAME:-printfarmer}"
DB_USER="${PF_DB_USER:-postgres}"
BACKUP_DIR="${PF_BACKUP_DIR:-$REPO_ROOT/backups}"

APP_MIGRATION_ID=""
SLICER_MIGRATION_ID=""
REQUIRE_APPLIED="20260730161147_BackfillLegacyHarvestedAt"
EXPECT_COUNT=""
APPLY=false
FORCE=false
SKIP_BACKUP=false

HISTORY_TABLE='"__EFMigrationsHistory"'

print_help() {
  cat <<EOF
Usage: $0 --app-migration-id <id> --slicer-migration-id <id> [options]

Collapse the deployed EF Core migration history to one baseline row per DbContext.
Runs read-only unless --yes is supplied.

Required:
  --app-migration-id ID       New AppDbContext baseline, e.g. 20260801120000_InitialV2
  --slicer-migration-id ID    New SlicerDbContext baseline, e.g. 20260801120500_SlicerInitialV2

Options:
  --container NAME     Postgres container name (default: $CONTAINER)
  --db NAME            Database name (default: $DB_NAME)
  --user NAME          Postgres user (default: $DB_USER)
  --require-applied ID Migration that must already be applied before collapsing.
                       Default: $REQUIRE_APPLIED
                       Pass 'none' to disable this guard.
  --expect-count N     Fail unless the history table currently has exactly N rows.
  --backup-dir DIR     Where to write the pg_dump (default: $BACKUP_DIR)
  --skip-backup        Skip the pg_dump. Not recommended.
  --force              Proceed even if API/slicer containers are still running.
  --yes                Actually apply the change. Without this, nothing is written.
  -h, --help           Show this help

Environment overrides: PF_DB_CONTAINER, PF_DB_NAME, PF_DB_USER, PF_BACKUP_DIR
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app-migration-id)    APP_MIGRATION_ID="$2"; shift 2;;
    --slicer-migration-id) SLICER_MIGRATION_ID="$2"; shift 2;;
    --container)           CONTAINER="$2"; shift 2;;
    --db)                  DB_NAME="$2"; shift 2;;
    --user)                DB_USER="$2"; shift 2;;
    --require-applied)     REQUIRE_APPLIED="$2"; shift 2;;
    --expect-count)        EXPECT_COUNT="$2"; shift 2;;
    --backup-dir)          BACKUP_DIR="$2"; shift 2;;
    --skip-backup)         SKIP_BACKUP=true; shift;;
    --force)               FORCE=true; shift;;
    --yes)                 APPLY=true; shift;;
    -h|--help)             print_help; exit 0;;
    *) log_error "Unknown argument: $1"; print_help; exit 1;;
  esac
done

# --- helpers -----------------------------------------------------------------

# Single scalar value, no headers or padding.
# No -i: every call passes SQL via -c, so leaving stdin attached would let
# docker exec swallow the script's own stdin.
psql_value() {
  docker exec "$CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" \
    -v ON_ERROR_STOP=1 -At -c "$1"
}

# Statement(s) with full error propagation.
psql_exec() {
  docker exec "$CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" \
    -v ON_ERROR_STOP=1 -c "$1"
}

# Migration IDs are interpolated into SQL, so constrain them to EF's own shape
# rather than trusting the caller.
validate_migration_id() {
  local label="$1" value="$2"
  if [[ ! "$value" =~ ^[0-9]{14}_[A-Za-z0-9_]+$ ]]; then
    log_error "$label is not a valid EF migration ID: '$value'"
    log_info  "Expected 14 digits, an underscore, then the name (e.g. 20260801120000_InitialV2)."
    exit 1
  fi
}

# --- validation --------------------------------------------------------------

log_header "PrintFarmer migration history squash (PostgreSQL)"

if [[ -z "$APP_MIGRATION_ID" || -z "$SLICER_MIGRATION_ID" ]]; then
  log_error "Both --app-migration-id and --slicer-migration-id are required."
  print_help
  exit 1
fi

validate_migration_id "--app-migration-id" "$APP_MIGRATION_ID"
validate_migration_id "--slicer-migration-id" "$SLICER_MIGRATION_ID"

if [[ "$APP_MIGRATION_ID" == "$SLICER_MIGRATION_ID" ]]; then
  log_error "The two baseline IDs are identical: '$APP_MIGRATION_ID'"
  log_info  "MigrationId is the primary key of the shared history table, so the two"
  log_info  "contexts must have distinct baseline IDs. Regenerate one with a different name."
  exit 1
fi

require_command docker

# --- preflight ---------------------------------------------------------------

log_header "Preflight"

if ! docker info >/dev/null 2>&1; then
  log_error "Cannot reach the Docker daemon."
  log_info  "Start it with: sudo systemctl start docker"
  log_info  "If it is already running, your user may not be in the docker group:"
  log_info  "  sudo usermod -aG docker \$USER   # then log out and back in"
  exit 1
fi

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  log_error "Database container '$CONTAINER' is not running."
  log_info  "Start it, or pass --container with the correct name. Currently running:"
  docker ps --format '  {{.Names}}' || true
  exit 1
fi
log_success "Database container '$CONTAINER' is running"

if ! psql_value "SELECT 1;" >/dev/null 2>&1; then
  log_error "Cannot query database '$DB_NAME' as user '$DB_USER' inside '$CONTAINER'."
  exit 1
fi
log_success "Connected to database '$DB_NAME' as '$DB_USER'"

# The API and slicer-host both run Database.Migrate() at startup. If either is up
# while we rewrite history, it can re-apply migrations mid-surgery.
RUNNING_APPS="$(docker ps --format '{{.Names}}' \
  | grep -E 'printfarmer-(api|slicer-host|web)' || true)"
if [[ -n "$RUNNING_APPS" ]]; then
  log_warn "These application containers are still running:"
  echo "$RUNNING_APPS" | sed 's/^/    /'
  log_warn "They call Database.Migrate() on startup and can race this operation."
  if [[ "$FORCE" != true ]]; then
    log_error "Refusing to continue. Stop them first, or pass --force."
    exit 1
  fi
  log_warn "--force supplied; continuing anyway."
else
  log_success "No API or slicer-host containers are running"
fi

TABLE_EXISTS="$(psql_value "SELECT to_regclass('public.$HISTORY_TABLE') IS NOT NULL;")"
if [[ "$TABLE_EXISTS" != "t" ]]; then
  log_error "Table $HISTORY_TABLE not found in '$DB_NAME'. Is this the right database?"
  exit 1
fi
log_success "History table $HISTORY_TABLE found"

# --- inspect -----------------------------------------------------------------

log_header "Current migration history"

CURRENT_COUNT="$(psql_value "SELECT COUNT(*) FROM $HISTORY_TABLE;")"
log_info "Applied migrations: $CURRENT_COUNT"

PRODUCT_VERSION="$(psql_value "SELECT \"ProductVersion\" FROM $HISTORY_TABLE ORDER BY \"MigrationId\" DESC LIMIT 1;")"
if [[ -z "$PRODUCT_VERSION" ]]; then
  log_error "History table is empty; there is nothing to squash."
  exit 1
fi
log_info "EF Core ProductVersion carried forward: $PRODUCT_VERSION"

echo
log_info "Oldest 3 and newest 3 entries:"
psql_exec "(SELECT \"MigrationId\" FROM $HISTORY_TABLE ORDER BY \"MigrationId\" ASC LIMIT 3)
           UNION ALL
           (SELECT \"MigrationId\" FROM $HISTORY_TABLE ORDER BY \"MigrationId\" DESC LIMIT 3);" || true

if [[ -n "$EXPECT_COUNT" ]]; then
  if [[ "$CURRENT_COUNT" != "$EXPECT_COUNT" ]]; then
    log_error "Expected exactly $EXPECT_COUNT applied migrations but found $CURRENT_COUNT."
    log_info  "This usually means the deployment is not at the revision you squashed from."
    exit 1
  fi
  log_success "Applied count matches --expect-count ($EXPECT_COUNT)"
fi

if [[ "$REQUIRE_APPLIED" != "none" ]]; then
  IS_APPLIED="$(psql_value "SELECT EXISTS (SELECT 1 FROM $HISTORY_TABLE WHERE \"MigrationId\" = '$REQUIRE_APPLIED');")"
  if [[ "$IS_APPLIED" != "t" ]]; then
    log_error "Required migration '$REQUIRE_APPLIED' has NOT been applied to this database."
    log_info  "Collapsing history now would discard that migration without ever running it."
    log_info  "Deploy the current build first, let it migrate, then re-run this script."
    exit 1
  fi
  log_success "Required migration '$REQUIRE_APPLIED' is applied"
fi

# --- plan --------------------------------------------------------------------

log_header "Plan"
log_info "Delete   : all $CURRENT_COUNT rows from $HISTORY_TABLE"
log_info "Insert   : $APP_MIGRATION_ID    (AppDbContext baseline)"
log_info "Insert   : $SLICER_MIGRATION_ID (SlicerDbContext baseline)"
log_info "Version  : $PRODUCT_VERSION"
log_info "Atomicity: single transaction; rolls back entirely on any error"

if [[ "$APPLY" != true ]]; then
  echo
  log_warn "DRY RUN - nothing was modified."
  log_info "Re-run with --yes to apply."
  exit 0
fi

# --- backup ------------------------------------------------------------------

if [[ "$SKIP_BACKUP" == true ]]; then
  log_warn "Skipping backup because --skip-backup was supplied."
else
  log_header "Backup"
  mkdir -p "$BACKUP_DIR"
  BACKUP_FILE="$BACKUP_DIR/printfarmer-${DB_NAME}-presquash-$(date -u +%Y%m%dT%H%M%SZ).sql"
  log_info "Dumping '$DB_NAME' to $BACKUP_FILE ..."
  if ! docker exec "$CONTAINER" pg_dump -U "$DB_USER" -d "$DB_NAME" > "$BACKUP_FILE"; then
    log_error "pg_dump failed. Aborting before any changes were made."
    rm -f "$BACKUP_FILE"
    exit 1
  fi
  if [[ ! -s "$BACKUP_FILE" ]]; then
    log_error "Backup file is empty. Aborting before any changes were made."
    rm -f "$BACKUP_FILE"
    exit 1
  fi
  log_success "Backup written: $BACKUP_FILE ($(du -h "$BACKUP_FILE" | cut -f1))"
  log_info "Restore with: docker exec -i $CONTAINER psql -U $DB_USER -d $DB_NAME < $BACKUP_FILE"
fi

# --- apply -------------------------------------------------------------------

log_header "Applying"

psql_exec "BEGIN;
DELETE FROM $HISTORY_TABLE;
INSERT INTO $HISTORY_TABLE (\"MigrationId\", \"ProductVersion\") VALUES
  ('$APP_MIGRATION_ID', '$PRODUCT_VERSION'),
  ('$SLICER_MIGRATION_ID', '$PRODUCT_VERSION');
COMMIT;"

# --- verify ------------------------------------------------------------------

log_header "Verification"

NEW_COUNT="$(psql_value "SELECT COUNT(*) FROM $HISTORY_TABLE;")"
if [[ "$NEW_COUNT" != "2" ]]; then
  log_error "Expected exactly 2 rows after the squash but found $NEW_COUNT."
  log_error "Restore from the backup above before starting the application."
  exit 1
fi

for expected in "$APP_MIGRATION_ID" "$SLICER_MIGRATION_ID"; do
  present="$(psql_value "SELECT EXISTS (SELECT 1 FROM $HISTORY_TABLE WHERE \"MigrationId\" = '$expected');")"
  if [[ "$present" != "t" ]]; then
    log_error "Baseline row '$expected' is missing after the squash."
    log_error "Restore from the backup above before starting the application."
    exit 1
  fi
done

log_success "History collapsed from $CURRENT_COUNT rows to 2 baseline rows"
psql_exec "SELECT \"MigrationId\", \"ProductVersion\" FROM $HISTORY_TABLE ORDER BY \"MigrationId\";" || true

log_header "Next steps"
log_info "1. Start the API and slicer-host containers."
log_info "2. Confirm startup logs show no pending migrations and no attempt to re-create tables."
log_info "3. Smoke-test the app, then retain the backup until you are satisfied."
log_success "Done."
