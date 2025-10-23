# Container Naming Standardization

## Overview
This document outlines the standardization of all Docker container names to use the "printfarmer-" prefix across all Docker Compose files.

## Problem Addressed
Previously, containers had inconsistent naming:
- Some used "pfarm-" prefix (from project directory name)
- Some used "printfarmer-" prefix 
- Some had no explicit container names, relying on Docker Compose auto-generation

This caused confusion and made container management difficult across different deployment modes.

## Solution
All containers now use explicit `container_name` entries with the "printfarmer-" prefix.

## Standardized Container Names

### Core Application Services
- **printfarmer-api** - Main .NET Core API backend
- **printfarmer-frontend** - React frontend with Nginx
- **printfarmer-redis** - Redis cache and SignalR backplane
- **printfarmer-database** - Generic database service (PostgreSQL/SQL Server/MySQL)

### Worker Services
- **printfarmer-orcaslicer-worker** - OrcaSlicer distributed slicing worker
- **printfarmer-prusaslicer-worker** - PrusaSlicer distributed slicing worker

### Proxy/Load Balancer
- **printfarmer-nginx-proxy** - Nginx reverse proxy for production

### Database Testing Services (databases.yml only)
- **printfarmer-database-postgres** - PostgreSQL for testing
- **printfarmer-database-sqlserver** - SQL Server for testing  
- **printfarmer-database-mysql** - MySQL for testing

### Monitoring & Observability Services
- **printfarmer-prometheus** - Metrics collection
- **printfarmer-grafana** - Metrics visualization
- **printfarmer-elasticsearch** - Log storage
- **printfarmer-logstash** - Log processing
- **printfarmer-kibana** - Log visualization
- **printfarmer-redis-exporter** - Redis metrics exporter
- **printfarmer-otel-collector** - OpenTelemetry collector
- **printfarmer-jaeger** - Distributed tracing

### Security Services
- **printfarmer-vault** - HashiCorp Vault for secrets management

## Files Updated

### Main Deployment Files
- **docker-compose.yml** - Added container names for all services
- **docker-compose.microservices.yml** - Added container names for all services
- **docker-compose.host-network.yml** - Added container names for all services

### Already Compliant Files
- **docker-compose.databases.yml** - Already had correct specific naming for testing
- **docker-compose.monitoring.yml** - Already had correct "printfarmer-" naming
- **docker-compose.telemetry.yml** - Already had correct "printfarmer-" naming
- **docker-compose.security.yml** - Already had correct "printfarmer-" naming
- **docker-compose.override.yml** - Already had correct database naming

## Benefits

1. **Consistent Naming**: All containers follow the same naming convention
2. **Clear Identification**: Easy to identify PrintFarmer containers in `docker ps`
3. **Predictable Container Names**: No dependency on project directory name
4. **Cross-Platform Consistency**: Same container names on all deployment environments
5. **Easier Debugging**: Logs and monitoring use consistent container identifiers

## Deployment Impact

### No Breaking Changes
- Container names are now explicit, so they won't change based on directory name
- All internal service-to-service communication uses service names, not container names
- External port mappings remain unchanged

### Migration Path
If you have existing containers with the old names:
```bash
# Stop old containers
docker compose down

# Remove old containers if needed
docker container prune

# Start with new naming
docker compose up -d
```

## Verification

To verify all containers use the correct naming:
```bash
docker ps --filter "name=printfarmer-" --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
```

All PrintFarmer containers should be listed with "printfarmer-" prefix.

## Database Naming Strategy Integration

This standardization aligns with the database naming strategy:
- **Main deployments**: Generic "printfarmer-database" for single database deployments
- **Testing**: Specific names like "printfarmer-database-postgres" for multi-database testing
- **Consistency**: All names follow the "printfarmer-" prefix pattern

## Future Considerations

- All new services should follow the "printfarmer-{service-name}" naming pattern
- Container names should be explicit in all Docker Compose files
- Service names (used for internal communication) can remain simple (e.g., "api", "redis")
- Container names are for external identification and management