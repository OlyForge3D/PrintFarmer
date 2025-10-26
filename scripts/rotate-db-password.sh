#!/usr/bin/env bash
# Rotate a database provider password in the generated .env file safely
# Usage: ./scripts/rotate-db-password.sh [--provider <postgres|sqlserver|mysql>] [--env FILE] [--restart]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ENV_FILE=""
RESTART=false
PROVIDER=""
APPLY_DB=false
YES=false
DRY_RUN=false

print_help() {
  cat <<EOF
Usage: $0 [--provider <postgres|sqlserver|mysql>] [--env FILE] [--restart]

Safely rotate the database password stored in the project's .env file.

Options:
  --provider   Which provider to rotate (postgres, sqlserver, mysql)
  --env FILE   Path to env file to update (default: .env, or .env.microservices/.env.monolithic)
  --restart    After updating .env, run 'docker compose --env-file <env> up -d' to restart services
  --apply-db   Attempt to apply the password change inside the running database container before updating .env
  --yes        Assume yes to confirmation prompts when applying changes to the DB
  --dry-run    Show what would be done but don't apply changes to the database
  -h,--help    Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --provider)
      PROVIDER="$2"; shift 2;;
    --env)
      ENV_FILE="$2"; shift 2;;
    --restart)
        RESTART=true; shift;;
      --apply-db)
        APPLY_DB=true; shift;;
      --yes)
        YES=true; shift;;
      --dry-run)
        DRY_RUN=true; shift;;
    -h|--help)
      print_help; exit 0;;
    *)
      echo "Unknown arg: $1"; print_help; exit 1;;
  esac
done

  # If provider not given, attempt to detect it from the env file or ConnectionStrings
  if [ -z "$PROVIDER" ]; then
    # We'll detect after resolving the env file path (below)
    DETECT_PROVIDER=true
  else
    DETECT_PROVIDER=false
  fi

# Resolve env file default
if [ -z "$ENV_FILE" ]; then
  if [ -f "$REPO_ROOT/.env" ]; then
    ENV_FILE="$REPO_ROOT/.env"
  elif [ -f "$REPO_ROOT/.env.microservices" ]; then
    ENV_FILE="$REPO_ROOT/.env.microservices"
  elif [ -f "$REPO_ROOT/.env.monolithic" ]; then
    ENV_FILE="$REPO_ROOT/.env.monolithic"
  else
    echo "No .env file found in repo root. Specify one with --env FILE" >&2
    exit 1
  fi
fi

echo "Using env file: $ENV_FILE"

# If provider was not supplied, try to infer it from the env file
if [ "${DETECT_PROVIDER:-false}" = true ]; then
  # Try DB_PROVIDER first
  maybe=$(grep -E '^DB_PROVIDER=' "$ENV_FILE" | tail -n1 | cut -d= -f2- | tr '[:upper:]' '[:lower:]' || true)
  if [ -n "$maybe" ]; then
    PROVIDER_LOWER="$maybe"
  else
    # Look for provider-specific keys
    if grep -qE '^POSTGRES_' "$ENV_FILE"; then
      PROVIDER_LOWER="postgres"
    elif grep -qE '^(MSSQL_|SQLSERVER_|DB_PASSWORD|MSSQL_SA_PASSWORD|SQLSERVER_PASSWORD)' "$ENV_FILE"; then
      PROVIDER_LOWER="sqlserver"
    elif grep -qE '^MYSQL_' "$ENV_FILE"; then
      PROVIDER_LOWER="mysql"
    else
      # Fallback to checking connection string hints
      conn=$(grep -E '^ConnectionStrings__Default=' "$ENV_FILE" | tail -n1 | cut -d= -f2- || true)
      conn_l=$(echo "$conn" | tr '[:upper:]' '[:lower:]')
      if echo "$conn_l" | grep -q 'host=postgres\|server=postgres'; then
        PROVIDER_LOWER="postgres"
      elif echo "$conn_l" | grep -q 'server=sqlserver\|data source=sqlserver'; then
        PROVIDER_LOWER="sqlserver"
      elif echo "$conn_l" | grep -q 'server=mysql\|host=mysql'; then
        PROVIDER_LOWER="mysql"
      else
        echo "Unable to detect DB provider from $ENV_FILE. Provide --provider explicitly." >&2
        exit 1
      fi
    fi
  fi
fi

PROVIDER_LOWER=$(echo "${PROVIDER_LOWER:-$PROVIDER}" | tr '[:upper:]' '[:lower:]')
if [[ "$PROVIDER_LOWER" != "postgres" && "$PROVIDER_LOWER" != "sqlserver" && "$PROVIDER_LOWER" != "mysql" ]]; then
  echo "Error: unknown provider '$PROVIDER_LOWER'" >&2; exit 1
fi

timestamp() { date -u +%Y%m%dT%H%M%SZ; }

backup_file="${ENV_FILE}.bak.$(timestamp)"
cp "$ENV_FILE" "$backup_file"
echo "Backup created: $backup_file"

generate_random_password() {
  if command -v openssl >/dev/null 2>&1; then
    pw=$(openssl rand -base64 18 | tr -d '/+' | cut -c1-16)
  else
    pw=$(tr -dc 'A-Za-z0-9!@#$%&*()-_=+' </dev/urandom 2>/dev/null | head -c 16 || echo "Pfarm$(date +%s)")
  fi
  # ensure complexity
  [[ "$pw" =~ [A-Z] ]] || pw="A$pw"
  [[ "$pw" =~ [a-z] ]] || pw="${pw}a"
  [[ "$pw" =~ [0-9] ]] || pw="${pw}1"
  [[ "$pw" =~ [^A-Za-z0-9] ]] || pw="${pw}!"
  echo "$pw"
}

mask_secret() {
  local s="$1"
  [ -z "$s" ] && { echo "(not set)"; return; }
  local len=${#s}
  if [ $len -le 8 ]; then echo "$s"; return; fi
  local head=${s:0:4}
  local tail=${s: -4}
  echo "${head}****${tail}"
}

# Helper: read a key from env file
get_env_value() {
  local key="$1"
  grep -E "^${key}=" "$ENV_FILE" | tail -n1 | cut -d= -f2- || true
}

NEW_PW=$(generate_random_password)
echo "Generated new password (will be written to $ENV_FILE)"

# Prepare replacement map
declare -A replacements

case "$PROVIDER_LOWER" in
  postgres)
    cur_db=$(get_env_value "POSTGRES_DB")
    cur_db=${cur_db:-printfarmer}
    cur_user=$(get_env_value "POSTGRES_USER")
    cur_user=${cur_user:-postgres}
    replacements[POSTGRES_PASSWORD]="$NEW_PW"
    # Update ConnectionStrings__Default if present and contains Host=postgres
    conn=$(get_env_value "ConnectionStrings__Default")
    if echo "$conn" | grep -qi "Host=postgres"; then
      newconn="Host=postgres;Database=${cur_db};Username=${cur_user};Password=${NEW_PW}"
      replacements[ConnectionStrings__Default]="$newconn"
    fi
    ;;
  sqlserver)
    cur_db=$(get_env_value "SQLSERVER_DB")
    cur_db=${cur_db:-printfarmer}
    # update both canonical and legacy keys
    replacements[SQLSERVER_PASSWORD]="$NEW_PW"
    replacements[MSSQL_SA_PASSWORD]="$NEW_PW"
    # Update ConnectionStrings__Default if it references sqlserver
    conn=$(get_env_value "ConnectionStrings__Default")
    if echo "$conn" | grep -qi "Server=sqlserver"; then
      newconn="Server=sqlserver;Database=${cur_db};User Id=sa;Password=${NEW_PW};TrustServerCertificate=True;"
      replacements[ConnectionStrings__Default]="$newconn"
    fi
    ;;
  mysql)
    cur_db=$(get_env_value "MYSQL_DB")
    cur_db=${cur_db:-printfarmer}
    cur_user=$(get_env_value "MYSQL_USER")
    cur_user=${cur_user:-root}
    replacements[MYSQL_PASSWORD]="$NEW_PW"
    replacements[MYSQL_ROOT_PASSWORD]="$NEW_PW"
    conn=$(get_env_value "ConnectionStrings__Default")
    if echo "$conn" | grep -qi "Server=mysql"; then
      newconn="Server=mysql;Database=${cur_db};User=${cur_user};Password=${NEW_PW};"
      replacements[ConnectionStrings__Default]="$newconn"
    fi
    ;;
esac

# If requested, attempt to change the password inside the running database before updating env
if [ "$APPLY_DB" = true ]; then
  echo "--apply-db requested: will attempt to change password inside running database container"

  if [ "$DRY_RUN" = true ]; then
    echo "DRY-RUN: would run DB password change commands inside database container"
  else
    # Determine current password to authenticate
    case "$PROVIDER_LOWER" in
      postgres)
        OLD_PW=$(get_env_value "POSTGRES_PASSWORD")
        DB_USER=$(get_env_value "POSTGRES_USER")
        DB_NAME=$(get_env_value "POSTGRES_DB")
        DB_USER=${DB_USER:-postgres}
        DB_NAME=${DB_NAME:-printfarmer}
        # Prefer running psql inside the DB container; if missing, fall back to official postgres client image
        cmd="psql -U \"$DB_USER\" -d \"$DB_NAME\" -c \"ALTER USER \"$DB_USER\" WITH PASSWORD '${NEW_PW}';\""
        echo "Attempting to run psql inside the database container: $cmd"
        if [ "$YES" != true ]; then
          read -p "Proceed to change Postgres password inside container? [y/N]: " conf || true
          [[ "$conf" =~ ^[Yy] ]] || { echo "Aborting database password change"; exit 1; }
        fi
        if docker compose --env-file "$ENV_FILE" exec -T database sh -c "command -v psql >/dev/null 2>&1 && $cmd"; then
          echo "Applied Postgres password inside DB container"
        else
          echo "psql not available inside DB container or exec failed — attempting fallback using official postgres client image"
          db_cid=$(docker compose --env-file "$ENV_FILE" ps -q database)
          if [ -z "$db_cid" ]; then echo "Database container not running or not found"; exit 1; fi
          network=$(docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}' "$db_cid")
          if [ -z "$network" ]; then echo "Failed to determine docker network for database container"; exit 1; fi
          docker run --rm --network "$network" -e PGPASSWORD="$OLD_PW" postgres:15 psql -h database -U "$DB_USER" -d "$DB_NAME" -c "ALTER USER \"$DB_USER\" WITH PASSWORD '${NEW_PW}';" || { echo "Fallback Postgres client failed"; echo "No changes applied to $ENV_FILE"; exit 1; }
        fi
        ;;
      sqlserver)
        OLD_PW=$(get_env_value "SQLSERVER_PASSWORD")
        if [ -z "$OLD_PW" ]; then
          OLD_PW=$(get_env_value "DB_PASSWORD")
        fi
        sqlcmd_cmd="/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \"$OLD_PW\" -Q \"ALTER LOGIN sa WITH PASSWORD='${NEW_PW}';\""
        echo "Attempting to run sqlcmd inside the database container: $sqlcmd_cmd"
        if [ "$YES" != true ]; then
          read -p "Proceed to change SQL Server SA password inside container? [y/N]: " conf || true
          [[ "$conf" =~ ^[Yy] ]] || { echo "Aborting database password change"; exit 1; }
        fi
        if docker compose --env-file "$ENV_FILE" exec -T database sh -c "command -v /opt/mssql-tools/bin/sqlcmd >/dev/null 2>&1 && $sqlcmd_cmd"; then
          echo "Applied SQL Server SA password inside DB container"
        else
          echo "sqlcmd not available inside DB container or exec failed — attempting fallback using mssql-tools client image"
          db_cid=$(docker compose --env-file "$ENV_FILE" ps -q database)
          if [ -z "$db_cid" ]; then echo "Database container not running or not found"; exit 1; fi
          network=$(docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}' "$db_cid")
          if [ -z "$network" ]; then echo "Failed to determine docker network for database container"; exit 1; fi
          # mcr.microsoft.com/mssql-tools is commonly available; try it as fallback
          docker run --rm --network "$network" mcr.microsoft.com/mssql-tools -S sqlserver -U sa -P "$OLD_PW" -Q "ALTER LOGIN sa WITH PASSWORD='${NEW_PW}';" || { echo "Fallback sqlcmd client failed"; echo "No changes applied to $ENV_FILE"; exit 1; }
        fi
        ;;
      mysql)
        OLD_PW=$(get_env_value "MYSQL_ROOT_PASSWORD")
        OLD_PW=${OLD_PW:-$(get_env_value "MYSQL_PASSWORD")}
        mysql_cmd="mysql -u root -p\"$OLD_PW\" -e \"ALTER USER 'root'@'%' IDENTIFIED BY '${NEW_PW}'; FLUSH PRIVILEGES;\""
        echo "Attempting to run mysql inside the database container: $mysql_cmd"
        if [ "$YES" != true ]; then
          read -p "Proceed to change MySQL root password inside container? [y/N]: " conf || true
          [[ "$conf" =~ ^[Yy] ]] || { echo "Aborting database password change"; exit 1; }
        fi
        if docker compose --env-file "$ENV_FILE" exec -T database sh -c "command -v mysql >/dev/null 2>&1 && $mysql_cmd"; then
          echo "Applied MySQL root password inside DB container"
        else
          echo "mysql client not available inside DB container or exec failed — attempting fallback using official mysql client image"
          db_cid=$(docker compose --env-file "$ENV_FILE" ps -q database)
          if [ -z "$db_cid" ]; then echo "Database container not running or not found"; exit 1; fi
          network=$(docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}' "$db_cid")
          if [ -z "$network" ]; then echo "Failed to determine docker network for database container"; exit 1; fi
          docker run --rm --network "$network" -e MYSQL_PWD="$OLD_PW" mysql:8.0 mysql -h database -u root -e "ALTER USER 'root'@'%' IDENTIFIED BY '${NEW_PW}'; FLUSH PRIVILEGES;" || { echo "Fallback mysql client failed"; echo "No changes applied to $ENV_FILE"; exit 1; }
        fi
        ;;
    esac
    echo "Password applied in database container"
  fi
fi

# Write new env file atomically
tmpfile="${ENV_FILE}.tmp.$(timestamp)"
touch "$tmpfile"

while IFS= read -r line || [ -n "$line" ]; do
  # preserve empty lines and comments
  if [[ -z "$line" || "$line" =~ ^# ]]; then
    echo "$line" >> "$tmpfile"
    continue
  fi

  key="${line%%=*}"
  val="${line#*=}"
  if [[ -n "${replacements[$key]:-}" ]]; then
    echo "$key=${replacements[$key]}" >> "$tmpfile"
    # mark as applied
    replacements[$key]=""
  else
    echo "$line" >> "$tmpfile"
  fi
done < "$ENV_FILE"

# Append any remaining replacements (keys that didn't exist in file)
for k in "${!replacements[@]}"; do
  v="${replacements[$k]}"
  if [ -n "$v" ]; then
    echo "$k=$v" >> "$tmpfile"
  fi
done

mv "$tmpfile" "$ENV_FILE"
echo "Updated $ENV_FILE"

echo "Summary (masked):"
case "$PROVIDER_LOWER" in
  postgres)
    echo "  POSTGRES_PASSWORD=$(mask_secret "$NEW_PW")"
    ;;
  sqlserver)
    echo "  MSSQL_SA_PASSWORD=$(mask_secret "$NEW_PW")"
    ;;
  mysql)
    echo "  MYSQL_PASSWORD=$(mask_secret "$NEW_PW")"
    ;;
esac

echo "Backup retained at: $backup_file"
echo "Remember to secure the env file: chmod 600 $ENV_FILE"

if [ "$RESTART" = true ]; then
  echo "Restarting services using docker compose --env-file $ENV_FILE up -d"
  (cd "$REPO_ROOT" && docker compose --env-file "$ENV_FILE" up -d)
  echo "Restart triggered"
fi

# Optional smoke test: poll the API health endpoint to ensure services recovered
if [ "$RESTART" = true ]; then
  # Configurable smoke test settings
  SMOKE_URL="http://localhost:5245/healthz"
  SMOKE_TIMEOUT=60   # seconds
  SMOKE_INTERVAL=2   # seconds

  echo "Running smoke test against $SMOKE_URL (timeout ${SMOKE_TIMEOUT}s)"
  start_ts=$(date +%s)
  end_ts=$((start_ts + SMOKE_TIMEOUT))
  success=false
  while [ $(date +%s) -le $end_ts ]; do
    if curl -fs --max-time 5 "$SMOKE_URL" >/dev/null 2>&1; then
      success=true
      break
    fi
    sleep $SMOKE_INTERVAL
  done

  if [ "$success" = true ]; then
    echo "Smoke test passed: $SMOKE_URL is reachable"
  else
    echo "Smoke test FAILED: $SMOKE_URL did not respond within ${SMOKE_TIMEOUT}s"
    echo "Showing recent docker compose logs (last 200 lines) to help debug:"
    (cd "$REPO_ROOT" && docker compose --env-file "$ENV_FILE" logs --no-color --tail 200) || true
    echo "You can inspect logs interactively with: docker compose --env-file $ENV_FILE logs"
    exit 2
  fi
fi

exit 0
