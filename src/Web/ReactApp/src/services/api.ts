import {
  ApiError,
  AuthenticationResult,
  CommandResult,
  CreateFilamentTypeRequest,
  CreatePrinterDto,
  DiscoveredPrinterDto,
  FilamentPresets,
  FilamentTypeDto,
  GcodeFile,
  GcodeHarvestOperation,
  GetGcodeFilesResponse,
  HealthStatus,
  JobQueuePrintJob,
  LoginRequest,
  ManufacturerDto,
  ModelDto,
  MoveRequest,
  MultiUploadResponse,
  Printer,
  PrinterDetails,
  RegisterRequest,
  ResolveHostnameRequest,
  ResolveHostnameResponse,
  TempTargets,
  UpdateFilamentTypeRequest,
  UpdateModelRequest,
  UpdatePrinterDto,
  UserDto
} from '@/types/api';
import type { AxiosError, AxiosInstance, AxiosRequestConfig } from 'axios';
import axios from 'axios';

export class ApiClient {
  private client: AxiosInstance;

  constructor() {
    // Use environment variable for API base URL, fallback to relative path for monolithic deployment
    const apiBaseUrl = import.meta.env.VITE_API_BASE_URL || '/api';

    this.client = axios.create({
      baseURL: apiBaseUrl,
      timeout: 30000,
      headers: {
        'Content-Type': 'application/json',
      },
    });

    // Request interceptor for authentication
    this.client.interceptors.request.use((config) => {
      const token = localStorage.getItem('auth-token');
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      return config;
    });

    // Response interceptor for error handling
    this.client.interceptors.response.use(
      (response) => response,
      (error: AxiosError) => {
        const apiError: ApiError = {
          message: error.message,
          statusCode: error.response?.status || 500,
          details: error.response?.data as string || undefined,
        };
        return Promise.reject(apiError);
      }
    );
  }

  // ============ Printer API methods ============

  async getPrinters(): Promise<Printer[]> {
    const response = await this.client.get<Printer[]>('/printers/fast');
    return response.data;
  }

  async getPrinter(id: string): Promise<Printer> {
    const response = await this.client.get<Printer>(`/printers/${id}`);
    return response.data;
  }

  async getPrinterDetails(id: string): Promise<PrinterDetails> {
    const response = await this.client.get<PrinterDetails>(`/printers/${id}/details`);
    return response.data;
  }

  async createPrinter(printer: CreatePrinterDto): Promise<Printer> {
    const response = await this.client.post<Printer>('/printers', printer);
    return response.data;
  }

  async updatePrinter(id: string, printer: UpdatePrinterDto): Promise<Printer> {
    const response = await this.client.put<Printer>(`/printers/${id}`, printer);
    return response.data;
  }

  async deletePrinter(id: string): Promise<void> {
    await this.client.delete(`/printers/${id}`);
  }

  async discoverPrinters(): Promise<DiscoveredPrinterDto[]> {
    const response = await this.client.get<DiscoveredPrinterDto[]>('/printers/discover');
    return response.data;
  }

  async startDiscoveryStream(): Promise<{ sessionId: string; message: string }> {
    const response = await this.client.post<{ sessionId: string; message: string }>('/printers/discover/stream');
    return response.data;
  }

  // ============ Printer Control API methods ============

  async setTemperatures(printerId: string, targets: TempTargets): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/temps`, targets);
    return response.data;
  }

  async movePrinter(printerId: string, move: MoveRequest): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/move`, move);
    return response.data;
  }

  async movePrinterTo(printerId: string, position: MoveRequest): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/moveto`, position);
    return response.data;
  }

  async homePrinter(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/home`);
    return response.data;
  }

  async homeXY(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/homexy`);
    return response.data;
  }

  async homeZ(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/homez`);
    return response.data;
  }

  async pausePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/pause`);
    return response.data;
  }

  async resumePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/resume`);
    return response.data;
  }

  async emergencyStop(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/emergency-stop`);
    return response.data;
  }

  async firmwareRestart(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(`/printers/${printerId}/firmware-restart`);
    return response.data;
  }

  // ============ Catalog API methods ============

  async getManufacturers(): Promise<ManufacturerDto[]> {
    const response = await this.client.get<ManufacturerDto[]>('/catalog/manufacturers');
    return response.data;
  }

  async createManufacturer(name: string): Promise<ManufacturerDto> {
    const response = await this.client.post<ManufacturerDto>('/catalog/manufacturers', { name });
    return response.data;
  }

  async updateManufacturer(id: string, name: string): Promise<ManufacturerDto> {
    const response = await this.client.put<ManufacturerDto>(`/catalog/manufacturers/${id}`, { name });
    return response.data;
  }

  async deleteManufacturer(id: string): Promise<void> {
    await this.client.delete(`/catalog/manufacturers/${id}`);
  }

  async getModels(manufacturerId?: string): Promise<ModelDto[]> {
    const params = manufacturerId ? { manufacturerId } : {};
    const response = await this.client.get<ModelDto[]>('/catalog/models', { params });
    return response.data;
  }

  async createModel(model: Omit<ModelDto, 'id'>): Promise<ModelDto> {
    const response = await this.client.post<ModelDto>('/catalog/models', model);
    return response.data;
  }

  async updateModel(id: string, request: UpdateModelRequest): Promise<ModelDto> {
    const response = await this.client.put<ModelDto>(`/catalog/models/${id}`, request);
    return response.data;
  }

  // Legacy method for simple name updates
  async updateModelName(id: string, name: string): Promise<ModelDto> {
    return this.updateModel(id, { name });
  }

  async deleteModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/models/${id}`);
  }

  // ============ Settings API methods ============
  // Network Discovery settings
  async getNetworkDiscoverySettings(): Promise<{ networkRanges: string[]; timeoutMs: number; maxConcurrentScans: number; ports: number[] }> {
    const resp = await this.client.get('/network-discovery/settings');
    // Backend returns camelCase via JSON options; map to consistent shape
    const data = resp.data as { networkRanges: string[]; timeoutMs: number; maxConcurrentScans: number; ports: number[] };
    return data;
  }

  async saveNetworkDiscoverySettings(payload: { networkRanges: string[]; timeoutMs: number; maxConcurrentScans: number; ports: number[] }): Promise<void> {
    await this.client.post('/network-discovery/settings', {
      networkRanges: payload.networkRanges,
      timeoutMs: payload.timeoutMs,
      maxConcurrentScans: payload.maxConcurrentScans,
      ports: payload.ports
    });
  }

  async autoDetectNetworkRanges(): Promise<string[]> {
    const resp = await this.client.post('/network-discovery/auto-detect', {});
    return (resp.data as { ranges: string[] }).ranges;
  }

  // ============ Filament Type API methods ============

  async getFilamentTypes(): Promise<FilamentTypeDto[]> {
    const response = await this.client.get<FilamentTypeDto[]>('/filamenttype');
    return response.data;
  }

  async createFilamentType(filamentType: CreateFilamentTypeRequest): Promise<FilamentTypeDto> {
    const response = await this.client.post<FilamentTypeDto>('/filamenttype', filamentType);
    return response.data;
  }

  async updateFilamentType(id: string, filamentType: UpdateFilamentTypeRequest): Promise<void> {
    await this.client.put(`/filamenttype/${id}`, filamentType);
  }

  async deleteFilamentType(id: string): Promise<void> {
    await this.client.delete(`/filamenttype/${id}`);
  }

  async getFilamentPresets(): Promise<FilamentPresets> {
    const response = await this.client.get<{ presets: FilamentPresets }>('/filamenttype/presets');
    return response.data.presets;
  }

  async saveFilamentPresets(presets: FilamentPresets): Promise<void> {
    await this.client.post('/filamenttype/presets', { presets });
  }

  // ============ Network utilities ============

  async resolveHostname(request: ResolveHostnameRequest): Promise<ResolveHostnameResponse> {
    const response = await this.client.post<ResolveHostnameResponse>('/resolve-hostname', request);
    return response.data;
  }

  // ============ G-code library methods ============

  async getGcodeFiles(page = 1, pageSize = 50): Promise<GcodeFile[]> {
    const response = await this.client.get<GcodeFile[]>('/gcode-files', {
      params: { page, pageSize }
    });
    return response.data;
  }

  async getGcodeFile(id: string): Promise<GcodeFile> {
    const response = await this.client.get<GcodeFile>(`/gcode-files/${id}`);
    return response.data;
  }

  async uploadGcodeFile(file: File, description?: string, tags?: string[]): Promise<GcodeFile> {
    const formData = new FormData();
    formData.append('file', file);
    if (description) formData.append('description', description);
    if (tags) formData.append('tags', JSON.stringify(tags));

    const response = await this.client.post<GcodeFile>('/gcode-files/upload', formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
    return response.data;
  }

  async deleteGcodeFile(id: string): Promise<void> {
    await this.client.delete(`/gcode-files/${id}`);
  }

  // ============ G-code harvest operations ============

  async startHarvestOperation(printerId: string, opts?: { includeSubdirectories?: boolean; maxFileSizeBytes?: number; modifiedAfter?: Date | string; fileExtensions?: string[]; minFileSizeBytes?: number; duplicateHandling?: string }): Promise<{ operationId: string }> {
    const payload = {
      printerId,
      includeSubdirectories: opts?.includeSubdirectories ?? true,
      maxFileSizeBytes: opts?.maxFileSizeBytes ?? 100 * 1024 * 1024,
      modifiedAfter: opts?.modifiedAfter ? (typeof opts.modifiedAfter === 'string' ? opts.modifiedAfter : opts.modifiedAfter.toISOString()) : undefined,
      fileExtensions: opts?.fileExtensions,
      minFileSizeBytes: opts?.minFileSizeBytes,
      duplicateHandling: opts?.duplicateHandling
    };
    const response = await this.client.post('/gcode-harvest/start', payload);
    return response.data as { operationId: string };
  }

  async startBulkHarvest(printerIds: string[], options: { includeSubfolders?: boolean; maxFileAge?: number; fileTypes?: string[]; minFileSize?: number; duplicateHandling?: string } = {}): Promise<{ operationIds: string[] }> {
    const modifiedAfter = options.maxFileAge ? new Date(Date.now() - options.maxFileAge) : undefined;
    const results = await Promise.all(printerIds.map(pid => this.startHarvestOperation(pid, {
      includeSubdirectories: options?.includeSubfolders ?? true,
      modifiedAfter,
      fileExtensions: options.fileTypes,
      minFileSizeBytes: options.minFileSize,
      duplicateHandling: options.duplicateHandling,
    }).catch(err => {
      console.error('Failed to start harvest for printer', pid, err);
      return null;
    })));
    return { operationIds: results.filter(r => r !== null).map(r => (r as { operationId: string }).operationId) };
  }

  async getHarvestOperations(printerId?: string): Promise<GcodeHarvestOperation[]> {
    const params = printerId ? { printerId } : {};
    const response = await this.client.get<GcodeHarvestOperation[]>('/gcode-harvest/active', { params });
    return response.data;
  }

  async getHarvestOperation(id: string): Promise<GcodeHarvestOperation> {
    const response = await this.client.get<GcodeHarvestOperation>(`/gcode-harvest/operations/${id}`);
    return response.data;
  }

  async getGcodeFilesWithFilter(request: Record<string, unknown>): Promise<GetGcodeFilesResponse> {
    const response = await this.client.get<GetGcodeFilesResponse>('/gcode-files', { params: request });
    return response.data;
  }

  async deleteGcodeFiles(filePaths: string[]): Promise<void> {
    await this.client.delete('/gcode-files', { data: { filePaths } });
  }

  async downloadGcodeFile(filePath: string): Promise<void> {
    const response = await this.client.get(`/gcode-files/download`, {
      params: { path: filePath },
      responseType: 'blob'
    });

    // Create a download link
    const url = window.URL.createObjectURL(new Blob([response.data]));
    const link = document.createElement('a');
    link.href = url;
    link.setAttribute('download', filePath.split('/').pop() || 'file.gcode');
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.URL.revokeObjectURL(url);
  }

  async uploadGcodeLibraryFile(file: File, virtualPath = '/'): Promise<void> {
    const form = new FormData();
    form.append('file', file);
    await this.client.post(`/gcode-files/upload`, form, {
      params: { path: virtualPath },
      headers: { 'Content-Type': 'multipart/form-data' }
    });
  }

  async uploadMultipleGcodeLibraryFiles(files: File[], virtualPath = '/'): Promise<MultiUploadResponse> {
    const form = new FormData();
    files.forEach(f => form.append('files', f));
    const resp = await this.client.post<MultiUploadResponse>(`/gcode-files/upload-multiple`, form, {
      params: { path: virtualPath },
      headers: { 'Content-Type': 'multipart/form-data' }
    });
    return resp.data as MultiUploadResponse;
  }

  async getGcodeUploadSettings(): Promise<import('@/types/api').GcodeUploadSettings> {
    const resp = await this.client.get('/gcode-files/settings');
    return resp.data as import('@/types/api').GcodeUploadSettings;
  }

  async updateGcodeUploadSettings(allowedExtensions: string[]): Promise<void> {
    await this.client.put('/gcode-files/settings', { allowedExtensions });
  }

  async moveGcodePath(sourcePath: string, destinationPath: string, overwrite = false): Promise<{ path: string; isDirectory: boolean; }> {
    const resp = await this.client.post('/gcode-files/move', { sourcePath, destinationPath, overwrite });
    return resp.data as { path: string; isDirectory: boolean; };
  }

  async getGcodeFileHash(virtualPath: string, algorithm: 'sha256' | 'sha1' = 'sha256'): Promise<{ fileName: string; size: number; algorithm: string; hash: string; }> {
    const resp = await this.client.get('/gcode-files/hash', { params: { path: virtualPath, algorithm } });
    return resp.data as { fileName: string; size: number; algorithm: string; hash: string; };
  }

  // ============ Job Queue methods ============

  async getJobQueue(printerId?: string): Promise<JobQueuePrintJob[]> {
    const params = printerId ? { printerId } : {};
    const response = await this.client.get<JobQueuePrintJob[]>('/job-queue', { params });
    return response.data;
  }

  async queuePrintJob(printerId: string, gcodeFileId: string, priority = 0): Promise<JobQueuePrintJob> {
    const response = await this.client.post<JobQueuePrintJob>('/job-queue', {
      printerId,
      gcodeFileId,
      priority
    });
    return response.data;
  }

  async cancelJob(jobId: string): Promise<void> {
    await this.client.patch(`/job-queue/${jobId}/cancel`);
  }

  async deleteJob(jobId: string): Promise<void> {
    await this.client.delete(`/job-queue/${jobId}`);
  }

  // ============ Printer file operations ============

  async uploadGcodeToPrinter(printerId: string, file: File): Promise<boolean> {
    const formData = new FormData();
    formData.append('file', file);

    const response = await this.client.post<{ success: boolean }>(`/printers/${printerId}/upload-gcode`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
    return response.data.success;
  }

  async startPrintFromFile(printerId: string, fileName: string): Promise<boolean> {
    const response = await this.client.post<{ success: boolean }>(`/printers/${printerId}/start-print`, {
      fileName
    });
    return response.data.success;
  }

  // ============ Health checks ============

  async getHealthStatus(): Promise<HealthStatus> {
    const response = await this.client.get<HealthStatus>('/health');
    return response.data as HealthStatus;
  }

  async getBasicHealth(): Promise<{ status: string }> {
    const response = await this.client.get<{ status: string }>('/healthz');
    return response.data;
  }

  // ============ Authentication API methods ============

  async login(credentials: LoginRequest): Promise<AuthenticationResult> {
    const response = await this.client.post<AuthenticationResult>('/auth/login', credentials);
    return response.data;
  }

  async register(userData: RegisterRequest): Promise<AuthenticationResult> {
    const response = await this.client.post<AuthenticationResult>('/auth/register', userData);
    return response.data;
  }

  async getCurrentUser(): Promise<UserDto> {
    const response = await this.client.get<UserDto>('/auth/me');
    return response.data;
  }

  async logout(): Promise<void> {
    await this.client.post('/auth/logout');
  }

  // ============ Generic request method ============

  async request<T>(config: AxiosRequestConfig): Promise<T> {
    const response = await this.client.request<T>(config);
    return response.data;
  }
}

// Export singleton instance
export const apiClient = new ApiClient();