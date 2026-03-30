# Maintenance V3 Beads

## Epic: Maintenance Plans V3 Redesign
- type: epic
- priority: P1
- labels: maintenance, v3-redesign
- description: Complete redesign of maintenance feature. Global task catalog with shared reusable tasks, plan templates deployed as schedules per printer, printer model feature flags for smart task scoping, required parts per task, dedicated category management. Replaces disconnected flat MaintenanceSchedule + hierarchical MaintenancePlan models with unified Task → Plan → Schedule architecture.

## Phase 0: Add printer model feature flags
- type: task
- priority: P1
- labels: maintenance, v3-redesign, backend, seed-data
- description: Add 9 new boolean feature flags to PrinterModel entity (HasCarbonFilter, HasHepaFilter, HasBowdenTube, HasPtfeLiner, HasLinearRails, HasLeadScrews, HasToolchanger, HasFilamentCutter, HasHeatedChamber). Update PrinterModelSeedDto, DataSeedService mapping, printer-models.yaml with researched values for all 43 models, and create EF migrations for PostgreSQL + SQL Server.

## Phase 1a: Create global MaintenanceTask entity and catalog
- type: task
- priority: P1
- labels: maintenance, v3-redesign, backend, data-model
- description: Create standalone MaintenanceTask entity (decoupled from plans) with fields for name, description, category, interval, scope rules (nullable bools matching printer model features), estimated duration, and difficulty. Create EF config, repository, and CRUD controller. Tasks are global shared catalog entries referenced by plans via many-to-many join.

## Phase 1b: Create TaskRequiredPart and PlanTask join entities
- type: task
- priority: P1
- labels: maintenance, v3-redesign, backend, data-model
- description: Create TaskRequiredPart join entity linking MaintenanceTask to MaintenanceComponent (parts) with quantity field. Create PlanTask join entity linking MaintenancePlan to MaintenanceTask with overridable interval. Update EF configurations and create migrations.

## Phase 1c: Create PrinterMaintenanceSchedule deployment entity
- type: task
- priority: P1
- labels: maintenance, v3-redesign, backend, data-model
- description: Create PrinterMaintenanceSchedule entity representing a plan deployed to a specific printer. Links Plan → Printer with fields for deployment date, last performed, next due, status. Rewire MaintenanceLog and MaintenanceAlert to reference schedules. Create deployment API endpoints.

## Phase 2: Seed data - tasks, parts, and plans YAML
- type: task
- priority: P1
- labels: maintenance, v3-redesign, seed-data
- description: Create 3 new YAML seed files (maintenance-parts.yaml, maintenance-tasks.yaml, maintenance-plans.yaml). Implement auto-assembly algorithm that matches task scope rules against printer model features to build plans. Replace single maintenance-schedules.yaml with the new structured seed data.

## Phase 3: API layer - task catalog, plan, and schedule APIs
- type: task
- priority: P1
- labels: maintenance, v3-redesign, backend, api
- description: Implement Task catalog CRUD API, updated Plan API with task references, Schedule deployment API (deploy plan to printer), and dedicated Category management API. Rewire alert engine and upcoming maintenance endpoint to use new Schedule entity.

## Phase 4a: Frontend - Task catalog and Plans redesign
- type: task
- priority: P1
- labels: maintenance, v3-redesign, frontend
- description: Create Tasks tab with global task catalog CRUD, redesign Plans tab to show plan templates with task composition, add schedule deployment flow from plan to printer. Update TypeScript types, API service, and React Query hooks.

## Phase 4b: Frontend - Parts inventory and category management
- type: task
- priority: P1
- labels: maintenance, v3-redesign, frontend
- description: Enhance Parts Inventory tab with task-parts associations. Create dedicated Category management page/section. Remove Components tab (redundant with new architecture). Update dashboard tab structure.

## Phase 5: Migration and cleanup
- type: task
- priority: P2
- labels: maintenance, v3-redesign, cleanup
- description: Create data migration to convert existing MaintenanceSchedule records to new PrinterMaintenanceSchedule entities. Deprecate flat MaintenanceSchedule model. Clean up dead code, unused endpoints, and orphaned UI components.
