# PrintFarmer Docker Configuration

This directory contains all Docker-related files for PrintFarmer, organized for maintainability and deployment automation.

## Directory Structure

```
scripts/docker/
├── README.md                 # This file
├── dockerfiles/             # All Dockerfile definitions  
│   ├── Dockerfile           # Main monolithic application
│   ├── Dockerfile.api       # API service (microservices)
│   ├── Dockerfile.frontend* # Frontend variants
│   ├── Dockerfile.orca*     # OrcaSlicer worker variants
│   ├── Dockerfile.prusa*    # (PrusaSlicer worker variants removed)
│   └── Dockerfile.slicer-base # Base slicer image
├── compose-templates/       # Docker Compose template files
│   ├── docker-compose.yml                # Main template
│   ├── docker-compose.microservices.yml  # Microservices architecture
│   ├── docker-compose.host-network.yml   # Host network mode
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

1. **Analyzes** the target deployment architecture (monolithic, microservices, host-network)
2. **Copies** the appropriate Dockerfiles to the repository root
3. **Generates** a single `docker-compose.yml` tailored to the architecture
4. **Copies** required configuration files to the root
5. **Deploys** using the generated configuration
6. **Cleans up** by removing generated files after deployment

## Architecture-Specific Files

### Monolithic Architecture
- Uses `Dockerfile` (single container with all services)
- Generates minimal `docker-compose.yml` with database and main app

### Microservices Architecture  
- Uses `Dockerfile.api`, `Dockerfile.frontend`, worker Dockerfiles
- Combines multiple compose templates into comprehensive configuration
- Supports optional services (monitoring, telemetry, etc.)

### Host Network Mode
- Uses microservices Dockerfiles with host networking
- Special frontend configuration for host network access
- Direct host network access for printer discovery

## Configuration Management

The deployment script intelligently selects and combines:
- **Base services**: API, frontend, database, Redis
- **Optional services**: Workers, monitoring, telemetry, security
- **Environment-specific**: Development overrides, production optimizations
- **Database providers**: PostgreSQL, SQL Server, MySQL, SQLite

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

# Deploy host network mode
./scripts/deploy-docker.sh --architecture host-network  

# Deploy monolithic mode
./scripts/deploy-docker.sh --architecture monolithic
```

Generated files now stay on disk after deployment for easier troubleshooting; use `--cleanup-generated` if you want the script to remove them automatically.