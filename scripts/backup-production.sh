#!/bin/bash
# PrintFarmer Production Backup Script

set -e

# Configuration
BACKUP_DIR="/backups/printfarmer"
RETENTION_DAYS=30
DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_PREFIX="printfarmer_backup_${DATE}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

print_status() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Create backup directory
mkdir -p "$BACKUP_DIR"

print_status "Starting PrintFarmer backup at $(date)"

# 1. Database Backup
print_status "Backing up database..."
case "${DB_PROVIDER:-sqlite}" in
    sqlite)
        docker exec printfarmer-api-1 sqlite3 /data/farm.db ".backup /data/backup_${DATE}.db"
        docker cp printfarmer-api-1:/data/backup_${DATE}.db "$BACKUP_DIR/${BACKUP_PREFIX}_database.db"
        docker exec printfarmer-api-1 rm /data/backup_${DATE}.db
        ;;
    postgres)
        docker exec printfarmer-postgres-1 pg_dump -U postgres printfarmer > "$BACKUP_DIR/${BACKUP_PREFIX}_database.sql"
        ;;
    sqlserver)
        docker exec printfarmer-sqlserver-1 /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" \
            -Q "BACKUP DATABASE printfarmer TO DISK = '/tmp/backup.bak'"
        docker cp printfarmer-sqlserver-1:/tmp/backup.bak "$BACKUP_DIR/${BACKUP_PREFIX}_database.bak"
        ;;
    mysql)
        docker exec printfarmer-mysql-1 mysqldump -u root -p"$MYSQL_ROOT_PASSWORD" printfarmer > "$BACKUP_DIR/${BACKUP_PREFIX}_database.sql"
        ;;
esac

# 2. Application Data Backup
print_status "Backing up application data..."
docker run --rm \
    -v printfarmer_app_data:/source:ro \
    -v "$BACKUP_DIR":/backup \
    alpine:latest \
    tar czf "/backup/${BACKUP_PREFIX}_app_data.tar.gz" -C /source .

# 3. Model Files Backup
print_status "Backing up uploaded models..."
docker run --rm \
    -v printfarmer_model_uploads:/source:ro \
    -v "$BACKUP_DIR":/backup \
    alpine:latest \
    tar czf "/backup/${BACKUP_PREFIX}_models.tar.gz" -C /source .

# 4. G-code Files Backup
print_status "Backing up G-code files..."
docker run --rm \
    -v printfarmer_gcode_storage:/source:ro \
    -v "$BACKUP_DIR":/backup \
    alpine:latest \
    tar czf "/backup/${BACKUP_PREFIX}_gcode.tar.gz" -C /source .

# 5. Slicer Profiles Backup
print_status "Backing up slicer profiles..."
docker run --rm \
    -v printfarmer_slicer_profiles:/source:ro \
    -v "$BACKUP_DIR":/backup \
    alpine:latest \
    tar czf "/backup/${BACKUP_PREFIX}_profiles.tar.gz" -C /source .

# 6. Configuration Backup
print_status "Backing up configuration files..."
tar czf "$BACKUP_DIR/${BACKUP_PREFIX}_config.tar.gz" \
    .env.* \
    docker-compose*.yml \
    deploy/ \
    2>/dev/null || print_warning "Some config files not found"

# 7. Create backup manifest
print_status "Creating backup manifest..."
cat > "$BACKUP_DIR/${BACKUP_PREFIX}_manifest.txt" << EOF
PrintFarmer Backup Manifest
===========================
Backup Date: $(date)
Backup Type: Full
Database Provider: ${DB_PROVIDER:-sqlite}

Files in this backup:
- ${BACKUP_PREFIX}_database.*     (Database dump)
- ${BACKUP_PREFIX}_app_data.tar.gz    (Application data)
- ${BACKUP_PREFIX}_models.tar.gz      (Uploaded models)
- ${BACKUP_PREFIX}_gcode.tar.gz       (Generated G-code)
- ${BACKUP_PREFIX}_profiles.tar.gz    (Slicer profiles)
- ${BACKUP_PREFIX}_config.tar.gz      (Configuration files)

Restoration Instructions:
1. Stop all services: docker compose down
2. Restore database from dump
3. Extract data volumes: tar xzf *_app_data.tar.gz
4. Extract model files: tar xzf *_models.tar.gz
5. Extract G-code files: tar xzf *_gcode.tar.gz
6. Extract profiles: tar xzf *_profiles.tar.gz
7. Restore configuration files
8. Start services: docker compose up -d
EOF

# 8. Cleanup old backups
print_status "Cleaning up old backups (older than $RETENTION_DAYS days)..."
find "$BACKUP_DIR" -name "printfarmer_backup_*" -mtime +$RETENTION_DAYS -delete

# 9. Calculate backup size
BACKUP_SIZE=$(du -sh "$BACKUP_DIR" | cut -f1)
print_status "Backup completed successfully!"
print_status "Backup location: $BACKUP_DIR"
print_status "Total backup size: $BACKUP_SIZE"

# 10. Optional: Upload to cloud storage
if [ -n "$BACKUP_UPLOAD_COMMAND" ]; then
    print_status "Uploading backup to remote storage..."
    eval "$BACKUP_UPLOAD_COMMAND '$BACKUP_DIR/${BACKUP_PREFIX}_*'"
fi

print_status "Backup process finished at $(date)"