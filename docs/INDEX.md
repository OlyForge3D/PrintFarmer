# Documentation Index

This page catalogs all PrintFarmer documentation files and their purposes.

## Core Documentation (Start Here!)

These are the main documentation files you should read first:

- **[README.md](../README.md)** - Project overview and quick start
- **[GETTING_STARTED.md](./GETTING_STARTED.md)** - Local development setup
- **[ARCHITECTURE.md](./ARCHITECTURE.md)** - System design and components
- **[ARCHITECTURE_FLOWS.md](./ARCHITECTURE_FLOWS.md)** - Deep-dive on request flows and system interactions
- **[DEPLOYMENT.md](./DEPLOYMENT.md)** - Docker and local deployment options
- **[API.md](./API.md)** - REST endpoints and SignalR reference
- **[FEATURES.md](./FEATURES.md)** - Feature documentation (locations, discovery, CSV, etc.)
- **[UI.md](./UI.md)** - Frontend components and pages
- **[DEVELOPMENT.md](./DEVELOPMENT.md)** - Code style, testing, and contribution guide
- **[TROUBLESHOOTING.md](./TROUBLESHOOTING.md)** - Common issues and solutions

## Project Management

- **[CONTRIBUTING.md](../CONTRIBUTING.md)** - Contribution guidelines
- **[SECURITY.md](../SECURITY.md)** - Security policy and reporting

## Advanced Topics

### Development & Testing

- **[TEST_COVERAGE_ANALYSIS.md](./TEST_COVERAGE_ANALYSIS.md)** - Test coverage status and goals
- **[TESTING_GUIDELINES.md](./TESTING_GUIDELINES.md)** - Testing patterns and best practices
- **[TESTING_SETUP_WIZARD_INTEGRATION.md](./TESTING_SETUP_WIZARD_INTEGRATION.md)** - Testing the setup wizard

### API Reference

- **[DTO_REFERENCE.md](./DTO_REFERENCE.md)** - Data transfer object definitions (PrinterFastDto, etc.)
- **[CSV_IMPORT_FORMAT_DETAILED.md](./CSV_IMPORT_FORMAT_DETAILED.md)** - CSV import format specification

### Performance & Optimization

- **[OPTIMIZATION_NOTES.md](./OPTIMIZATION_NOTES.md)** - Printer list performance fix and caching strategy
- **[SLICER_SERVICE_METRICS.md](./SLICER_SERVICE_METRICS.md)** - Slicer service performance metrics
- **[HARVEST_METADATA_OPTIMIZATION.md](./HARVEST_METADATA_OPTIMIZATION.md)** - Gcode harvest optimization

### Features Deep-Dives

- **[3D_MODEL_TAGGING_SYSTEM.md](./3D_MODEL_TAGGING_SYSTEM.md)** - Model tagging architecture
- **[ORCASLICER_PROFILES_HIERARCHY.md](./ORCASLICER_PROFILES_HIERARCHY.md)** - OrcaSlicer profile organization
- **[SLICER_LIBRARY_ARCHITECTURE.md](./SLICER_LIBRARY_ARCHITECTURE.md)** - Slicer integration architecture
- **[PRINTER_DISCOVERY_ARCHITECTURE.md](./PRINTER_DISCOVERY_ARCHITECTURE.md)** - Network discovery system
- **docs/NGINX_HEALTH_ENDPOINT_FIX.md** - Nginx health check configuration
- **docs/MICROSERVICES_NGINX_FIX_STEPS.md** - Nginx fixes for microservices

### Testing & Quality

- **docs/TESTING_GUIDELINES.md** - Testing best practices
- **docs/TESTING_PATTERNS.md** - Common testing patterns
- **docs/TESTING_SETUP_WIZARD_INTEGRATION.md** - Testing setup wizard feature
- **docs/QUICK_TEST_IMPLEMENTATION_GUIDE.md** - Quick test implementation
- **docs/TEST_COVERAGE_ANALYSIS.md** - Code coverage analysis
- **docs/PHASE_7_AUTHENTICATION_TEST_PLAN.md** - Authentication testing
- **docs/PHASE_7_AUTHENTICATION_SUMMARY.md** - Authentication testing results

### OrcaSlicer Integration

- **docs/ORCASLICER_ASSETS_QUICK_START.md** - OrcaSlicer assets setup
- **docs/ORCASLICER_ASSETS_REFERENCE.md** - OrcaSlicer asset reference
- **docs/ORCASLICER_BINARY_OPTIMIZATION.md** - OrcaSlicer binary optimization
- **docs/ORCA_WIZARD_FLOW_DIAGRAM.md** - OrcaSlicer wizard flow
- **docs/ORCA_IMPORT_WIZARD_IMPLEMENTATION.md** - Implementing profile import
- **docs/IMPORT_OFFICIAL_PROFILES_FEATURE.md** - Official profile import feature
- **docs/SLICER_LIBRARY_ARCHITECTURE.md** - Slicer library design
- **docs/SLICER_SERVICE_METRICS.md** - Slicer service monitoring
- **docs/SLICER_WORKER_API_KEYS.md** - Slicer worker authentication
- **docs/SLICER_WORKER_CI_SECURITY.md** - Slicer worker security
- **src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_1/.copilot-instructions.md** - OrcaSlicer worker setup

### Features

- **docs/SETUP_WIZARD_DEPLOYMENT_INTEGRATION.md** - Setup wizard deployment
- **docs/EXTERNAL_STORAGE_DATA_PERSISTENCE.md** - External storage configuration
- **docs/THUMBNAIL_SUPPORT_IMPLEMENTATION.md** - Thumbnail generation
- **docs/THUMBNAIL_GENERATION.md** - Thumbnail feature details
- **docs/STORAGE_PATH_CONFIGURATION.md** - Storage path setup
- **docs/PRINTER_DISCOVERY_ARCHITECTURE.md** - Network discovery system
- **docs/DISCOVERY_CANCELLATION_TOKEN_DESIGN.md** - Discovery service design
- **docs/DISCOVERY_SERVICE_TROUBLESHOOTING.md** - Discovery troubleshooting
- **docs/CSV_IMPORT_FORMAT_DETAILED.md** - CSV import format reference

### Job & Harvest System

- **docs/JOB_STATE_MACHINE.md** - Job state diagram
- **docs/HARVEST_ERROR_TRACKING.md** - Error tracking in harvester
- **docs/HARVEST_FILESADDED_COUNTER_FIX.md** - File counting fix
- **docs/HARVEST_COMPLETION_BUG_FIX.md** - Completion detection fix
- **docs/REDIS_HARVEST_QUEUE_IMPLEMENTATION.md** - Redis queue system

### Monitoring & Observability

- **docs/HEALTH_CHECK_DISCOVERY_IMPLEMENTATION.md** - Health check implementation
- **docs/HEALTH_CHECK_URI_PARSING_FIX.md** - Health check URI parsing
- **docs/SQL_SERVER_HEALTH_TROUBLESHOOTING.md** - SQL Server health checks
- **docs/DATABASE_HEALTH_CHECK_IMPROVEMENTS.md** - Database health monitoring
- **docs/CORRELATION_ID_PROPAGATION.md** - Request tracing
- **docs/DEPLOY_SCRIPT_HEALTH_CHECKS.md** - Deployment health verification

### Controls & Settings

- **docs/CONTROLS_GUIDE.md** - Printer controls documentation
- **docs/SETTINGS_ADVANCED_UI_PLAN.md** - Advanced settings UI
- **docs/ENUM_BACKEND_SELECTOR.md** - Backend selection enum

### Tools & Utilities

- **docs/DOTNET_SDK_INSTALLATION.md** - .NET SDK setup
- **docs/RELEASE_GUIDE.md** - Release process
- **docs/DEPLOYMENT_CONFIG_PERSISTENCE.md** - Configuration persistence
- **docs/DEPLOYMENT_HOST_NETWORK_ANALYSIS.md** - Network mode analysis
- **docs/DEPLOYMENT_SCRIPT_IMPROVEMENTS.md** - Script improvements
- **docs/FILE_CONSISTENCY_COMPLETE.md** - File consistency verification
- **docs/NPM_WORKSPACES_INTEGRATION_COMPLETE.md** - npm workspaces setup

### Improvements & Enhancements

- **docs/improvements/harvest-table-column-customization.md** - Harvest table customization

## Historical/Archived

These files document completed work and may be relevant for understanding past decisions:

- **archived/** - All archived documentation
- **PHASE_1_COMPLETION_REPORT.md** - Phase 1 completion status
- **PHASE2_PR.md** - Phase 2 pull request summary
- **PRODUCTION_READINESS.md** - Production readiness checklist
- **CODE_CONSOLIDATION_PHASE2.md** - Code consolidation work

## Technical Deep Dives

### Architecture & Design

- **CODE_FLOW.md** - Application code flow
- **SIGNALR_WIRING_TRACE.md** - SignalR connection tracing
- **HEARTBEAT_ARCHITECTURE.md** - Heartbeat system design
- **PRUSALINK_STATUS_FLOW_ANALYSIS.md** - PrusaLink integration flow
- **PRINTER_LIST_PERFORMANCE_FIX.md** - Performance optimization

### Refactoring & Consolidation

- **CONSOLIDATION_OPPORTUNITIES.md** - Code consolidation opportunities
- **API_ARCHITECTURE_REFACTORING_PLAN.md** - API refactoring plans
- **API_IMPLEMENTATION_PROGRESS.md** - API implementation status
- **BACKEND_CLIENT_MIGRATION_PLAN.md** - Client migration strategy
- **BACKEND_CAPABILITY_ABSTRACTION.md** - Capability abstraction design
- **BACKEND_CAPABILITY_FACTORY_USAGE.md** - Factory pattern usage
- **BACKEND_CAPABILITIES_TOOLHEAD_REFACTORING.md** - Toolhead refactoring
- **BACKEND_PLUGIN_ARCHITECTURE.md** - Plugin system design
- **INTERFACE_DOCUMENTATION_SUMMARY.md** - Interface documentation
- **INTERFACE_REFACTORING_PLAN.md** - Interface refactoring strategy
- **PLUGIN_SYSTEM_REFACTORING.md** - Plugin system improvements
- **PLUGIN_SYSTEM_FIXES_SUMMARY.md** - Plugin fixes summary

### Migration & Cleanup

- **REACT_MIGRATION_README.md** - React migration documentation
- **DOCUMENTATION_AUDIT_AND_CLEANUP.md** - Documentation organization
- **DOCKER_REORGANIZATION_SUMMARY.md** - Docker structure reorganization
- **DOCKER_SCRIPTS_REFACTORING.md** - Script refactoring
- **CONTAINER_NAMING_STANDARDIZATION.md** - Container naming conventions
- **PROJECT_STRUCTURE_REORGANIZATION_ANALYSIS.md** - Project structure changes
- **WARNING_CLEANUP_SUMMARY.md** - Compiler warning cleanup

### Integration & Third-Party

- **OCTOPRINT_CLIENT_AUDIT.md** - OctoPrint client audit
- **OCTOPRINT_CLIENT_QUICK_REFERENCE.md** - OctoPrint quick reference
- **octoprint-vs-moonraker-parity.md** - API parity comparison

## Feature-Specific Documentation

### Location System

- **LOCATION_SYSTEM_IMPLEMENTATION.md** - Location system design
- **LOCATION_SYSTEM_UI_SUMMARY.md** - Location UI overview
- **PRINTER_LOCATION_DRAG_DROP_GUIDE.md** - Drag-and-drop assignment guide

### Testing

- **TEST_COVERAGE_IMPROVEMENT_PLAN.md** - Coverage improvement roadmap
- **TESTING_ANALYSIS_SUMMARY.md** - Analysis of test targets
- **TESTING_ANALYSIS_HIGH_IMPACT_TARGETS.json** - High-impact test targets
- **TESTING_IMPLEMENTATION_EXAMPLES.md** - Test implementation examples
- **TESTING_TARGETS_QUICK_REFERENCE.md** - Quick test reference

### UI & Styling

- **src/Web/ReactApp/UI_COMPONENTS_GUIDE.md** - Component guide
- **src/Web/ReactApp/COLOR_SYSTEM_GUIDE.md** - Color system documentation
- **src/Web/ReactApp/START_HARVEST_REDESIGN_PLAN.md** - Harvest UI redesign

## Quick Lookups

- **DEPLOY_DOCKER_FULL_OPTIONS_SUMMARY.md** - Docker deployment options
- **DEPLOYMENT_ARCHITECTURES.md** - Deployment architecture options

---

## How to Use This Index

1. **New to PrintFarmer?** Start with:
   - [README.md](../README.md)
   - [GETTING_STARTED.md](./GETTING_STARTED.md)
   - [ARCHITECTURE.md](./ARCHITECTURE.md)

2. **Want to contribute?**
   - [DEVELOPMENT.md](./DEVELOPMENT.md)
   - [CONTRIBUTING.md](../CONTRIBUTING.md)

3. **Deploying to production?**
   - [DEPLOYMENT.md](./DEPLOYMENT.md)
   - [DEPLOYMENT_READINESS_CHECK.md](./docs/DEPLOYMENT_READINESS_CHECK.md)

4. **Having issues?**
   - [TROUBLESHOOTING.md](./TROUBLESHOOTING.md)
   - Check specific feature documentation

5. **Need quick answers?**
   - [QUICK_REFERENCE.md](./QUICK_REFERENCE.md)

---

**Note**: This documentation is organized into core docs (in `/docs/`) and root-level files for project management (CONTRIBUTING.md, SECURITY.md). The `/archived/` folder contains historical documentation for reference.
