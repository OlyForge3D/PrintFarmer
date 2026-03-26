import { apiClient } from '@/services/api';
import type { BasicHealthStatus, DetailedHealthStatus, HealthStatus } from '@/types/api';
import {
  ApiError,
  CatalogContext,
  CreateExtruderModelDto,
  CreateFilamentTypeRequest,
  CreateHotendModelDto,
  CreateNozzleModelDto,
  CreatePrinterDto,
  CreateToolheadModelDto,
  ExtruderModelDefinition,
  FailureDetectionEvent,
  FilamentPresets,
  FilamentTypeDto,
  GcodeFile,
  GcodeHarvestOperation,
  GcodeHarvestStatus,
  HistoryJob,
  HistoryListResponse,
  HistoryTotals,
  HotendModelDefinition,
  ManufacturerDto,
  ManufacturersByContext,
  NozzleModelDefinition,
  PrinterBackendCapabilitiesDto,
  PrinterCapabilitiesDto,
  PrinterModelDto,
  Printer,
  PrinterCameraUrls,
  PrinterDetails,
  PrinterFast,
  QueuedPrintJobWithFileMetaDto,
  StartDiscoveryRequest,
  ToolheadModelDefinition,
  UpdateExtruderModelDto,
  UpdateHotendModelDto,
  UpdateNozzleModelDto,
  UpdatePrinterDto,
  UpdateToolheadModelDefDto,
  FileHealthSummaryDto,
  FileHealthAuditDto,
  FileIssuesSummaryDto,
  FileHealthDetailDto,
  SpoolmanDbFilamentEntry,
  SpoolmanDbMaterialEntry,
  SpoolmanDbImportRequest,
  OfdBrand,
  OfdBrandDetail,
  OfdFlattenedEntry,
  OfdImportRequest,
  SpoolmanBulkUpdateFilamentsRequest,
  SpoolmanBulkUpdateResult,
  SpoolmanVendor,
  SpoolmanMaterial,
  SpoolmanUpdateFilamentRequest,
  SpoolmanFilament,
  SpoolmanUpdateSpoolRequest,
  SpoolmanBulkUpdateSpoolsRequest,
  NfcDeviceDto,
  CreateNfcDeviceDto,
  UpdateNfcDeviceDto,
  NfcScanHistoryDto,
  PagedResponse,
  NotificationDto,
  ObicoServer,
  CreateObicoServerRequest,
  JobStateHistoryDto,
  UpdateObicoServerRequest,
  TimezoneInfo,
} from '@/types/api';
import type { UseQueryOptions } from '@tanstack/react-query';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { toast } from 'sonner';

// Type alias for query options that omit queryKey and queryFn (already provided by hooks)
type QueryOptions<TData, TError = ApiError> = Omit<UseQueryOptions<TData, TError>, 'queryKey' | 'queryFn'>;

// ============ Query Keys ============
export const queryKeys = {
  printers: ['printers'] as const,
  printer: (id: string) => ['printers', id] as const,
  printerDetails: (id: string) => ['printers', id, 'details'] as const,
  printerHistory: (id: string, options?: { limit?: number; start?: number; since?: Date; before?: Date; order?: string }) => 
    ['printers', id, 'history', options] as const,
  printerHistoryJob: (printerId: string, jobId: string) => ['printers', printerId, 'history', jobId] as const,
  printerHistoryTotals: (printerId: string) => ['printers', printerId, 'history', 'totals'] as const,
  manufacturers: ['manufacturers'] as const,
  models: (manufacturerId?: string) => ['models', manufacturerId] as const,
  hotendModels: ['hotend-models'] as const,
  extruderModels: ['extruder-models'] as const,
  toolheadModels: ['toolhead-models'] as const,
  nozzleModels: ['nozzle-models'] as const,
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
  notifications: ['notifications'] as const,
  unreadCount: ['notifications', 'unread-count'] as const,
  notificationPreferences: ['notifications', 'preferences'] as const,
  costSummary: (days?: number) => ['costs', 'summary', days] as const,
  costs: ['costs'] as const,
  costsByPrinter: (days?: number) => ['costs', 'by-printer', days] as const,
  costsByMaterial: (days?: number) => ['costs', 'by-material', days] as const,
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
} as const;

// ============ Printer Hooks ============

export function usePrinters(options?: QueryOptions<Printer[]>) {
  return useQuery({
    queryKey: queryKeys.printers,
    queryFn: () => apiClient.getPrinters(),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function usePrintersFast(includeDisabled = false, options?: QueryOptions<PrinterFast[]>) {
  const queryKey = includeDisabled
    ? [...queryKeys.printers, 'fast', 'include-disabled']
    : [...queryKeys.printers, 'fast'];
  return useQuery({
    queryKey,
    queryFn: () => apiClient.getPrintersFast(includeDisabled),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function usePrinterCameraUrls(options?: QueryOptions<PrinterCameraUrls[]>) {
  return useQuery({
    queryKey: [...queryKeys.printers, 'camera-urls'],
    queryFn: () => apiClient.getPrinterCameraUrls(),
    staleTime: 300000, // 5 minutes - camera URLs are more static
    ...options,
  });
}

export function usePrinterBackendCapabilities(options?: QueryOptions<PrinterBackendCapabilitiesDto[]>) {
  return useQuery({
    queryKey: [...queryKeys.printers, 'backend-capabilities'],
    queryFn: () => apiClient.getPrinterBackendCapabilities(),
    staleTime: 600000, // 10 minutes - backend capabilities rarely change
    ...options,
  });
}

export function usePrinterBackendCapabilitiesSingle(printerId: string | null, options?: QueryOptions<PrinterBackendCapabilitiesDto>) {
  return useQuery({
    queryKey: [...queryKeys.printers, printerId, 'backend-capabilities'],
    queryFn: () => {
      if (!printerId) throw new Error('printerId is required');
      return apiClient.getPrinterBackendCapabilitiesSingle(printerId);
    },
    enabled: !!printerId,
    staleTime: 600000, // 10 minutes
    ...options,
  });
}

/**
 * Hook to get printers with camera URLs.
 * Camera URLs are now included directly in PrinterFastDto from the database,
 * so this simply transforms PrinterFast to Printer interface.
 * 
 * SIMPLIFIED: No longer merges data from multiple endpoints - camera URLs
 * are stored in DB during printer discovery and returned with printer data.
 */
export function usePrintersWithCameraUrls(includeDisabled = false) {
  const printersQuery = usePrintersFast(includeDisabled);

  return useMemo(() => {
    if (printersQuery.data) {
      // Transform PrinterFast to Printer interface - camera URLs are already included
      const printers: Printer[] = printersQuery.data.map(printerFast => ({
        ...printerFast,
        isReachable: printerFast.isOnline,
        // Camera URLs from database (discovered at registration)
        cameraStreamUrl: printerFast.cameraStreamUrl,
        cameraSnapshotUrl: printerFast.cameraSnapshotUrl,
        // Optional runtime properties (not available from fast endpoint)
        progress: undefined,
        jobName: undefined,
        thumbnailUrl: undefined,
        x: undefined,
        y: undefined,
        z: undefined,
        hotendTemp: undefined,
        bedTemp: undefined,
        hotendTarget: undefined,
        bedTarget: undefined,
        spoolInfo: undefined,
      } as Printer));

      return {
        data: printers,
        isLoading: printersQuery.isLoading,
        isError: printersQuery.isError,
        error: printersQuery.error,
        refetch: printersQuery.refetch,
        isSuccess: printersQuery.isSuccess,
        isFetching: printersQuery.isFetching,
      };
    }
    
    // Return loading/error states when data is not ready
    return {
      data: undefined,
      isLoading: printersQuery.isLoading,
      isError: printersQuery.isError,
      error: printersQuery.error,
      refetch: printersQuery.refetch,
      isSuccess: false,
      isFetching: printersQuery.isFetching,
    };
  }, [printersQuery.data, printersQuery.refetch, printersQuery.isLoading, printersQuery.isError, printersQuery.error, printersQuery.isSuccess, printersQuery.isFetching]);
}

export function usePrinter(id: string, options?: QueryOptions<Printer>) {
  return useQuery({
    queryKey: queryKeys.printer(id),
    queryFn: () => apiClient.getPrinter(id),
    enabled: !!id,
    staleTime: 30000,
    ...options,
  });
}

export function usePrinterDetails(id: string, options?: QueryOptions<PrinterDetails>) {
  return useQuery({
    queryKey: queryKeys.printerDetails(id),
    queryFn: () => apiClient.getPrinterDetails(id),
    enabled: !!id,
    staleTime: 30000,
    retry: (failureCount, error) => {
      // Don't retry on 404 (printer not found, likely deleted)
      if (error?.statusCode === 404) return false;
      // Retry other errors up to 2 times
      return failureCount < 2;
    },
    ...options,
  });
}

export function useFailureDetectionHistory(
  printerId?: string,
  take = 5,
  options?: QueryOptions<FailureDetectionEvent[]>
) {
  return useQuery({
    queryKey: queryKeys.failureDetectionHistory(printerId, take),
    queryFn: () => apiClient.getFailureDetectionHistory({ printerId, take }),
    enabled: !!printerId,
    staleTime: 30_000,
    ...options,
  });
}

export function usePrintSessionTimeline(
  jobId?: string,
  options?: QueryOptions<JobStateHistoryDto>
) {
  return useQuery({
    queryKey: queryKeys.printSessionTimeline(jobId),
    queryFn: () => apiClient.getAnalyticsJobStateHistory(jobId!),
    enabled: !!jobId,
    staleTime: 30_000,
    retry: (failureCount, error) => {
      if (error?.statusCode === 404) {
        return false;
      }

      return failureCount < 2;
    },
    ...options,
  });
}

export function useCreatePrinter() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (printer: CreatePrinterDto) => apiClient.createPrinter(printer),
    onMutate: async (printer) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.printers });
      const previous = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      const temp: Printer = {
        id: `temp-${Date.now()}`,
        name: printer.name,
        backendUrl: printer.serverUrl,
        notes: printer.notes,
        isOnline: false,
        isReachable: false,
        backend: printer.backend,
        manufacturerName: undefined,
        modelName: undefined,
        apiKey: printer.apiKey,
        originalServerUrl: printer.originalServerUrl || printer.serverUrl,
        ipAddress: undefined,
        state: 'Creating...'
      } as Printer;
      if (previous) {
        queryClient.setQueryData(queryKeys.printers, [...previous, temp]);
      } else {
        queryClient.setQueryData(queryKeys.printers, [temp]);
      }
      return { previous, tempId: temp.id };
    },
    onError: (_err, _vars, ctx) => {
      if (ctx?.previous) {
        queryClient.setQueryData(queryKeys.printers, ctx.previous);
      } else {
        // Remove entire query to guarantee no stale temp remains
        queryClient.removeQueries({ queryKey: queryKeys.printers });
      }
      toast.error('Failed to create printer');
    },
    onSuccess: (created, _vars, ctx) => {
      const list = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      if (list) {
        queryClient.setQueryData(queryKeys.printers, list.map(p => (ctx?.tempId && p.id === ctx.tempId) ? (created as Printer) : p));
      }
      toast.success(`Printer "${created.name}" created`);
    },
    onSettled: (_data, error) => {
      if (!error) {
        queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      }
    }
  });
}

export function useUpdatePrinter() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, printer }: { id: string; printer: UpdatePrinterDto }) =>
      apiClient.updatePrinter(id, printer),
    onMutate: async ({ id, printer }) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.printers });
      await queryClient.cancelQueries({ queryKey: queryKeys.printer(id) });

      const previousPrinters = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      const previousPrinter = queryClient.getQueryData<Printer>(queryKeys.printer(id));

      if (previousPrinters) {
        const next = previousPrinters.map(p => p.id === id ? { ...p, ...printer } as Printer : p);
        queryClient.setQueryData(queryKeys.printers, next);
      }
      if (previousPrinter) {
        queryClient.setQueryData(queryKeys.printer(id), { ...previousPrinter, ...printer });
      }

      return { previousPrinters, previousPrinter };
    },
    onError: (_err, { id }, context) => {
      if (context?.previousPrinters) {
        queryClient.setQueryData(queryKeys.printers, context.previousPrinters);
      }
      if (context?.previousPrinter) {
        queryClient.setQueryData(queryKeys.printer(id), context.previousPrinter);
      }
    },
    onSuccess: (updated, { id }) => {
      // Ensure final server response is applied
      queryClient.setQueryData(queryKeys.printer(id), updated);
      const list = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      if (list) {
        queryClient.setQueryData(queryKeys.printers, list.map(p => p.id === id ? updated : p));
      }
    },
    onSettled: (_data, _error, { id }) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      queryClient.invalidateQueries({ queryKey: queryKeys.printer(id) });
      queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(id) });
    }
  });
}

export function useDeletePrinter() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => apiClient.deletePrinter(id),
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.printers });
      const previous = queryClient.getQueryData<Printer[]>(queryKeys.printers);
      if (previous) {
        queryClient.setQueryData(queryKeys.printers, previous.filter(p => p.id !== id));
      }
      return { previous };
    },
    onError: (_err, _id, ctx) => {
      if (ctx?.previous) queryClient.setQueryData(queryKeys.printers, ctx.previous);
      toast.error('Failed to delete printer');
    },
    onSuccess: () => {
      toast.success('Printer deleted');
    },
    onSettled: (_d, _e, id) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      queryClient.removeQueries({ queryKey: queryKeys.printer(id!) });
      queryClient.removeQueries({ queryKey: queryKeys.printerDetails(id!) });
    }
  });
}

export function useDiscoverPrinters() {
  return useMutation({
    mutationFn: () => apiClient.discoverPrinters(),
  });
}

export function useStartDiscoveryStream() {
  return useMutation({
    mutationFn: (request?: StartDiscoveryRequest) => apiClient.startDiscoveryStream(request),
  });
}

export function useCancelDiscoveryStream() {
  return useMutation({
    mutationFn: (sessionId: string) => apiClient.cancelDiscoveryStream(sessionId),
  });
}

// ============ Catalog Hooks ============

export function useManufacturers(options?: QueryOptions<ManufacturerDto[]>) {
  return useQuery({
    queryKey: queryKeys.manufacturers,
    queryFn: () => apiClient.getManufacturers(),
    staleTime: 300000, // 5 minutes - manufacturers change rarely
    ...options,
  });
}

export function useCreateManufacturer() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (name: string) => apiClient.createManufacturer(name),
    onMutate: async (name) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.manufacturers });
      const previous = queryClient.getQueryData<ManufacturerDto[]>(queryKeys.manufacturers);
      const temp: ManufacturerDto = { id: `temp-${Date.now()}`, name };
      if (previous) {
        queryClient.setQueryData(queryKeys.manufacturers, [...previous, temp]);
      } else {
        queryClient.setQueryData(queryKeys.manufacturers, [temp]);
      }
      return { previous };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.previous) queryClient.setQueryData(queryKeys.manufacturers, ctx.previous);
      toast.error('Failed to create manufacturer');
    },
    onSuccess: (created) => {
      const list = queryClient.getQueryData<ManufacturerDto[]>(queryKeys.manufacturers);
      if (list) {
        queryClient.setQueryData(queryKeys.manufacturers, list.map(m => m.id.startsWith('temp-') && m.name === created.name ? created : m));
      }
      toast.success(`Manufacturer "${created.name}" created`);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.manufacturers });
    }
  });
}

export function useModels(manufacturerId?: string, options?: QueryOptions<PrinterModelDto[]>) {
  return useQuery({
    queryKey: queryKeys.models(manufacturerId),
    queryFn: () => apiClient.getModels(manufacturerId),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useModelDefaultCapabilities(modelId?: string, options?: QueryOptions<PrinterCapabilitiesDto | null>) {
  return useQuery({
    queryKey: ['model-default-capabilities', modelId],
    queryFn: () => modelId ? apiClient.getModelDefaultCapabilities(modelId) : Promise.resolve(null),
    enabled: !!modelId,
    staleTime: 300000, // 5 minutes - default capabilities are static
    ...options,
  });
}

export function useCreateModel() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (model: Omit<PrinterModelDto, 'id'>) => apiClient.createModel(model),
    onMutate: async (model) => {
      await queryClient.cancelQueries({ queryKey: queryKeys.models(model.manufacturerId) });
      const key = queryKeys.models(model.manufacturerId);
      const previous = queryClient.getQueryData<PrinterModelDto[]>(key);
      const temp: PrinterModelDto = { id: `temp-${Date.now()}`, ...model } as PrinterModelDto;
      if (previous) {
        queryClient.setQueryData(key, [...previous, temp]);
      } else {
        queryClient.setQueryData(key, [temp]);
      }
      return { previous, key, model };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.previous && ctx.key) queryClient.setQueryData(ctx.key, ctx.previous);
      toast.error('Failed to create model');
    },
    onSuccess: (created, vars, ctx) => {
      if (ctx?.key) {
        const list = queryClient.getQueryData<PrinterModelDto[]>(ctx.key);
        if (list) {
          queryClient.setQueryData(ctx.key, list.map(m => m.id.startsWith('temp-') && m.name === created.name ? created : m));
        }
      }
      toast.success(`Model "${created.name}" created`);
    },
    onSettled: (_d, _e, vars) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.models(vars.manufacturerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.models() });
    }
  });
}

// ============ Component Model Hooks ============

export function useHotendModels(options?: QueryOptions<HotendModelDefinition[]>) {
  return useQuery({
    queryKey: queryKeys.hotendModels,
    queryFn: () => apiClient.getHotendModels(),
    staleTime: 300000, // 5 minutes - component models change rarely
    ...options,
  });
}

export function useExtruderModels(options?: QueryOptions<ExtruderModelDefinition[]>) {
  return useQuery({
    queryKey: queryKeys.extruderModels,
    queryFn: () => apiClient.getExtruderModels(),
    staleTime: 300000, // 5 minutes - component models change rarely
    ...options,
  });
}

export function useToolheadModels(options?: QueryOptions<ToolheadModelDefinition[]>) {
  return useQuery({
    queryKey: queryKeys.toolheadModels,
    queryFn: () => apiClient.getToolheadModels(),
    staleTime: 300000, // 5 minutes - component models change rarely
    ...options,
  });
}

export function useNozzleModels(options?: QueryOptions<NozzleModelDefinition[]>) {
  return useQuery({
    queryKey: queryKeys.nozzleModels,
    queryFn: () => apiClient.getNozzleModels(),
    staleTime: 300000, // 5 minutes - component models change rarely
    ...options,
  });
}

// ============ Component Model Mutation Hooks ============

// Hotend mutations
export function useCreateHotendModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateHotendModelDto) => apiClient.createHotendModel(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.hotendModels });
      toast.success('Hotend model created');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to create hotend model: ${error.message}`);
    },
  });
}

export function useUpdateHotendModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateHotendModelDto }) =>
      apiClient.updateHotendModel(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.hotendModels });
      toast.success('Hotend model updated');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to update hotend model: ${error.message}`);
    },
  });
}

export function useDeleteHotendModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteHotendModel(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.hotendModels });
      toast.success('Hotend model deleted');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to delete hotend model: ${error.message}`);
    },
  });
}

// Extruder mutations
export function useCreateExtruderModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateExtruderModelDto) => apiClient.createExtruderModel(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.extruderModels });
      toast.success('Extruder model created');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to create extruder model: ${error.message}`);
    },
  });
}

export function useUpdateExtruderModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateExtruderModelDto }) =>
      apiClient.updateExtruderModel(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.extruderModels });
      toast.success('Extruder model updated');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to update extruder model: ${error.message}`);
    },
  });
}

export function useDeleteExtruderModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteExtruderModel(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.extruderModels });
      toast.success('Extruder model deleted');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to delete extruder model: ${error.message}`);
    },
  });
}

// Toolhead mutations
export function useCreateToolheadModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateToolheadModelDto) => apiClient.createToolheadModel(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.toolheadModels });
      toast.success('Toolhead model created');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to create toolhead model: ${error.message}`);
    },
  });
}

export function useUpdateToolheadModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateToolheadModelDefDto }) =>
      apiClient.updateToolheadModel(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.toolheadModels });
      toast.success('Toolhead model updated');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to update toolhead model: ${error.message}`);
    },
  });
}

export function useDeleteToolheadModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteToolheadModel(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.toolheadModels });
      toast.success('Toolhead model deleted');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to delete toolhead model: ${error.message}`);
    },
  });
}

// Nozzle mutations
export function useCreateNozzleModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateNozzleModelDto) => apiClient.createNozzleModel(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nozzleModels });
      toast.success('Nozzle model created');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to create nozzle model: ${error.message}`);
    },
  });
}

export function useUpdateNozzleModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateNozzleModelDto }) =>
      apiClient.updateNozzleModel(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nozzleModels });
      toast.success('Nozzle model updated');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to update nozzle model: ${error.message}`);
    },
  });
}

export function useDeleteNozzleModel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteNozzleModel(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nozzleModels });
      toast.success('Nozzle model deleted');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to delete nozzle model: ${error.message}`);
    },
  });
}

// Contextual Manufacturer query
export function useManufacturersByContext(
  context: CatalogContext,
  options?: QueryOptions<ManufacturersByContext>
) {
  return useQuery({
    queryKey: ['manufacturers', 'by-context', context],
    queryFn: () => apiClient.getManufacturersByContext(context),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

// ============ Settings Hooks ============

export function useFilamentTypes(options?: QueryOptions<FilamentTypeDto[]>) {
  return useQuery({
    queryKey: queryKeys.filamentTypes,
    queryFn: () => apiClient.getFilamentTypes(),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useFilamentTypesPaged(
  page = 1,
  pageSize = 50,
  search?: string,
  options?: QueryOptions<PagedResponse<FilamentTypeDto>>
) {
  return useQuery({
    queryKey: queryKeys.filamentTypesPaged(page, pageSize, search),
    queryFn: () => apiClient.getFilamentTypesPaged(page, pageSize, search),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useFilamentPresets(options?: QueryOptions<FilamentPresets>) {
  return useQuery({
    queryKey: queryKeys.filamentPresets,
    queryFn: () => apiClient.getFilamentPresets(),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useSaveFilamentPresets() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (presets: FilamentPresets) => apiClient.saveFilamentPresets(presets),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

export function useImportFilamentTypesFromSpoolman() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => apiClient.importFilamentTypesFromSpoolman(),
    onSuccess: () => {
      // Invalidate both filament types and presets since new types were added
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

export function useExportFilamentTypesCsv() {
  return useMutation({
    mutationFn: () => apiClient.exportFilamentTypesCsv(),
  });
}

export function useImportFilamentTypesCsv() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (file: File) => apiClient.importFilamentTypesCsv(file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

export function useSpoolmanDbFilaments(options?: QueryOptions<SpoolmanDbFilamentEntry[]>) {
  return useQuery({
    queryKey: queryKeys.spoolmanDbFilaments,
    queryFn: () => apiClient.getSpoolmanDbFilaments(),
    staleTime: 3600000, // 1 hour – data rarely changes
    enabled: false, // Only fetch when explicitly triggered
    ...options,
  });
}

export function useSpoolmanDbMaterials(options?: QueryOptions<SpoolmanDbMaterialEntry[]>) {
  return useQuery({
    queryKey: queryKeys.spoolmanDbMaterials,
    queryFn: () => apiClient.getSpoolmanDbMaterials(),
    staleTime: 3600000,
    enabled: false,
    ...options,
  });
}

export function useImportFromSpoolmanDb() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (request: SpoolmanDbImportRequest) => apiClient.importFromSpoolmanDb(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

// ─── Open Filament Database hooks ────────────────────────────────────────

export function useOfdBrands(options?: QueryOptions<OfdBrand[]>) {
  return useQuery({
    queryKey: queryKeys.ofdBrands,
    queryFn: () => apiClient.getOfdBrands(),
    staleTime: 600_000,
    ...options,
  });
}

export function useOfdBrandMaterials(brandSlug: string, options?: QueryOptions<OfdBrandDetail>) {
  return useQuery({
    queryKey: queryKeys.ofdBrandMaterials(brandSlug),
    queryFn: () => apiClient.getOfdBrandMaterials(brandSlug),
    staleTime: 600_000,
    enabled: !!brandSlug,
    ...options,
  });
}

export function useOfdFilaments(
  brandSlug: string,
  materialSlug: string,
  brandName: string,
  materialName: string,
  options?: QueryOptions<OfdFlattenedEntry[]>
) {
  return useQuery({
    queryKey: queryKeys.ofdFilaments(brandSlug, materialSlug),
    queryFn: () => apiClient.getOfdFilaments(brandSlug, materialSlug, brandName, materialName),
    staleTime: 600_000,
    enabled: !!brandSlug && !!materialSlug,
    ...options,
  });
}

export function useImportFromOfd() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (request: OfdImportRequest) => apiClient.importFromOfd(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

export function useSpoolmanVendors(options?: QueryOptions<SpoolmanVendor[]>) {
  return useQuery({
    queryKey: queryKeys.spoolmanVendors,
    queryFn: () => apiClient.getVendors(),
    staleTime: 300_000, // 5 minutes
    ...options,
  });
}

export function useSpoolmanMaterials(options?: QueryOptions<SpoolmanMaterial[]>) {
  return useQuery({
    queryKey: queryKeys.spoolmanMaterials,
    queryFn: () => apiClient.getMaterials(),
    staleTime: 300_000, // 5 minutes
    ...options,
  });
}

export function useBulkUpdateFilaments() {
  const queryClient = useQueryClient();
  return useMutation<SpoolmanBulkUpdateResult, ApiError, SpoolmanBulkUpdateFilamentsRequest>({
    mutationFn: (request: SpoolmanBulkUpdateFilamentsRequest) => apiClient.bulkUpdateFilaments(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.spoolmanVendors });
    },
  });
}

export function useUpdateFilament() {
  const queryClient = useQueryClient();
  return useMutation<SpoolmanFilament, ApiError, { id: number; request: SpoolmanUpdateFilamentRequest }>({
    mutationFn: ({ id, request }) => apiClient.updateFilament(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.spoolmanVendors });
    },
  });
}

export function useCreateFilament() {
  const queryClient = useQueryClient();
  return useMutation<SpoolmanFilament, ApiError, SpoolmanUpdateFilamentRequest>({
    mutationFn: (request) => apiClient.createFilament(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.spoolmanVendors });
    },
  });
}

export function useDeleteFilament() {
  const queryClient = useQueryClient();
  return useMutation<void, ApiError, number>({
    mutationFn: (id: number) => apiClient.deleteFilament(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.spoolmanVendors });
    },
  });
}

export function useBulkDeleteFilaments() {
  const queryClient = useQueryClient();
  return useMutation<SpoolmanBulkUpdateResult, ApiError, number[]>({
    mutationFn: (filamentIds: number[]) => apiClient.bulkDeleteFilaments(filamentIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.spoolmanVendors });
    },
  });
}

export function useExportSpoolmanFilamentsCsv() {
  return useMutation({
    mutationFn: () => apiClient.exportSpoolmanFilamentsCsv(),
  });
}

// ---- Spool CRUD hooks ----

export function useCreateSpool() {
  return useMutation<unknown, ApiError, SpoolmanUpdateSpoolRequest>({
    mutationFn: (request) => apiClient.createSpool(request),
  });
}

export function useUpdateSpool() {
  return useMutation<unknown, ApiError, { id: number; request: SpoolmanUpdateSpoolRequest }>({
    mutationFn: ({ id, request }) => apiClient.updateSpool(id, request),
  });
}

export function useDeleteSpool() {
  return useMutation<void, ApiError, number>({
    mutationFn: (id: number) => apiClient.deleteSpool(id),
  });
}

export function useBulkUpdateSpools() {
  return useMutation<SpoolmanBulkUpdateResult, ApiError, SpoolmanBulkUpdateSpoolsRequest>({
    mutationFn: (request: SpoolmanBulkUpdateSpoolsRequest) => apiClient.bulkUpdateSpools(request),
  });
}

export function useBulkDeleteSpools() {
  return useMutation<SpoolmanBulkUpdateResult, ApiError, number[]>({
    mutationFn: (spoolIds: number[]) => apiClient.bulkDeleteSpools(spoolIds),
  });
}

export function useImportSpoolmanFilamentsCsv() {
  return useMutation<SpoolmanBulkUpdateResult, ApiError, File>({
    mutationFn: (file: File) => apiClient.importSpoolmanFilamentsCsv(file),
  });
}

export function useImportSpoolmanSpoolsCsv() {
  return useMutation<SpoolmanBulkUpdateResult, ApiError, File>({
    mutationFn: (file: File) => apiClient.importSpoolmanSpoolsCsv(file),
  });
}

export function useSyncExternalMaterials() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => apiClient.syncExternalMaterials(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

export function useCreateFilamentType() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (dto: CreateFilamentTypeRequest) => apiClient.createFilamentType(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
    },
  });
}

export function useUpdateFilamentType() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: CreateFilamentTypeRequest }) => 
      apiClient.updateFilamentType(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
    },
  });
}

export function useDeleteFilamentType() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => apiClient.deleteFilamentType(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["filament-types"] });
    },
  });
}

// ============ G-code Library Hooks ============

export function useGcodeFiles(page = 1, pageSize = 50, options?: QueryOptions<GcodeFile[]>) {
  return useQuery({
    queryKey: queryKeys.gcodeFiles(page, pageSize),
    queryFn: async () => {
  const resp = await apiClient.getGcodeFiles(page, pageSize);
  return resp;
    },
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useGcodeFile(id: string, options?: QueryOptions<GcodeFile>) {
  return useQuery({
    queryKey: queryKeys.gcodeFile(id),
    queryFn: () => apiClient.getGcodeFile(id),
    enabled: !!id,
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useUploadGcodeFile() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ file, description, tags }: { file: File; description?: string; tags?: string[] }) =>
      apiClient.uploadGcodeFile(file, description, tags),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
    },
  });
}

export function useDeleteGcodeFile() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: string) => apiClient.deleteGcodeFile(id),
    onSuccess: (_, id) => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      queryClient.removeQueries({ queryKey: queryKeys.gcodeFile(id) });
    },
  });
}

// ============ Harvest Operations Hooks ============

export function useHarvestOperations(printerId?: string, options?: QueryOptions<GcodeHarvestOperation[]>) {
  return useQuery({
    queryKey: queryKeys.harvestOperations(printerId),
    queryFn: () => apiClient.getHarvestOperations(printerId),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function useHarvestOperation(id: string, options?: QueryOptions<GcodeHarvestOperation>) {
  return useQuery({
    queryKey: queryKeys.harvestOperation(id),
    queryFn: () => apiClient.getHarvestOperation(id),
    enabled: !!id,
    refetchInterval: (query) => {
      const data = query.state.data;
      return data?.status === GcodeHarvestStatus.Running ? 5000 : false; // Poll while running
    },
    staleTime: 0, // Always fresh for running operations
    ...options,
  });
}

export function useStartHarvestOperation() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (printerId: string) => apiClient.startHarvestOperation(printerId),
    onSuccess: (_, printerId) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.harvestOperations(printerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.harvestOperations() });
    },
  });
}

export function useCancelHarvestOperation() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (operationId: string) => apiClient.cancelHarvestOperation(operationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.harvestOperations() });
    },
  });
}

export function useRestartHarvestDiscovery() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (operationId: string) => apiClient.restartHarvestDiscovery(operationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.harvestOperations() });
    },
  });
}

// ============ Job Queue Hooks ============

export function useJobQueue(printerId?: string, options?: QueryOptions<QueuedPrintJobWithFileMetaDto[]>) {
  return useQuery({
    queryKey: queryKeys.jobQueue(printerId),
    queryFn: () => apiClient.getJobQueue(printerId),
    staleTime: 30000, // 30 seconds
    refetchInterval: 30000, // Auto-refresh every 30 seconds
    ...options,
  });
}

export function useQueuePrintJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ printerId, gcodeFileId, priority = 0 }: { printerId: string; gcodeFileId: string; priority?: number }) =>
      apiClient.queuePrintJob(printerId, gcodeFileId, priority),
    onMutate: async (vars) => {
      const { printerId, gcodeFileId, priority = 0 } = vars;
      await queryClient.cancelQueries({ queryKey: queryKeys.jobQueue(printerId) });
      await queryClient.cancelQueries({ queryKey: queryKeys.jobQueue() });
      const printerQueueKey = queryKeys.jobQueue(printerId);
      const globalQueueKey = queryKeys.jobQueue();
      const prevPrinterQueue = queryClient.getQueryData<QueuedPrintJobWithFileMetaDto[]>(printerQueueKey);
      const prevGlobalQueue = queryClient.getQueryData<QueuedPrintJobWithFileMetaDto[]>(globalQueueKey);
      const tempId = `temp-${Date.now()}`;
      const temp: QueuedPrintJobWithFileMetaDto = {
        job: {
          id: tempId,
          name: 'Queuing...',
          gcodeFileId,
          fileName: 'Queuing...',
          assignedPrinterId: printerId,
          status: 'Queued',
          priority,
          queuePosition: 0,
          createdAtUtc: new Date().toISOString(),
          updatedAtUtc: new Date().toISOString(),
          queuedAtUtc: new Date().toISOString(),
        },
      };
      if (prevPrinterQueue) queryClient.setQueryData(printerQueueKey, [temp, ...prevPrinterQueue]); else queryClient.setQueryData(printerQueueKey, [temp]);
      if (prevGlobalQueue) queryClient.setQueryData(globalQueueKey, [temp, ...prevGlobalQueue]); else queryClient.setQueryData(globalQueueKey, [temp]);
      return { prevPrinterQueue, prevGlobalQueue, printerQueueKey, globalQueueKey, tempId };
    },
    onError: (_e, vars, ctx) => {
      if (ctx?.prevPrinterQueue && ctx.printerQueueKey) {
        queryClient.setQueryData(ctx.printerQueueKey, ctx.prevPrinterQueue);
      } else if (ctx?.printerQueueKey) {
        const cur = queryClient.getQueryData<QueuedPrintJobWithFileMetaDto[]>(ctx.printerQueueKey);
        if (cur) queryClient.setQueryData(ctx.printerQueueKey, cur.filter(j => j.job.id !== ctx?.tempId && !j.job.id.startsWith('temp-')) || undefined);
      }
      if (ctx?.prevGlobalQueue && ctx.globalQueueKey) {
        queryClient.setQueryData(ctx.globalQueueKey, ctx.prevGlobalQueue);
      } else if (ctx?.globalQueueKey) {
        const cur = queryClient.getQueryData<QueuedPrintJobWithFileMetaDto[]>(ctx.globalQueueKey);
        if (cur) queryClient.setQueryData(ctx.globalQueueKey, cur.filter(j => j.job.id !== ctx?.tempId && !j.job.id.startsWith('temp-')) || undefined);
      }
      toast.error('Failed to queue print job');
    },
    onSuccess: (_job, vars) => {
      // Invalidate and refetch to get proper server data instead of manual update
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue(vars.printerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() });
      toast.success('Print job queued');
    },
    onSettled: (_d, _e, vars) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue(vars.printerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() });
    }
  });
}

export function useCancelPrintQueueJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (jobId: string) => apiClient.cancelPrintQueueJob(jobId),
    onMutate: async (jobId) => {
      const allQueues = queryClient.getQueriesData<QueuedPrintJobWithFileMetaDto[]>({ queryKey: ['job-queue'] });
      const snapshots = allQueues.map(([key, value]) => ({ key, value }));
      allQueues.forEach(([key, jobs]) => {
        if (jobs) {
          queryClient.setQueryData<QueuedPrintJobWithFileMetaDto[]>(
            key as readonly unknown[], 
            jobs.map(j => j.job.id === jobId ? { ...j, job: { ...j.job, status: 'Cancelled', updatedAtUtc: new Date().toISOString() } } : j)
          );
        }
      });
      return { snapshots };
    },
    onError: (_e, _id, ctx) => {
      ctx?.snapshots?.forEach(s => queryClient.setQueryData<QueuedPrintJobWithFileMetaDto[]>(s.key as readonly unknown[], s.value));
      toast.error('Failed to cancel job');
    },
    onSuccess: () => {
      toast.success('Job cancelled');
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['job-queue'] });
    }
  });
}

export function useDeletePrintQueueJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (jobId: string) => apiClient.deletePrintQueueJob(jobId),
    onMutate: async (jobId) => {
      const allQueues = queryClient.getQueriesData<QueuedPrintJobWithFileMetaDto[]>({ queryKey: ['job-queue'] });
      const snapshots = allQueues.map(([key, value]) => ({ key, value }));
      allQueues.forEach(([key, jobs]) => {
        if (jobs) {
          queryClient.setQueryData<QueuedPrintJobWithFileMetaDto[]>(key as readonly unknown[], jobs.filter(j => j.job.id !== jobId));
        }
      });
      return { snapshots };
    },
    onError: (_e, _id, ctx) => {
      ctx?.snapshots?.forEach(s => queryClient.setQueryData<QueuedPrintJobWithFileMetaDto[]>(s.key as readonly unknown[], s.value));
      toast.error('Failed to delete job');
    },
    onSuccess: () => {
      toast.success('Job deleted');
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['job-queue'] });
    }
  });
}

// ============ Printer Operations Hooks ============

export function useUploadGcodeToPrinter() {
  return useMutation({
    mutationFn: ({ printerId, file }: { printerId: string; file: File }) =>
      apiClient.uploadGcodeToPrinter(printerId, file),
  });
}

export function useStartPrintFromFile() {
  return useMutation({
    mutationFn: ({ printerId, fileName }: { printerId: string; fileName: string }) =>
      apiClient.startPrintFromFile(printerId, fileName),
  });
}

// ============ Health Check Hooks ============

export function useHealthStatus(options?: QueryOptions<HealthStatus>) {
  return useQuery({
    queryKey: queryKeys.health,
    queryFn: async () => {
      const raw = (await apiClient.getHealthStatus()) as unknown; // backend detailed or basic
      if (typeof raw === 'object' && raw !== null) {
        const r = raw as Record<string, unknown>;
        if (typeof r.results === 'object' && r.results !== null) {
          const startup = r.startup && typeof r.startup === 'object' ? r.startup as Record<string, unknown> : undefined;
          return {
            kind: 'detailed',
            status: String(r.status ?? 'unknown'),
            totalChecksDuration: String(r.totalChecksDuration ?? ''),
            startup: startup ? {
              phase: String(startup.phase ?? 'Unknown'),
              ready: Boolean(startup.ready),
              failed: Boolean(startup.failed),
              failureMessage: startup.failureMessage ? String(startup.failureMessage) : undefined,
              failureStackTrace: startup.failureStackTrace ? String(startup.failureStackTrace) : undefined,
              initStartedUtc: startup.initStartedUtc ? String(startup.initStartedUtc) : undefined,
              initCompletedUtc: startup.initCompletedUtc ? String(startup.initCompletedUtc) : undefined,
              initDurationMs: typeof startup.initDurationMs === 'number' ? startup.initDurationMs : undefined,
            } : undefined,
            results: r.results as DetailedHealthStatus['results']
          } satisfies DetailedHealthStatus;
        }
        return { kind: 'basic', status: String(r.status ?? 'unknown') } satisfies BasicHealthStatus;
      }
      return { kind: 'basic', status: 'unknown' } satisfies BasicHealthStatus;
    },
    staleTime: 30000,
    ...options,
  });
}

export function useBasicHealth(options?: QueryOptions<BasicHealthStatus>) {
  return useQuery({
    queryKey: ['health', 'basic'],
    queryFn: async () => {
      const raw = (await apiClient.getBasicHealth()) as unknown;
      if (typeof raw === 'object' && raw !== null) {
        const r = raw as Record<string, unknown>;
        return { kind: 'basic', status: String(r.status ?? 'unknown') } satisfies BasicHealthStatus;
      }
      return { kind: 'basic', status: 'unknown' } satisfies BasicHealthStatus;
    },
    staleTime: 10000,
    ...options,
  });
}

// ============ Printer History Hooks ============

export function usePrinterHistory(
  printerId: string, 
  options?: {
    limit?: number;
    start?: number;
    since?: Date;
    before?: Date;
    order?: string;
  },
  queryOptions?: QueryOptions<HistoryListResponse>
) {
  return useQuery({
    queryKey: queryKeys.printerHistory(printerId, options),
    queryFn: () => apiClient.getPrinterHistory(printerId, options),
    staleTime: 30000,
    enabled: !!printerId,
    ...queryOptions,
  });
}

export function usePrinterHistoryJob(
  printerId: string, 
  jobId: string, 
  queryOptions?: QueryOptions<HistoryJob>
) {
  return useQuery({
    queryKey: queryKeys.printerHistoryJob(printerId, jobId),
    queryFn: () => apiClient.getPrinterHistoryJob(printerId, jobId),
    staleTime: 300000, // History jobs don't change often
    enabled: !!printerId && !!jobId,
    ...queryOptions,
  });
}

export function usePrinterHistoryTotals(
  printerId: string,
  queryOptions?: QueryOptions<HistoryTotals>
) {
  return useQuery({
    queryKey: queryKeys.printerHistoryTotals(printerId),
    queryFn: () => apiClient.getPrinterHistoryTotals(printerId),
    staleTime: 300000, // History totals don't change often
    enabled: !!printerId,
    ...queryOptions,
  });
}

// ============ File Consistency Hooks ============

export function useFileHealthSummary(options?: QueryOptions<FileHealthSummaryDto>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.health,
    queryFn: () => apiClient.getFileHealthSummary(),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useFileAuditHistory(
  pageSize: number = 20,
  options?: QueryOptions<FileHealthAuditDto[]>
) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.auditHistory(pageSize),
    queryFn: () => apiClient.getFileAuditHistory(pageSize),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useFilesWithIssues(options?: QueryOptions<FileIssuesSummaryDto>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.filesWithIssues,
    queryFn: () => apiClient.getFilesWithIssues(),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useModel3DHealth(id: string, options?: QueryOptions<FileHealthDetailDto>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.model3DHealth(id),
    queryFn: () => apiClient.getModel3DHealth(id),
    staleTime: 60000, // 1 minute
    enabled: !!id,
    ...options,
  });
}

export function useGcodeFileHealth(id: string, options?: QueryOptions<FileHealthDetailDto>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.gcodeFileHealth(id),
    queryFn: () => apiClient.getGcodeFileHealth(id),
    staleTime: 60000, // 1 minute
    enabled: !!id,
    ...options,
  });
}

// ============ NFC Device Hooks ============

export function useNfcDevices(options?: QueryOptions<NfcDeviceDto[]>) {
  return useQuery({
    queryKey: queryKeys.nfcDevices,
    queryFn: () => apiClient.getNfcDevices(),
    staleTime: 30_000,
    refetchInterval: 30_000,
    ...options,
  });
}

export function useNfcDevice(id: string, options?: QueryOptions<NfcDeviceDto>) {
  return useQuery({
    queryKey: queryKeys.nfcDevice(id),
    queryFn: () => apiClient.getNfcDevice(id),
    staleTime: 30_000,
    refetchInterval: 30_000,
    enabled: !!id,
    ...options,
  });
}

export function useNfcDeviceScanHistory(id: string, options?: QueryOptions<NfcScanHistoryDto[]>) {
  return useQuery({
    queryKey: queryKeys.nfcDeviceHistory(id),
    queryFn: () => apiClient.getNfcDeviceScanHistory(id),
    staleTime: 30_000,
    refetchInterval: 30_000,
    enabled: !!id,
    ...options,
  });
}

export function useCreateNfcDevice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateNfcDeviceDto) => apiClient.createNfcDevice(dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nfcDevices });
      toast.success('NFC device registered');
    },
    onError: (err: ApiError) => toast.error(`Failed to register device: ${err.message}`),
  });
}

export function useUpdateNfcDevice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateNfcDeviceDto }) => apiClient.updateNfcDevice(id, dto),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nfcDevices });
      toast.success('NFC device updated');
    },
    onError: (err: ApiError) => toast.error(`Failed to update device: ${err.message}`),
  });
}

export function useDeleteNfcDevice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteNfcDevice(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.nfcDevices });
      toast.success('NFC device removed');
    },
    onError: (err: ApiError) => toast.error(`Failed to remove device: ${err.message}`),
  });
}

// ============ Notification Hooks ============

export function useNotifications(options?: QueryOptions<NotificationDto[]> & { limit?: number }) {
  const limit = options?.limit;
  return useQuery({
    queryKey: [...queryKeys.notifications, limit],
    queryFn: () => apiClient.getNotifications(limit),
    staleTime: 30_000, // 30s
    ...options,
  });
}

export function useUnreadCount(options?: QueryOptions<number>) {
  return useQuery({
    queryKey: queryKeys.unreadCount,
    queryFn: () => apiClient.getUnreadCount(),
    staleTime: 10_000, // 10s
    ...options,
  });
}

export function useMarkNotificationAsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationId: string) => apiClient.markNotificationAsRead(notificationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
    },
    onError: (err: ApiError) => toast.error(`Failed to mark as read: ${err.message}`),
  });
}

export function useMarkAllNotificationsAsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationIds: string[]) => apiClient.markMultipleNotificationsAsRead(notificationIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
      toast.success('All notifications marked as read');
    },
    onError: (err: ApiError) => toast.error(`Failed to mark as read: ${err.message}`),
  });
}

export function useDeleteNotification() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (notificationId: string) => apiClient.deleteNotification(notificationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.notifications });
      queryClient.invalidateQueries({ queryKey: queryKeys.unreadCount });
      toast.success('Notification deleted');
    },
    onError: (err: ApiError) => toast.error(`Failed to delete: ${err.message}`),
  });
}

// ============ Cost Tracking Hooks ============

export function useCostSummary(days?: number, options?: QueryOptions<import("@/types/api").CostSummary>) {
  return useQuery({
    queryKey: queryKeys.costSummary(days),
    queryFn: () => apiClient.getCostSummary(days),
    staleTime: 300_000, // 5 minutes - cost data is relatively stable
    ...options,
  });
}

export function useCostsByPrinter(days?: number, options?: QueryOptions<import("@/types/api").CostByPrinter[]>) {
  return useQuery({
    queryKey: queryKeys.costsByPrinter(days),
    queryFn: () => apiClient.getCostsByPrinter(days),
    staleTime: 300_000, // 5 minutes
    ...options,
  });
}

export function useCostsByMaterial(days?: number, options?: QueryOptions<import("@/types/api").CostByMaterial[]>) {
  return useQuery({
    queryKey: queryKeys.costsByMaterial(days),
    queryFn: () => apiClient.getCostsByMaterial(days),
    staleTime: 300_000, // 5 minutes
    ...options,
  });
}

export function useCostOverTime(options?: QueryOptions<import("@/types/api").CostOverTime[]>) {
  return useQuery({
    queryKey: queryKeys.costOverTime,
    queryFn: () => apiClient.getCostOverTime(),
    staleTime: 300_000, // 5 minutes
    ...options,
  });
}

// ============ Job Scheduling Hooks ============

export function useScheduledJobs(options?: QueryOptions<import("@/types/api").ScheduledJob[]>) {
  return useQuery({
    queryKey: queryKeys.scheduledJobs,
    queryFn: () => apiClient.getScheduledJobs(),
    staleTime: 30_000, // 30 seconds
    ...options,
  });
}

export function useScheduledJob(jobId: string, options?: QueryOptions<import("@/types/api").ScheduledJob>) {
  return useQuery({
    queryKey: queryKeys.scheduledJob(jobId),
    queryFn: () => apiClient.getScheduledJob(jobId),
    staleTime: 30_000,
    enabled: !!jobId,
    ...options,
  });
}

export function useTimezones(options?: QueryOptions<TimezoneInfo[]>) {
  return useQuery({
    queryKey: queryKeys.timezones,
    queryFn: () => apiClient.getTimezones(),
    staleTime: 600_000, // 10 minutes - timezones rarely change
    ...options,
  });
}

export function useScheduleJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ jobId, request }: { jobId: string; request: import("@/types/api").ScheduleJobRequest }) =>
      apiClient.scheduleJob(jobId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs });
      toast.success('Job scheduled successfully');
    },
    onError: (err: ApiError) => toast.error(`Failed to schedule job: ${err.message}`),
  });
}

export function useRescheduleJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ jobId, request }: { jobId: string; request: import("@/types/api").ScheduleJobRequest }) =>
      apiClient.rescheduleJob(jobId, request),
    onSuccess: (_, variables) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs });
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJob(variables.jobId) });
      toast.success('Job rescheduled successfully');
    },
    onError: (err: ApiError) => toast.error(`Failed to reschedule job: ${err.message}`),
  });
}

export function useCancelSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) => apiClient.cancelSchedule(jobId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs });
      toast.success('Schedule cancelled');
    },
    onError: (err: ApiError) => toast.error(`Failed to cancel schedule: ${err.message}`),
  });
}

export function usePauseSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) => apiClient.pauseSchedule(jobId),
    onSuccess: (_, jobId) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs });
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJob(jobId) });
      toast.success('Schedule paused');
    },
    onError: (err: ApiError) => toast.error(`Failed to pause schedule: ${err.message}`),
  });
}

export function useResumeSchedule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (jobId: string) => apiClient.resumeSchedule(jobId),
    onSuccess: (_, jobId) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJobs });
      queryClient.invalidateQueries({ queryKey: queryKeys.scheduledJob(jobId) });
      toast.success('Schedule resumed');
    },
    onError: (err: ApiError) => toast.error(`Failed to resume schedule: ${err.message}`),
  });
}


// ============ Obico ML Server Hooks ============

export function useObicoServers(options?: QueryOptions<ObicoServer[]>) {
  return useQuery({
    queryKey: queryKeys.obicoServers,
    queryFn: () => apiClient.getObicoServers(),
    staleTime: 300_000, // 5 minutes - server list changes infrequently
    ...options,
  });
}

export function useCreateObicoServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateObicoServerRequest) => apiClient.createObicoServer(data),
    onSuccess: (server) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.obicoServers });
      toast.success(`Obico server "${server.name}" created`);
    },
    onError: (err: ApiError) => toast.error(`Failed to create server: ${err.message}`),
  });
}

export function useUpdateObicoServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateObicoServerRequest }) => 
      apiClient.updateObicoServer(id, data),
    onSuccess: (server) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.obicoServers });
      queryClient.invalidateQueries({ queryKey: queryKeys.obicoServer(server.id) });
      toast.success(`Obico server "${server.name}" updated`);
    },
    onError: (err: ApiError) => toast.error(`Failed to update server: ${err.message}`),
  });
}

export function useDeleteObicoServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteObicoServer(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.obicoServers });
      toast.success('Obico server deleted');
    },
    onError: (err: ApiError) => toast.error(`Failed to delete server: ${err.message}`),
  });
}

export function useTestObicoServerHealth() {
  return useMutation({
    mutationFn: (id: string) => apiClient.testObicoServerHealth(id),
    onError: (err: ApiError) => toast.error(`Health test failed: ${err.message}`),
  });
}
