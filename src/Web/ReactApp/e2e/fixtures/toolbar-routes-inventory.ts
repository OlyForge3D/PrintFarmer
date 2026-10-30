// Explicit pathname fixtures for the real routed App. No catch-all API responses.
// Shapes mirror services/{project,maintenance,maintenancePlan,partsInventory}Service
// and the catalog/Spoolman/location DTOs in src/types.
const timestamp = '2026-10-30T12:00:00Z';
const manufacturer = { id: 'mfg-1', name: 'OlyForge', isActive: true };
const location = {
  id: 'loc-1', name: 'Workshop', path: '/Workshop', parentId: null,
  depth: 0, sortOrder: 0, printerCount: 1, totalPrinterCount: 1,
  createdAt: timestamp, modifiedAt: timestamp, isActive: true,
};
const printer = {
  id: 'printer-1', name: 'Workshop printer', isOnline: true, isEnabled: true,
  state: 'Idle', backend: 'Moonraker', inMaintenance: false, location,
  model: { id: 'model-1', name: 'CoreXY 250', manufacturer },
  toolheads: [], tags: [], capabilities: {},
};
const filamentType = {
  id: 'type-1', name: 'Workshop PLA', defaultTemperatures: { hotend: 210, bed: 60 },
  isAbrasive: false, needsEnclosure: false, defaultPricePerKg: 20, defaultDensity: 1.24,
};
const spoolmanFilament = {
  id: 1, name: 'Workshop blue PLA', material: 'PLA', vendor: 'OlyForge',
  colorHex: '2463eb', diameter: 1.75, density: 1.24, weight: 1000,
  price: 20, settingsExtruderTemp: 210, settingsBedTemp: 60,
};
const part = {
  id: 'part-1', sku: 'BRACKET-01', name: 'Workshop bracket', description: 'Printed bracket',
  modelFileRef: null, defaultBinId: 'bin-1', defaultBinCode: 'BIN-01',
  defaultBinName: 'Bracket bin', onHand: 2, reorderPoint: 10,
  needsReorder: true, isActive: true, createdAt: timestamp, updatedAt: timestamp,
};
const task = {
  id: 'task-1', taskName: 'Inspect nozzle', category: 'Nozzle',
  description: 'Inspect the nozzle for wear', intervalDays: 30, intervalHours: null,
  estimatedDurationMinutes: 10, priority: 2, isActive: true, isDefault: false,
  taskComponents: [], createdAt: timestamp, updatedAt: timestamp,
};
const component = {
  id: 'component-1', name: 'Workshop spare nozzle', category: 'Nozzle', sku: 'NOZZLE-04',
  description: 'Replacement nozzle', unitCost: 12, supplier: 'OlyForge',
  inStock: 0, minimumStock: 2, createdAt: timestamp, updatedAt: timestamp,
};
const catalogComponent = { manufacturerId: 'mfg-1', manufacturerName: 'OlyForge', description: 'Workshop component' };

export function inventoryRouteSeeds(catalogFilaments = false): Record<string, unknown> {
  return {
    '/api/printers': [printer],
    '/api/locations': [location],
    '/api/locations/tree': [{ ...location, children: [] }],
    '/api/locations/loc-1/printers': [{
      printerId: printer.id, printerName: printer.name, locationId: location.id,
      locationName: location.name, isOnline: true, status: 'Idle', currentJobName: null,
    }],
    '/api/catalog/manufacturers': [manufacturer],
    '/api/catalog/manufacturers/by-context/Printers': {
      withItems: [{ ...manufacturer, itemCount: 1 }], withoutItems: [],
    },
    '/api/catalog/manufacturers/by-context/Toolheads': {
      withItems: [{ ...manufacturer, itemCount: 1 }], withoutItems: [],
    },
    '/api/catalog/manufacturers/by-context/Extruders': {
      withItems: [{ ...manufacturer, itemCount: 1 }], withoutItems: [],
    },
    '/api/catalog/manufacturers/by-context/Hotends': {
      withItems: [{ ...manufacturer, itemCount: 1 }], withoutItems: [],
    },
    '/api/catalog/manufacturers/by-context/Nozzles': {
      withItems: [{ ...manufacturer, itemCount: 1 }], withoutItems: [],
    },
    '/api/catalog/printer-models': [{
      id: 'model-1', name: 'CoreXY 250', manufacturerId: 'mfg-1',
      manufacturerName: 'OlyForge', maxX: 250, maxY: 250, maxZ: 250,
      motionType: 'CoreXY', defaultBackend: 'Moonraker', supportedFilamentTypes: [],
    }],
    '/api/catalog/toolheads': [{ ...catalogComponent, id: 'toolhead-1', name: 'Workshop toolhead' }],
    '/api/catalog/extruders': [{ ...catalogComponent, id: 'extruder-1', name: 'Workshop extruder', gearRatio: '5:1', isDirectDrive: true }],
    '/api/catalog/hotends': [{ ...catalogComponent, id: 'hotend-1', name: 'Workshop hotend', maxTemp: 300, isHighFlow: false, nozzleInterface: 'V6' }],
    '/api/catalog/nozzles': [{
      ...catalogComponent, id: 'nozzle-1', name: 'Workshop nozzle', diameter: 0.4,
      maxTemp: 300, nozzleType: 'Brass', hardnessOverride: 'Auto', isHardened: false,
      nozzleInterface: 'V6', nozzleMaterialId: 'brass-1',
    }],
    '/api/catalog/nozzle-materials': [{ id: 'brass-1', name: 'Brass', isHardened: false, defaultMaxTemp: 300, isBuiltIn: true }],
    // The catalog requests a paged envelope; Clusters requests the list contract.
    '/api/filament-types': catalogFilaments
      ? { items: [filamentType], page: 1, pageSize: 50, totalCount: 51, totalPages: 2 }
      : [filamentType],
    '/api/material-clusters': [{
      id: 'cluster-1', name: 'Workshop PLA equivalents', description: 'Equivalent PLA materials',
      createdAt: timestamp, updatedAt: timestamp,
      members: [{ filamentTypeId: filamentType.id, filamentTypeName: filamentType.name, addedAt: timestamp }],
    }],
    '/api/spoolman/health': { configured: true, success: true },
    '/api/spoolman/config': { baseUrl: 'https://spoolman.invalid', enabled: true },
    '/api/spoolman/filter-options': { materials: ['PLA'], vendors: ['OlyForge'], locations: ['Workshop'] },
    '/api/spoolman/filaments/filter-options': { materials: ['PLA'], vendors: ['OlyForge'] },
    '/api/spoolman/filaments': { items: [spoolmanFilament], totalCount: 51 },
    '/api/spoolman/spools': {
      items: [{
        id: 1, name: 'Workshop spool', filamentId: 1, filamentName: spoolmanFilament.name,
        material: 'PLA', vendor: 'OlyForge', colorHex: '2463eb', initialWeightG: 1000,
        remainingWeightG: 500, usedWeightG: 500, location: 'Workshop', archived: false,
      }],
      totalCount: 51,
    },
    '/api/projects': [{
      id: 'project-1', name: 'Workshop enclosure', description: 'Enclosure print project',
      status: 'InProgress', priority: 1, totalFiles: 2, completedFiles: 1,
      totalPrints: 4, completedPrints: 2, progressPercent: 50,
      createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/project-templates': [{
      id: 'template-1', name: 'Workshop template', description: 'Reusable enclosure project',
      totalFiles: 2, totalPrints: 4, createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/parts-inventory': [part],
    '/api/bins': [{
      id: 'bin-1', code: 'BIN-01', name: 'Bracket bin', location: 'Workshop',
      notes: 'Printed brackets', isActive: true, createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/parts-inventory/mappings': [{
      id: 'mapping-1', partInventoryId: part.id, sku: part.sku, gcodeFileId: 'bracket.gcode',
      printProjectFileId: null, quantity: 2, createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/parts-inventory/reorder': [{
      partInventoryId: part.id, sku: part.sku, name: part.name, onHand: 2, reorderPoint: 10, deficit: 8,
    }],
    '/api/maintenance/tasks': [task],
    '/api/maintenance/tasks/categories': ['Nozzle'],
    '/api/maintenance/plans': [{
      id: 'plan-1', name: 'Workshop monthly plan', description: 'Monthly nozzle inspection',
      isActive: true, isDefault: false, createdAt: timestamp, updatedAt: timestamp,
      planTasks: [{ id: 'plan-task-1', maintenancePlanId: 'plan-1', maintenanceTaskId: task.id, sortOrder: 0, task }],
    }],
    '/api/maintenance/schedules': [{
      id: 'deployment-1', maintenancePlanId: 'plan-1', planName: 'Workshop monthly plan',
      printerId: printer.id, printerName: printer.name, isActive: true,
      deployedAt: timestamp, createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/maintenance/components': [component],
    '/api/maintenance/components/categories': ['Nozzle'],
    '/api/maintenance/components/low-stock': [component],
    '/api/maintenance/alerts': [{
      id: 'alert-1', printerId: printer.id, title: 'Inspect nozzle', message: 'Nozzle inspection due',
      severity: 2, status: 0, printerHoursAtTrigger: 100, createdAt: timestamp, updatedAt: timestamp,
    }],
    '/api/maintenance/upcoming': [{
      id: 'upcoming-1', taskId: task.id, printerId: printer.id, printerName: printer.name,
      taskName: task.taskName, component: 'Nozzle', priority: 2, intervalType: 'days',
      intervalValue: 30, dueDate: '2026-10-30T14:00:00Z', daysUntilDue: 0,
      isOverdue: false, isDueToday: true,
    }],
    '/api/maintenance/statistics/fleet': [{
      printerId: printer.id, printerName: printer.name, manufacturerName: 'OlyForge',
      modelName: 'CoreXY 250', isOnline: true, inMaintenance: false, totalPrintHours: 120,
      totalJobsCompleted: 30, totalJobsFailed: 1, totalFilamentUsedGrams: 2500,
      totalFilamentUsedMeters: 800, daysUntilNextMaintenance: 0, nextMaintenanceTask: task.taskName,
    }],
    '/api/maintenance/analytics/trends': [{
      date: timestamp, printerName: printer.name, component: 'Nozzle', taskName: task.taskName, cost: 12,
    }],
    '/api/maintenance/analytics/component-lifespan': [{ component: 'Nozzle', avgLifespanHours: 120, replacements: 1 }],
    '/api/maintenance/analytics/cost': [{ month: '2026-10', totalCost: 12 }],
    '/api/maintenance/analytics/uptime': [{
      printerId: printer.id, printerName: printer.name, uptimePercent: 98, maintenanceCount: 1, totalDowntimeMinutes: 10,
    }],
  };
}

export const inventoryRouteStates = [
  { name: 'projects', path: '/projects', marker: 'Workshop enclosure', command: 'New Project', filtersDisclosureCount: 1 },
  { name: 'filaments', path: '/spools', tab: 'filaments', marker: 'Workshop blue PLA', command: 'Add a new filament', filtersDisclosureCount: 1 },
  { name: 'spools', path: '/spools', tab: 'spools', marker: 'Workshop blue PLA', command: 'Add a new spool', filtersDisclosureCount: 1 },
  { name: 'clusters', path: '/spools', tab: 'clusters', marker: 'Workshop PLA equivalents', command: 'New Cluster', filtersDisclosureCount: 0 },
  { name: 'maintenance-dashboard', path: '/maintenance', tab: 'dashboard', marker: 'Workshop printer', filtersDisclosureCount: 0 },
  { name: 'maintenance-schedule', path: '/maintenance', tab: 'schedule', marker: 'Upcoming Maintenance', filtersDisclosureCount: 0 },
  { name: 'maintenance-library', path: '/maintenance', tab: 'library', marker: 'Workshop monthly plan', command: 'New Plan', filtersDisclosureCount: 0 },
  { name: 'maintenance-task-catalog', path: '/maintenance', tab: 'library', subTab: 'tasks', marker: 'Inspect nozzle', command: 'New Task', filtersDisclosureCount: 1 },
  { name: 'maintenance-inventory', path: '/maintenance', tab: 'inventory', marker: 'Workshop spare nozzle', command: 'Add Part', filtersDisclosureCount: 1 },
  { name: 'maintenance-analytics', path: '/maintenance', tab: 'analytics', marker: 'Workshop printer', filtersDisclosureCount: 0 },
  { name: 'parts-skus', path: '/parts-inventory', tab: 'skus', marker: 'Workshop bracket', command: 'Add SKU', filtersDisclosureCount: 1 },
  { name: 'parts-bins', path: '/parts-inventory', tab: 'bins', marker: 'Bracket bin', command: 'Add bin', filtersDisclosureCount: 1 },
  { name: 'parts-mappings', path: '/parts-inventory', tab: 'mappings', marker: 'bracket.gcode', command: 'Add mapping', filtersDisclosureCount: 1 },
  { name: 'parts-reorder', path: '/parts-inventory', tab: 'reorder', marker: 'Workshop bracket', filtersDisclosureCount: 0 },
  { name: 'locations', path: '/locations', marker: 'Workshop printer', command: 'Add location', filtersDisclosureCount: 1 },
  { name: 'catalog-printers', path: '/catalog', tab: 'printers', marker: 'CoreXY 250', command: 'Add', filtersDisclosureCount: 1 },
  { name: 'catalog-filaments', path: '/catalog', tab: 'filaments', marker: 'Workshop PLA', command: 'Add', filtersDisclosureCount: 0 },
  { name: 'catalog-toolheads', path: '/catalog', tab: 'toolheads', marker: 'Workshop toolhead', command: 'Add', filtersDisclosureCount: 1 },
  { name: 'catalog-extruders', path: '/catalog', tab: 'extruders', marker: 'Workshop extruder', command: 'Add', filtersDisclosureCount: 1 },
  { name: 'catalog-hotends', path: '/catalog', tab: 'hotends', marker: 'Workshop hotend', command: 'Add', filtersDisclosureCount: 1 },
  { name: 'catalog-nozzles', path: '/catalog', tab: 'nozzles', marker: 'Workshop nozzle', command: 'Add', filtersDisclosureCount: 1 },
] as const;
