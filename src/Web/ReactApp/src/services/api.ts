/* eslint-disable local/pf-no-unguarded-console */
// Get hash for a G-code file (returns string)
import { getApiBaseUrl } from "@/common/utils/apiUrlHelpers";
import {
  ApiError,
  PrintJobStatusDto,
  AuthenticationResult,
  CommandResult,
  CreateFilamentTypeRequest,
  CreatePrinterDto,
  DiscoveredPrinterDto,
  FilamentPresets,
  FilamentTypeDto,
  GcodeFile,
  GcodeLibraryFile,
  GcodeHarvestOperation,
  GetGcodeFilesResponse,
  HealthStatus,
  HistoryJob,
  HistoryListResponse,
  HistoryTotals,
  JobQueuePrintJob,
  LoginRequest,
  ManufacturerDto,
  MoveRequest,
  Printer,
  PrinterCameraUrls,
  PrinterCapabilitiesDto,
  PrinterBackendCapabilitiesDto,
  PrinterDetails,
  PrinterFast,
  PrinterFileDto,
  PrinterModelDto,
  RegisterRequest,
  ResolveHostnameRequest,
  StartDiscoveryRequest,
  ResolveHostnameResponse,
  SlicerModelAliasDto,
  SpoolmanDiscoveryResult,
  SpoolmanFilamentImportResult,
  TempTargets,
  UpdateFilamentTypeRequest,
  UpdateModelAliasesRequest,
  UpdateModelRequest,
  UpdatePrinterDto,
  UserDto,
  DiscoveredGcodeFileDto,
  GcodeHarvestResultDto,
  BulkImportResponse,
} from "@/types/api";
import type { AxiosError, AxiosInstance, AxiosRequestConfig } from "axios";
import axios from "axios";

export class ApiClient {
  // Utility to generate a correlation ID (UUID v4)
  private static generateCorrelationId(): string {
    // Use crypto API if available, fallback to random
    if (typeof crypto !== "undefined" && crypto.randomUUID) {
      return crypto.randomUUID();
    }
    // Fallback: simple random string
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(
      /[xy]/g,
      function (c) {
        const r = (Math.random() * 16) | 0,
          v = c === "x" ? r : (r & 0x3) | 0x8;
        return v.toString(16);
      }
    );
  }
  // ============ Generic Settings API methods ============
  /**
   * Get settings for any settings class by class name
   */
  async getSettings<T = Record<string, unknown>>(
    className: string
  ): Promise<T> {
    const res = await this.client.get(`/settings/${className}`);
    return res.data;
  }

  /**
   * Save settings for any settings class by class name
   */
  async saveSettings<T = Record<string, unknown>>(
    className: string,
    settings: T
  ): Promise<void> {
    await this.client.post(`/settings/${className}`, settings);
  }

  /**
   * Get all settings metadata for dynamic UI generation
   */
  async getSettingsMetadata(): Promise<Array<Record<string, unknown>>> {
    const res = await this.client.get("/settings/metadata");
    return res.data;
  }

  /**
   * Get all unified settings
   */
  async getAllSettings(): Promise<Record<string, unknown>> {
    const res = await this.client.get("/settings");
    return res.data;
  }

  /**
   * Save all unified settings
   */
  async saveAllSettings(settings: Record<string, unknown>): Promise<void> {
    await this.client.post("/settings", settings);
  }

  private client: AxiosInstance;

  constructor() {
    // Use shared utility to properly construct API base URL
    const apiBaseUrl = getApiBaseUrl();

    this.client = axios.create({
      baseURL: apiBaseUrl,
      timeout: 30000,
    });

    // Request interceptor for authentication and correlationId
    this.client.interceptors.request.use((config) => {
      const token = localStorage.getItem("auth-token");
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      // Add correlationId header to every request
      config.headers["X-Correlation-Id"] = ApiClient.generateCorrelationId();

      // Set Content-Type for non-FormData requests
      // FormData has its own Content-Type with boundary, so we let the browser/axios handle it
      if (!(config.data instanceof FormData)) {
        config.headers["Content-Type"] = "application/json";
      }

      return config;
    });

    // Response interceptor for error handling
    this.client.interceptors.response.use(
      (response) => response,
      (error: AxiosError) => {
        // Handle 401 Unauthorized - clear token and redirect to login
        if (error.response?.status === 401) {
          localStorage.removeItem("auth-token");
          // Only redirect if not already on auth pages
          if (
            window.location.pathname !== "/login" &&
            window.location.pathname !== "/register"
          ) {
            window.location.href = "/login";
          }
        }

        const apiError: ApiError = {
          message: error.message,
          statusCode: error.response?.status || 500,
          details: (error.response?.data as string) || undefined,
        };
        return Promise.reject(apiError);
      }
    );
  }

  // ===== Harvest/discovered file API methods =====
  // Get discovered G-code files for a harvest operation
  async getDiscoveredGcodeFiles(
    harvestOperationId: string
  ): Promise<DiscoveredGcodeFileDto[]> {
    const resp = await this.client.get<DiscoveredGcodeFileDto[]>(
      `/gcode-harvest/operations/${harvestOperationId}/files`
    );
    return resp.data;
  }

  // Import selected discovered G-code files
  async importSelectedGcodeFiles(
    dto: { harvestOperationId: string; fileIds: string[] },
    options?: { timeout?: number }
  ): Promise<GcodeHarvestResultDto> {
    // Backend exposes this endpoint under /api/gcode-harvest/import
    const config: AxiosRequestConfig = options?.timeout
      ? { timeout: options.timeout }
      : {};
    const resp = await this.client.post<GcodeHarvestResultDto>(
      `/gcode-harvest/import`,
      dto,
      config
    );
    return resp.data;
  }

  // Skip a discovered G-code file in a harvest operation
  async skipDiscoveredGcodeFile(
    operationId: string,
    fileId: string
  ): Promise<DiscoveredGcodeFileDto> {
    const resp = await this.client.post<DiscoveredGcodeFileDto>(
      `/gcode-harvest/operations/${operationId}/files/${fileId}/skip`,
      {}
    );
    return resp.data;
  }

  // Retry a discovered G-code file in a harvest operation
  async retryDiscoveredGcodeFile(
    operationId: string,
    fileId: string
  ): Promise<DiscoveredGcodeFileDto> {
    const resp = await this.client.post<DiscoveredGcodeFileDto>(
      `/gcode-harvest/operations/${operationId}/files/${fileId}/retry`,
      {}
    );
    return resp.data;
  }

  // Get hash for a G-code file (returns string)
  async getGcodeFileHash(
    path: string,
    algorithm: "sha256" | "sha1" = "sha256"
  ): Promise<string> {
    const resp = await this.client.get<{ hash: string }>(`/gcode-files/hash`, {
      params: { path, algorithm },
    });
    return resp.data.hash;
  }

  // ============ Printer API methods ============

  async getPrinters(includeDisabled?: boolean): Promise<Printer[]> {
    // Get lightweight list of all printers
    const params = includeDisabled ? { includeDisabled: true } : undefined;
    const response = await this.client.get<PrinterFast[]>("/printers", { params });
    // Cast to Printer[] for compatibility; fast objects are subset of Printer
    return response.data as unknown as Printer[];
  }

  async getPrintersFast(includeDisabled?: boolean): Promise<PrinterFast[]> {
    const params = includeDisabled ? { includeDisabled: true } : undefined;
    const response = await this.client.get<PrinterFast[]>("/printers", { params });
    return response.data;
  }

  async getPrinterCameraUrls(): Promise<PrinterCameraUrls[]> {
    const response = await this.client.get<PrinterCameraUrls[]>(
      "/printers/camera-urls"
    );
    return response.data;
  }

  async getPrinterBackendCapabilities(): Promise<PrinterBackendCapabilitiesDto[]> {
    const response = await this.client.get<PrinterBackendCapabilitiesDto[]>(
      "/printers/backend-capabilities"
    );
    return response.data;
  }

  async getPrinterBackendCapabilitiesSingle(printerId: string): Promise<PrinterBackendCapabilitiesDto> {
    const response = await this.client.get<PrinterBackendCapabilitiesDto>(
      `/printers/${printerId}/backend-capabilities`
    );
    return response.data;
  }

  async getPrinter(id: string): Promise<Printer> {
    const response = await this.client.get<Printer>(`/printers/${id}`);
    return response.data;
  }

  async getPrinterDetails(id: string): Promise<PrinterDetails> {
    const response = await this.client.get<PrinterDetails>(
      `/printers/${id}/details`
    );
    return response.data;
  }

  async exportPrintersByIds(
    ids?: string[]
  ): Promise<import("@/types/api").PrinterWithCapabilitiesDto[]> {
    const resp = await this.client.post<
      import("@/types/api").PrinterWithCapabilitiesDto[]
    >("/printers/export", ids || []);
    return resp.data;
  }

  /**
   * Request a server-generated export file (CSV or JSON) and stream-download it in the browser.
   * onProgress is optional and receives (loaded, total?) bytes while streaming.
   */
  async streamExportFile(
    ids?: string[],
    format: "json" | "csv" = "json",
    filename?: string,
    onProgress?: (loaded: number, total?: number) => void
  ): Promise<void> {
    const base = (this.client.defaults.baseURL as string) || "/api";
    const url = `${base.replace(
      /\/$/,
      ""
    )}/printers/export/file?format=${encodeURIComponent(format)}`;

    const token = localStorage.getItem("auth-token");
    const correlationId = ApiClient.generateCorrelationId();

    const resp = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        "X-Correlation-Id": correlationId,
      },
      body: JSON.stringify(ids || []),
      // Keep credentials false - API uses bearer token header
    });

    if (!resp.ok) {
      const text = await resp.text().catch(() => undefined);
      throw new Error(
        `Export failed: ${resp.status} ${resp.statusText}${
          text ? ` - ${text}` : ""
        }`
      );
    }

    // Try to determine filename from Content-Disposition header if not provided
    const contentDisposition = resp.headers.get("content-disposition");
    const derivedName = (() => {
      if (filename) return filename;
      if (!contentDisposition)
        return `printfarmer-printers-${new Date()
          .toISOString()
          .slice(0, 10)}.${format}`;
      const m = /filename\*=UTF-8''([^;\n]+)/i.exec(contentDisposition);
      if (m && m[1]) return decodeURIComponent(m[1]);
      const m2 = /filename="?([^";]+)"?/i.exec(contentDisposition);
      if (m2 && m2[1]) return m2[1];
      return `printfarmer-printers-${new Date()
        .toISOString()
        .slice(0, 10)}.${format}`;
    })();

    // If there's no body stream available, fall back to blob()
    const reader = resp.body?.getReader();
    if (!reader) {
      const blob = await resp.blob();
      const urlObj = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = urlObj;
      a.download = derivedName;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.URL.revokeObjectURL(urlObj);
      return;
    }

    const contentLengthHeader = resp.headers.get("content-length");
    const total = contentLengthHeader
      ? parseInt(contentLengthHeader, 10)
      : undefined;
    const chunks: Uint8Array[] = [];
    let loaded = 0;
    try {
      // Read the stream
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value) {
          chunks.push(value);
          loaded += value.byteLength;
          if (onProgress) onProgress(loaded, total);
        }
      }
    } finally {
      try {
        await reader.cancel();
      } catch (err) {
        console.debug("reader cancel failed", err);
      }
    }

    // Combine chunks into a single Uint8Array (backed by an ArrayBuffer we control)
    const totalLen = chunks.reduce((s, c) => s + c.byteLength, 0);
    const combined = new Uint8Array(totalLen);
    let offset = 0;
    for (const c of chunks) {
      combined.set(c, offset);
      offset += c.byteLength;
    }
    // Create blob from the combined Uint8Array (ArrayBufferView allowed as BlobPart)
    const blob = new Blob([combined], {
      type:
        resp.headers.get("content-type") ||
        (format === "json" ? "application/json" : "text/csv"),
    });
    const urlObj = window.URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = urlObj;
    a.download = derivedName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    window.URL.revokeObjectURL(urlObj);
  }

  /**
   * Upload a printer import file (starts the import process)
   */
  async uploadPrinterImport(formData: FormData): Promise<void> {
    await this.client.post("/printers/import", formData);
  }

  async createPrinter(printer: CreatePrinterDto): Promise<Printer> {
    const response = await this.client.post<Printer>("/printers", printer);
    return response.data;
  }

  async bulkCreatePrinters(
    printers: CreatePrinterDto[],
    options?: { duplicateHandling?: string }
  ): Promise<BulkImportResponse> {
    const qp = options?.duplicateHandling
      ? `?duplicateHandling=${encodeURIComponent(options.duplicateHandling)}`
      : "";
    const resp = await this.client.post<BulkImportResponse>(
      `/printers/bulk${qp}`,
      printers
    );
    return resp.data;
  }

  async updatePrinter(id: string, printer: UpdatePrinterDto): Promise<Printer> {
    const response = await this.client.put<Printer>(`/printers/${id}`, printer);
    return response.data;
  }

  async refreshCameraUrls(id: string): Promise<Printer> {
    const response = await this.client.post<Printer>(`/printers/${id}/refresh-cameras`);
    return response.data;
  }

  async deletePrinter(id: string): Promise<void> {
    await this.client.delete(`/printers/${id}`);
  }

  async discoverPrinters(): Promise<DiscoveredPrinterDto[]> {
    const response = await this.client.get<DiscoveredPrinterDto[]>(
      "/printers/discover"
    );
    return response.data;
  }

  async startDiscoveryStream(
    request?: StartDiscoveryRequest
  ): Promise<{ sessionId: string; message: string }> {
    const response = await this.client.post<{
      sessionId: string;
      message: string;
    }>("/printers/discover/stream", request || {});
    return response.data;
  }

  async cancelDiscoveryStream(sessionId: string): Promise<{ message: string }> {
    const response = await this.client.post<{ message: string }>(
      `/printers/discover/${sessionId}/cancel`,
      {}
    );
    return response.data;
  }

  // ============ Printer Control API methods ============

  async setTemperatures(
    printerId: string,
    targets: TempTargets
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/temps`,
      targets
    );
    return response.data;
  }

  async movePrinter(
    printerId: string,
    move: MoveRequest
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/move`,
      move
    );
    return response.data;
  }

  async movePrinterTo(
    printerId: string,
    position: MoveRequest
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/moveto`,
      position
    );
    return response.data;
  }

  async homePrinter(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/home`
    );
    return response.data;
  }

  async homeXY(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/homexy`
    );
    return response.data;
  }

  async homeZ(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/homez`
    );
    return response.data;
  }

  async pausePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/pause`
    );
    return response.data;
  }

  async resumePrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/resume`
    );
    return response.data;
  }

  async emergencyStop(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/emergency-stop`
    );
    return response.data;
  }

  async firmwareRestart(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/firmware-restart`
    );
    return response.data;
  }

  async disableMotors(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/disable-motors`
    );
    return response.data;
  }

  // ============ Printer History API methods ============

  async getPrinterHistory(
    printerId: string,
    options?: {
      limit?: number;
      start?: number;
      since?: Date;
      before?: Date;
      order?: string;
    }
  ): Promise<HistoryListResponse> {
    const params: Record<string, string | number> = {};
    if (options?.limit) params.limit = options.limit;
    if (options?.start) params.start = options.start;
    if (options?.since) params.since = options.since.toISOString();
    if (options?.before) params.before = options.before.toISOString();
    if (options?.order) params.order = options.order;

    const response = await this.client.get<HistoryListResponse>(
      `/printers/${printerId}/history`,
      { params }
    );
    return response.data;
  }

  async getPrinterHistoryJob(
    printerId: string,
    jobId: string
  ): Promise<HistoryJob> {
    const response = await this.client.get<HistoryJob>(
      `/printers/${printerId}/history/${jobId}`
    );
    return response.data;
  }

  async getPrinterHistoryTotals(printerId: string): Promise<HistoryTotals> {
    const response = await this.client.get<HistoryTotals>(
      `/printers/${printerId}/history/totals`
    );
    return response.data;
  }

  // ============ Printer Files API methods ============

  async getPrinterFileList(printerId: string): Promise<PrinterFileDto[]> {
    const response = await this.client.get<PrinterFileDto[]>(
      `/printers/${printerId}/files`
    );
    return response.data;
  }

  // ============ Catalog API methods ============

  async getManufacturers(): Promise<ManufacturerDto[]> {
    const response = await this.client.get<ManufacturerDto[]>(
      "/catalog/manufacturers"
    );
    return response.data;
  }

  async createManufacturer(name: string, url?: string, description?: string): Promise<ManufacturerDto> {
    const response = await this.client.post<ManufacturerDto>(
      "/catalog/manufacturers",
      { name, url, description }
    );
    return response.data;
  }

  async updateManufacturer(id: string, name: string): Promise<ManufacturerDto> {
    const response = await this.client.put<ManufacturerDto>(
      `/catalog/manufacturers/${id}`,
      { name }
    );
    return response.data;
  }

  async deleteManufacturer(id: string): Promise<void> {
    await this.client.delete(`/catalog/manufacturers/${id}`);
  }

  async getModels(manufacturerId?: string): Promise<PrinterModelDto[]> {
    const params = manufacturerId ? { manufacturerId } : {};
    const response = await this.client.get<PrinterModelDto[]>(
      "/catalog/printer-models",
      { params }
    );
    return response.data;
  }

  async createModel(
    model: Omit<PrinterModelDto, "id">
  ): Promise<PrinterModelDto> {
    const response = await this.client.post<PrinterModelDto>(
      "/catalog/printer-models",
      model
    );
    return response.data;
  }

  async updateModel(
    id: string,
    request: UpdateModelRequest
  ): Promise<PrinterModelDto> {
    const response = await this.client.put<PrinterModelDto>(
      `/catalog/printer-models/${id}`,
      request
    );
    return response.data;
  }

  // Legacy method for simple name updates
  async updateModelName(id: string, name: string): Promise<PrinterModelDto> {
    return this.updateModel(id, { name });
  }

  // Delete a model by id
  async deleteModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/printer-models/${id}`);
  }

  // Get slicer model aliases for a printer model
  async getModelAliases(modelId: string): Promise<SlicerModelAliasDto[]> {
    const response = await this.client.get<SlicerModelAliasDto[]>(
      `/catalog/printer-models/${modelId}/aliases`
    );
    return response.data;
  }

  // Update slicer model aliases for a printer model
  async updateModelAliases(
    modelId: string,
    request: UpdateModelAliasesRequest
  ): Promise<SlicerModelAliasDto[]> {
    const response = await this.client.put<SlicerModelAliasDto[]>(
      `/catalog/printer-models/${modelId}/aliases`,
      request
    );
    return response.data;
  }

  // Get default capabilities for a printer model
  async getModelDefaultCapabilities(
    modelId: string
  ): Promise<PrinterCapabilitiesDto | null> {
    try {
      const response = await this.client.get<PrinterCapabilitiesDto>(
        `/printers/model/${modelId}/default-capabilities`
      );
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 204) {
        return null; // No default capabilities available
      }
      throw error;
    }
  }

  // ============ File type API methods ============

  // ============ Filament Type API methods ============

  async getFilamentTypes(): Promise<FilamentTypeDto[]> {
    const response = await this.client.get<FilamentTypeDto[]>(
      "/filament-types"
    );
    return response.data;
  }

  async createFilamentType(
    filamentType: CreateFilamentTypeRequest
  ): Promise<FilamentTypeDto> {
    const response = await this.client.post<FilamentTypeDto>(
      "/filament-types",
      filamentType
    );
    return response.data;
  }

  async updateFilamentType(
    id: string,
    filamentType: UpdateFilamentTypeRequest
  ): Promise<void> {
    await this.client.put(`/filament-types/${id}`, filamentType);
  }

  async deleteFilamentType(id: string): Promise<void> {
    await this.client.delete(`/filament-types/${id}`);
  }

  async getFilamentPresets(): Promise<FilamentPresets> {
    const response = await this.client.get<{ presets: FilamentPresets }>(
      "/filament-types/presets"
    );
    return response.data.presets;
  }

  async saveFilamentPresets(presets: FilamentPresets): Promise<void> {
    await this.client.post("/filament-types/presets", { presets });
  }

  async importFilamentTypesFromSpoolman(): Promise<SpoolmanFilamentImportResult> {
    const response = await this.client.post<SpoolmanFilamentImportResult>(
      "/filament-types/import-from-spoolman"
    );
    return response.data;
  }

  async scanNetworkForSpoolman(): Promise<SpoolmanDiscoveryResult[]> {
    const response = await this.client.post<SpoolmanDiscoveryResult[]>(
      "/spoolman/scan-network"
    );
    return response.data;
  }

  // ============ Network utilities ============

  async resolveHostname(
    request: ResolveHostnameRequest
  ): Promise<ResolveHostnameResponse> {
    const response = await this.client.post<ResolveHostnameResponse>(
      "/resolve-hostname",
      request
    );
    return response.data;
  }

  // ============ G-code library methods ============

  async getGcodeFiles(page = 1, pageSize = 50): Promise<GcodeFile[]> {
    const response = await this.client.get<GcodeFile[]>("/gcode-files", {
      params: { page, pageSize },
    });
    return response.data;
  }

  async getGcodeFile(id: string): Promise<GcodeFile> {
    const response = await this.client.get<GcodeFile>(`/gcode-files/${id}`);
    return response.data;
  }

  async uploadGcodeFile(
    file: File,
    description?: string,
    tags?: string[]
  ): Promise<GcodeFile> {
    const formData = new FormData();
    formData.append("file", file);
    if (description) formData.append("description", description);
    if (tags) formData.append("tags", JSON.stringify(tags));

    const response = await this.client.post<GcodeFile>(
      "/gcode-files/upload",
      formData,
      {
        headers: {
          "Content-Type": "multipart/form-data",
        },
      }
    );
    return response.data;
  }

  async deleteGcodeFile(id: string): Promise<void> {
    await this.client.delete(`/gcode-files/${id}`);
  }

  async createGcodeDirectory(path: string): Promise<{ success: boolean; message?: string }> {
    const response = await this.client.post<{ success: boolean; message?: string }>(
      `/gcode-files/folder`,
      { path }
    );
    return response.data;
  }

  async queryGcodeLibrary(
    search?: string,
    material?: string,
    nozzleDiameter?: number,
    targetPrinterId?: string
  ): Promise<GcodeLibraryFile[]> {
    const params: Record<string, unknown> = {};
    if (search) params.search = search;
    if (material) params.material = material;
    if (nozzleDiameter) params.nozzleDiameter = nozzleDiameter;
    if (targetPrinterId) params.targetPrinterId = targetPrinterId;

    const response = await this.client.get<GcodeLibraryFile[]>("/gcode-files-library", {
      params,
    });
    return response.data;
  }

  // ============ G-code harvest operations ============

  async startHarvestOperation(
    printerId: string,
    opts?: {
      includeSubdirectories?: boolean;
      maxFileSizeBytes?: number;
      modifiedAfter?: Date | string;
      fileExtensions?: string[];
      minFileSizeBytes?: number;
      duplicateHandling?: string;
    }
  ): Promise<{ queueItemId: string }> {
    const payload = {
      printerId,
      includeSubdirectories: opts?.includeSubdirectories ?? true,
      maxFileSizeBytes: opts?.maxFileSizeBytes ?? 100 * 1024 * 1024,
      modifiedAfter: opts?.modifiedAfter
        ? typeof opts.modifiedAfter === "string"
          ? opts.modifiedAfter
          : opts.modifiedAfter.toISOString()
        : undefined,
      fileExtensions: opts?.fileExtensions,
      minFileSizeBytes: opts?.minFileSizeBytes,
      duplicateHandling: opts?.duplicateHandling,
    };
    const response = await this.client.post("/gcode-harvest/start", payload);
    return response.data as { queueItemId: string };
  }

  async harvestSingleFile(
    printerId: string,
    filename: string
  ): Promise<{ queueItemId: string }> {
    const response = await this.client.post(
      `/gcode-harvest/printers/${printerId}/files/harvest`,
      null,
      {
        params: {
          filename,
        },
      }
    );
    return response.data as { queueItemId: string };
  }

  async startBulkHarvest(
    printerIds: string[],
    options: {
      includeSubfolders?: boolean;
      maxFileAge?: number;
      fileTypes?: string[];
      minFileSize?: number;
      duplicateHandling?: string;
    } = {}
  ): Promise<{ operationIds: string[] }> {
    const modifiedAfter = options.maxFileAge
      ? new Date(Date.now() - options.maxFileAge)
      : undefined;
    const results = await Promise.all(
      printerIds.map((pid) =>
        this.startHarvestOperation(pid, {
          includeSubdirectories: options?.includeSubfolders ?? true,
          modifiedAfter,
          fileExtensions: options.fileTypes,
          minFileSizeBytes: options.minFileSize,
          duplicateHandling: options.duplicateHandling,
        }).catch((err) => {
          console.error("Failed to start harvest for printer", pid, err);
          return null;
        })
      )
    );
    return {
      operationIds: results
        .filter((r) => r !== null)
        .map((r) => (r as { queueItemId: string }).queueItemId),
    };
  }

  async getHarvestOperations(
    printerId?: string,
    status?: string,
    limit?: number,
    offset?: number
  ): Promise<GcodeHarvestOperation[]> {
    const params: Record<string, string | number> = {};
    if (printerId) params.printerId = printerId;
    if (status) params.status = status;
    if (limit) params.limit = limit;
    if (offset) params.offset = offset;

    const response = await this.client.get<GcodeHarvestOperation[]>(
      "/gcode-harvest/operations",
      { params }
    );
    return response.data;
  }

  async getHarvestOperation(id: string): Promise<GcodeHarvestOperation> {
    const response = await this.client.get<GcodeHarvestOperation>(
      `/gcode-harvest/operations/${id}`
    );
    return response.data;
  }

  async waitForHarvestOperationCreated(
    printerId: string,
    timeoutMs: number = 10000
  ): Promise<string | null> {
    const startTime = Date.now();
    const pollInterval = 200; // Poll every 200ms

    while (Date.now() - startTime < timeoutMs) {
      try {
        const operations = await this.getHarvestOperations(printerId, "Processing");
        if (operations.length > 0) {
          return operations[0].id;
        }
      } catch {
        // Continue polling even if endpoint errors
        console.debug("Polling for harvest operation creation...");
      }
      await new Promise((resolve) => setTimeout(resolve, pollInterval));
    }

    return null;
  }

  async getActiveHarvestForPrinter(
    printerId: string
  ): Promise<GcodeHarvestOperation | null> {
    const response = await this.client.get<GcodeHarvestOperation | null>(
      `/gcode-harvest/printers/${printerId}/active`
    );
    return response.data;
  }

  async getAllActiveHarvests(): Promise<GcodeHarvestOperation[]> {
    const response = await this.client.get<GcodeHarvestOperation[]>(
      "/gcode-harvest/active"
    );
    return response.data;
  }

  async restartHarvestDiscovery(operationId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(
      `/gcode-harvest/operations/${operationId}/restart-discovery`
    );
    return response.data === true;
  }

  async cancelHarvestOperation(operationId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(
      `/gcode-harvest/operations/${operationId}/cancel`
    );
    return response.data;
  }

  /**
   * Skip a file in a harvest operation (mark as skipped and emit update)
   * @param operationId The harvest operation ID
   * @param fileId The file ID to skip
   * @returns Promise<boolean> indicating success
   */
  async skipHarvestFile(operationId: string, fileId: string): Promise<boolean> {
    const response = await this.client.post<boolean>(
      `/gcode-harvest/operations/${operationId}/files/${fileId}/skip`
    );
    return response.data === true;
  }

  /**
   * Retry a file in a harvest operation (reset error and reprocess)
   * @param operationId The harvest operation ID
   * @param fileId The file ID to retry
   * @returns Promise<boolean> indicating success
   */
  async retryHarvestFile(
    operationId: string,
    fileId: string
  ): Promise<boolean> {
    const response = await this.client.post<boolean>(
      `/gcode-harvest/operations/${operationId}/files/${fileId}/retry`
    );
    return response.data === true;
  }

  async getGcodeFilesWithFilter(
    request: Record<string, unknown>
  ): Promise<GetGcodeFilesResponse> {
    // Filter out undefined values and viewMode (viewMode is for frontend routing, not API)
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const { viewMode, ...apiRequest } = request;
    const params = Object.fromEntries(
      Object.entries(apiRequest).filter(([, value]) => value !== undefined)
    );
    
    // Debug logging
    if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
      console.log('[API Client] getGcodeFilesWithFilter params after filtering:', params);
    }
    
    const response = await this.client.get<GetGcodeFilesResponse>(
      "/gcode-files",
      { params }
    );
    return response.data;
  }

  async getGcodeFilesQuery(
    request: Record<string, unknown>
  ): Promise<GetGcodeFilesResponse> {
    // New efficient endpoint with database-level filtering
    // Filter out undefined values and viewMode (viewMode is for frontend routing, not API)
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const { viewMode, ...apiRequest } = request;
    const params = Object.fromEntries(
      Object.entries(apiRequest).filter(([, value]) => value !== undefined)
    );
    
    // Debug logging
    if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { gcodeFileBrowser?: boolean } }).PrintFarmerDebug?.gcodeFileBrowser) {
      console.log('[API Client] getGcodeFilesQuery params after filtering:', params);
    }
    
    const response = await this.client.get<GetGcodeFilesResponse>(
      "/gcode-files/query",
      { params }
    );
    return response.data;
  }

  async getGcodeFilesFolders(): Promise<Array<{ path: string; fileName: string; isDirectory: boolean }>> {
    const response = await this.client.get<Array<{ path: string; fileName: string; isDirectory: boolean }>>(
      "/gcode-files/folders"
    );
    return response.data;
  }

  async get3DModelsQuery(
    request: Record<string, unknown>
  ): Promise<unknown> {
    // Query endpoint for 3D models with filtering, pagination, and sorting
    // Backend uses POST for consistency with other complex queries
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const { viewMode, ...apiRequest } = request;

    const response = await this.client.post(
      "/3d-models/query",
      apiRequest
    );
    return response.data;
  }

  async deleteGcodeFiles(fileIds: string[]): Promise<void> {
    await this.client.delete("/gcode-files", { data: { fileIds } });
  }

  async deleteModel3dFile(id: string): Promise<void> {
    await this.client.delete(`/3d-models/${id}`);
  }

  async deleteModel3dFiles(modelIds: string[]): Promise<void> {
    await this.client.delete("/3d-models", { data: { modelIds } });
  }

  async downloadGcodeFile(filePath: string, originalName?: string): Promise<void> {
    const response = await this.client.get(`/gcode-files/download`, {
      params: { path: filePath },
      responseType: "blob",
    });

    // Create a download link using original filename if available
    const url = window.URL.createObjectURL(new Blob([response.data]));
    const link = document.createElement("a");
    link.href = url;
    const fileName = originalName || filePath.split("/").pop() || "file.gcode";
    link.setAttribute("download", fileName);
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.URL.revokeObjectURL(url);
  }

  async uploadGcodeLibraryFile(
    file: File,
    virtualPath = "/",
    onProgress?: (fileName: string, progress: number) => void
  ): Promise<GcodeLibraryFile> {
    const form = new FormData();
    form.append("file", file);
    
    // Create a new XMLHttpRequest to track progress
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();

      // Track upload progress
      if (onProgress) {
        xhr.upload.addEventListener("progress", (event) => {
          if (event.lengthComputable) {
            const percentComplete = (event.loaded / event.total) * 100;
            onProgress(file.name, percentComplete);
          }
        });
      }

      xhr.addEventListener("load", () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            const response = JSON.parse(xhr.responseText) as GcodeLibraryFile;
            if (onProgress) {
              onProgress(file.name, 100);
            }
            resolve(response);
          } catch {
            reject(new Error("Failed to parse upload response"));
          }
        } else {
          reject(new Error(`Upload failed: ${xhr.statusText}`));
        }
      });

      xhr.addEventListener("error", () => {
        reject(new Error("Upload request failed"));
      });

      xhr.addEventListener("abort", () => {
        reject(new Error("Upload was cancelled"));
      });

      // Build URL with params
      const params = new URLSearchParams({ path: virtualPath });
      xhr.open("POST", `/api/gcode-files/upload?${params.toString()}`);

      // Set auth header if available
      const token = localStorage.getItem("auth_token");
      if (token) {
        xhr.setRequestHeader("Authorization", `Bearer ${token}`);
      }

      xhr.send(form);
    });
  }

  /**
   * Upload a single 3D model file.
   * Supports progress tracking via XMLHttpRequest upload event.
   */
  async uploadModel3dFile(
    file: File,
    virtualPath = "/",
    onProgress?: (fileName: string, progress: number) => void
  ): Promise<import("@/types/api").Model3DUploadResultDto> {
    const form = new FormData();
    form.append("file", file);
    
    // Create a new XMLHttpRequest to track progress
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();

      // Track upload progress
      if (onProgress) {
        xhr.upload.addEventListener("progress", (event) => {
          if (event.lengthComputable) {
            const percentComplete = (event.loaded / event.total) * 100;
            onProgress(file.name, percentComplete);
          }
        });
      }

      xhr.addEventListener("load", () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            const response = JSON.parse(xhr.responseText) as import("@/types/api").Model3DUploadResultDto;
            if (onProgress) {
              onProgress(file.name, 100);
            }
            resolve(response);
          } catch {
            reject(new Error("Failed to parse upload response"));
          }
        } else {
          reject(new Error(`Upload failed: ${xhr.statusText}`));
        }
      });

      xhr.addEventListener("error", () => {
        reject(new Error("Upload request failed"));
      });

      xhr.addEventListener("abort", () => {
        reject(new Error("Upload was cancelled"));
      });

      // Build URL with params
      const params = new URLSearchParams({ path: virtualPath });
      xhr.open("POST", `/api/3d-models/upload?${params.toString()}`);

      // Set auth header if available
      const token = localStorage.getItem("auth_token");
      if (token) {
        xhr.setRequestHeader("Authorization", `Bearer ${token}`);
      }

      xhr.send(form);
    });
  }

  async getGcodeUploadSettings(): Promise<
    import("@/types/api").GcodeUploadSettings
  > {
    const resp = await this.client.get("/gcode-files/settings");
    return resp.data as import("@/types/api").GcodeUploadSettings;
  }

  async updateGcodeUploadSettings(allowedExtensions: string[]): Promise<void> {
    await this.client.put("/gcode-files/settings", { allowedExtensions });
  }

  async moveGcodePath(
    sourcePath: string,
    destinationPath: string,
    overwrite = false
  ): Promise<{ path: string; isDirectory: boolean }> {
    const resp = await this.client.post("/gcode-files/move", {
      sourcePath,
      destinationPath,
      overwrite,
    });
    return resp.data as { path: string; isDirectory: boolean };
  }

  async moveGcodeFiles(
    fileIds: string[],
    targetFolderPath: string
  ): Promise<{ success: boolean; message?: string }> {
    const resp = await this.client.post("/gcode-files/move", {
      modelIds: fileIds,
      targetDirectoryId: targetFolderPath,
    });
    return resp.data as { success: boolean; message?: string };
  }

  // ============ 3D Model methods ============

  async listModelsFolders(): Promise<Array<{ path: string; fileName: string; isDirectory: boolean }>> {
    const response = await this.client.get<Array<{ path: string; fileName: string; isDirectory: boolean }>>(
      "/3d-models/folders"
    );
    return response.data;
  }

  async moveModel3dFiles(
    fileIds: string[],
    targetFolderPath: string
  ): Promise<{ success: boolean; message?: string }> {
    const resp = await this.client.post("/3d-models/move", {
      modelIds: fileIds,
      targetDirectoryId: targetFolderPath,
    });
    return resp.data as { success: boolean; message?: string };
  }

  // ============ Job Queue methods ============
  // NOTE: This is the simpler job queue API for basic queue management.
  // For the advanced Print Queue Dashboard with detailed analytics, see Print Queue methods below.

  async getJobQueue(printerId?: string): Promise<JobQueuePrintJob[]> {
    const params = printerId ? { printerId } : {};
    const response = await this.client.get<JobQueuePrintJob[]>("/job-queue", {
      params,
    });
    return response.data;
  }

  async queuePrintJob(
    printerId: string,
    gcodeFileId: string,
    priority = 0
  ): Promise<JobQueuePrintJob> {
    const response = await this.client.post<JobQueuePrintJob>("/job-queue", {
      printerId,
      gcodeFileId,
      priority,
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
    formData.append("file", file);

    const response = await this.client.post<{ success: boolean }>(
      `/printers/${printerId}/upload-gcode`,
      formData,
      {
        headers: {
          "Content-Type": "multipart/form-data",
        },
      }
    );
    return response.data.success;
  }

  async startPrintFromFile(
    printerId: string,
    fileName: string
  ): Promise<boolean> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/print`,
      { fileName }
    );
    if (!response.data.success) {
      throw new Error(response.data.message || response.data.error || 'Failed to start print');
    }
    return true;
  }

  async deletePrinterFile(
    printerId: string,
    fileName: string
  ): Promise<boolean> {
    const response = await this.client.delete<CommandResult>(
      `/printers/${printerId}/files`,
      { data: { fileName } }
    );
    if (!response.data.success) {
      throw new Error(response.data.message || response.data.error || 'Failed to delete file');
    }
    return true;
  }

  // ============ Health checks ============

  async getHealthStatus(): Promise<HealthStatus> {
    const response = await this.client.get<HealthStatus>("/health");
    return response.data as HealthStatus;
  }

  async getBasicHealth(): Promise<{ status: string }> {
    const response = await this.client.get<{ status: string }>("/healthz");
    return response.data;
  }

  // ============ Authentication API methods ============

  async login(credentials: LoginRequest): Promise<AuthenticationResult> {
    // Backend expects the field name `UsernameOrEmail` (model uses UsernameOrEmail).
    // Frontend `LoginRequest` type historically used `username` so map that to
    // `usernameOrEmail` to remain backwards-compatible and avoid model binding
    // validation errors (400 Bad Request).
    const usernameOrEmail =
      (credentials as LoginRequest & { username?: string }).usernameOrEmail ??
      (credentials as LoginRequest & { username?: string }).username;

    const payload = {
      usernameOrEmail,
      password: credentials.password,
    } as Record<string, string>;

    const response = await this.client.post<AuthenticationResult>(
      "/auth/login",
      payload
    );
    return response.data;
  }

  async register(userData: RegisterRequest): Promise<AuthenticationResult> {
    const response = await this.client.post<AuthenticationResult>(
      "/auth/register",
      userData
    );
    return response.data;
  }

  async getCurrentUser(): Promise<UserDto> {
    const response = await this.client.get<UserDto>("/auth/me");
    return response.data;
  }

  async logout(): Promise<void> {
    await this.client.post("/auth/logout");
  }

  async forgotPassword(
    email: string
  ): Promise<{ success: boolean; message: string }> {
    const response = await this.client.post<{
      success: boolean;
      message: string;
    }>("/auth/forgot-password", { email });
    return response.data;
  }

  async resetPassword(
    token: string,
    email: string,
    newPassword: string,
    confirmPassword: string
  ): Promise<{ success: boolean; message: string }> {
    const response = await this.client.post<{
      success: boolean;
      message: string;
    }>("/auth/reset-password", { token, email, newPassword, confirmPassword });
    return response.data;
  }

  async confirmEmail(
    token: string
  ): Promise<{ success: boolean; message: string }> {
    const response = await this.client.post<{
      success: boolean;
      message: string;
    }>("/auth/confirm-email", { token });
    return response.data;
  }

  async resendEmailConfirmation(): Promise<{
    success: boolean;
    message: string;
  }> {
    const response = await this.client.post<{
      success: boolean;
      message: string;
    }>("/auth/resend-confirmation");
    return response.data;
  }

  // ============ Generic request method ============

  async request<T>(config: AxiosRequestConfig): Promise<T> {
    const response = await this.client.request<T>(config);
    return response.data;
  }
  // Get print job status for Moonraker printers
  async getPrintJobStatus(
    printerId: string
  ): Promise<PrintJobStatusDto | null> {
    try {
      const response = await this.client.get<PrintJobStatusDto>(
        `/printers/${printerId}/printjob`
      );
      return response.data;
    } catch {
      return null;
    }
  }

  // ============ File Consistency API methods ============

  /**
   * Get overall file health summary
   */
  async getFileHealthSummary(): Promise<
    import("@/types/api").FileHealthSummaryDto
  > {
    const response = await this.client.get<
      import("@/types/api").FileHealthSummaryDto
    >("/fileconsistency/health/summary");
    return response.data;
  }

  /**
   * Get audit history with pagination
   */
  async getFileAuditHistory(
    pageSize: number = 20
  ): Promise<import("@/types/api").FileHealthAuditDto[]> {
    const response = await this.client.get<
      import("@/types/api").FileHealthAuditDto[]
    >("/fileconsistency/audits/history", {
      params: { pageSize },
    });
    return response.data;
  }

  /**
   * Get all files with health issues
   */
  async getFilesWithIssues(): Promise<
    import("@/types/api").FileIssuesSummaryDto
  > {
    const response = await this.client.get<
      import("@/types/api").FileIssuesSummaryDto
    >("/fileconsistency/issues");
    return response.data;
  }

  /**
   * Get detailed health information for a specific Model3D file
   */
  async getModel3DHealth(
    id: string
  ): Promise<import("@/types/api").FileHealthDetailDto> {
    const response = await this.client.get<
      import("@/types/api").FileHealthDetailDto
    >(`/fileconsistency/model3d/${id}/health`);
    return response.data;
  }

  /**
   * Get detailed health information for a specific GcodeFile
   */
  async getGcodeFileHealth(
    id: string
  ): Promise<import("@/types/api").FileHealthDetailDto> {
    const response = await this.client.get<
      import("@/types/api").FileHealthDetailDto
    >(`/fileconsistency/gcode/${id}/health`);
    return response.data;
  }

  // ============ Location API methods ============
  async getAllLocations(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/locations');
    return response.data;
  }

  async getLocationById(id: string): Promise<Record<string, unknown>> {
    const response = await this.client.get(`/locations/${id}`);
    return response.data;
  }

  async createLocation(request: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.post('/locations', request);
    return response.data;
  }

  async updateLocation(id: string, request: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(`/locations/${id}`, request);
    return response.data;
  }

  async deleteLocation(id: string): Promise<void> {
    await this.client.delete(`/locations/${id}`);
  }

  // ============ Printer Location API methods ============
  async getAllPrinterLocations(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/printers');
    return response.data || [];
  }

  async assignPrinterToLocation(printerId: string, locationId: string): Promise<Record<string, unknown>> {
    const response = await this.client.post(`/printers/${printerId}/location`, { locationId });
    return response.data;
  }

  async removePrinterFromLocation(printerId: string): Promise<Record<string, unknown>> {
    const response = await this.client.post(`/printers/${printerId}/location/remove`, {});
    return response.data;
  }

  // ============ Tag API methods ============
  async listTags(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/tags');
    return response.data || [];
  }

  async searchTags(query: string): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/tags/search', {
      params: { q: query }
    });
    return response.data || [];
  }

  async getPopularTags(count: number = 10): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/tags/popular', {
      params: { count: count * 2 }
    });
    return response.data || [];
  }

  async getTagAnalytics(): Promise<Record<string, unknown> | null> {
    const response = await this.client.get('/tags/analytics');
    return response.data || null;
  }

  async createTag(name: string, color?: string, description?: string): Promise<Record<string, unknown> | null> {
    const response = await this.client.post('/tags', {
      name,
      color,
      description,
    });
    return response.data || null;
  }

  async deleteTag(tagId: string): Promise<void> {
    await this.client.delete(`/tags/${tagId}`);
  }

  async getTagById(tagId: string): Promise<Record<string, unknown> | null> {
    const response = await this.client.get(`/tags/${tagId}`);
    return response.data || null;
  }

  async assignTagToObject(objectId: string, tagId: string, objectType: 'Model3D' | 'GcodeFile'): Promise<void> {
    await this.client.post(`/tags/${objectId}/${tagId}/assign`, null, {
      params: { objectType }
    });
  }

  async removeTagFromObject(objectId: string, tagId: string, objectType: 'Model3D' | 'GcodeFile'): Promise<void> {
    await this.client.delete(`/tags/${objectId}/${tagId}/remove`, {
      params: { objectType }
    });
  }

  async getObjectTags(objectId: string, objectType: 'Model3D' | 'GcodeFile'): Promise<Record<string, unknown>[]> {
    const response = await this.client.get(`/tags/object/${objectId}`, {
      params: { objectType }
    });
    return response.data || [];
  }

  async getGcodeFileTags(gcodeFileId: string): Promise<Record<string, unknown>[]> {
    return this.getObjectTags(gcodeFileId, 'GcodeFile');
  }

  async filterModelsWithAllTags(tagIds: string[]): Promise<string[]> {
    const response = await this.client.get('/tags/models/filter/all-tags', {
      params: { tags: tagIds.join(',') }
    });
    return response.data || [];
  }

  async filterModelsWithAnyTag(tagIds: string[]): Promise<string[]> {
    const response = await this.client.get('/tags/models/filter/any-tags', {
      params: { tags: tagIds.join(',') }
    });
    return response.data || [];
  }

  async filterModelsComplex(
    includeAllTagIds?: string[],
    includeAnyTagIds?: string[],
    excludeTagIds?: string[]
  ): Promise<string[]> {
    const params: Record<string, string> = {};
    if (includeAllTagIds && includeAllTagIds.length > 0) {
      params.includeAll = includeAllTagIds.join(',');
    }
    if (includeAnyTagIds && includeAnyTagIds.length > 0) {
      params.includeAny = includeAnyTagIds.join(',');
    }
    if (excludeTagIds && excludeTagIds.length > 0) {
      params.exclude = excludeTagIds.join(',');
    }
    const response = await this.client.get('/tags/models/filter', { params });
    return response.data || [];
  }

  // ============ Job Scheduling API methods ============
  async scheduleJob(jobId: string, request: Record<string, unknown>): Promise<Record<string, unknown>> {
    const req = request as { scheduledStartTime: Date; timeZone?: string; recurrencePattern?: string; recurrenceEndDate?: Date };
    const response = await this.client.post(`/jobscheduling/${jobId}/schedule`, {
      scheduledStartTime: req.scheduledStartTime.toISOString(),
      timeZone: req.timeZone || 'UTC',
      recurrencePattern: req.recurrencePattern || null,
      recurrenceEndDate: req.recurrenceEndDate?.toISOString() || null,
    });
    return response.data;
  }

  async rescheduleJob(jobId: string, request: Record<string, unknown>): Promise<Record<string, unknown>> {
    const req = request as { newScheduledTime: Date; timeZone?: string };
    const response = await this.client.put(`/jobscheduling/${jobId}/reschedule`, {
      newScheduledTime: req.newScheduledTime.toISOString(),
      timeZone: req.timeZone || 'UTC',
    });
    return response.data;
  }

  async cancelScheduling(jobId: string): Promise<void> {
    await this.client.delete(`/jobscheduling/${jobId}/schedule`);
  }

  async pauseScheduling(jobId: string): Promise<void> {
    await this.client.post(`/jobscheduling/${jobId}/pause`);
  }

  async resumeScheduling(jobId: string): Promise<void> {
    await this.client.post(`/jobscheduling/${jobId}/resume`);
  }

  async getScheduledJob(jobId: string): Promise<Record<string, unknown> | null> {
    try {
      const response = await this.client.get(`/jobscheduling/${jobId}`);
      return response.data;
    } catch (error: unknown) {
      if ((error as Record<string, unknown>).statusCode === 404) {
        return null;
      }
      throw error;
    }
  }

  async getScheduledJobs(dateFrom?: Date, dateTo?: Date): Promise<Record<string, unknown>[]> {
    const params = new URLSearchParams();
    if (dateFrom) {
      params.append('dateFrom', dateFrom.toISOString());
    }
    if (dateTo) {
      params.append('dateTo', dateTo.toISOString());
    }
    const response = await this.client.get(`/jobscheduling/scheduled?${params.toString()}`);
    return response.data || [];
  }

  async getExecutionHistory(jobId: string): Promise<Record<string, unknown>[]> {
    const response = await this.client.get(`/jobscheduling/${jobId}/executions`);
    return response.data || [];
  }

  async getAvailableTimeZones(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/jobscheduling/timezones');
    return response.data || [];
  }

  // ============ Prediction API methods ============
  async getPrediction(jobId: string): Promise<Record<string, unknown>> {
    const response = await this.client.get(`/predictions/jobs/${jobId}/completion`);
    return response.data;
  }

  async getStatistics(jobId: string): Promise<Record<string, unknown> | null> {
    try {
      const response = await this.client.get(`/predictions/jobs/${jobId}/statistics`);
      return response.data;
    } catch (error: unknown) {
      if ((error as Record<string, unknown>).statusCode === 404) {
        return null;
      }
      throw error;
    }
  }

  async getMaterialStats(
    material?: string,
    printerId?: string,
    minSampleSize?: number
  ): Promise<Record<string, unknown>> {
    const params: Record<string, string> = {};
    if (material) params.material = material;
    if (printerId) params.printerId = printerId;
    if (minSampleSize) params.minSampleSize = minSampleSize.toString();
    const response = await this.client.get('/predictions/stats/by-material', { params });
    return response.data || {};
  }

  async getModelStats(modelId: string, material?: string): Promise<Record<string, unknown> | null> {
    try {
      const params: Record<string, string> = {};
      if (material) params.material = material;
      const response = await this.client.get(`/predictions/stats/model/${modelId}`, { params });
      return response.data;
    } catch (error: unknown) {
      if ((error as Record<string, unknown>).statusCode === 404) {
        return null;
      }
      throw error;
    }
  }

  async recordCompletion(jobId: string, request: Record<string, unknown>): Promise<void> {
    await this.client.post(`/predictions/jobs/${jobId}/completion-record`, request);
  }

  // ============ System Logs API methods ============
  /**
   * Get system logs with optional filtering by parameters
   */
  async getSystemLogs(params: Record<string, string>): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/systemlogs', { params });
    return response.data || [];
  }

  /**
   * Get system logs with advanced query string search
   */
  async getSystemLogsQuery(query: string): Promise<Record<string, unknown>[]> {
    const response = await this.client.get(`/systemlogs?query=${encodeURIComponent(query)}`);
    return response.data || [];
  }

  /**
   * Export system logs as JSON blob with optional filtering
   */
  async exportSystemLogs(params: Record<string, string>): Promise<Blob> {
    const response = await this.client.get('/systemlogs/export', {
      params,
      responseType: 'blob'
    });
    return response.data;
  }

  // ============ File Upload API methods ============
  /**
   * Upload a 3D model file with progress tracking
   */
  async uploadModel(
    file: File,
    onProgress?: (progressEvent: { loaded: number; total?: number }) => void
  ): Promise<Record<string, unknown>> {
    const form = new FormData();
    form.append('file', file, file.name);
    
    const response = await this.client.post('/models', form, {
      headers: {
        'Content-Type': 'multipart/form-data'
      },
      onUploadProgress: onProgress
    });
    return response.data;
  }

  /**
   * Get list of all 3D models
   */
  async getModels3D(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/models');
    return response.data || [];
  }

  // ============ User Management API methods ============
  /**
   * Get all users
   */
  async getUsers(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/users');
    return response.data || [];
  }

  /**
   * Get all roles
   */
  async getRoles(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/users/roles');
    return response.data || [];
  }

  /**
   * Check username and email availability
   */
  async checkUserAvailability(
    username?: string,
    email?: string
  ): Promise<{ usernameExists?: boolean; emailExists?: boolean }> {
    const params = new URLSearchParams();
    if (username) params.append('username', username);
    if (email) params.append('email', email);
    const response = await this.client.get(`/users/availability?${params.toString()}`);
    return response.data || {};
  }

  /**
   * Create a new user
   */
  async createUser(userData: {
    username: string;
    email: string;
    password: string;
    firstName?: string;
    lastName?: string;
    roleIds?: string[];
    accessibleAreas?: string[];
  }): Promise<Record<string, unknown>> {
    const response = await this.client.post('/users', userData);
    return response.data;
  }

  /**
   * Update a user
   */
  async updateUser(userId: string, updates: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(`/users/${userId}`, updates);
    return response.data;
  }

  // ============ Setup & Initialization API methods ============

  /**
   * Get setup status
   */
  async getSetupStatus(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/setup/status');
    return response.data;
  }

  /**
   * Create initial admin account
   */
  async createInitialAdmin(adminData: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.post('/setup/initial-admin', adminData);
    return response.data;
  }

  // ============ Spoolman Integration API methods ============

  /**
   * Test Spoolman connection
   */
  async testSpoolmanConnection(): Promise<Record<string, unknown>> {
    const response = await this.client.post('/spoolman/test');
    return response.data;
  }

  /**
   * Get Spoolman health status
   */
  async getSpoolmanHealth(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/spoolman/health');
    return response.data;
  }

  /**
   * Get Spoolman configuration
   */
  async getSpoolmanConfig(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/spoolman/config');
    return response.data;
  }

  /**
   * Save Spoolman configuration
   */
  async saveSpoolmanConfig(config: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.post('/spoolman/config', config);
    return response.data;
  }

  /**
   * Get spools from Spoolman
   */
  async getSpools(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/spoolman/spools');
    return response.data;
  }

  // ============ Settings API methods ============

  /**
   * Get password policy settings
   */
  async getPasswordPolicy(): Promise<Response> {
    return this.client.get('/settings/security/password-policy');
  }

  // ============ Diagnostics API methods ============

  /**
   * Get diagnostics summary
   */
  async getDiagnosticsSummary(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/diagnostics/summary');
    return response.data;
  }

  // ============ Filament Types API methods ============

  /**
   * Get filament type presets
   */
  async getFilamentTypePresets(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/filament-types/presets');
    return response.data;
  }

  // ============ Printer Maintenance API methods ============

  /**
   * Get printer maintenance logs
   */
  async getPrinterMaintenance(printerId: string): Promise<Record<string, unknown>> {
    const response = await this.client.get(`/printers/${printerId}/maintenance`);
    return response.data;
  }

  /**
   * Update printer maintenance
   */
  async updatePrinterMaintenance(printerId: string, maintenance: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(`/printers/${printerId}/maintenance`, maintenance);
    return response.data;
  }

  // ============ Models 3D File Operations ============

  /**
   * Get 3D models list (for TagAdmin)
   */
  async get3DModelsList(): Promise<Record<string, unknown>> {
    const response = await this.client.get('/3d-models');
    return response.data;
  }

  // ============ G-code File Operations ============

  /**
   * Get gcode folder structure
   */
  async getGcodeFolderStructure(params?: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.get('/gcode-files/folder', { params });
    return response.data;
  }

  // ============ Catalog API methods ============

  /**
   * Get printer models from catalog
   */
  async getCatalogPrinterModels(manufacturerId?: string): Promise<Record<string, unknown>> {
    const response = await this.client.get('/catalog/printer-models', {
      params: manufacturerId ? { manufacturerId } : {}
    });
    return response.data;
  }

  /**
   * Get printer model details from catalog
   */
  async getCatalogPrinterModel(id: string): Promise<Record<string, unknown>> {
    const response = await this.client.get(`/catalog/printer-models/${id}`);
    return response.data;
  }

  // ============ Tags API methods ============

  /**
   * Get all tags
   */
  async getTags(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/tags');
    return response.data || [];
  }

  /**
   * Create a new tag
   */
  async createNewTag(tagData: { name: string; color?: string; description?: string }): Promise<Record<string, unknown>> {
    const response = await this.client.post('/tags', tagData);
    return response.data;
  }

  /**
   * Delete a tag by ID
   */
  async deleteTagById(id: string): Promise<void> {
    await this.client.delete(`/tags/${id}`);
  }

  /**
   * Bulk assign tags to models
   */
  async bulkAssignTags(modelIds: string[], tagIds: string[]): Promise<Record<string, unknown>> {
    const response = await this.client.post('/tags/bulk-assign', {
      modelIds,
      tagIds
    });
    return response.data;
  }

  // ============ Printers Import/Export API methods ============

  /**
   * Import printers from file
   */
  async importPrinters(printers: Record<string, unknown>[]): Promise<Record<string, unknown>> {
    const response = await this.client.post('/printers/import', printers);
    return response.data;
  }

  /**
   * Set printer maintenance
   */
  async setPrinterMaintenance(printerId: string, maintenanceData: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(`/printers/${printerId}/maintenance`, maintenanceData);
    return response.data;
  }

  // ============ 3D Models API methods ============

  /**
   * Get all 3D models
   */
  async get3DModels(): Promise<Record<string, unknown>[]> {
    const response = await this.client.get('/3d-models');
    return response.data || [];
  }

  /**
   * Update 3D model
   */
  async update3DModel(id: string, modelData: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(`/3d-models/${id}`, modelData);
    return response.data;
  }

  /**
   * Delete 3D model
   */
  async delete3DModel(id: string): Promise<void> {
    await this.client.delete(`/3d-models/${id}`);
  }

  // ============ Slicer API methods ============

  /**
   * Get slicer job status
   */
  async getSlicerJobStatus(jobId: string): Promise<Record<string, unknown>> {
    const response = await this.client.get(`/slicer/jobs/${encodeURIComponent(jobId)}/status`);
    return response.data;
  }

  // ============ Generic API methods ============

  /**
   * Generic POST request for any endpoint (useful for various commands)
   */
  async genericPost(path: string, data?: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.post(path, data);
    return response.data;
  }

  /**
   * Generic GET request for any endpoint
   */
  async genericGet(path: string, params?: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.get(path, { params });
    return response.data;
  }

  /**
   * Generic PUT request for any endpoint
   */
  async genericPut(path: string, data?: Record<string, unknown>): Promise<Record<string, unknown>> {
    const response = await this.client.put(path, data);
    return response.data;
  }

  /**
   * Generic DELETE request for any endpoint
   */
  async genericDelete(path: string): Promise<void> {
    await this.client.delete(path);
  }

  // ============ Missing Methods (Placeholder Implementations) ============

  /**
   * Update password policy settings
   */
  async updatePasswordPolicy(policy: Record<string, unknown>): Promise<Response> {
    return this.client.put('/settings/security/password-policy', policy);
  }

  /**
   * Get 3D model details by ID
   */
  async getModel3DDetails(modelId?: string): Promise<Record<string, unknown>> {
    if (!modelId) return {};
    const response = await this.client.get(`/3d-models/${modelId}`);
    return response.data as Record<string, unknown>;
  }

  /**
   * Update 3D model
   */
  async updateModel3D(modelId?: string, updates?: Record<string, unknown>): Promise<Record<string, unknown>> {
    if (!modelId) return {};
    const response = await this.client.put(`/3d-models/${modelId}`, updates);
    return response.data as Record<string, unknown>;
  }

  /**
   * Assign tag to 3D model
   */
  async assignTagToModel(modelId?: string, tagId?: string): Promise<void> {
    if (!modelId || !tagId) return;
    await this.client.post(`/3d-models/${modelId}/tags/${tagId}`, {});
  }

  /**
   * Remove tag from 3D model
   */
  async removeTagFromModel(modelId?: string, tagId?: string): Promise<void> {
    if (!modelId || !tagId) return;
    await this.client.delete(`/3d-models/${modelId}/tags/${tagId}`);
  }

  // ============ Job Queue Analytics methods (advanced dashboard) ============
  // NOTE: These are read-only analytics methods for the Print Queue Dashboard.
  // For basic queue management (queue, cancel, delete jobs), use Job Queue methods above.
  // Endpoint: /api/job-queue-analytics (renamed from /api/printQueue for clarity)

  /**
   * Get all queued and printing jobs with optional filtering (analytics view)
   */
  async getAnalyticsQueueJobs(
    filterStatus?: string,
    filterModel?: string,
    filterMaterial?: string,
    limit: number = 50,
    offset: number = 0
  ): Promise<unknown[]> {
    const params = new URLSearchParams();
    if (filterStatus) params.append("filterStatus", filterStatus);
    if (filterModel) params.append("filterModel", filterModel);
    if (filterMaterial) params.append("filterMaterial", filterMaterial);
    params.append("limit", limit.toString());
    params.append("offset", offset.toString());

    const response = await this.client.get(`/job-queue-analytics?${params.toString()}`);
    return response.data;
  }

  /**
   * Get print jobs for a specific printer (analytics view)
   */
  async getAnalyticsPrinterQueue(
    printerId: string,
    limit: number = 50
  ): Promise<unknown[]> {
    const response = await this.client.get(`/job-queue-analytics/printer/${printerId}`, {
      params: { limit },
    });
    return response.data;
  }

  /**
   * Get queue statistics (analytics)
   */
  async getAnalyticsQueueStats(): Promise<unknown> {
    const response = await this.client.get(`/job-queue-analytics/stats`);
    return response.data;
  }

  /**
   * Get queue statistics by model (analytics)
   */
  async getAnalyticsQueueModelStats(): Promise<unknown[]> {
    const response = await this.client.get(`/job-queue-analytics/stats/models`);
    return response.data;
  }

  /**
   * Get queue history with pagination (analytics)
   */
  async getAnalyticsQueueHistory(
    limit: number = 50,
    offset: number = 0,
    sortBy: string = "completedAtUtc"
  ): Promise<unknown> {
    const response = await this.client.get(`/job-queue-analytics/history`, {
      params: { limit, offset, sortBy },
    });
    return response.data;
  }

  /**
   * Enqueue a new print job
   */
  async enqueueJob(request: unknown): Promise<unknown> {
    const response = await this.client.post(`/job-queue`, request);
    return response.data;
  }

  /**
   * Update a print job
   */
  async updateJob(jobId: string, request: unknown): Promise<unknown> {
    const response = await this.client.put(
      `/job-queue/${jobId}`,
      request
    );
    return response.data;
  }

  /**
   * Update job priority
   */
  async updateJobPriority(jobId: string, newPriority: number): Promise<unknown> {
    const response = await this.client.put(
      `/job-queue/${jobId}/priority`,
      { newPriority }
    );
    return response.data;
  }

  /**
   * Pause a print job
   */
  async pauseJob(jobId: string): Promise<unknown> {
    const response = await this.client.post(
      `/job-queue/${jobId}/pause`
    );
    return response.data;
  }

  /**
   * Resume a print job
   */
  async resumeJob(jobId: string): Promise<unknown> {
    const response = await this.client.post(
      `/job-queue/${jobId}/resume`
    );
    return response.data;
  }

  /**
   * Cancel a print job
   */
  async cancelPrintQueueJob(jobId: string): Promise<void> {
    await this.client.delete(`/job-queue/${jobId}`);
  }

  /**
   * Rerun a completed job (add it back to queue)
   */
  async rerunJob(jobId: string): Promise<unknown> {
    const response = await this.client.post(
      `/job-queue/${jobId}/rerun`
    );
    return response.data;
  }

  /**
   * Bulk cancel multiple print jobs
   */
  async bulkCancelJobs(request: unknown): Promise<unknown> {
    const response = await this.client.post(`/job-queue/bulk/cancel`, request);
    return response.data;
  }

  /**
   * Seed history from printer APIs
   */
  async seedHistory(printerIds?: string[], daysBack: number = 30): Promise<void> {
    await this.client.post(`/job-queue/history/seed`, {
      printerIds,
      daysBack,
    });
  }

  /**
   * Get detailed information about a specific job (analytics)
   */
  async getAnalyticsJobDetails(jobId: string): Promise<unknown> {
    const response = await this.client.get(`/job-queue-analytics/jobs/${jobId}`);
    return response.data;
  }

  /**
   * Update job details (name, priority, notes, tags, material, nozzle)
   */
  async updateJobDetails(jobId: string, updates: unknown): Promise<unknown> {
    const response = await this.client.put(
      `/job-queue/${jobId}`,
      updates
    );
    return response.data;
  }

  /**
   * Update job notes only
   */
  async updateJobNotes(jobId: string, notes: string): Promise<void> {
    await this.client.put(`/job-queue/${jobId}/notes`, {
      notes: notes || null,
    });
  }

  /**
   * Get timeline events with optional filtering (analytics)
   */
  async getAnalyticsTimeline(
    dateFrom?: Date,
    dateTo?: Date,
    printerId?: string,
    filterStatus?: string,
    limit: number = 100
  ): Promise<unknown[]> {
    const params = new URLSearchParams();
    if (dateFrom) params.append("dateFrom", dateFrom.toISOString());
    if (dateTo) params.append("dateTo", dateTo.toISOString());
    if (printerId) params.append("printerId", printerId);
    if (filterStatus) params.append("filterStatus", filterStatus);
    params.append("limit", limit.toString());

    const response = await this.client.get(`/job-queue-analytics/timeline?${params.toString()}`);
    return response.data;
  }

  /**
   * Get state history for a specific job (analytics)
   */
  async getAnalyticsJobStateHistory(jobId: string): Promise<unknown> {
    const response = await this.client.get(
      `/job-queue-analytics/jobs/${jobId}/state-history`
    );
    return response.data;
  }

  /**
   * Get duration analytics with optional filtering (analytics)
   */
  async getAnalyticsDurationAnalytics(
    printerId?: string,
    dateFrom?: Date,
    dateTo?: Date
  ): Promise<unknown> {
    const params = new URLSearchParams();
    if (printerId) params.append("printerId", printerId);
    if (dateFrom) params.append("dateFrom", dateFrom.toISOString());
    if (dateTo) params.append("dateTo", dateTo.toISOString());

    const response = await this.client.get(
      `/job-queue-analytics/duration-analytics?${params.toString()}`
    );
    return response.data;
  }
}

// Export singleton instance
export const apiClient = new ApiClient();
