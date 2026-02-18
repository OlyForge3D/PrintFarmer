# PrintFarmer Docker Configuration

This directory contains all Docker-related files for PrintFarmer, organized for maintainability and deployment automation.

## Directory Structure

```
scripts/docker/
├── README.md                 # This file
├── dockerfiles/             # All Dockerfile definitions
│   ├── Dockerfile           # Main application
│   ├── Dockerfile.api       # API service
│   ├── Dockerfile.base-*    # Base images (aspnet, nginx, node, sdk, ubuntu, etc.)
│   ├── Dockerfile.frontend  # Frontend build
│   ├── Dockerfile.multistage # Multi-stage consolidated build
│   ├── Dockerfile.nginx-proxy # Nginx reverse proxy
│   └── Dockerfile.printer-discovery # Printer discovery service
├── compose-templates/       # Docker Compose template files
│   ├── docker-compose.yml                # Main template (base services)
│   ├── docker-compose.common.yml         # Shared healthcheck anchors
│   ├── docker-compose.database.postgres.yml  # PostgreSQL database config
│   ├── docker-compose.database.sqlserver.yml  # SQL Server database config
│   ├── docker-compose.discovery.yml      # Printer discovery service
│   ├── docker-compose.monitoring.yml     # Monitoring stack (Prometheus + Grafana)
│   ├── docker-compose.monitoring.lite.yml # Lightweight monitoring (no Grafana)
│   ├── docker-compose.orcaslicer-worker.yml # OrcaSlicer worker service
│   ├── docker-compose.pgadmin.yml        # pgAdmin web UI
│   ├── docker-compose.registry.yml       # Local Docker registry
│   ├── docker-compose.security.yml       # Security configurations
│   ├── docker-compose.slicer-host.yml    # Slicer host service
│   └── docker-compose.telemetry.yml      # OpenTelemetry + Jaeger
└── configs/                 # Configuration files
    ├── docker-entrypoint-config.sh       # Container initialization
    ├── otel-collector-config.yaml        # OpenTelemetry collector
    ├── prometheus.yml                     # Prometheus monitoring
    └── security-config.json              # Security configurations
```

## Deployment Process

The deployment script (`../deploy-docker.sh`) now:

1. **Analyzes** the target deployment architecture
2. **Copies** the appropriate Dockerfiles to the repository root
3. **Generates** a single `docker-compose.yml` tailored to the configuration
4. **Copies** required configuration files to the root
5. **Deploys** using the generated configuration
6. **Cleans up** by removing generated files after deployment

## Architecture Overview

### Standard Deployment
- Uses `Dockerfile.api`, `Dockerfile.frontend`, worker Dockerfiles
- Combines multiple compose templates into comprehensive configuration
- Supports optional services (monitoring, telemetry, etc.)

## Configuration Management

The deployment script intelligently selects and combines:
- **Base services**: API, frontend, nginx-proxy
- **Database**: PostgreSQL (default) or SQL Server - always a separate container
- **Observability**: Prometheus + Grafana (monitoring), OpenTelemetry + Jaeger (telemetry) - enabled by default
- **Optional services**: Workers, security, registry, discovery, pgAdmin

**Note**: SQLite is NOT supported for Docker deployments. All Docker deployments require a proper database container (PostgreSQL or SQL Server) for production reliability.

## Benefits of This Structure

1. **Clean Repository**: Root directory no longer cluttered with Docker files
2. **Deterministic Deployments**: Each deployment gets exactly the files it needs
3. **Easy Maintenance**: Related files grouped logically
4. **Architecture Clarity**: Clear separation between deployment modes
5. **Conflict Prevention**: No leftover files from different architectures
6. **Version Control**: Better tracking of changes to specific architectures

## Usage

The deployment script handles all file management automatically:

```bash
# Deploy microservices architecture
./scripts/deploy-docker.sh --architecture microservices

# Deploy monolithic mode
./scripts/deploy-docker.sh --architecture monolithic
```

Generated files now stay on disk after deployment for easier troubleshooting; use `--cleanup-generated` if you want the script to remove them automatically.