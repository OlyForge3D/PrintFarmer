import axios from 'axios';
import type { AxiosInstance, AxiosRequestConfig, AxiosError } from 'axios';
import { 
  Printer, 
  CreatePrinterDto, 
  UpdatePrinterDto, 
  PrinterDetails, 
  ManufacturerDto, 
  ModelDto, 
  FilamentPresets,
  ResolveHostnameRequest,
  ResolveHostnameResponse,
  GcodeFile,
  GcodeHarvestOperation,
  JobQueuePrintJob,
  ApiError,
  LoginRequest,
  RegisterRequest,
  AuthenticationResult,
  UserDto,
  DiscoveredPrinterDto 
} from '@/types/api';

export class ApiClient {
  private client: AxiosInstance;

  constructor() {
    this.client = axios.create({
      baseURL: '/api',
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
    const response = await this.client.get<Printer[]>('/printers');
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

  // ============ Catalog API methods ============

  async getManufacturers(): Promise<ManufacturerDto[]> {
    const response = await this.client.get<ManufacturerDto[]>('/catalog/manufacturers');
    return response.data;
  }

  async createManufacturer(name: string): Promise<ManufacturerDto> {
    const response = await this.client.post<ManufacturerDto>('/catalog/manufacturers', { name });
    return response.data;
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

  // ============ Settings API methods ============

  async getFilamentPresets(): Promise<FilamentPresets> {
    const response = await this.client.get<FilamentPresets>('/presets');
    return response.data;
  }

  async saveFilamentPresets(presets: FilamentPresets): Promise<void> {
    await this.client.post('/presets', presets);
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

  async startHarvestOperation(printerId: string): Promise<GcodeHarvestOperation> {
    const response = await this.client.post<GcodeHarvestOperation>(`/printers/${printerId}/harvest`);
    return response.data;
  }

  async getHarvestOperations(printerId?: string): Promise<GcodeHarvestOperation[]> {
    const params = printerId ? { printerId } : {};
    const response = await this.client.get<GcodeHarvestOperation[]>('/harvest-operations', { params });
    return response.data;
  }

  async getHarvestOperation(id: string): Promise<GcodeHarvestOperation> {
    const response = await this.client.get<GcodeHarvestOperation>(`/harvest-operations/${id}`);
    return response.data;
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

  async getHealthStatus(): Promise<any> {
    const response = await this.client.get('/health');
    return response.data;
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