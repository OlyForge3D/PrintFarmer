# Database Configuration Cleanup

This document describes the database configuration cleanup implemented to provide clean, provider-specific database services in Docker Compose templates.

## Problem

Previously, all Docker Compose templates contained mixed environment variables for all database providers (PostgreSQL, SQL Server, MySQL) in a single database service definition. This created confusion and unnecessary configuration bloat.

**Before:**
```yaml
  database:
    image: postgres:15-alpine
    container_name: printfarmer-database
    environment:
      # Mixed environment variables for all providers
      POSTGRES_DB: ${POSTGRES_DB:-printfarmer}
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}
      ACCEPT_EULA: "Y"  # SQL Server specific
      SA_PASSWORD: ${SA_PASSWORD:-YourStrong@Passw0rd}  # SQL Server specific
      MYSQL_ROOT_PASSWORD: ${MYSQL_ROOT_PASSWORD:-rootpass}  # MySQL specific
      MYSQL_DATABASE: ${MYSQL_DATABASE:-farm}  # MySQL specific
    # ... hardcoded PostgreSQL health checks and volumes
```

## Solution

The compose generator now dynamically generates provider-specific database service configurations based on the `--db-provider` parameter.

**After (PostgreSQL example):**
```yaml
  database:
    image: postgres:16-alpine
    container_name: printfarmer-database
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-farm}
      POSTGRES_USER: ${POSTGRES_USER:-farmuser}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-farmpass}
    volumes:
      - database_data:/var/lib/postgresql/data
    ports:
      - "${DB_PORT:-5432}:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-farmuser} -d ${POSTGRES_DB:-farm}"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks:
      - printfarmer-network
```

## Features

### Provider-Specific Templates

Each database provider has its own template with appropriate:
- Docker image and version
- Environment variables specific to that provider
- Correct health checks
- Proper volume mounts
- Appropriate default ports

### Supported Providers

1. **PostgreSQL** (`postgres`) - Default
   - Image: `postgres:16-alpine`
   - Port: 5432
   - Environment: `POSTGRES_*` variables

2. **SQL Server** (`sqlserver`)
   - Image: `mcr.microsoft.com/mssql/server:2022-latest`
   - Port: 1433
   - Environment: `SA_PASSWORD`, `ACCEPT_EULA`, `MSSQL_PID`

3. **MySQL** (`mysql`)
   - Image: `mysql:8.0`
   - Port: 3306
   - Environment: `MYSQL_*` variables

### Dynamic Generation

The compose generator processes templates and replaces the database service section with the appropriate provider-specific configuration.

## Usage

### Via Compose Generator

```bash
# Generate with PostgreSQL (default)
./scripts/docker/compose-generator.sh --architecture microservices --db-provider postgres

# Generate with SQL Server
./scripts/docker/compose-generator.sh --architecture microservices --db-provider sqlserver

# Generate with MySQL
./scripts/docker/compose-generator.sh --architecture microservices --db-provider mysql
```

### Via Deploy Script

The deploy script automatically passes the selected database provider to the compose generator:

```bash
# Use environment variable
DB_PROVIDER=sqlserver ./scripts/deploy-docker.sh --architecture microservices

# Or let the interactive prompts handle it
./scripts/deploy-docker.sh --architecture microservices
```

## Architecture Support

- **Monolithic**: Uses SQLite by default, no database service cleanup needed
- **Microservices**: Supports all providers with dynamic database service generation
- **Host-network**: Supports all providers with dynamic database service generation

## Files

### Database Templates

- `scripts/docker/database-templates/postgres.yml` - PostgreSQL configuration
- `scripts/docker/database-templates/sqlserver.yml` - SQL Server configuration  
- `scripts/docker/database-templates/mysql.yml` - MySQL configuration

### Modified Files

- `scripts/docker/compose-generator.sh` - Added database configuration generation
- `scripts/deploy-docker.sh` - Pass DB_PROVIDER to compose generator
- `scripts/docker/compose-templates/*.yml` - Templates with mixed config (now cleaned up dynamically)

## Benefits

1. **Clean Configuration**: Only relevant environment variables for chosen provider
2. **Correct Health Checks**: Provider-specific health check commands
3. **Proper Defaults**: Sensible defaults for each database type
4. **No Configuration Bloat**: No unused environment variables
5. **Maintainable**: Easy to add new providers or modify existing ones
6. **Backward Compatible**: Existing deployments continue working

## Testing

The cleanup has been tested with all supported architectures and database providers:

```bash
# Test all providers with microservices
./compose-generator.sh --architecture microservices --db-provider postgres --dry-run
./compose-generator.sh --architecture microservices --db-provider sqlserver --dry-run
./compose-generator.sh --architecture microservices --db-provider mysql --dry-run

# Test host-network architecture
./compose-generator.sh --architecture host-network --db-provider postgres --dry-run
```

All tests confirmed proper database service replacement with provider-specific configurations.