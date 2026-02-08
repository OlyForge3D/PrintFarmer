# PrintFarmer Docker Configuration

This directory contains all Docker-related files for PrintFarmer, organized for maintainability and deployment automation.

## Directory Structure

```
scripts/docker/
├── README.md                 # This file
├── dockerfiles/             # All Dockerfile definitions  
│   ├── Dockerfile           # Main application
│   ├── Dockerfile.api       # API service
│   ├── Dockerfile.frontend* # Frontend variants
│   ├── Dockerfile.orca*     # OrcaSlicer worker variants
│   ├── Dockerfile.prusa*    # (PrusaSlicer worker variants removed)
│   └── Dockerfile.slicer-base # Base slicer image
├── compose-templates/       # Docker Compose template files
│   ├── docker-compose.yml                # Main template
│   ├── docker-compose.override.yml       # Development overrides
│   ├── docker-compose.databases.yml      # Database services
│   ├── docker-compose.monitoring.yml     # Monitoring stack
│   ├── docker-compose.telemetry.yml      # Telemetry/observability
│   ├── docker-compose.security.yml       # Security configurations
│   └── docker-compose.registry.yml       # Local registry setup
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
- **Optional services**: Workers, security, registry, discovery
- **Environment-specific**: Development overrides, production optimizations

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