# Documentation Index

Use this page as the navigation entry point for current PrintFarmer
documentation. Historical implementation notes that no longer describe the
repository are intentionally omitted.

## Start Here

- [README](../README.md) - Project overview and quick start
- [Getting Started](./GETTING_STARTED.md) - Local setup
- [Development Guide](./DEVELOPMENT.md) - Code style, tests, local development,
  and contribution workflow
- [Architecture](./ARCHITECTURE.md) - System components and data flow
- [Architecture Flows](./ARCHITECTURE_FLOWS.md) - Detailed request and service
  interactions
- [Module Migration Pattern](./MODULE_MIGRATION_PATTERN.md) - Step-by-step
  playbook for extracting a `Farm.Modules.*` vertical-slice assembly out of
  `Farm.Web.Api`, worked through the `Farm.Modules.SmartPlug` pilot (#2036)
- [Features](./FEATURES.md) - User-facing feature overview
- [Troubleshooting](./TROUBLESHOOTING.md) - Common problems and remedies

## API And Data Contracts

- [API Documentation](./API.md) - REST and SignalR overview
- [API Reference](./API_REFERENCE.md) - Endpoint reference
- [API Documentation Guide](./API_DOCUMENTATION_GUIDE.md) - Maintaining API
  documentation
- [DTO Reference](./DTO_REFERENCE.md) - Shared data-transfer contracts
- [CSV Import Format](./CSV_IMPORT_FORMAT_DETAILED.md) - Printer CSV schema and
  examples
- [G-code Harvesting API](./GCODE_HARVESTING_API.md) - Harvesting endpoints
- [Obico ML API](./OBICO_ML_API.md) - Obico integration contract

## Deployment And Operations

- [Deployment Guide](./DEPLOYMENT.md) - Local and hosted deployment options
- [Deployment Quick Reference](./DEPLOYMENT_QUICK_REFERENCE.md) - Common
  deployment commands
- [Docker Deployment](./DOCKER_DEPLOYMENT.md) - Complete Docker guide
- [Docker Teardown](./DOCKER_TEARDOWN.md) - Safe deployment removal
- [Microservices Deployment](./MICROSERVICES_DEPLOYMENT_GUIDE.md) -
  Multi-service topology
- [Ubuntu Quickstart](./UBUNTU_DEPLOYMENT_QUICKSTART.md) - Ubuntu host setup
- [Deployment Hardware](./DEPLOYMENT_HARDWARE.md) - Capacity guidance
- [Local Docker Registry](./LOCAL_DOCKER_REGISTRY.md) - Private registry setup
- [pgAdmin Setup](./PGADMIN_SETUP.md) - PostgreSQL administration
- [Release Guide](./RELEASE_GUIDE.md) - Release process
- [iOS Beta Release Checklist](./IOS_BETA_RELEASE_CHECKLIST.md) - APNs/signing
  readiness gate and rollback controls for the operator-first iOS beta
- [Licensing And Source](./LICENSING_AND_SOURCE.md) - Corresponding source,
  SBOM, and provenance requirements

## Slicing And Workers

- [OrcaSlicer Integration](./ORCASLICER_INTEGRATION.md) - Architecture,
  profiles, pipeline, and diagnostics
- [Slicer Configuration](./SLICER_CONFIGURATION.md) - Slicer client setup
- [Slicer Runtime Settings](./SlicerRuntimeSettings.md) - Runtime settings
- [Slicer Service Metrics](./SLICER_SERVICE_METRICS.md) - Monitoring
- [Slicer Worker API Keys](./SLICER_WORKER_API_KEYS.md) - Worker authentication
- [Slicer Worker CI Security](./SLICER_WORKER_CI_SECURITY.md) - CI safeguards
- [Worker Authentication](./WORKER_AUTHENTICATION.md) - Worker trust model
- [Worker Sandboxing](./WORKER_SANDBOXING.md) - Runtime isolation
- [AppImage Preseed](./APPIMAGE_PRESEED.md) - Preseed asset production
- [AppImage Uploader Runbook](./APPIMAGE_UPLOADER_RUNBOOK.md) - Publishing
  preseed assets

## Features And Administration

- [Auto Dispatch](./AUTO_DISPATCH.md) - Dispatch modes and scoring
- [Job Queue Architecture](./JOB_QUEUE_ARCHITECTURE.md) - Queue services and
  workflows
- [Job State Machine](./job-state-machine.md) - Job lifecycle
- [Tagging System](./TAGGING_SYSTEM.md) - Polymorphic tagging
- [Controls Guide](./CONTROLS_GUIDE.md) - Printer controls
- [Discovery Troubleshooting](./DISCOVERY_SERVICE_TROUBLESHOOTING.md) -
  Printer discovery diagnostics
- [Settings Architecture](./SETTINGS_ARCHITECTURE.md) - Canonical admin and
  settings surface architecture
- [Operator Feature Gates](./OPERATOR_FEATURE_GATES.md) - Feature availability
- [Operator Native Push](./OPERATOR_NATIVE_PUSH.md) - Native push setup
- [Offline Write Replay](./OFFLINE_WRITE_REPLAY.md) - Offline mutation replay
- [Permission Model](./PERMISSION_MODEL.md) - Resource:action permissions,
  the `admin` implication, deny precedence, the `farm_admin` bypass, and how
  to add a new enforced permission
- [Role Permission Precedence](./ROLE_PERMISSION_PRECEDENCE.md) - Grant/deny
  precedence rule for `RolePermission.Granted`

## Frontend And Design

- [UI Overview](./UI.md) - Frontend pages and component architecture
- [Design System](./DESIGN_SYSTEM.md) - Tokens, themes, components, and
  accessibility
- [Frontend UI Components](./FRONTEND_UI_COMPONENTS.md) - Component reference
- [UI Styling Index](./UI_STYLING_INDEX.md) - Styling navigation
- [UI Reorganization Requirements](./design/ui-reorganization-requirements.md)
  - Historical design requirements
- [Settings Redesign V2](./design/settings-redesign-v2.md) - Historical
  settings design proposal

## Testing And CI

- [CI](./CI.md) - Affected-test selection and required checks
- [Contract Drift Gate](./CONTRACT_DRIFT_GATE.md) - Wire-contract corpus drift check and reviewed exception allowlist
- [Testing Guidelines](./TESTING_GUIDELINES.md) - Deployment script test
  guidance
- [Testing Patterns](./TESTING_PATTERNS.md) - Reusable backend test patterns
- [Test Documentation Index](./TEST_DOCUMENTATION_INDEX.md) - Test
  documentation navigation and documentation-health commands
- [Deployment Testing](./DEPLOYMENT_TESTING.md) - Deployment test suites
- [Deployment Testing Checklist](./DEPLOYMENT_TESTING_CHECKLIST.md) - Required
  validation for deployment changes
- [Error Recovery](./ERROR_RECOVERY.md) - Recovery and resilience validation

## Supporting References

- [Competitive Analysis](./COMPETITIVE_ANALYSIS.md)
- [Image Naming Convention](./IMAGE_NAMING_CONVENTION.md)
- [Installation: .NET SDK](./DOTNET_SDK_INSTALLATION.md)
- [Installation: ruamel.yaml](./INSTALL_RUAMEL_YAML.md)
- [ruamel.yaml Dependency](./RUAMEL_YAML_DEPENDENCY.md)
- [Issue Operating Convention](./ISSUE_OPERATING_CONVENTION.md)
