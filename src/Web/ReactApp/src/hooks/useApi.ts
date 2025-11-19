import { apiClient } from '@/services/api';
import type { BasicHealthStatus, DetailedHealthStatus, HealthStatus } from '@/types/api';
import {
  ApiError,
  CreatePrinterDto,
  FilamentPresets,
  FilamentTypeDto,
  GcodeFile,
  GcodeHarvestOperation,
  GcodeHarvestStatus,
  HistoryJob,
  HistoryListResponse,
  HistoryTotals,
  JobQueuePrintJob,
  ManufacturerDto,
  PrinterCapabilitiesDto,
  PrinterModelDto,
  Printer,
  PrinterCameraUrls,
  PrinterDetails,
  PrinterFast,
  StartDiscoveryRequest,
  UpdatePrinterDto,
  FileHealthSummaryDto,
  FileHealthAuditDto,
  FileIssuesSummaryDto,
  FileHealthDetailDto,
} from '@/types/api';
import type { UseQueryOptions } from '@tanstack/react-query';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { toast } from 'sonner';

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
  filamentTypes: ['filament-types'] as const,
  filamentPresets: ['presets', 'filament'] as const,
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
} as const;

// ============ Printer Hooks ============

export function usePrinters(options?: UseQueryOptions<Printer[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.printers,
    queryFn: () => apiClient.getPrinters(),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function usePrintersFast(includeDisabled = false, options?: UseQueryOptions<PrinterFast[], ApiError>) {
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

export function usePrinterCameraUrls(options?: UseQueryOptions<PrinterCameraUrls[], ApiError>) {
  return useQuery({
    queryKey: [...queryKeys.printers, 'camera-urls'],
    queryFn: () => apiClient.getPrinterCameraUrls(),
    staleTime: 300000, // 5 minutes - camera URLs are more static
    ...options,
  });
}

export function usePrintersWithCameraUrls(includeDisabled = false) {
  const printersQuery = usePrintersFast(includeDisabled);
  const cameraUrlsQuery = usePrinterCameraUrls();

  return useMemo(() => {
    if (printersQuery.data && cameraUrlsQuery.data) {
      // Create a map of camera URLs by printer ID for efficient lookup
      const cameraUrlsMap = new Map<string, PrinterCameraUrls>();
      cameraUrlsQuery.data.forEach(camera => {
        cameraUrlsMap.set(camera.id, camera);
      });

      // Merge camera URLs into printer data and convert PrinterFast to Printer
      const printersWithCameraUrls: Printer[] = printersQuery.data.map(printerFast => {
        const cameraUrls = cameraUrlsMap.get(printerFast.id);
        return {
          ...printerFast,
          isReachable: printerFast.isOnline, // Add missing property for Printer interface
          cameraStreamUrl: cameraUrls?.cameraStreamUrl,
          cameraSnapshotUrl: cameraUrls?.cameraSnapshotUrl,
          // Add other missing Printer properties with defaults
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
        } as Printer;
      });

      return {
        data: printersWithCameraUrls,
        isLoading: printersQuery.isLoading || cameraUrlsQuery.isLoading,
        isError: printersQuery.isError || cameraUrlsQuery.isError,
        error: printersQuery.error || cameraUrlsQuery.error,
        refetch: printersQuery.refetch,
        isSuccess: printersQuery.isSuccess && cameraUrlsQuery.isSuccess,
        isFetching: printersQuery.isFetching || cameraUrlsQuery.isFetching,
      };
    }

    // Return loading/error states from fast query if no data yet
    return {
      data: undefined,
      isLoading: printersQuery.isLoading || cameraUrlsQuery.isLoading,
      isError: printersQuery.isError || cameraUrlsQuery.isError,
      error: printersQuery.error || cameraUrlsQuery.error,
      refetch: printersQuery.refetch,
      isSuccess: false,
      isFetching: printersQuery.isFetching || cameraUrlsQuery.isFetching,
    };
  }, [printersQuery, cameraUrlsQuery]);
}

export function usePrinter(id: string, options?: UseQueryOptions<Printer, ApiError>) {
  return useQuery({
    queryKey: queryKeys.printer(id),
    queryFn: () => apiClient.getPrinter(id),
    enabled: !!id,
    staleTime: 30000,
    ...options,
  });
}

export function usePrinterDetails(id: string, options?: UseQueryOptions<PrinterDetails, ApiError>) {
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
        serverUrl: printer.serverUrl,
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

export function useManufacturers(options?: UseQueryOptions<ManufacturerDto[], ApiError>) {
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

export function useModels(manufacturerId?: string, options?: UseQueryOptions<PrinterModelDto[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.models(manufacturerId),
    queryFn: () => apiClient.getModels(manufacturerId),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useModelDefaultCapabilities(modelId?: string, options?: UseQueryOptions<PrinterCapabilitiesDto | null, ApiError>) {
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

// ============ Settings Hooks ============

export function useFilamentTypes(options?: UseQueryOptions<FilamentTypeDto[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.filamentTypes,
    queryFn: () => apiClient.getFilamentTypes(),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useFilamentPresets(options?: UseQueryOptions<FilamentPresets, ApiError>) {
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
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentTypes });
      queryClient.invalidateQueries({ queryKey: queryKeys.filamentPresets });
    },
  });
}

// ============ G-code Library Hooks ============

export function useGcodeFiles(page = 1, pageSize = 50, options?: UseQueryOptions<GcodeFile[], ApiError>) {
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

export function useGcodeFile(id: string, options?: UseQueryOptions<GcodeFile, ApiError>) {
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

export function useHarvestOperations(printerId?: string, options?: UseQueryOptions<GcodeHarvestOperation[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.harvestOperations(printerId),
    queryFn: () => apiClient.getHarvestOperations(printerId),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function useHarvestOperation(id: string, options?: UseQueryOptions<GcodeHarvestOperation, ApiError>) {
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

export function useJobQueue(printerId?: string, options?: UseQueryOptions<JobQueuePrintJob[], ApiError>) {
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
      const prevPrinterQueue = queryClient.getQueryData<JobQueuePrintJob[]>(printerQueueKey);
      const prevGlobalQueue = queryClient.getQueryData<JobQueuePrintJob[]>(globalQueueKey);
      const temp: JobQueuePrintJob = {
        id: `temp-${Date.now()}`,
        printerId,
        gcodeFileId,
        gcodeFileName: 'Queuing...',
        status: 0,
        priority,
        queuedAt: new Date(),
        createdAt: new Date(),
        updatedAt: new Date()
      } as JobQueuePrintJob;
      if (prevPrinterQueue) queryClient.setQueryData(printerQueueKey, [temp, ...prevPrinterQueue]); else queryClient.setQueryData(printerQueueKey, [temp]);
      if (prevGlobalQueue) queryClient.setQueryData(globalQueueKey, [temp, ...prevGlobalQueue]); else queryClient.setQueryData(globalQueueKey, [temp]);
      return { prevPrinterQueue, prevGlobalQueue, printerQueueKey, globalQueueKey, tempId: temp.id };
    },
    onError: (_e, vars, ctx) => {
      if (ctx?.prevPrinterQueue && ctx.printerQueueKey) {
        queryClient.setQueryData(ctx.printerQueueKey, ctx.prevPrinterQueue);
      } else if (ctx?.printerQueueKey) {
        const cur = queryClient.getQueryData<JobQueuePrintJob[]>(ctx.printerQueueKey);
        if (cur) queryClient.setQueryData(ctx.printerQueueKey, cur.filter(j => j.id !== ctx?.tempId && !j.id.startsWith('temp-')) || undefined);
      }
      if (ctx?.prevGlobalQueue && ctx.globalQueueKey) {
        queryClient.setQueryData(ctx.globalQueueKey, ctx.prevGlobalQueue);
      } else if (ctx?.globalQueueKey) {
        const cur = queryClient.getQueryData<JobQueuePrintJob[]>(ctx.globalQueueKey);
        if (cur) queryClient.setQueryData(ctx.globalQueueKey, cur.filter(j => j.id !== ctx?.tempId && !j.id.startsWith('temp-')) || undefined);
      }
      toast.error('Failed to queue print job');
    },
    onSuccess: (job, vars, ctx) => {
      const printerQueueKey = queryKeys.jobQueue(vars.printerId);
      const globalQueueKey = queryKeys.jobQueue();
      const upd = (key: readonly unknown[]) => {
        const list = queryClient.getQueryData<JobQueuePrintJob[]>(key);
        if (list) queryClient.setQueryData<JobQueuePrintJob[]>(key, list.map(j => (ctx?.tempId && j.id === ctx.tempId) ? job : j));
      };
      upd(printerQueueKey); upd(globalQueueKey);
      toast.success('Print job queued');
    },
    onSettled: (_d, _e, vars) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue(vars.printerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() });
    }
  });
}

export function useCancelJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (jobId: string) => apiClient.cancelJob(jobId),
    onMutate: async (jobId) => {
      const allQueues = queryClient.getQueriesData<JobQueuePrintJob[]>({ queryKey: ['job-queue'] });
      const snapshots = allQueues.map(([key, value]) => ({ key, value }));
      allQueues.forEach(([key, jobs]) => {
        if (jobs) {
          queryClient.setQueryData<JobQueuePrintJob[]>(key as readonly unknown[], jobs.map(j => j.id === jobId ? { ...j, status: 4, updatedAt: new Date() } : j));
        }
      });
      return { snapshots };
    },
    onError: (_e, _id, ctx) => {
      ctx?.snapshots?.forEach(s => queryClient.setQueryData<JobQueuePrintJob[]>(s.key as readonly unknown[], s.value));
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

export function useDeleteJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (jobId: string) => apiClient.deleteJob(jobId),
    onMutate: async (jobId) => {
      const allQueues = queryClient.getQueriesData<JobQueuePrintJob[]>({ queryKey: ['job-queue'] });
      const snapshots = allQueues.map(([key, value]) => ({ key, value }));
      allQueues.forEach(([key, jobs]) => {
        if (jobs) {
          queryClient.setQueryData<JobQueuePrintJob[]>(key as readonly unknown[], jobs.filter(j => j.id !== jobId));
        }
      });
      return { snapshots };
    },
    onError: (_e, _id, ctx) => {
      ctx?.snapshots?.forEach(s => queryClient.setQueryData<JobQueuePrintJob[]>(s.key as readonly unknown[], s.value));
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

export function useHealthStatus(options?: UseQueryOptions<HealthStatus, ApiError>) {
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

export function useBasicHealth(options?: UseQueryOptions<BasicHealthStatus, ApiError>) {
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
  queryOptions?: UseQueryOptions<HistoryListResponse, ApiError>
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
  queryOptions?: UseQueryOptions<HistoryJob, ApiError>
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
  queryOptions?: UseQueryOptions<HistoryTotals, ApiError>
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

export function useFileHealthSummary(options?: UseQueryOptions<FileHealthSummaryDto, ApiError>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.health,
    queryFn: () => apiClient.getFileHealthSummary(),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useFileAuditHistory(
  pageSize: number = 20,
  options?: UseQueryOptions<FileHealthAuditDto[], ApiError>
) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.auditHistory(pageSize),
    queryFn: () => apiClient.getFileAuditHistory(pageSize),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useFilesWithIssues(options?: UseQueryOptions<FileIssuesSummaryDto, ApiError>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.filesWithIssues,
    queryFn: () => apiClient.getFilesWithIssues(),
    staleTime: 60000, // 1 minute
    ...options,
  });
}

export function useModel3DHealth(id: string, options?: UseQueryOptions<FileHealthDetailDto, ApiError>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.model3DHealth(id),
    queryFn: () => apiClient.getModel3DHealth(id),
    staleTime: 60000, // 1 minute
    enabled: !!id,
    ...options,
  });
}

export function useGcodeFileHealth(id: string, options?: UseQueryOptions<FileHealthDetailDto, ApiError>) {
  return useQuery({
    queryKey: queryKeys.fileConsistency.gcodeFileHealth(id),
    queryFn: () => apiClient.getGcodeFileHealth(id),
    staleTime: 60000, // 1 minute
    enabled: !!id,
    ...options,
  });
}