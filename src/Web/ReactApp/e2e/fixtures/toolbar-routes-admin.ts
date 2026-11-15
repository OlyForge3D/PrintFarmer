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
  '/api/settings/metadata': [
    {
      key: 'SystemLog', className: 'SystemLogSettings', displayName: 'System logging',
      description: 'System log configuration.', group: 'System', order: 1,
      properties: [{
        name: 'enabled', type: 'Boolean', attributes: [],
        display: { name: 'Enable system logging', inputType: 'Checkbox' },
      }],
    },
    {
      key: 'General', className: 'GeneralSettings', displayName: 'Farm identity',
      description: 'Farm identity and defaults.', group: 'General', order: 1,
      properties: [{
        name: 'farmName', type: 'String', attributes: [],
        display: { name: 'Farm name', inputType: 'Text' },
      }],
    },
    {
      key: 'System', className: 'SystemSettings', displayName: 'System settings',
      description: 'Network discovery configuration.', group: 'System', order: 2,
      properties: [
        {
          name: 'discoveryEnabled', type: 'Boolean', attributes: [],
          display: { name: 'Network discovery enabled', inputType: 'Boolean' },
        },
        {
          name: 'discoverySubnets', type: 'String[]', attributes: [],
          display: { name: 'Discovery subnets', inputType: 'Subnet', isMulti: true },
        },
      ],
    },
    {
      key: 'Operations', className: 'OperationsSettings', displayName: 'Automation settings',
      description: 'Automated cost calculation defaults.', group: 'Operations', order: 3,
      properties: [{
        name: 'enableAutomaticCostCalculation', type: 'Boolean', attributes: [],
        display: { name: 'Automatic cost calculation', inputType: 'Boolean' },
      }],
    },
    {
      key: 'Slicing', className: 'SlicingSettings', displayName: 'Slicer defaults',
      description: 'Default slicing parameters.', group: 'Slicing', order: 4,
      properties: [{
        name: 'defaultInfillDensity', type: 'Number', attributes: [],
        display: { name: 'Default infill density', inputType: 'Number', unit: '%' },
      }],
    },
    {
      key: 'Integrations', className: 'IntegrationSettings', displayName: 'Integration defaults',
      description: 'External service connection defaults.', group: 'Integrations', order: 5,
      properties: [{
        name: 'defaultConnectionTimeoutSeconds', type: 'Number', attributes: [],
        display: { name: 'Default connection timeout', inputType: 'Number', unit: 'seconds' },
      }],
    },
    {
      key: 'CostTracking', className: 'CostTrackingSettings', displayName: 'Cost tracking',
      group: 'Operations', properties: [
        { name: 'electricityRatePerKwh', type: 'Double', attributes: [], display: { name: 'Electricity rate per kWh', inputType: 'Number' } },
      ],
    },
    {
      key: 'Obico', className: 'ObicoSettings', displayName: 'Obico',
      group: 'Monitoring', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable Obico detection', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'QueuePlanning', className: 'QueuePlanningSettings', displayName: 'Queue planning',
      group: 'Operations', properties: [
        { name: 'workdayStartHourUtc', type: 'Int32', attributes: [], display: { name: 'Workday start hour (UTC)', inputType: 'Number' } },
      ],
    },
    {
      key: 'OperatorFeatures', className: 'OperatorFeatureSettings', displayName: 'Operator features',
      group: 'Operations', properties: [
        { name: 'attentionEnabled', type: 'Boolean', attributes: [], display: { name: 'Attention feed', inputType: 'Checkbox' } },
      ],
    },
    {
      key: 'AutoTagging', className: 'AutoTaggingSettings', displayName: 'Auto-tagging rules',
      group: 'Operations', properties: [
        { name: 'enabled', type: 'Boolean', attributes: [], display: { name: 'Enable automatic tagging', inputType: 'Checkbox' } },
      ],
    },
  ],
  '/api/settings/groups': [
    { key: 'General', displayName: 'General', order: 1 },
    { key: 'System', displayName: 'System', order: 2 },
    { key: 'Operations', displayName: 'Operations', order: 3 },
    { key: 'Slicing', displayName: 'Slicing', order: 4 },
    { key: 'Integrations', displayName: 'Integrations', order: 5 },
    { key: 'Monitoring', displayName: 'Monitoring', order: 6 },
  ],
  '/api/settings': {
    SystemLog: { enabled: true },
    General: { farmName: 'Fixture farm' },
    System: { discoveryEnabled: true, discoverySubnets: ['192.0.2.0/24'] },
    Operations: { enableAutomaticCostCalculation: true },
    Slicing: { defaultInfillDensity: 20 },
    Integrations: { defaultConnectionTimeoutSeconds: 30 },
    CostTracking: { electricityRatePerKwh: 0.15 },
    Obico: { enabled: false },
    QueuePlanning: { workdayStartHourUtc: 8 },
    OperatorFeatures: { attentionEnabled: true },
    AutoTagging: { enabled: false },
  },
  '/api/settings/security/password-policy': {
    minLength: 8,
    requireUppercase: false,
    requireLowercase: false,
    requireDigit: false,
    requireSymbol: false,
  },
  '/api/spoolman/config': { baseUrl: '' },
  '/api/admin/integrations/home-assistant/settings': { enabled: false, baseUrl: '', tokenMasked: '' },
  '/api/admin/integrations/telegram/settings': {
    enabled: false, chatId: '', botTokenMasked: '', includeSnapshots: false,
  },
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
  '/api/bed-types': [],
  '/api/nfc-devices': [],
  '/api/nfc/bindings': [],
  '/api/obico-servers': [],
  '/api/failure-detection/status': {
    monitoringEnabled: false, confidenceThreshold: 0.8, scanIntervalSeconds: 30,
    configuredPrinterCount: 0, activelyMonitoredPrinterCount: 0,
    lastAnalyzedPrinterCount: 0, lastFailureCount: 0, printers: [],
  },
  '/api/printer-groups': [],
  '/api/custom-fields/definitions': [],
  '/api/webhooks': [],
  '/api/webhooks/event-types': [],
  '/api/admin/roles': [],
  '/api/admin/permissions/catalog': { resources: [] },
  '/api/quotas': [],
  '/api/users': [],
  '/api/users/roles': [],
  '/api/tags': [],
  '/api/3d-models': [],
  '/api/gcode-files/query': { files: [], totalCount: 0, page: 1, pageSize: 50 },
  '/api/slicer/profiles/library-hierarchy': { byHierarchy: {} },
  '/api/slicer/profiles/extended': {
    processProfiles: [],
    filamentProfiles: [],
    machineProfiles: [],
  },
  '/api/slicer/profiles/custom': {
    profiles: [], totalCount: 0, machineProfileCount: 0, processProfileCount: 0, filamentProfileCount: 0,
  },
  '/api/slicers/engines': [],
  '/api/3d-models/picker': [],
  '/api/printers/demo-1/details': {
    id: 'demo-1', name: 'Atlas', backend: 'Moonraker',
    modelName: 'Elegoo Centauri Carbon', manufacturerName: 'Elegoo',
  },
  '/api/slice/my-jobs': [],
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
  { name: 'analytics-production', path: '/analytics', tab: 'Production', ready: 'Jobs Over Time', filtersDisclosureCount: 0 },
  { name: 'analytics-cost', path: '/analytics', tab: 'Cost', ready: 'Fixture printer', filtersDisclosureCount: 0 },
  { name: 'analytics-fleet', path: '/analytics', tab: 'Fleet', ready: 'Jobs Over Time', filtersDisclosureCount: 0 },
  { name: 'scheduling', path: '/scheduling', ready: 'Fixture bracket', filtersDisclosureCount: 0 },
  { name: 'settings-preferences', path: '/settings', tab: 'Preferences', ready: 'Items per page', filtersDisclosureCount: 0 },
  { name: 'settings-api-keys', path: '/settings', tab: 'API Keys', ready: 'Fixture desktop key', filtersDisclosureCount: 0 },
  { name: 'settings-notifications', path: '/settings', tab: 'Notifications', ready: 'Event × Channel Matrix', filtersDisclosureCount: 0 },
  { name: 'settings-passkeys', path: '/settings', tab: 'Passkeys', ready: 'Fixture security key', filtersDisclosureCount: 0 },
  { name: 'admin-hub', path: '/admin', ready: 'Database', filtersDisclosureCount: 0 },
  { name: 'admin-farm-settings', path: '/admin/settings?tab=identity&sub=details', ready: 'Farm name', filtersDisclosureCount: 0 },
  {
    name: 'admin-system-settings',
    path: '/admin/settings?tab=system&field=System.discoverySubnets',
    ready: 'Discovery subnets',
    filtersDisclosureCount: 0,
  },
  {
    name: 'admin-cost-settings',
    path: '/admin/settings?tab=costs&field=CostTracking.electricityRatePerKwh',
    ready: 'Electricity rate per kWh',
    filtersDisclosureCount: 0,
  },
  { name: 'admin-failure-detection', path: '/admin/settings?tab=failure-detection', ready: 'Failure Detection Runtime', filtersDisclosureCount: 0 },
  { name: 'admin-planning', path: '/admin/settings?tab=planning&field=QueuePlanning.workdayStartHourUtc', ready: 'Workday start hour', filtersDisclosureCount: 0 },
  { name: 'admin-operator-features', path: '/admin/settings?tab=operator-features&field=OperatorFeatures.attentionEnabled', ready: 'Attention feed', filtersDisclosureCount: 0 },
  { name: 'admin-auto-tagging', path: '/admin/settings?tab=data&sub=auto-tagging&field=AutoTagging.enabled', ready: 'Enable automatic tagging', filtersDisclosureCount: 0 },
  {
    name: 'admin-slicing-defaults',
    path: '/admin/settings?tab=slicing&sub=defaults&field=Slicing.defaultInfillDensity',
    ready: 'Default infill density',
    filtersDisclosureCount: 0,
  },
  {
    name: 'admin-connections',
    path: '/admin/settings?tab=integrations&sub=connections&field=Integrations.defaultConnectionTimeoutSeconds',
    ready: 'Default connection timeout',
    filtersDisclosureCount: 0,
  },
  { name: 'admin-user-accounts', path: '/admin/settings?tab=users&sub=accounts', ready: 'No users found', filtersDisclosureCount: 0 },
  { name: 'admin-tags', path: '/admin/settings?tab=data&sub=tags', ready: 'No tags created yet', filtersDisclosureCount: 0 },
  { name: 'admin-slicer-profiles', path: '/admin/settings?tab=slicing&sub=profiles', ready: 'Showing 0 profile(s)', filtersDisclosureCount: 0 },
  { name: 'admin-bed-types', path: '/admin/settings?tab=slicing&sub=bed-types', ready: 'No bed types configured', filtersDisclosureCount: 0 },
  { name: 'admin-nfc-devices', path: '/admin/settings?tab=hardware&sub=nfc', ready: 'No NFC devices registered', filtersDisclosureCount: 0 },
  { name: 'admin-nfc-bindings', path: '/admin/settings?tab=hardware&sub=nfc', ready: 'No NFC tags have been bound yet', filtersDisclosureCount: 0 },
  { name: 'admin-printer-groups', path: '/admin/settings?tab=hardware&sub=printer-groups', ready: 'No printer groups yet', filtersDisclosureCount: 0 },
  { name: 'admin-custom-fields', path: '/admin/settings?tab=data&sub=custom-fields', ready: 'No printer custom fields', filtersDisclosureCount: 0 },
  { name: 'admin-webhooks', path: '/admin/settings?tab=integrations&sub=webhooks', ready: 'No webhooks configured', filtersDisclosureCount: 0 },
  { name: 'admin-roles', path: '/admin/settings?tab=users&sub=roles', ready: 'No roles yet', filtersDisclosureCount: 0 },
  { name: 'admin-quotas', path: '/admin/settings?tab=quotas', ready: 'No quotas configured yet', filtersDisclosureCount: 0 },
  { name: 'admin-status', path: '/admin/status', ready: 'fixture-host', filtersDisclosureCount: 0 },
  { name: 'admin-workers', path: '/admin/workers', ready: 'Fixture worker', filtersDisclosureCount: 1 },
  { name: 'admin-worker-jobs', path: '/admin/workers?workerTab=jobs', ready: 'New Slice Job', filtersDisclosureCount: 0 },
  { name: 'admin-login-audit', path: '/admin/login-audit', ready: 'fixture.operator', filtersDisclosureCount: 1 },
  { name: 'admin-data-management', path: '/admin/data-management', ready: 'Export Catalog', filtersDisclosureCount: 0 },
  { name: 'admin-power-monitors', path: '/admin/power-monitors', ready: 'plug.invalid', filtersDisclosureCount: 0 },
  { name: 'profiles-import-first-step', path: '/profiles/import', ready: 'Fixture CoreXY', filtersDisclosureCount: 0 },
] as const;
