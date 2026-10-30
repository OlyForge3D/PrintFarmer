export const tracerPrinter = {
  id: 'printer-1', name: 'Printer Alpha', manufacturerName: 'Prusa',
  modelName: 'MK4', backend: 'PrusaLink', isOnline: true, isEnabled: true,
  state: 'Idle', nozzleDiameter: 0.4,
};

const job = {
  id: 'job-1', name: 'Fixture gear', status: 'Queued', priority: 'Normal', rowVersion: 'fixture-revision',
  queuePosition: 0, requiredMaterialType: 'PLA', requiredNozzleDiameter: 0.4,
  estimatedPrintTimeSeconds: 3600, estimatedFilamentUsageGrams: 20,
  createdAtUtc: '2026-10-30T12:00:00Z', updatedAtUtc: '2026-10-30T12:00:00Z',
  queuedAtUtc: '2026-10-30T12:00:00Z', copies: 1, completedCopies: 0,
  remainingCopies: 1, tags: [], assignedPrinterId: 'printer-1',
};

export const tracerRouteSeeds: Record<string, unknown> = {
  '/api/printers': [tracerPrinter],
  '/api/printers/summary': [tracerPrinter],
  '/api/catalog/manufacturers': [{ id: 'prusa', name: 'Prusa' }],
  '/api/catalog/printer-models': [],
  '/api/tags/objects': {},
  '/api/job-queue-analytics/printer-summaries': [],
  '/api/printers/filament-coverage': [],
  '/api/printers/camera-urls': [],
  '/api/printers/backend-capabilities': [],
  '/api/settings/NetworkDiscovery': { enableDiscovery: true, lastHeartbeat: '2026-10-30T14:00:00Z' },
  '/api/job-scheduling/timezones': [{ id: 'UTC', displayName: 'UTC', offset: '+00:00' }],
  '/api/dispatch-settings': {
    eTag: 'fixture-dispatch', autoDispatchEnabled: false, autoDispatchMode: 'Manual',
    idleThresholdSeconds: 60, minimumScoreThreshold: 0, maxConcurrentDispatches: 1,
    loadBalancingStrategy: 'RoundRobin', updatedAt: '2026-10-30T12:00:00Z',
  },
  '/api/auto-dispatch/status': {
    globalEnabled: false, printers: [{
      printerId: 'printer-1', printerName: 'Printer Alpha', enabled: false,
      state: 'Idle', queueDepth: 1, isReady: false, readyGateChecks: [],
      dispatchStateETag: 'fixture-dispatch', printerETag: 'fixture-printer',
    }],
  },
  '/api/maintenance/alerts': [],
  '/api/maintenance/components': [],
  '/api/services/summary': {
    totalServices: 1, runningServices: 1, enabledServices: 1, disabledServices: 0,
    servicesWithErrors: 0, byCategory: {},
  },
  '/api/services': [],
  '/api/tasks': [],
  '/api/statistics/costs/summary': {
    totalCostUsd: 10, averageCostPerJobUsd: 10, jobsWithCostData: 1,
    totalMaterialCostUsd: 5, totalEnergyCostUsd: 1, totalMachineTimeCostUsd: 4,
    totalLaborCostUsd: 0, mostExpensiveMaterialCost: 5,
  },
  '/api/nfc/bindings': [],
  '/api/printer-groups': [{
    id: 'group-1', name: 'Workshop', description: 'Seeded printer group',
    printerIds: ['printer-1'], printerCount: 1,
    createdDate: '2026-10-29T12:00:00Z', updatedDate: '2026-10-30T12:00:00Z',
  }],
  '/api/cameras/display': [{
    id: 'camera-1', name: 'Workbench camera', isEnabled: true, sortOrder: 0,
    isStandalone: true, printerIds: [], printers: [], isLocationDerived: false,
    source: 'Standalone', cameraType: 'Generic', healthStatus: 'Unknown',
  }],
  '/api/job-queue-analytics': [{ job, assignedPrinter: { id: 'printer-1', name: 'Printer Alpha', modelName: 'MK4' } }],
  '/api/job-queue-analytics/stats': {
    totalQueued: 1, totalPrinting: 0, totalPaused: 0, averageWaitTimeMinutes: 5,
    assumptions: { workdayStartHourUtc: 0, workdayEndHourUtc: 24, bedClearMinutes: 5 },
    byModel: {},
  },
  '/api/job-queue': [{
    printerId: 'printer-1', printerName: 'Printer Alpha', printerModel: 'MK4',
    isAvailable: true, queuedJobsCount: 1,
  }],
  '/api/job-queue-analytics/timeline': [{
    jobId: 'job-1', jobName: 'Fixture gear', printerId: 'printer-1',
    printerName: 'Printer Alpha', state: 'Queued', enteredAtUtc: '2026-10-30T13:00:00Z',
  }],
  '/api/job-queue-analytics/history': {
    entries: [{
      id: 'history-1', jobName: 'Completed bracket', printerName: 'Printer Alpha',
      status: 'Completed', completionPercentage: 100, actualPrintTimeSeconds: 3600,
      startedAtUtc: '2026-10-30T12:00:00Z', completedAtUtc: '2026-10-30T13:00:00Z',
      toolheadUsages: [],
    }],
    totalCount: 1, currentPage: 0, pageSize: 50,
    stats: { totalCompleted: 1, totalFailed: 0, totalCancelled: 0, successRate: 100, averageDurationMinutes: 60, totalPrintTimeMinutes: 60 },
  },
  '/api/dispatch/history': {
    items: [{
      id: 'dispatch-1', printJobId: 'job-1', jobName: 'Fixture gear',
      printerId: 'printer-1', printerName: 'Printer Alpha', action: 'Dispatched',
      createdAtUtc: '2026-10-30T13:00:00Z', reason: 'Seeded local history',
    }], totalCount: 1, page: 1, pageSize: 20,
  },
};

export const tracerRouteStates = [
  { name: 'dashboard', path: '/dashboard', heading: 'Printer Dashboard' },
  { name: 'printers', path: '/printers', heading: 'Printers', populated: 'Printer Alpha' },
  { name: 'cameras', path: '/cameras', heading: 'Cameras', populated: 'Workbench camera' },
  { name: 'printer-groups', path: '/printer-groups', heading: 'Printer Groups', populated: 'Workshop' },
  { name: 'queue', path: '/printQueue', heading: 'Print Queue Dashboard', populated: 'Fixture gear' },
  { name: 'queue-timeline', path: '/printQueue/timeline', heading: 'Print Queue Dashboard', populated: 'Fixture gear' },
  { name: 'queue-history', path: '/printQueue/history', heading: 'Print Queue Dashboard', populated: 'Completed bracket' },
  { name: 'queue-dispatch-log', path: '/printQueue/dispatch-log', heading: 'Print Queue Dashboard', populated: 'Fixture gear' },
  { name: 'nfc-bindings', path: '/nfc-bindings', heading: 'NFC Tag Bindings', populated: 'No NFC tags have been bound yet.' },
  { name: 'auto-dispatch', path: '/auto-dispatch', heading: 'Auto-Dispatch', populated: 'Printer Alpha' },
] as const;
