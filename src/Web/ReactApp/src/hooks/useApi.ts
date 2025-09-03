import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import type { UseQueryOptions } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { 
  Printer, 
  CreatePrinterDto, 
  UpdatePrinterDto, 
  PrinterDetails,
  ManufacturerDto,
  ModelDto,
  FilamentPresets,
  GcodeFile,
  GcodeHarvestOperation,
  JobQueuePrintJob,
  ApiError 
} from '@/types/api';

// ============ Query Keys ============
export const queryKeys = {
  printers: ['printers'] as const,
  printer: (id: string) => ['printers', id] as const,
  printerDetails: (id: string) => ['printers', id, 'details'] as const,
  manufacturers: ['manufacturers'] as const,
  models: (manufacturerId?: string) => ['models', manufacturerId] as const,
  filamentPresets: ['presets', 'filament'] as const,
  gcodeFiles: (page?: number, pageSize?: number) => ['gcode-files', page, pageSize] as const,
  gcodeFile: (id: string) => ['gcode-files', id] as const,
  harvestOperations: (printerId?: string) => ['harvest-operations', printerId] as const,
  harvestOperation: (id: string) => ['harvest-operations', id] as const,
  jobQueue: (printerId?: string) => ['job-queue', printerId] as const,
  health: ['health'] as const,
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
    ...options,
  });
}

export function useCreatePrinter() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (printer: CreatePrinterDto) => apiClient.createPrinter(printer),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    },
  });
}

export function useUpdatePrinter() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: ({ id, printer }: { id: string; printer: UpdatePrinterDto }) => 
      apiClient.updatePrinter(id, printer),
    onSuccess: (_, { id }) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      queryClient.invalidateQueries({ queryKey: queryKeys.printer(id) });
      queryClient.invalidateQueries({ queryKey: queryKeys.printerDetails(id) });
    },
  });
}

export function useDeletePrinter() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (id: string) => apiClient.deletePrinter(id),
    onSuccess: (_, id) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      queryClient.removeQueries({ queryKey: queryKeys.printer(id) });
      queryClient.removeQueries({ queryKey: queryKeys.printerDetails(id) });
    },
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
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.manufacturers });
    },
  });
}

export function useModels(manufacturerId?: string, options?: UseQueryOptions<ModelDto[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.models(manufacturerId),
    queryFn: () => apiClient.getModels(manufacturerId),
    staleTime: 300000, // 5 minutes
    ...options,
  });
}

export function useCreateModel() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (model: Omit<ModelDto, 'id'>) => apiClient.createModel(model),
    onSuccess: (_, model) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.models() });
      queryClient.invalidateQueries({ queryKey: queryKeys.models(model.manufacturerId) });
    },
  });
}

// ============ Settings Hooks ============

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

// ============ G-code Library Hooks ============

export function useGcodeFiles(page = 1, pageSize = 50, options?: UseQueryOptions<GcodeFile[], ApiError>) {
  return useQuery({
    queryKey: queryKeys.gcodeFiles(page, pageSize),
    queryFn: () => apiClient.getGcodeFiles(page, pageSize),
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
      return data?.status === 0 ? 5000 : false; // Poll while running
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
    onSuccess: (_, { printerId }) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue(printerId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.jobQueue() });
    },
  });
}

export function useCancelJob() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (jobId: string) => apiClient.cancelJob(jobId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['job-queue'] });
    },
  });
}

export function useDeleteJob() {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (jobId: string) => apiClient.deleteJob(jobId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['job-queue'] });
    },
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

export function useHealthStatus(options?: UseQueryOptions<any, ApiError>) {
  return useQuery({
    queryKey: queryKeys.health,
    queryFn: () => apiClient.getHealthStatus(),
    staleTime: 30000, // 30 seconds
    ...options,
  });
}

export function useBasicHealth(options?: UseQueryOptions<{ status: string }, ApiError>) {
  return useQuery({
    queryKey: ['health', 'basic'],
    queryFn: () => apiClient.getBasicHealth(),
    staleTime: 10000, // 10 seconds
    ...options,
  });
}