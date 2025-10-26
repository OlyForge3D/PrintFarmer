# Database Service Naming Strategy

## Problem Solved
Fixed inconsistent container naming that was causing orphaned containers and port conflicts:
- `pfarm-sqlserver-1` (orphaned) vs `pfarm-database-1` (current)

## Consistent Naming Strategy

### **Production/Main Deployments** (Single Database)
All main compose files use `database` service with generic `printfarmer-database` container.
**Only ONE database runs at a time** - configured via environment variables:

| Compose File | Service Name | Container Name | Purpose |
|--------------|--------------|----------------|---------|
| `docker-compose.microservices.yml` | `database` | `printfarmer-database` | Microservices (PostgreSQL or SQL Server) |
| `docker-compose.host-network.yml` | `database` | `printfarmer-database` | Host network (PostgreSQL or SQL Server) |
| `docker-compose.override.yml` | `database` | `printfarmer-database` | Development overrides |

### **Multi-Database Testing** (databases.yml only)
For testing **multiple databases simultaneously** - unique container names required:

| Service | Container Name | Port | Purpose |
|---------|----------------|------|---------|
| `postgres` | `printfarmer-database-postgres` | 5432 | PostgreSQL testing instance |
| `sqlserver` | `printfarmer-database-sqlserver` | 1433 | SQL Server testing instance |
| `mysql` | `printfarmer-database-mysql` | 3306 | MySQL testing instance |

**Key Difference**: 
- **Main deployments**: One database at a time = generic container name
- **Testing file**: Multiple databases simultaneously = unique container names

## Environment-Based Configuration

### **SQL Server Production** (Recommended for your use case)
```bash
# Use SQL Server configuration
cp .env.sqlserver.example .env
docker compose -f docker-compose.microservices.yml up
```

Environment variables:
```env
DATABASE_IMAGE=mcr.microsoft.com/mssql/server:2022-latest
DATABASE_PORT=1433
MSSQL_SA_PASSWORD=YourStrong!Passw0rd
DB_PROVIDER=SqlServer
```
Note: The deploy script now generates a strong `MSSQL_SA_PASSWORD` automatically if you don't provide one. The generated password is written to the generated `.env` file and is displayed masked by the deploy script summary; ensure the `.env` file is stored securely (chmod 600).

### **PostgreSQL (Default)**
```bash
# Use PostgreSQL configuration  
cp .env.postgres.example .env
docker compose -f docker-compose.microservices.yml up
```

### **Database Testing**
```bash
# Test with all databases
docker compose -f docker-compose.yml -f docker-compose.databases.yml up
```

## Benefits

1. **Consistent Container Names**: Always `printfarmer-database` for main deployments
2. **Database Agnostic**: Same service name regardless of database engine
3. **Production Flexible**: Easy to switch between PostgreSQL and SQL Server
4. **No Orphaned Containers**: Consistent naming prevents conflicts
5. **Environment Driven**: Configure database via environment variables

## Migration Commands

```bash
# Clean up old inconsistent containers
./cleanup-docker.sh

# Deploy with SQL Server (production)
cp .env.sqlserver.example .env
docker compose -f docker-compose.microservices.yml up --remove-orphans

# Deploy with PostgreSQL (default)
cp .env.postgres.example .env  
docker compose -f docker-compose.microservices.yml up --remove-orphans
```

This approach gives you the flexibility to use SQL Server in production while maintaining consistent container naming across all deployments.

## Why Two Different Naming Patterns?

**The confusion explained:**

1. **Main Deployment Files** (`microservices.yml`, `host-network.yml`):
   - **Purpose**: Production/staging deployments with ONE database
   - **Container**: `printfarmer-database` (generic name)
   - **Benefit**: Same container name regardless of PostgreSQL vs SQL Server

2. **Testing File** (`databases.yml`):
   - **Purpose**: Run multiple database engines simultaneously for testing
   - **Containers**: `printfarmer-database-postgres`, `printfarmer-database-sqlserver`, `printfarmer-database-mysql`
   - **Benefit**: Can test all database providers at once without conflicts

**Example Scenario:**
```bash
# Production: Only SQL Server runs
docker compose -f docker-compose.microservices.yml up
# Creates: printfarmer-database (SQL Server)

# Testing: All databases run simultaneously  
docker compose -f docker-compose.databases.yml up
# Creates: printfarmer-database-postgres, printfarmer-database-sqlserver, printfarmer-database-mysql
```

**Bottom Line**: Two different use cases require two different naming strategies. The main deployments are consistent with generic names.