#!/bin/bash
# PostgreSQL initialization script - runs once on first container start
# This ensures password authentication is enabled

set -e

echo "PrintFarmer PostgreSQL Initialization Script"
echo "=============================================="

# Only run on first initialization
if [ ! -f "$PGDATA/postgresql.conf" ]; then
    echo "First time initialization detected..."
    
    # PostgreSQL will create the database with POSTGRES_PASSWORD set
    # We just need to ensure password auth is enabled
    echo "Password authentication will be configured on startup"
else
    echo "Database already initialized, skipping init script"
fi

# Ensure password authentication is enabled in pg_hba.conf
if [ -f "$PGDATA/pg_hba.conf" ]; then
    echo "Configuring pg_hba.conf for password authentication..."
    
    # Backup original
    cp "$PGDATA/pg_hba.conf" "$PGDATA/pg_hba.conf.backup"
    
    # Replace trust with scram-sha256 (or md5 for older versions)
    sed -i 's/\(local.*all.*all.*\)trust$/\1scram-sha256/' "$PGDATA/pg_hba.conf"
    sed -i 's/\(host.*all.*all.*127\.0\.0\.1.*\)trust$/\1scram-sha256/' "$PGDATA/pg_hba.conf"
    sed -i 's/\(host.*all.*all.*::1.*\)trust$/\1scram-sha256/' "$PGDATA/pg_hba.conf"
    
    echo "✓ pg_hba.conf updated for password auth"
    echo "Configuration:"
    grep -E "local|host" "$PGDATA/pg_hba.conf" | head -5
fi

echo "PostgreSQL initialization complete"
