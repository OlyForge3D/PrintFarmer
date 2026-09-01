/**
 * React Query key factory, extracted from `useApi.ts` so it can be imported
 * without pulling in that file's top-level `apiClient` import. Consumers that
 * only need query keys (not the hooks themselves) — notably
 * `QueueRealtimeBridge.tsx`, statically mounted by `App.tsx` — must not force
 * the `ApiClient` monolith into the eager bundle. See issue #2343.
 */
export const queryKeys = {
  printers: ['printers'] as const,
  printer: (id: string) => ['printers', id] as const,
  printerDetails: (id: string) => ['printers', id, 'details'] as const,
  printerHistory: (id: string, options?: { limit?: number; start?: number; since?: Date; before?: Date; order?: string }) =>
    ['printers', id, 'history', options] as const,
  printerHistoryJob: (printerId: string, jobId: string) => ['printers', printerId, 'history', jobId] as const,
  printerHistoryTotals: (printerId: string) => ['printers', printerId, 'history', 'totals'] as const,
  printJobObjects: (printerId: string) => ['printers', printerId, 'printjob', 'objects'] as const,
  manufacturers: ['manufacturers'] as const,
  models: (manufacturerId?: string) => ['models', manufacturerId] as const,
  hotendModels: ['hotend-models'] as const,
  extruderModels: ['extruder-models'] as const,
  toolheadModels: ['toolhead-models'] as const,
  nozzleModels: ['nozzle-models'] as const,
  nozzleMaterials: ['nozzle-materials'] as const,
  filamentTypes: ['filament-types'] as const,
  filamentTypesPaged: (page?: number, pageSize?: number, search?: string) => ['filament-types', page, pageSize, search] as const,
  filamentPresets: ['presets', 'filament'] as const,
  spoolmanDbFilaments: ['spoolmandb', 'filaments'] as const,
  spoolmanDbMaterials: ['spoolmandb', 'materials'] as const,
  ofdBrands: ['ofd', 'brands'] as const,
  ofdBrandMaterials: (slug: string) => ['ofd', 'brands', slug, 'materials'] as const,
  ofdFilaments: (brandSlug: string, materialSlug: string) => ['ofd', 'filaments', brandSlug, materialSlug] as const,
  spoolmanVendors: ['spoolman', 'vendors'] as const,
  spoolmanMaterials: ['spoolman', 'materials'] as const,
  gcodeFiles: (page?: number, pageSize?: number) => ['gcode-files', page, pageSize] as const,
  gcodeFile: (id: string) => ['gcode-files', id] as const,
  harvestOperations: (printerId?: string) => ['harvest-operations', printerId] as const,
  harvestOperation: (id: string) => ['harvest-operations', id] as const,
  jobQueue: (printerId?: string) => ['job-queue', printerId] as const,
  health: ['health'] as const,
  fileConsistency: {
    health: ['file-consistency', 'health'] as const,
    auditHistory: (pageSize?: number) => ['file-consistency', 'audits', pageSize] as const,
    filesWithIssues: ['file-consistency', 'issues'] as const,
    model3DHealth: (id: string) => ['file-consistency', 'model3d', id] as const,
    gcodeFileHealth: (id: string) => ['file-consistency', 'gcode', id] as const,
  },
  nfcDevices: ['nfc-devices'] as const,
  nfcDevice: (id: string) => ['nfc-devices', id] as const,
  nfcDeviceHistory: (id: string) => ['nfc-devices', id, 'history'] as const,
  nfcBindings: ['nfc-bindings'] as const,
  notifications: ['notifications'] as const,
  unreadCount: ['notifications', 'unread-count'] as const,
  notificationPreferences: ['notifications', 'preferences'] as const,
  costSummary: (days?: number, startDate?: string, endDate?: string) => ['costs', 'summary', days, startDate, endDate] as const,
  costs: ['costs'] as const,
  costsByPrinter: (days?: number, startDate?: string, endDate?: string) => ['costs', 'by-printer', days, startDate, endDate] as const,
  costsByMaterial: (days?: number, startDate?: string, endDate?: string) => ['costs', 'by-material', days, startDate, endDate] as const,
  costsByJob: (days?: number, startDate?: string, endDate?: string) => ['costs', 'by-job', days, startDate, endDate] as const,
  costOverTime: ['costs', 'over-time'] as const,
  scheduledJobs: ['scheduled-jobs'] as const,
  scheduledJob: (jobId: string) => ['scheduled-jobs', jobId] as const,
  jobExecutions: (jobId: string) => ['scheduled-jobs', jobId, 'executions'] as const,
  timezones: ['timezones'] as const,
  obicoServers: ['obico-servers'] as const,
  obicoServer: (id: string) => ['obico-servers', id] as const,
  failureDetectionHistory: (printerId?: string, take?: number) => (
    ['failure-detection', 'history', printerId ?? null, take ?? null] as const
  ),
  printSessionTimeline: (jobId?: string) => (
    ['job-queue-analytics', 'jobs', jobId ?? null, 'state-history'] as const
  ),
  bedTypes: ['bed-types'] as const,
  customFieldDefinitions: (entityType: string) => ['custom-field-definitions', entityType] as const,
  customFieldValues: (entityType: string, entityId: string) => ['custom-field-values', entityType, entityId] as const,
} as const;
