import { toolbarRouteUser } from './toolbar-routed';

// Synthetic read-only API responses. No device addresses, keys or operational
// commands in this fixture refer to a real installation.
const timestamp = '2026-10-30T12:00:00Z';
const printer = {
  id: 'toolbar-printer', name: 'Fixture printer', status: 'Idle',
  printerModelId: 'toolbar-model', isEnabled: true, isOnline: true,
};
const worker = {
  id: 'toolbar-worker', serviceId: 'fixture-service', name: 'Fixture worker',
  endpointUrl: 'http://worker.invalid', capabilities: ['slicing'], status: 'Online',
  totalSlots: 2, freeSlots: 2, activeJobs: 0, completedJobs: 12, failedJobs: 1,
  averageProcessingTimeSeconds: 30, lastHeartbeat: timestamp, registeredAt: timestamp,
  apiKey: '', version: 'fixture', createdAt: timestamp, updatedAt: timestamp,
  isDisabled: false,
};

export const toolbarAdminSeeds: Record<string, unknown> = {
  '/api/printers': [printer],
  '/api/settings/metadata': [{
    key: 'General', className: 'GeneralSettings', displayName: 'Farm identity',
    description: 'Farm identity and defaults.', group: 'General', order: 1,
    properties: [{
      name: 'farmName', type: 'String', attributes: [],
      display: { name: 'Farm name', inputType: 'Text' },
    }],
  }],
  '/api/settings/groups': [{ key: 'General', displayName: 'General', order: 1 }],
  '/api/settings': { General: { farmName: 'Fixture farm' } },
  '/api/settings/farm': {
    electricityRatePerKwh: 0.15, defaultMachineHourlyRate: 1,
    averagePrinterWattage: 150, canWrite: true, rowVersion: 'fixture',
    slicerMode: 'Simple', enabledModes: ['Simple', 'Advanced'],
  },
  '/api/settings/user': {
    userId: toolbarRouteUser.id, theme: 'dark', locale: 'en-US',
    itemsPerPage: 25, defaultSlicerPreset: null, printablesUsername: null,
    rowVersion: 'fixture', printerControlMode: 'Guided',
  },
  '/api/apikeys/settings': { hashingEnabled: true },
  '/api/settings/CostTracking': {
    rowVersion: 'fixture', enableAutomaticCostCalculation: true,
    electricityRatePerKwh: 0.15, averagePrinterWattage: 150,
    defaultMachineHourlyRate: 1, laborMarkupPercent: 0, profitMarginTargetPercent: 20,
  },
  [`/api/users/${toolbarRouteUser.id}/apikeys`]: [{
    id: 'fixture-key', name: 'Fixture desktop key', isActive: true,
    createdAt: timestamp, purpose: 'Desktop', scopes: 'ModelRead',
    scopeNames: ['ModelRead'], isExpired: false,
  }],
  '/api/auth/passkey/credentials': [{
    id: 1, deviceName: 'Fixture security key', aaguidDescription: 'Security key',
    createdAt: timestamp, lastUsedAt: timestamp,
  }],
  '/api/notifications/preferences': {
    userId: toolbarRouteUser.id, enableEmailNotifications: false,
    enablePushNotifications: false, enableInAppNotifications: true,
    enableTelegramNotifications: false, notifyOnCompletion: true,
    notifyOnFailure: true, notifyOnStart: false, notifyOnPause: false,
    frequency: 'RealTime', retentionDays: 30,
    eventChannelPreferences: ['JobStarted', 'JobCompleted', 'JobFailed', 'JobPaused'].map(eventType => ({
      eventType, inApp: true, email: false, push: false, telegram: false,
    })),
  },
  '/api/notifications/preferences/capabilities': {
    supportedEventTypes: ['JobStarted', 'JobCompleted', 'JobFailed', 'JobPaused'],
  },
  '/api/statistics/summary': {
    totalJobs: 10, completedJobs: 9, failedJobs: 1, cancelledJobs: 0,
    successRate: 90, totalCost: 20, totalFilamentGrams: 500, totalPrintHours: 40,
  },
  '/api/statistics/jobs-over-time': [
    { date: '2026-10-29', completed: 4, failed: 1, cancelled: 0 },
    { date: '2026-10-30', completed: 5, failed: 0, cancelled: 0 },
  ],
  '/api/statistics/cost-over-time': [{ date: '2026-10-29', cost: 8 }, { date: '2026-10-30', cost: 12 }],
  '/api/statistics/filament-by-material': [{ material: 'PLA', grams: 500 }],
  '/api/statistics/printer-utilization': [{
    printerId: printer.id, printerName: printer.name, totalJobs: 10,
    completedJobs: 9, failedJobs: 1, totalPrintHours: 40, successRate: 90,
  }],
  '/api/statistics/costs/summary': {
    totalCostUsd: 20, averageCostPerJobUsd: 2, jobsWithCostData: 10,
    totalMaterialCostUsd: 10, totalEnergyCostUsd: 2, totalMachineTimeCostUsd: 6,
    totalLaborCostUsd: 2, mostExpensiveMaterial: 'PLA', mostExpensiveMaterialCost: 10,
  },
  '/api/statistics/costs/by-printer': [{
    printerId: printer.id, printerName: printer.name, totalCostUsd: 20,
    averageCostPerJobUsd: 2, jobCount: 10, materialCostUsd: 10,
    energyCostUsd: 2, machineTimeCostUsd: 6, laborCostUsd: 2,
  }],
  '/api/statistics/costs/by-material': [{
    materialType: 'PLA', totalCostUsd: 20, averageCostPerJobUsd: 2,
    jobCount: 10, totalFilamentUsageGrams: 500,
  }],
  '/api/statistics/costs/by-job': {
    items: [{
      jobId: 'fixture-job', jobName: 'Fixture bracket', printerName: printer.name,
      filamentName: 'Fixture PLA', materialType: 'PLA', filamentUsedGrams: 50,
      totalCostUsd: 2, materialCostUsd: 1, energyCostUsd: 0.2,
      machineTimeCostUsd: 0.6, laborCostUsd: 0.2, printTimeSeconds: 3600,
      completedAt: timestamp,
    }], nextCursor: null,
  },
  '/api/predictive-analytics/active-alerts': [],
  '/api/predictive-analytics/maintenance-forecast': [{
    printerId: printer.id, printerName: printer.name,
    upcomingTasks: [{ taskName: 'Nozzle inspection', estimatedDaysUntilDue: 30, priority: 'Low' }],
  }],
  '/api/job-scheduling/timezones': [{ id: 'UTC', displayName: 'UTC', baseUtcOffset: '00:00:00' }],
  '/api/job-scheduling/scheduled': [{
    id: 'fixture-schedule', jobId: 'fixture-job', jobName: 'Fixture bracket',
    printerName: printer.name, printerId: printer.id,
    scheduledStartTimeUtc: '2026-10-30T16:00:00Z',
    scheduledLocalTime: '2026-10-30T16:00:00', timeZone: 'UTC',
    recurrencePattern: null, recurrenceInterval: 1, isActive: true,
    isPaused: false, requiresOperatorReauthorization: false, status: 'active',
  }],
  '/api/workers/': [worker],
  '/api/workers/by-status/Online': [worker],
  '/api/admin/security/login-audit': {
    items: Array.from({ length: 50 }, (_, index) => ({
      id: `fixture-login-${index}`, timestamp, username: `fixture.operator.${index}`,
      success: true, ipAddress: '192.0.2.1', userAgent: 'Fixture browser',
    })), totalCount: 51, page: 1, pageSize: 50,
  },
  '/api/admin/power-monitors': [{
    id: 'fixture-monitor', printerId: printer.id, printerName: printer.name,
    provider: 'Kasa', deviceAddress: 'plug.invalid', electricityRatePerKwh: 0.15,
    enabled: true, createdAt: timestamp, updatedAt: timestamp,
  }],
  '/api/admin/overview': {
    checkedAt: timestamp, overallStatus: 'Healthy',
    subsystems: [
      { key: 'database', name: 'Database', status: 'Healthy', detail: 'Available' },
      { key: 'workers', name: 'Workers', status: 'Healthy', detail: '1 online' },
    ], attention: [],
  },
  '/api/system/info': {
    app: { version: 'fixture', uptime: '01:00:00', hostname: 'fixture-host' },
    cpu: { cores: 4, usagePercent: 12 },
    memory: { usedBytes: 1073741824, totalBytes: 8589934592 },
    disk: { usedBytes: 10737418240, totalBytes: 107374182400, archiveBytes: 1024, databaseBytes: 4096 },
    database: { engine: 'SQLite', version: 'fixture', printerCount: 1, archiveCount: 1, migrationHeads: ['fixture'] },
    services: [{ name: 'Fixture scheduler', version: 'fixture', health: 'Healthy' }],
  },
  '/api/catalog/manufacturers': [{ id: 'fixture-manufacturer', name: 'Fixture manufacturer' }],
  '/api/catalog/printer-models': [{
    id: 'toolbar-model', name: 'Fixture CoreXY', manufacturerId: 'fixture-manufacturer',
  }],
};

export const toolbarAdminStates = [
  { name: 'analytics-production', path: '/analytics', tab: 'Production', ready: 'Jobs Over Time' },
  { name: 'analytics-cost', path: '/analytics', tab: 'Cost', ready: 'Fixture printer' },
  { name: 'analytics-fleet', path: '/analytics', tab: 'Fleet', ready: 'Jobs Over Time' },
  { name: 'scheduling', path: '/scheduling', ready: 'Fixture bracket' },
  { name: 'settings-preferences', path: '/settings', tab: 'Preferences', ready: 'Items per page' },
  { name: 'settings-api-keys', path: '/settings', tab: 'API Keys', ready: 'Fixture desktop key' },
  { name: 'settings-notifications', path: '/settings', tab: 'Notifications', ready: 'Event × Channel Matrix' },
  { name: 'settings-passkeys', path: '/settings', tab: 'Passkeys', ready: 'Fixture security key' },
  { name: 'admin-hub', path: '/admin', ready: 'Database' },
  { name: 'admin-farm-settings', path: '/admin/settings', ready: 'Electricity' },
  { name: 'admin-status', path: '/admin/status', ready: 'fixture-host' },
  { name: 'admin-workers', path: '/admin/workers', ready: 'Fixture worker' },
  { name: 'admin-login-audit', path: '/admin/login-audit', ready: 'fixture.operator' },
  { name: 'admin-data-management', path: '/admin/data-management', ready: 'Export Catalog' },
  { name: 'admin-power-monitors', path: '/admin/power-monitors', ready: 'plug.invalid' },
  { name: 'profiles-import-first-step', path: '/profiles/import', ready: 'Fixture CoreXY' },
] as const;
