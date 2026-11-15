import type { BrowserContext } from '@playwright/test';
import { inventoryRouteSeeds } from '../fixtures/toolbar-routes-inventory';
import { toolbarRouteUser } from '../fixtures/toolbar-routed';
import { toolbarAdminSeeds } from '../fixtures/toolbar-routes-admin';
import { MotionType, PrinterBackend, PrintJobPriority, PrintProjectStatus, type BackgroundServiceStatus, type BackgroundServicesSummary, type CostSummary, type NfcDeviceDto, type NfcScanHistoryDto, type Printer, type ManufacturerDto, type PrinterModelDto, type PrintProjectListDto, type QueueChangeFeed, type QueueChangeWatermark, type QueueHistoryPageDto, type QueueOverviewDto, type QueuedPrintJobWithFileMetaDto, type SpoolmanFilament, type TimezoneInfo } from '../../src/types/api';
import { MaintenanceAlertStatus, type MaintenanceAlert } from '../../src/types/maintenance';

export const captureOrigin = 'http://127.0.0.1:4178';
export const captureTime = '2026-10-30T14:00:00Z';
export type CaptureTheme = 'light' | 'dark' | 'matrix' | 'blueprint' | 'ratos' | 'voron' | 'farm' | 'forge' | 'system';
const inventory = inventoryRouteSeeds();
const catalogInventory = inventoryRouteSeeds(true);

export const printers = ([
  ['demo-1', 'Atlas', 'Printing', 64, 'enclosure-panel.gcode', 'Elegoo', 'Elegoo Centauri Carbon', PrinterBackend.SDCP, MotionType.CoreXY],
  ['demo-2', 'Birch', 'Printing', 32, 'sensor-mount.gcode', 'Snapmaker', 'Snapmaker U1', PrinterBackend.Moonraker, MotionType.CoreXY],
  ['demo-3', 'Cedar', 'Idle', 0, '', 'Voron', 'Voron Trident 300', PrinterBackend.Moonraker, MotionType.CoreXY],
  ['demo-4', 'Delta', 'Paused', 48, 'cable-guide.gcode', 'Sovol', 'Sovol SV08', PrinterBackend.Moonraker, MotionType.CoreXY],
  ['demo-5', 'Ember', 'Idle', 0, '', 'Ratrig', 'RatRig V-Core 4 400', PrinterBackend.Moonraker, MotionType.CoreXY],
  ['demo-6', 'Finch', 'Offline', 0, '', 'Flashforge', 'Flashforge AD5X', PrinterBackend.FlashForge, MotionType.CoreXY],
  ['demo-7', 'Grove', 'Printing', 76, 'bracket-set.gcode', 'Prusa', 'Prusa MK4S', PrinterBackend.PrusaLink, MotionType.Cartesian],
  ['demo-8', 'Hazel', 'Idle', 0, '', 'Prusa', 'Prusa CORE One L', PrinterBackend.PrusaLink, MotionType.CoreXY],
] as const).map(([id, name, state, progress, fileName, manufacturerName, modelName, backend, motionType]) => ({
  id, name, state, progress, fileName,
  backend, manufacturerName, modelName, motionType,
  isOnline: state !== 'Offline', isEnabled: true, inMaintenance: false,
  hotendTemp: state === 'Printing' ? 210 : 23,
  hotendTarget: state === 'Printing' ? 210 : 0,
  bedTemp: state === 'Printing' ? 60 : 23,
  bedTarget: state === 'Printing' ? 60 : 0,
  printTimeLeftSeconds: state === 'Printing' ? 3600 : undefined,
  obicoEnabled: false,
} satisfies Printer));

const manufacturers = [...new Set(printers.map(printer => printer.manufacturerName))].map(name => ({
  id: `demo-${name.toLowerCase()}`, name,
} satisfies ManufacturerDto));
const printerModels = printers.map(printer => ({
  id: `model-${printer.id}`, name: printer.modelName,
  manufacturerId: `demo-${printer.manufacturerName.toLowerCase()}`,
  motionType: printer.motionType, defaultBackend: printer.backend,
} satisfies PrinterModelDto));

const dashboardQueue = [
  {
    job: {
      id: 'dashboard-job-1', name: 'Enclosure panel set', fileName: 'enclosure-panel.gcode',
      status: 'Printing', priority: PrintJobPriority.High, queuePosition: 1,
      createdAtUtc: captureTime, updatedAtUtc: captureTime, queuedAtUtc: captureTime,
      copies: 1, completedCopies: 0, remainingCopies: 1,
      estimatedPrintTimeSeconds: 5400,
    },
    gcodeFile: {
      id: 'dashboard-file-1', name: 'Enclosure panel set', fileName: 'enclosure-panel.gcode',
      fileSizeBytes: 2400000, materialType: 'PLA', createdAtUtc: captureTime,
    },
    assignedPrinter: {
      id: 'demo-1', name: 'Atlas', modelName: 'Elegoo Centauri Carbon',
      status: 'Printing', isOnline: true,
    },
  },
  {
    job: {
      id: 'dashboard-job-2', name: 'Sensor mount', fileName: 'sensor-mount.gcode',
      status: 'Queued', priority: PrintJobPriority.Normal, queuePosition: 2,
      createdAtUtc: captureTime, updatedAtUtc: captureTime, queuedAtUtc: captureTime,
      copies: 1, completedCopies: 0, remainingCopies: 1,
    },
    gcodeFile: {
      id: 'dashboard-file-2', name: 'Sensor mount', fileName: 'sensor-mount.gcode',
      fileSizeBytes: 1800000, materialType: 'PETG', createdAtUtc: captureTime,
    },
    assignedPrinter: {
      id: 'demo-2', name: 'Birch', modelName: 'Snapmaker U1',
      status: 'Printing', isOnline: true,
    },
  },
] satisfies QueuedPrintJobWithFileMetaDto[];

const dashboardHistory = {
  entries: [
    {
      id: 'dashboard-history-1', jobName: 'Cable guide', printerName: 'Cedar',
      status: 'Completed', completionPercentage: 100, startedAtUtc: captureTime,
      completedAtUtc: captureTime, actualPrintTimeSeconds: 3720,
    },
    {
      id: 'dashboard-history-2', jobName: 'Sensor bracket', printerName: 'Delta',
      status: 'Failed', completionPercentage: 42, startedAtUtc: captureTime,
      completedAtUtc: captureTime, actualPrintTimeSeconds: 1680, failureReason: 'Synthetic fixture failure',
    },
  ],
  totalCount: 2, currentPage: 1, pageSize: 5,
  stats: {
    totalCompleted: 1, totalFailed: 1, totalCancelled: 0,
    successRate: 50, averageDurationMinutes: 45, totalPrintTimeMinutes: 90,
  },
} satisfies QueueHistoryPageDto;

const dashboardServices = [
  {
    serviceId: 'demo-monitor', displayName: 'Printer health monitor',
    description: 'Checks printer connectivity', category: 'Monitoring',
    isRunning: true, isEnabled: true, lastRunTime: captureTime,
    successfulRuns: 120, failedRuns: 0, intervalSeconds: 60,
  },
  {
    serviceId: 'demo-scheduler', displayName: 'Maintenance scheduler',
    description: 'Plans upcoming maintenance', category: 'Maintenance',
    isRunning: true, isEnabled: true, lastRunTime: captureTime,
    successfulRuns: 24, failedRuns: 0, intervalSeconds: 3600,
  },
  {
    serviceId: 'demo-backup', displayName: 'Workshop backup',
    description: 'Synthetic service status for visual coverage', category: 'Storage',
    isRunning: false, isEnabled: true, lastRunTime: captureTime,
    lastError: 'Synthetic fixture error', lastErrorTime: captureTime,
    successfulRuns: 3, failedRuns: 1, intervalSeconds: 3600,
  },
] satisfies BackgroundServiceStatus[];

const dashboardServicesSummary = {
  totalServices: 3, runningServices: 2, enabledServices: 3,
  disabledServices: 0, servicesWithErrors: 1,
  byCategory: {
    Monitoring: { total: 1, running: 1, withErrors: 0 },
    Maintenance: { total: 1, running: 1, withErrors: 0 },
    Storage: { total: 1, running: 0, withErrors: 1 },
  },
} satisfies BackgroundServicesSummary;

const dashboardCostSummary = {
  totalCostUsd: 42.75, averageCostPerJobUsd: 21.38, jobsWithCostData: 2,
  totalMaterialCostUsd: 24.5, totalEnergyCostUsd: 5.25,
  totalMachineTimeCostUsd: 10, totalLaborCostUsd: 3,
  mostExpensiveMaterial: 'PETG', mostExpensiveMaterialCost: 16,
} satisfies CostSummary;

const dashboardTasks = {
  generatedAt: captureTime,
  groups: [{
    anchorKind: 'now',
    tasks: [{
      id: 'dashboard-task-1', taskType: 'MaintenanceDue',
      entityType: 'Printer', entityId: 'demo-1',
      title: 'Review Atlas nozzle wear', description: 'Synthetic dashboard task',
      status: 'Pending', priority: 'High', createdAt: captureTime,
      relatedEntityCount: 1, anchorKind: 'now',
      anchorAtUtc: captureTime, sourceKind: 'maintenance', sourceId: 'dashboard-alert-1',
    }],
  }],
};

const dashboardAlerts = [
  {
    id: 'dashboard-alert-1', printerId: 'demo-1', title: 'Nozzle inspection overdue',
    message: 'Inspect the nozzle before the next long print.',
    severity: 4, status: MaintenanceAlertStatus.Active, printerHoursAtTrigger: 120,
    createdAt: captureTime, updatedAt: captureTime,
  },
] satisfies MaintenanceAlert[];

const dashboardResponses: Record<string, unknown> = {
  'GET /api/tasks': dashboardTasks,
  'GET /api/job-queue-analytics': dashboardQueue,
  'GET /api/job-queue-analytics/history': dashboardHistory,
  'GET /api/job-scheduling/timezones': [{
    id: 'UTC',
    displayName: 'Coordinated Universal Time',
    offset: '+00:00',
  } satisfies TimezoneInfo],
  'GET /api/statistics/costs/summary': dashboardCostSummary,
  'GET /api/services': dashboardServices,
  'GET /api/services/summary': dashboardServicesSummary,
  'GET /api/maintenance/alerts': dashboardAlerts,
};

const queueResponses: Record<string, unknown> = {
  'GET /api/job-queue': [{
    printerId: 'demo-3',
    printerName: 'Cedar',
    printerModel: 'Voron Trident 300',
    isAvailable: true,
    queuedJobsCount: 0,
    nozzleDiameter: 0.4,
    supportedMaterials: ['PLA', 'PETG'],
  } satisfies QueueOverviewDto],
  'GET /api/job-queue-analytics': dashboardQueue,
  'GET /api/job-queue-analytics/history': dashboardHistory,
  'GET /api/dispatch-settings': {
    autoDispatchEnabled: false,
    autoDispatchMode: 'Manual',
    idleThresholdSeconds: 30,
    minimumScoreThreshold: 0,
    maxConcurrentDispatches: 1,
    loadBalancingStrategy: 'Balanced',
    updatedAt: captureTime,
  },
  'GET /api/job-queue-analytics/stats': {
    totalQueued: 1,
    totalPrinting: 1,
    totalPaused: 0,
    averageWaitTimeMinutes: 15,
    assumptions: { workdayStartHourUtc: 8, workdayEndHourUtc: 17, bedClearMinutes: 10 },
    byModel: {},
  },
};

const printerMaintenanceResponses: Record<string, unknown> = {
  'GET /api/printers/demo-1/details': {
    id: 'demo-1',
    name: 'Atlas',
    modelName: 'Elegoo Centauri Carbon',
    backend: PrinterBackend.SDCP,
    toolheads: [],
    supportsPerToolAttribution: false,
  },
  'GET /api/maintenance/printers/demo-1/statistics': {
    id: 'maintenance-stats-demo-1',
    printerId: 'demo-1',
    totalPrintHours: 120,
    totalJobsCompleted: 30,
    totalJobsFailed: 1,
    totalFilamentUsedGrams: 2500,
    totalFilamentUsedMeters: 800,
    lastSyncTime: captureTime,
    createdAt: captureTime,
    updatedAt: captureTime,
  },
  'GET /api/maintenance/printers/demo-1/logs': [],
  'GET /api/maintenance/schedules': [],
  'GET /api/maintenance/printers/demo-1/alerts': [],
  'GET /api/maintenance/upcoming': [],
};

const filesLibraryResponses: Record<string, unknown> = {
  'POST /api/3d-models/files/query': {
    items: [],
    totalItems: 0,
    totalSize: 0,
    page: 1,
    pageSize: 50,
    totalPages: 0,
  },
  'GET /api/3d-models/folders': [],
  'GET /api/gcode-files/folders': [],
};

const filesLibraryPopulatedResponses: Record<string, unknown> = {
  'POST /api/3d-models/files/query': {
    items: [{
      source: 'Model',
      id: 'fixture-model-01',
      path: '/workshop',
      name: 'Synthetic calibration bracket.stl',
      fileName: 'calibration-bracket.stl',
      fileSize: 184320,
      fileType: 'stl',
      uploadedAt: captureTime,
      url: '/api/3d-models/file/fixture-model-01',
    }],
    totalItems: 1,
    totalSize: 184320,
    page: 1,
    pageSize: 50,
    totalPages: 1,
  },
};

const deletedPartialDeleteFile = {
  source: 'Model',
  id: 'fixture-model-01',
  path: '/workshop',
  name: 'Synthetic calibration bracket.stl',
  fileName: 'calibration-bracket.stl',
  fileSize: 184320,
  fileType: 'stl',
  uploadedAt: captureTime,
  url: '/api/3d-models/file/fixture-model-01',
};

const blockedPartialDeleteFile = {
  source: 'Model',
  id: 'fixture-model-02',
  path: '/workshop',
  name: 'Synthetic active-job guard.stl',
  fileName: 'active-job-guard.stl',
  fileSize: 98304,
  fileType: 'stl',
  uploadedAt: captureTime,
  url: '/api/3d-models/file/fixture-model-02',
};

const filesLibraryPartialDeleteResponse = {
  'POST /api/3d-models/files/query': {
    items: [deletedPartialDeleteFile, blockedPartialDeleteFile],
    totalItems: 2,
    totalSize: 282624,
    page: 1,
    pageSize: 50,
    totalPages: 1,
  },
};

const filesLibraryGcodeResponses: Record<string, unknown> = {
  'POST /api/3d-models/files/query': {
    items: [{
      source: 'Gcode',
      id: 'fixture-gcode-01',
      path: '/gcode',
      name: 'Synthetic calibration bracket.gcode',
      fileName: 'calibration-bracket.gcode',
      fileSize: 24576,
      fileType: 'gcode',
      uploadedAt: captureTime,
      url: '/api/gcode-files/file/fixture-gcode-01',
      extractedPrintTime: 1800,
      extractedMaterial: 'PLA',
      extractedNozzleDiameter: 0.4,
    }],
    totalItems: 1,
    totalSize: 24576,
    page: 1,
    pageSize: 50,
    totalPages: 1,
  },
};

const captureUser = {
  ...toolbarRouteUser,
  id: 'marketing-operator',
  username: 'demo',
  displayName: 'Demo operator',
  email: 'demo@example.invalid',
};

const systemCapabilities = {
  architecture: 'x64',
  slicingEnabled: true,
  modelFilesEnabled: true,
  thumbnailGenerationEnabled: true,
  gcodeUploadEnabled: true,
  clientThumbnailUploadEnabled: true,
  idempotentModelUploadEnabled: true,
  modelThumbnailReplacementEnabled: true,
};

const nfcDevice = {
  id: 'fixture-nfc-device-01',
  name: 'Demo NFC Reader',
  ipAddress: '192.0.2.42',
  printerId: 'demo-1',
  printerName: 'Atlas',
  firmwareVersion: '1.2.0',
  wifiRssi: -58,
  nfcReaderOk: true,
  freeHeap: 65536,
  isOnline: true,
  lastHeartbeat: captureTime,
  lastScanAt: captureTime,
  lastScannedSpoolId: 1,
  createdAt: captureTime,
} satisfies NfcDeviceDto;
const nfcScanHistory = [{
  id: 'fixture-nfc-scan-01',
  nfcDeviceId: nfcDevice.id,
  deviceName: nfcDevice.name,
  spoolId: 1,
  tagFormat: 'NTAG215',
  materialType: 'PLA',
  brandName: 'Demo workshop',
  action: 'spool_set',
  scannedAt: captureTime,
}] satisfies NfcScanHistoryDto[];

const syntheticPrinterGroup = {
  id: 'fixture-printer-group-01',
  name: 'Synthetic fixture group',
  description: 'Fixture-only printer group.',
  createdDate: captureTime,
  updatedDate: captureTime,
  printerCount: 0,
};

const responses: Record<string, unknown> = {
  ...Object.fromEntries(Object.entries(inventory).map(([path, value]) => [`GET ${path}`, value])),
  ...Object.fromEntries(Object.entries(toolbarAdminSeeds).map(([path, value]) => [`GET ${path}`, value])),
  'GET /api/users/marketing-operator/apikeys': toolbarAdminSeeds['/api/users/toolbar-fixture-user/apikeys'],
  'GET /api/setup/status': { needsSetup: false },
  'GET /api/setup/bootstrap': { baseUrl: '' },
  'GET /api/auth/me': captureUser,
  'GET /api/settings/SignalR': { logLevel: 'Error', consoleLoggingEnabled: false },
  'GET /api/settings/FarmIdentity': { farmName: 'PrintFarmer', timeZone: 'UTC' },
  'GET /api/settings/Slicer': { enabled: true },
  'GET /api/settings/NetworkDiscovery': { enableDiscovery: false },
  'GET /api/workers/': [],
  'GET /api/cameras/display': [],
  'GET /api/printer-groups': [],
  'GET /api/nfc/bindings': [],
  'GET /api/notifications': [],
  'GET /api/notifications/unread/count': { unreadCount: 0 },
  'GET /api/tasks/count': { count: 0 },
  'GET /api/health': { status: 'Healthy', results: {}, startup: { phase: 'Ready', ready: true, failed: false } },
  'GET /api/healthz': { status: 'Healthy' },
  'GET /api/system/info': {
    app: { version: 'demo', uptime: '01:00:00', hostname: 'demo-workshop' },
    cpu: { cores: 4, usagePercent: 10 }, memory: { usedBytes: 1000000000, totalBytes: 8000000000 },
    disk: { usedBytes: 1000000000, totalBytes: 8000000000, archiveBytes: 0, databaseBytes: 0 },
    services: [], database: { engine: 'SQLite', version: 'demo', printerCount: printers.length, archiveCount: 0 },
  },
  'GET /api/admin/release-updates': {
    status: 'UpToDate', updateAvailable: false, installedVersion: 'demo', channel: 'Stable',
    latestVersion: 'demo', latestTag: 'demo', latestReleaseName: 'Demo',
    latestPublishedAt: captureTime, releaseUrl: undefined,
    lastCheckedAt: captureTime, lastSuccessfulCheckAt: captureTime,
    isStale: false, error: undefined, checkIntervalSeconds: 3600, upgradeDocsUrl: '/docs',
  },
  'GET /api/job-queue/changes/watermark': { latestSequence: 0 } satisfies QueueChangeWatermark,
  'GET /api/job-queue/changes': { afterSequence: 0, nextSequence: 0, hasMore: false, events: [] } satisfies QueueChangeFeed,
  'GET /api/catalog/manufacturers': manufacturers,
  'GET /api/catalog/printer-models': printerModels,
  'GET /api/tags/objects': [],
  'GET /api/job-queue-analytics/printer-summaries': [],
  'GET /api/auto-dispatch/status': { globalEnabled: false, printers: [] },
  'GET /api/job-queue/subscription-resources': { printerIds: [], jobIds: [], projectIds: [] },
  'GET /api/system/capabilities': systemCapabilities,
  'GET /api/printers': printers,
  'GET /api/printers/summary': printers,
  'GET /api/printers/camera-urls': [],
  'GET /api/printers/backend-capabilities': [],
  'GET /api/printers/filament-coverage': [],
  'GET /api/projects': [
    { id: 'project-demo-1', name: 'Workshop enclosure', description: 'Panels, brackets and cable guides for the demo workshop.',
      status: PrintProjectStatus.InProgress, priority: 1, totalFiles: 6, completedFiles: 3,
      totalPrints: 12, completedPrints: 7, progressPercent: 58,
      createdAt: captureTime },
    { id: 'project-demo-2', name: 'Sensor mounting kit', description: 'A repeatable mounting kit for the workbench.',
      status: PrintProjectStatus.InProgress, priority: 2, totalFiles: 4, completedFiles: 1,
      totalPrints: 8, completedPrints: 2, progressPercent: 25,
      createdAt: captureTime },
    { id: 'project-demo-3', name: 'Cable management', description: 'Clips and guides to finish the assembly.',
      status: PrintProjectStatus.Completed, priority: 1, totalFiles: 3, completedFiles: 3,
      totalPrints: 9, completedPrints: 9, progressPercent: 100,
      createdAt: captureTime, completedAt: captureTime },
  ] satisfies PrintProjectListDto[],
  'GET /api/project-templates': inventory['/api/project-templates'],
  'GET /api/spoolman/health': inventory['/api/spoolman/health'],
  'GET /api/spoolman/config': inventory['/api/spoolman/config'],
  'GET /api/spoolman/filter-options': inventory['/api/spoolman/filter-options'],
  'GET /api/spoolman/filaments/filter-options': inventory['/api/spoolman/filaments/filter-options'],
  'GET /api/spoolman/filaments': {
    items: ['Ocean blue', 'Signal orange', 'Graphite'].map((name, index) => ({
      id: index + 1, name: `${name} PLA`, material: 'PLA', vendor: 'Demo workshop',
      colorHex: ['#2463eb', '#ed7d31', '#536172'][index], diameter: 1.75,
      density: 1.24, weight: 1000, price: 20, settingsExtruderTemp: 210, settingsBedTemp: 60,
    } satisfies SpoolmanFilament)), totalCount: 3,
  },
  'GET /api/spoolman/spools': {
    items: ['Ocean blue', 'Signal orange', 'Graphite'].map((name, index) => ({
      id: index + 1, name: `${name} spool`, filamentId: index + 1, filamentName: `${name} PLA`,
      material: 'PLA', vendor: 'Demo workshop', colorHex: ['#2463eb', '#ed7d31', '#536172'][index],
      initialWeightG: 1000, remainingWeightG: [740, 460, 890][index],
      usedWeightG: [260, 540, 110][index], location: 'Demo workshop', archived: false,
    })), totalCount: 3,
  },
};

const setupNetworkSettings = {
  rowVersion: 'synthetic-setup-version',
  enableDiscovery: false,
  discoverySubnets: [],
  ports: [],
  clientTimeoutMs: 5000,
  requestDelayMs: 100,
  maxConcurrentRequests: 5,
  maxRetries: 3,
};

// Exact-origin assets only; an unexpected API, write or external request never reaches a server.
type CaptureOptions = {
  theme?: CaptureTheme;
  catalogFilaments?: boolean;
  dashboard?: boolean;
  adminWorkers?: boolean;
  profileImport?: boolean;
  inactiveUser?: boolean;
  printQueue?: boolean;
  printerMaintenance?: boolean;
  printerGroupsPopulated?: boolean;
  filesLibrary?: boolean;
  filesLibraryPopulated?: boolean;
  setupWizard?: boolean;
  slicerWorkers?: boolean;
  notificationDrawer?: boolean;
  contrastPreference?: 'high' | 'normal';
  nfcPairing?: boolean;
  slicerCapabilityState?: 'error' | 'disabled' | 'loading';
  nfcDeviceDetail?: boolean;
  filesLibraryGcode?: boolean;
  authenticated?: boolean;
  permissionFallback?: boolean;
  routeChunkFailure?: boolean | 'once';
  routeChunkLoading?: boolean;
  flow?: {
    setupCompletion?: boolean;
    overlayMutation?: 'task-create-success' | 'task-create-error' | 'location-create-success' | 'location-create-error' | 'printer-group-create-success' | 'printer-group-create-error' | 'file-delete-partial' | 'project-create-error' | 'nfc-link-error' | 'harvest-success' | 'harvest-error' | 'webhook-create-error' | 'role-create-error' | 'tag-create-error';
  };
};

export async function isolateCapture(context: BrowserContext, {
  theme = 'dark',
  catalogFilaments = false,
  dashboard = false,
  adminWorkers = false,
  profileImport = false,
  inactiveUser = false,
  printQueue = false,
  printerMaintenance = false,
  printerGroupsPopulated = false,
  filesLibrary = false,
  filesLibraryPopulated = false,
  setupWizard = false,
  slicerWorkers = false,
  notificationDrawer = false,
  contrastPreference,
  nfcPairing = false,
  slicerCapabilityState,
  nfcDeviceDetail = false,
  filesLibraryGcode = false,
  authenticated = true,
  permissionFallback = false,
  routeChunkFailure = false,
  routeChunkLoading = false,
  flow = {},
}: CaptureOptions = {}) {
  const violations: string[] = [];
  let setupCompleted = false;
  let partialFileDeleteCompleted = false;
  let routeChunkFailureCount = 0;
  let createdLocation: Record<string, unknown> | undefined;
  let createdPrinterGroup: Record<string, unknown> | undefined;
  let pendingRequestReleased = false;
  let releasePendingRequestResolver: (() => void) | undefined;
  const releasePendingRequest = () => {
    pendingRequestReleased = true;
    releasePendingRequestResolver?.();
  };
  const pendingRequest = () => pendingRequestReleased
    ? Promise.resolve()
    : new Promise<void>(resolve => { releasePendingRequestResolver = resolve; });
  await context.routeWebSocket(/.*/, socket => {
    const url = new URL(socket.url());
    if (url.origin === captureOrigin.replace('http:', 'ws:') && url.pathname === '/') {
      socket.send(JSON.stringify({ type: 'connected' }));
      return;
    }
    if (url.origin !== captureOrigin.replace('http:', 'ws:') || !/^\/hubs\/(printers|slicer|slicers|slicer-registry|queue|notifications|nfc|maintenance)$/.test(url.pathname)) {
      violations.push(`Unexpected socket: ${url.origin}${url.pathname}`);
      socket.close();
      return;
    }
    socket.onMessage(message => {
      if (typeof message !== 'string') {
        violations.push('Unexpected binary hub message');
        socket.close();
        return;
      }
      for (const record of message.split('\u001e').filter(Boolean)) {
        const frame = JSON.parse(record);
        if (frame.protocol === 'json') {
          socket.send('{}\u001e');
          if (nfcPairing && url.pathname === '/hubs/nfc') {
            socket.send(`${JSON.stringify({
              type: 1,
              target: 'nfctagunknown',
              arguments: [{ tagUid: 'FIXTURE-TAG-001', printerId: 'fixture-printer', readAt: captureTime }],
            })}\u001e`);
          }
        }
        else if (frame.type === 1 && frame.invocationId) {
          socket.send(`${JSON.stringify({ type: 3, invocationId: frame.invocationId })}\u001e`);
        }
      }
    });
  });
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const key = `${request.method()} ${url.pathname}`;
    if (
      (routeChunkLoading && url.origin === captureOrigin && url.pathname === '/src/features/slicer/pages/WorkerManagementPage.tsx') ||
      (slicerCapabilityState === 'loading' && key === 'GET /api/system/capabilities')
    ) {
      await pendingRequest();
    }
    const isWorkerManagementChunk = url.origin === captureOrigin && url.pathname === '/src/features/slicer/pages/WorkerManagementPage.tsx';
    if (
      isWorkerManagementChunk &&
      (routeChunkFailure === true || (routeChunkFailure === 'once' && routeChunkFailureCount++ === 0))
    ) {
      await route.abort('failed');
      return;
    }
    if (url.origin !== captureOrigin) {
      // The app's optional web-font stylesheet is deliberately fulfilled offline.
      if (url.origin === 'https://fonts.googleapis.com' && request.resourceType() === 'stylesheet') {
        await route.fulfill({ contentType: 'text/css', body: '/* Offline: system font fallback. */' });
        return;
      }
      violations.push(`External request blocked: ${request.method()} ${url.origin}${url.pathname}`);
      await route.abort('blockedbyclient');
      return;
    }
    if (url.pathname === '/src/main.tsx') {
      await route.fulfill({ contentType: 'application/javascript', body: "import '/e2e/fixtures/toolbar-routed-app.mjs';" });
      return;
    }
    if (/^\/(api|hubs)(\/|$)/.test(url.pathname)) {
      if (request.method() === 'POST' && /^\/hubs\/(printers|slicer|slicers|slicer-registry|queue|notifications|nfc|maintenance)\/negotiate$/.test(url.pathname)) {
        await route.fulfill({ json: {
          negotiateVersion: 1, connectionId: 'marketing', connectionToken: 'marketing',
          availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
        } });
        return;
      }
      if (flow.setupCompletion && key === 'POST /api/settings/NetworkDiscovery') {
        const submitted = request.postDataJSON();
        if (JSON.stringify(submitted) !== JSON.stringify(setupNetworkSettings)) {
          violations.push('Unexpected setup-completion settings payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic setup payload.' } });
          return;
        }
        setupCompleted = true;
        await route.fulfill({ json: setupNetworkSettings });
        return;
      }
      if (flow.overlayMutation === 'file-delete-partial' && key === 'DELETE /api/3d-models') {
        const expected = { ids: ['fixture-model-01', 'fixture-model-02'] };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected file-delete payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic file-delete payload.' } });
          return;
        }
        partialFileDeleteCompleted = true;
        await route.fulfill({ json: {
          deleted: 1,
          errors: 1,
          total: 2,
          results: [
            { id: 'fixture-model-01', status: 'Deleted' },
            { id: 'fixture-model-02', status: 'Blocked', reason: 'ActiveJobs' },
          ],
        } });
        return;
      }
      if (flow.overlayMutation === 'project-create-error' && key === 'POST /api/projects') {
        const expected = { name: 'Synthetic coverage project', priority: 0 };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected project-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic project payload.' } });
          return;
        }
        await route.fulfill({ status: 503, json: { message: 'Synthetic project creation failure.' } });
        return;
      }
      if (flow.overlayMutation === 'nfc-link-error' && key === 'POST /api/nfc/link') {
        const expected = {
          tagUid: 'FIXTURE-TAG-001',
          spoolId: 1,
          readAt: captureTime,
          printerId: 'fixture-printer',
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected NFC-link payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic NFC-link payload.' } });
          return;
        }
        await route.fulfill({ status: 503, json: { message: 'Synthetic NFC link failure.' } });
        return;
      }
      if (
        (flow.overlayMutation === 'harvest-success' || flow.overlayMutation === 'harvest-error') &&
        key === 'POST /api/job-queue/dashboard-history-1/harvest'
      ) {
        const submitted = request.postDataJSON();
        const operationKey = typeof submitted?.operationKey === 'string' ? submitted.operationKey : '';
        if (
          Object.keys(submitted ?? {}).join(',') !== 'operationKey' ||
          operationKey.length === 0
        ) {
          violations.push('Unexpected harvest payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic harvest payload.' } });
          return;
        }
        if (flow.overlayMutation === 'harvest-error') {
          await route.fulfill({ status: 503, json: { message: 'Synthetic harvest service failure.' } });
          return;
        }
        await route.fulfill({ json: {
          printJobId: 'dashboard-history-1',
          harvestedAt: captureTime,
          binId: 'fixture-bin-01',
          binCode: 'BIN-01',
          alreadyHarvested: false,
          adjustments: [{
            id: 'fixture-harvest-adjustment-01',
            partInventoryId: 'fixture-part-01',
            sku: 'BRACKET-001',
            binId: 'fixture-bin-01',
            binCode: 'BIN-01',
            delta: 2,
            resultingBalance: 7,
            reason: 'harvest',
            printJobId: 'dashboard-history-1',
            operationKey,
            createdAt: captureTime,
          }],
          outputs: [{
            sequence: 1,
            partInventoryId: 'fixture-part-01',
            partSku: 'BRACKET-001',
            quantity: 2,
            expectedBinId: 'fixture-bin-01',
            expectedBinCode: 'BIN-01',
            actualBinId: 'fixture-bin-01',
            actualBinCode: 'BIN-01',
            origin: 'GcodeMapping',
            overrideApplied: false,
            createdAt: captureTime,
          }],
        } });
        return;
      }
      if (flow.overlayMutation === 'webhook-create-error' && key === 'POST /api/webhooks') {
        const expected = {
          name: 'Synthetic coverage webhook',
          url: 'https://example.invalid/fixture-only',
          eventTypes: '*',
          maxConsecutiveFailures: 10,
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected webhook-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic webhook payload.' } });
          return;
        }
        await route.fulfill({ status: 503, json: { message: 'Synthetic webhook creation failure.' } });
        return;
      }
      if (flow.overlayMutation === 'role-create-error' && key === 'POST /api/admin/roles') {
        const expected = { name: 'coverage_role', displayName: 'Coverage role' };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected role-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic role payload.' } });
          return;
        }
        await route.fulfill({ status: 503, json: { message: 'Synthetic role creation failure.' } });
        return;
      }
      if (flow.overlayMutation === 'tag-create-error' && key === 'POST /api/tags') {
        const expected = {
          name: 'Synthetic coverage tag',
          color: '#d74242',
          description: '',
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected tag-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic tag payload.' } });
          return;
        }
        await route.fulfill({ status: 503, json: { message: 'Synthetic tag creation failure.' } });
        return;
      }
      if (
        (flow.overlayMutation === 'task-create-success' || flow.overlayMutation === 'task-create-error') &&
        key === 'POST /api/tasks'
      ) {
        const expected = {
          title: 'Synthetic coverage task',
          description: 'Fixture-only task mutation.',
          priority: 'High',
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected task-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic task payload.' } });
          return;
        }
        if (flow.overlayMutation === 'task-create-error') {
          await route.fulfill({ status: 503, json: { message: 'Synthetic task creation failure.' } });
          return;
        }
        await route.fulfill({ status: 201, json: {
          id: 'synthetic-task-coverage-01',
          taskType: 'Custom',
          entityType: 'Manual',
          entityId: '',
          title: expected.title,
          description: expected.description,
          status: 'Pending',
          priority: expected.priority,
          createdAt: captureTime,
          relatedEntityCount: 1,
        } });
        return;
      }
      if (
        (flow.overlayMutation === 'location-create-success' || flow.overlayMutation === 'location-create-error') &&
        key === 'POST /api/locations'
      ) {
        const expected = {
          name: 'Synthetic coverage location',
          description: 'Fixture-only location mutation.',
          parentId: null,
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected location-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic location payload.' } });
          return;
        }
        if (flow.overlayMutation === 'location-create-error') {
          await route.fulfill({ status: 503, json: { message: 'Synthetic location creation failure.' } });
          return;
        }
        createdLocation = {
          id: 'synthetic-location-coverage-01',
          name: expected.name,
          description: expected.description,
          path: `/${expected.name}`,
          parentId: null,
          depth: 0,
          sortOrder: 0,
          printerCount: 0,
          totalPrinterCount: 0,
          createdAt: captureTime,
          modifiedAt: captureTime,
          isActive: true,
        };
        await route.fulfill({ status: 201, json: createdLocation });
        return;
      }
      if (
        (flow.overlayMutation === 'printer-group-create-success' || flow.overlayMutation === 'printer-group-create-error') &&
        key === 'POST /api/printer-groups'
      ) {
        const expected = {
          name: 'Synthetic coverage group',
          description: 'Fixture-only printer group mutation.',
        };
        if (JSON.stringify(request.postDataJSON()) !== JSON.stringify(expected)) {
          violations.push('Unexpected printer-group-create payload.');
          await route.fulfill({ status: 400, json: { message: 'Unexpected synthetic printer-group payload.' } });
          return;
        }
        if (flow.overlayMutation === 'printer-group-create-error') {
          await route.fulfill({ status: 503, json: { message: 'Synthetic printer-group creation failure.' } });
          return;
        }
        createdPrinterGroup = {
          id: 'synthetic-group-coverage-01',
          ...expected,
          createdDate: captureTime,
          updatedDate: captureTime,
          printerCount: 0,
        };
        await route.fulfill({ status: 201, json: createdPrinterGroup });
        return;
      }
      if (
        !Object.hasOwn(responses, key) &&
        !((dashboard || flow.setupCompletion) && Object.hasOwn(dashboardResponses, key)) &&
        !(printQueue && Object.hasOwn(queueResponses, key)) &&
        !(printerMaintenance && Object.hasOwn(printerMaintenanceResponses, key)) &&
        !(filesLibrary && Object.hasOwn(filesLibraryResponses, key)) &&
        !(filesLibrary && flow.overlayMutation === 'file-delete-partial' && Object.hasOwn(filesLibraryPartialDeleteResponse, key)) &&
        !(filesLibrary && filesLibraryGcode && Object.hasOwn(filesLibraryGcodeResponses, key)) &&
        !(nfcDeviceDetail && key === 'GET /api/nfc-devices/fixture-nfc-device-01/history')
      ) {
        violations.push(`Missing capture fixture: ${key}`);
        await route.fulfill({ status: 503, json: { message: `Missing capture fixture: ${key}` } });
        return;
      }
      if (slicerCapabilityState === 'error' && key === 'GET /api/system/capabilities') {
        await route.fulfill({ status: 503, json: { message: 'Synthetic capability lookup failure.' } });
        return;
      }
      const response = notificationDrawer && key === 'GET /api/notifications'
        ? [{
          id: 'fixture-notification-unread',
          userId: captureUser.id,
          type: 'SystemAlert',
          subject: 'Synthetic printer needs attention',
          body: 'A fixture-only printer status requires review.',
          isRead: false,
          createdAt: captureTime,
        }]
        : notificationDrawer && key === 'GET /api/notifications/unread/count'
          ? { unreadCount: 1 }
        : setupWizard && key === 'GET /api/setup/status'
        ? { needsSetup: !setupCompleted }
        : flow.setupCompletion && key === 'GET /api/settings/NetworkDiscovery'
          ? setupNetworkSettings
        : nfcDeviceDetail && key === 'GET /api/nfc-devices'
          ? [nfcDevice]
        : nfcDeviceDetail && key === 'GET /api/nfc-devices/fixture-nfc-device-01/history'
          ? nfcScanHistory
        : inactiveUser && key === 'GET /api/auth/me'
        ? { ...captureUser, isActive: false }
        : permissionFallback && key === 'GET /api/auth/me'
          ? { ...captureUser, roles: ['operator'], permissions: [] }
        : printerMaintenance && Object.hasOwn(printerMaintenanceResponses, key)
          ? printerMaintenanceResponses[key]
        : filesLibrary && filesLibraryGcode && Object.hasOwn(filesLibraryGcodeResponses, key)
          ? filesLibraryGcodeResponses[key]
        : filesLibrary && flow.overlayMutation === 'file-delete-partial' && Object.hasOwn(filesLibraryPartialDeleteResponse, key)
          ? partialFileDeleteCompleted
            ? {
              ...filesLibraryPartialDeleteResponse['POST /api/3d-models/files/query'],
              items: [blockedPartialDeleteFile],
              totalItems: 1,
              totalSize: blockedPartialDeleteFile.fileSize,
            }
            : filesLibraryPartialDeleteResponse['POST /api/3d-models/files/query']
        : filesLibrary && Object.hasOwn(filesLibraryResponses, key)
          ? filesLibraryPopulated ? filesLibraryPopulatedResponses[key] : filesLibraryResponses[key]
        : printQueue && Object.hasOwn(queueResponses, key)
          ? queueResponses[key]
        : flow.overlayMutation === 'location-create-success' && createdLocation &&
          (key === 'GET /api/locations' || key === 'GET /api/locations/tree') &&
          Array.isArray(responses[key])
          ? [...responses[key], key === 'GET /api/locations/tree' ? { ...createdLocation, children: [] } : createdLocation]
        : printerGroupsPopulated && key === 'GET /api/printer-groups'
          ? [syntheticPrinterGroup]
        : flow.overlayMutation === 'printer-group-create-success' && createdPrinterGroup &&
          key === 'GET /api/printer-groups' && Array.isArray(responses[key])
          ? [...responses[key], createdPrinterGroup]
        : (dashboard || flow.setupCompletion) && Object.hasOwn(dashboardResponses, key)
        ? dashboardResponses[key]
        : slicerWorkers && key === 'GET /api/workers/'
          ? toolbarAdminSeeds['/api/workers/']
        : adminWorkers && key === 'GET /api/workers/'
          ? toolbarAdminSeeds['/api/workers/']
          : adminWorkers && key === 'GET /api/workers/by-status/Online'
            ? toolbarAdminSeeds['/api/workers/by-status/Online']
        : profileImport && key === 'GET /api/catalog/manufacturers'
          ? toolbarAdminSeeds['/api/catalog/manufacturers']
          : profileImport && key === 'GET /api/catalog/printer-models'
            ? toolbarAdminSeeds['/api/catalog/printer-models']
        : key === 'GET /api/filament-types' && catalogFilaments
          ? catalogInventory['/api/filament-types']
          : slicerCapabilityState === 'disabled' && key === 'GET /api/system/capabilities'
            ? { ...systemCapabilities, slicingEnabled: false, architecture: 'arm64', platformNote: 'Slicing is disabled.' }
          : responses[key];
      await route.fulfill({ json: response });
      return;
    }
    const isLocalViewerAsset = url.pathname === '/assets/hdri/studio_small_03_1k.hdr';
    if (request.method() !== 'GET' || (
      ['fetch', 'xhr', 'eventsource'].includes(request.resourceType()) &&
      url.pathname !== '/assets/orcaslicer/manifest.json' &&
      !isLocalViewerAsset
    )) {
      violations.push(`Unexpected request: ${key}`);
      await route.abort('blockedbyclient');
      return;
    }
    await route.continue();
  });
  await context.addInitScript(({ theme, contrastPreference, authenticated }) => {
    const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=/g, '');
    if (authenticated) {
      localStorage.setItem('auth-token', `${encode({ alg: 'none' })}.${encode({ sub: 'marketing-operator', exp: 4102444800 })}.synthetic-not-a-credential`);
    }
    localStorage.setItem('pf-theme', theme);
    if (contrastPreference) localStorage.setItem('pf-contrast', contrastPreference);
    for (const id of ['dashboard', 'printers', 'print-queue', 'settings', 'gcode-library', 'projects', 'filaments']) {
      localStorage.setItem(`pf-tour-seen-${id}`, 'true');
    }
  }, { theme, contrastPreference, authenticated });
  return {
    violations,
    releasePendingRequest,
  };
}
