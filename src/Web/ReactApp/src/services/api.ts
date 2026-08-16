/* eslint-disable local/pf-no-unguarded-console */
// Get hash for a G-code file (returns string)
import { getApiBaseUrl } from "@/common/utils/apiUrlHelpers";
import {
  ApiError,
  PrintJobStatusDto,
  AuthenticationResult,
  BedType,
  CatalogContext,
  CommandResult,
  CreateBedTypeRequest,
  CreateExtruderModelDto,
  CreateFilamentTypeRequest,
  CreateHotendModelDto,
  CreateNozzleModelDto,
  CreatePrinterDto,
  CreatePrinterGroupRequest,
  CreateToolheadModelDto,
  DiscoveredPrinterDto,
  ExtruderModelDefinition,
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
  HotendModelDefinition,
  JobStateHistoryDto,
  JobQueuePrintJob,
  LoginRequest,
  ManufacturerDto,
  ManufacturersByContext,
  MoveRequest,
  NozzleModelDefinition,
  Printer,
  PrinterCameraUrlResult,
  PrinterCameraUrls,
  PrinterCapabilitiesDto,
  PrinterBackendCapabilitiesDto,
  PrinterDetails,
  PrinterFast,
  PrinterSummary,
  PrintJobPriority,
  PrintJobObjectListDto,
  PrinterFileDto,
  PrinterGroup,
  PrinterGroupAccessRule,
  PrinterGroupDetail,
  PrinterModelDto,
  PrinterVersionInfo,
  PrinterQueueSummaryDto,
  QueuedPrintJobWithFileMetaDto,
  QueuedPrintJobDto,
  DispatchClientResult,
  BedClearAcknowledgementResult,
  QueueChangeFeed,
  QueueSubscriptionResources,
  QueueHistoryPageDto,
  QueueOverviewDto,
  QueueStatsDto,
  RegisterRequest,
  SystemInfo,
  ResolveHostnameRequest,
  RoleDto,
  RoleSummary,
  RoleDetail,
  CreateCustomRoleRequest,
  UpdateCustomRoleRequest,
  PermissionCatalog,
  RolePermissions,
  UpdateRolePermissionsRequest,
  UpdateRolePermissionsResponse,
  SetAccessRulesRequest,
  StartDiscoveryRequest,
  ResolveHostnameResponse,
  ProfileSchemasResponse,
  ProfileTypeSchema,
  RegisterDiscoveredPrinterRequest,
  SlicerModelAliasDto,
  SpoolmanDiscoveryResult,
  SpoolmanFilamentImportResult,
  TempTargets,
  TestConnectionRequest,
  TestConnectionResponse,
  ToolheadModelDefinition,
  UpdateExtruderModelDto,
  UpdateFilamentTypeRequest,
  UpdateHotendModelDto,
  UpdateModelAliasesRequest,
  UpdateModelRequest,
  UpdateNozzleModelDto,
  UpdatePrinterDto,
  UpdatePrinterGroupRequest,
  UpdateToolheadModelDefDto,
  UserDto,
  DiscoveredGcodeFileDto,
  SetModelDispatchDefaultsRequest,
  ApplyModelDefaultsResult,
  GcodeHarvestResultDto,
  BulkImportResponse,
  SpoolmanSpool,
  SpoolFilterOptions,
  FilamentFilterOptions,
  SpoolmanFilament,
  SpoolmanVendor,
  SpoolmanMaterial,
  SpoolmanBulkUpdateFilamentsRequest,
  SpoolmanBulkUpdateResult,
  SpoolmanUpdateFilamentRequest,
  SpoolmanUpdateSpoolRequest,
  SpoolmanBulkUpdateSpoolsRequest,
  FilamentCsvImportResult,
  SpoolmanDbFilamentEntry,
  SpoolmanDbMaterialEntry,
  SpoolmanDbImportRequest,
  SpoolmanDbImportResult,
  OfdBrand,
  OfdBrandDetail,
  OfdFlattenedEntry,
  OfdImportRequest,
  OfdImportResult,
  ConnectionDiagnosticsResponse,
  PagedResponse,
  SystemCapabilities,
  DispatchHistoryPageDto,
  FailureDetectionEvent,
  NotificationDto,
  NotificationCapabilitiesResponse,
  NotificationPreferencesDto,
  TelegramSettingsDto,
  TelegramTestResult,
  UpdateBedTypeRequest,
  UpdateTelegramSettingsRequest,
  UpdateNotificationPreferencesRequest,
  UnreadCountResponse,
  ScheduledJob,
  JobExecution,
  ScheduleJobRequest,
  RescheduleJobRequest,
  AutoDispatchGlobalStatus,
  AutoDispatchDetailedStatus,
  AutoDispatchReadyResult,
  AutoDispatchStatus,
  ObicoServer,
  CreateObicoServerRequest,
  UpdateObicoServerRequest,
  ObicoServerHealthResponse,
  TimelineEventDto,
  TimezoneInfo,
  MaterialClusterDto,
  CreateMaterialClusterRequest,
  UpdateMaterialClusterRequest,
  QuotaDto,
  CreateQuotaRequest,
  UpdateQuotaRequest,
  CheckQuotaRequest,
  QuotaCheckResult,
  UserBalanceDto,
  BalanceTransactionDto,
  BalanceAdjustRequest,
  ZOffsetSaveRequest,
  CustomFieldDefinition,
  CustomFieldEntityType,
  CustomFieldValue,
  CreateCustomFieldDefinitionRequest,
  UpdateCustomFieldDefinitionRequest,
} from "@/types/api";

type HistoryJobWire = Omit<
  HistoryJob,
  'jobId' | 'endTime' | 'filamentUsed' | 'printDuration' | 'startTime' | 'totalDuration' | 'auxiliaryData' | 'thumbnailUrl'
> & {
  jobId?: string;
  job_id?: string;
  endTime?: number;
  end_time?: number;
  filamentUsed?: number;
  filament_used?: number;
  printDuration?: number;
  print_duration?: number;
  startTime?: number;
  start_time?: number;
  totalDuration?: number;
  total_duration?: number;
  auxiliaryData?: HistoryJob['auxiliaryData'];
  auxiliary_data?: HistoryJob['auxiliaryData'];
  thumbnailUrl?: string;
  thumbnail_url?: string;
};

type HistoryTotalsWire = Partial<HistoryTotals> & {
  job_totals?: {
    total_jobs?: number;
    total_print_time?: number;
    total_filament_used?: number;
    longest_job?: number;
    longest_print?: number;
  };
  auxiliary_totals?: HistoryTotals['auxiliaryTotals'];
};

function normalizeHistoryJob(job: HistoryJobWire): HistoryJob {
  return {
    exists: job.exists,
    filename: job.filename,
    metadata: job.metadata,
    status: job.status,
    user: job.user,
    jobId: job.jobId ?? job.job_id ?? '',
    endTime: job.endTime ?? job.end_time,
    filamentUsed: job.filamentUsed ?? job.filament_used ?? 0,
    printDuration: job.printDuration ?? job.print_duration ?? 0,
    startTime: job.startTime ?? job.start_time ?? 0,
    totalDuration: job.totalDuration ?? job.total_duration ?? 0,
    auxiliaryData: job.auxiliaryData ?? job.auxiliary_data,
    thumbnailUrl: job.thumbnailUrl ?? job.thumbnail_url,
  };
}

function normalizeHistoryTotals(totals: HistoryTotalsWire): HistoryTotals {
  if (totals.jobTotals) {
    return totals as HistoryTotals;
  }

  const jobTotals = totals.job_totals;
  return {
    jobTotals: {
      totalJobs: jobTotals?.total_jobs ?? 0,
      totalPrintTime: jobTotals?.total_print_time ?? 0,
      totalFilament: jobTotals?.total_filament_used ?? 0,
      longestJob: jobTotals?.longest_job ?? 0,
      longestPrint: jobTotals?.longest_print ?? 0,
    },
    auxiliaryTotals: totals.auxiliaryTotals ?? totals.auxiliary_totals,
  };
}
import type {
  PrintablesDownloadHistoryItem,
  GeometryUploadResultDto,
  PrintablesCollectionSummary,
  PrintablesModelSummary,
  PrintablesOAuthStatus,
  PrintablesPagedResponse,
  ThreeMfMetadata
} from "@/types/models";
import type { AxiosError, AxiosInstance, AxiosRequestConfig, AxiosResponse } from "axios";
import axios from "axios";
import { resetAuthenticatedSignalRSession } from "@/common/auth/authenticatedSignalRSession";
import { notifyAuthenticationExpired } from "@/common/auth/authenticationExpiration";
import type {
  ModelCollection,
  ModelCollectionMembership,
  CreateModelCollectionRequest,
  UpdateModelCollectionRequest,
} from "@/types/models";
import type { TagOption, UpdateTagRequest } from "@/types/admin";

/**
 * Extended Axios request config with PrintFarmer-specific interceptor bypass flags.
 * Pass a `PfRequestConfig` to `apiClient.request()` when you need to suppress the
 * default 401 redirect behaviour for endpoints that signal soft failures via 401
 * (e.g. passkey assertion completion).
 */
export interface PfRequestConfig extends AxiosRequestConfig {
  /**
   * When `true`, a 401 response will not trigger the global token-clear and
   * redirect-to-/login behaviour in the response interceptor.  Use this for
   * endpoints that legitimately return 401 to indicate a failed operation
   * rather than an expired session.
   */
  skipAuthRedirect?: boolean;
}

interface PfInternalRequestConfig extends PfRequestConfig {
  authTokenAtRequest?: string | null;
}

const AUTO_DISPATCH_API_BASE = "/auto-dispatch";

interface PrintablesModelSummaryApiDto {
  id: string;
  name?: string;
  title?: string;
  slug?: string | null;
  authorHandle?: string | null;
  authorName?: string | null;
  author?: string | null;
  thumbnailUrl?: string | null;
  likesCount?: number;
  likeCount?: number;
  downloadCount?: number;
  downloadsCount?: number;
  fileCount?: number;
  sourceUrl?: string;
}

interface PrintablesCursorApiResponse<T> {
  items?: T[];
  nextCursor?: string | null;
  hasMore?: boolean;
}

interface PrintablesSearchApiResponse<T> {
  items?: T[];
  hasMore?: boolean;
  offset?: number;
  limit?: number;
}

type SerializedArray<T> = T[] | string | null | undefined;

interface GcodeFileApiResponse extends Omit<GcodeFile,
  "filamentPerExtruderWeightG" |
  "filamentPerExtruderLengthMm" |
  "filamentPerExtruderColorHex" |
  "filamentPerExtruderType"> {
  filamentPerExtruderWeightG?: SerializedArray<number>;
  filamentPerExtruderLengthMm?: SerializedArray<number>;
  filamentPerExtruderColorHex?: SerializedArray<string>;
  filamentPerExtruderType?: SerializedArray<string>;
}

interface GetGcodeFilesApiResponse extends Omit<GetGcodeFilesResponse, "files"> {
  files: GcodeFileApiResponse[];
}

/** Tag shape returned by tag-read endpoints; mirrors `TagDto` in `tagService.ts`. */
interface TagRecord {
  id: string;
  name: string;
  color?: string;
  description?: string;
}

/**
 * One entry from the batched fleet tag-read endpoint
 * (`GET /api/tags/objects`, #1146 item 1). `tags` is always an array
 * (possibly empty), never omitted, so "no tags" is distinguishable from
 * "object filtered out / inaccessible".
 */
export interface ObjectTagsDto {
  objectId: string;
  tags: TagRecord[];
}

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

  private static normalizePrintablesUsername(username: string): string {
    return username.trim().replace(/^@+/, "");
  }

  private static mapPrintablesModelSummary(model: PrintablesModelSummaryApiDto): PrintablesModelSummary {
    return {
      id: model.id,
      title: model.title ?? model.name ?? "",
      slug: model.slug ?? null,
      author: model.author ?? model.authorHandle ?? model.authorName ?? "Unknown",
      thumbnailUrl: model.thumbnailUrl ?? null,
      likesCount: model.likesCount ?? model.likeCount,
      downloadsCount: model.downloadsCount ?? model.downloadCount,
      fileCount: model.fileCount,
      sourceUrl: model.sourceUrl,
    };
  }

  private static parseSerializedNumberArray(value: SerializedArray<number>): number[] | undefined {
    if (Array.isArray(value)) {
      return value;
    }

    if (typeof value !== "string" || value.trim().length === 0) {
      return undefined;
    }

    try {
      const parsed: unknown = JSON.parse(value);
      return Array.isArray(parsed) && parsed.every((item) => typeof item === "number") ? parsed : undefined;
    } catch {
      return undefined;
    }
  }

  private static parseSerializedStringArray(value: SerializedArray<string>): string[] | undefined {
    if (Array.isArray(value)) {
      return value;
    }

    if (typeof value !== "string" || value.trim().length === 0) {
      return undefined;
    }

    try {
      const parsed: unknown = JSON.parse(value);
      return Array.isArray(parsed) && parsed.every((item) => typeof item === "string") ? parsed : undefined;
    } catch {
      return undefined;
    }
  }

  private static mapGcodeFile(file: GcodeFileApiResponse): GcodeFile {
    return {
      ...file,
      filamentPerExtruderWeightG: ApiClient.parseSerializedNumberArray(file.filamentPerExtruderWeightG),
      filamentPerExtruderLengthMm: ApiClient.parseSerializedNumberArray(file.filamentPerExtruderLengthMm),
      filamentPerExtruderColorHex: ApiClient.parseSerializedStringArray(file.filamentPerExtruderColorHex),
      filamentPerExtruderType: ApiClient.parseSerializedStringArray(file.filamentPerExtruderType),
    };
  }

  private static mapGcodeFilesResponse(response: GetGcodeFilesApiResponse): GetGcodeFilesResponse {
    return {
      ...response,
      files: response.files.map(ApiClient.mapGcodeFile),
    };
  }

  private static normalizePrintablesCursorPage(
    page: PrintablesCursorApiResponse<PrintablesModelSummaryApiDto> | PrintablesPagedResponse<PrintablesModelSummary> | null | undefined,
  ): PrintablesPagedResponse<PrintablesModelSummary> {
    if (!page) {
      return { items: [], nextCursor: null, hasMore: false };
    }

    const items = (page.items ?? []).map((item) => ApiClient.mapPrintablesModelSummary(item as PrintablesModelSummaryApiDto));
    return {
      items,
      nextCursor: typeof page.nextCursor === "string" ? page.nextCursor : null,
      hasMore: Boolean(page.hasMore),
    };
  }

  private static normalizePrintablesHistoryPage(
    page:
      | PrintablesCursorApiResponse<PrintablesModelSummaryApiDto & { downloadedAt?: string | null }>
      | PrintablesPagedResponse<PrintablesDownloadHistoryItem>
      | null
      | undefined,
  ): PrintablesPagedResponse<PrintablesDownloadHistoryItem> {
    if (!page) {
      return { items: [], nextCursor: null, hasMore: false };
    }

    const items = (page.items ?? []).map((item) => {
      const mappedModel = ApiClient.mapPrintablesModelSummary(item as PrintablesModelSummaryApiDto);
      return {
        ...mappedModel,
        downloadedAt: (item as { downloadedAt?: string | null }).downloadedAt ?? null,
      };
    });

    return {
      items,
      nextCursor: typeof page.nextCursor === "string" ? page.nextCursor : null,
      hasMore: Boolean(page.hasMore),
    };
  }

  private static normalizePrintablesSearchPage(
    page: PrintablesSearchApiResponse<PrintablesModelSummaryApiDto> | PrintablesPagedResponse<PrintablesModelSummary> | null | undefined,
  ): PrintablesPagedResponse<PrintablesModelSummary> {
    if (!page) {
      return { items: [], hasMore: false, offset: 0, limit: 0 };
    }

    const items = (page.items ?? []).map((item) => ApiClient.mapPrintablesModelSummary(item as PrintablesModelSummaryApiDto));
    return {
      items,
      hasMore: Boolean(page.hasMore),
      offset: typeof page.offset === "number" ? page.offset : 0,
      limit: typeof page.limit === "number" ? page.limit : items.length,
    };
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
   * Get all settings group metadata for sidebar organization
   */
  async getSettingsGroups(): Promise<Array<{ key: string; displayName: string; description?: string; icon?: string; order: number }>> {
    const res = await this.client.get("/settings/groups");
    return res.data;
  }

  /**
   * Get all unified settings
   */
  async getAllSettings(): Promise<Record<string, unknown>> {
    const res = await this.client.get("/settings");
    return res.data;
  }

  // ============ Cost Tracking Settings ============

  /** Get cost tracking settings. */
  async getCostTrackingSettings(): Promise<import("@/types/api").CostTrackingSettings> {
    return this.getSettings<import("@/types/api").CostTrackingSettings>("CostTracking");
  }

  /** Update cost tracking settings. */
  async updateCostTrackingSettings(settings: import("@/types/api").CostTrackingSettings): Promise<void> {
    return this.saveSettings("CostTracking", settings);
  }

  // ========== Background Services API ==========

  /**
   * Get status of all background services
   */
  async getBackgroundServices(): Promise<import("@/types/api").BackgroundServiceStatus[]> {
    const res = await this.client.get("/services");
    return res.data;
  }

  /**
   * Get summary of background services status
   */
  async getBackgroundServicesSummary(): Promise<import("@/types/api").BackgroundServicesSummary> {
    const res = await this.client.get("/services/summary");
    return res.data;
  }

  /**
   * Get status of a specific background service
   */
  async getBackgroundServiceStatus(serviceId: string): Promise<import("@/types/api").BackgroundServiceStatus> {
    const res = await this.client.get(`/services/${serviceId}`);
    return res.data;
  }

  private client: AxiosInstance;

  constructor() {
    // Use shared utility to properly construct API base URL
    const apiBaseUrl = getApiBaseUrl();

    this.client = axios.create({
      baseURL: apiBaseUrl,
      timeout: 30000,
      paramsSerializer: {
        // ASP.NET Core expects repeated keys for arrays: tagIds=a&tagIds=b
        // Axios v1+ defaults to bracket notation (tagIds[]=a) which .NET ignores
        indexes: null,
      },
    });

    // Request interceptor for authentication and correlationId
    this.client.interceptors.request.use((config) => {
      const token = localStorage.getItem("auth-token");
      (config as PfInternalRequestConfig).authTokenAtRequest = token;
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
      async (error: AxiosError) => {
        const requestConfig = error.config as PfInternalRequestConfig | undefined;
        // Handle 401 Unauthorized — clear token and redirect to login unless
        // the caller set skipAuthRedirect:true on the request config to handle
        // the 401 inline (e.g. passkey assertion, which the backend signals
        // with 401 for failed credentials rather than as a session expiry).
        if (
          error.response?.status === 401 &&
          !requestConfig?.skipAuthRedirect &&
          requestConfig?.authTokenAtRequest === localStorage.getItem("auth-token")
        ) {
          let invalidatedCurrentSession = false;
          try {
            await resetAuthenticatedSignalRSession();
          } catch (resetError) {
            console.error(
              "Failed to reset authenticated SignalR session after a 401 response.",
              resetError,
            );
          }
          if (requestConfig.authTokenAtRequest === localStorage.getItem("auth-token")) {
            localStorage.removeItem("auth-token");
            notifyAuthenticationExpired();
            invalidatedCurrentSession = true;
          }
          // Only redirect if not already on auth pages
          if (
            invalidatedCurrentSession &&
            window.location.pathname !== "/login" &&
            window.location.pathname !== "/register"
          ) {
            window.location.href = "/login";
          }
        }

        const responseData = error.response?.data;

        // Legacy string-shape `details`: keep this behavior for existing
        // callers. Only surface a string when the body itself is a string or
        // carries `{ error: string }`. Never stringify objects into `details`.
        const detailMessage = typeof responseData === 'string'
          ? responseData
          : (responseData as { error?: string })?.error ?? undefined;

        // Prefer a ProblemDetails-style top-level message from the body:
        // backend emits `{ message: "..." }` for some endpoints and
        // `{ detail: "..." }` for `application/problem+json`. Fall back to the
        // axios error message only when neither exists. Preserve the raw body
        // and the axios-error flag so feature callers (e.g. partsHarvest,
        // partsInventory) can recover canonical `code`/`mismatches`/`details`
        // extensions instead of collapsing every failure into an opaque error.
        const bodyRecord =
          responseData && typeof responseData === 'object'
            ? (responseData as { message?: unknown; detail?: unknown })
            : undefined;
        const bodyMessage =
          typeof bodyRecord?.message === 'string' && bodyRecord.message.length > 0
            ? bodyRecord.message
            : typeof bodyRecord?.detail === 'string' && bodyRecord.detail.length > 0
              ? bodyRecord.detail
              : undefined;

        const apiError: ApiError = {
          message: bodyMessage ?? (detailMessage || error.message),
          statusCode: error.response?.status || 500,
          details: detailMessage,
          data: responseData,
          isAxiosError: axios.isAxiosError(error),
        };
        return Promise.reject(apiError);
      }
    );
  }

  // ===== Generic HTTP methods for ad-hoc API calls =====
  /**
   * Perform a GET request
   */
  async get<T>(url: string, config?: AxiosRequestConfig): Promise<AxiosResponse<T>> {
    return this.client.get<T>(url, config);
  }

  /**
   * Perform a POST request
   */
  async post<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<AxiosResponse<T>> {
    return this.client.post<T>(url, data, config);
  }

  /**
   * Perform a PUT request
   */
  async put<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<AxiosResponse<T>> {
    return this.client.put<T>(url, data, config);
  }

  /**
   * Perform a PATCH request
   */
  async patch<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<AxiosResponse<T>> {
    return this.client.patch<T>(url, data, config);
  }

  /**
   * Perform a DELETE request
   */
  async delete<T>(url: string, config?: AxiosRequestConfig): Promise<AxiosResponse<T>> {
    return this.client.delete<T>(url, config);
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

  async getPrinterSummary(includeDisabled?: boolean): Promise<PrinterSummary[]> {
    const params = includeDisabled ? { includeDisabled: true } : undefined;
    const response = await this.client.get<PrinterSummary[]>("/printers/summary", { params });
    return response.data;
  }

  async getPrinterCameraUrls(): Promise<PrinterCameraUrls[]> {
    const response = await this.client.get<PrinterCameraUrls[]>(
      "/printers/camera-urls"
    );
    return response.data;
  }

  async getPrinterCameraUrl(id: string): Promise<PrinterCameraUrlResult> {
    const response = await this.client.get<PrinterCameraUrlResult>(
      `/printers/${id}/camera/url`
    );
    return response.data;
  }

  async getPrinterSnapshot(id: string): Promise<Blob> {
    const response = await this.client.get<Blob>(
      `/printers/${id}/snapshot`,
      {
        params: { _: Date.now() },
        responseType: "blob",
      }
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

  async getPrinterVersionInfo(printerId: string): Promise<PrinterVersionInfo> {
    const response = await this.client.get<PrinterVersionInfo>(`/printers/${printerId}/version`);
    return response.data;
  }

  async getPrintJobObjects(printerId: string): Promise<PrintJobObjectListDto> {
    const response = await this.client.get<PrintJobObjectListDto>(
      `/printers/${printerId}/printjob/objects`
    );
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

  /**
   * Test connectivity to a printer backend before adding the printer.
   * Returns success/failure with a human-readable message.
   */
  async testConnection(request: TestConnectionRequest): Promise<TestConnectionResponse> {
    const response = await this.client.post<TestConnectionResponse>("/printers/test-connection", request);
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

  async updatePrinter(
    id: string,
    printer: UpdatePrinterDto,
    reviewedRowVersion: string
  ): Promise<Printer> {
    const response = await this.client.put<Printer>(
      `/printers/${id}`,
      printer,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    return response.data;
  }

  async refreshCameraUrls(id: string): Promise<Printer> {
    const response = await this.client.post<Printer>(`/printers/${id}/refresh-cameras`);
    return response.data;
  }

  /**
   * Fetches the read-only calibration-eligibility context for a printer
   * (issue #1616): eligibility, missing inputs, and every profile-owned and
   * residual manual field currently persisted.
   */
  async getCalibrationContext(
    id: string,
    slicerType = "OrcaSlicer"
  ): Promise<import("@/types/api").CalibrationContextDto> {
    const response = await this.client.get<import("@/types/api").CalibrationContextDto>(
      `/printers/${id}/calibration-context`,
      { params: { slicerType } }
    );
    return response.data;
  }

  /**
   * Sets or edits the residual calibration-eligibility fields that remain
   * manual after profile-owned sourcing (issue #1616, PR-3): per-toolhead
   * metrology, the hardware sign-off timestamp, excludedRegions (explicit
   * `[]` is honored), activeToolheadIndex, capability flags, and the
   * confirm-only firmware-identity-verified flag. Additive and distinct
   * from `updatePrinter` — never touches firmware family/version/dialect.
   */
  async updateCalibrationSetup(
    id: string,
    request: import("@/types/api").CalibrationSetupRequestDto,
    reviewedRowVersion: string
  ): Promise<import("@/types/api").CalibrationSetupResultDto> {
    const response = await this.client.put<import("@/types/api").CalibrationSetupResultDto>(
      `/printers/${id}/calibration-setup`,
      request,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    return response.data;
  }

  /**
   * Re-probes the printer's firmware identity on demand and persists the detected
   * facts to the columns the calibration gate reads.
   *
   * This is deliberately separate from `GET /printers/{id}/version`, which reads
   * firmware live through an in-memory cache and never writes the database. That
   * split is why a printer can display a firmware version in the sidebar while
   * calibration still reports the firmware inputs as missing.
   *
   * Detection never marks the identity verified — that stays a human action.
   */
  async detectPrinterFirmware(
    id: string
  ): Promise<import("@/types/api").FirmwareDetectionResultDto> {
    const response = await this.client.post<import("@/types/api").FirmwareDetectionResultDto>(
      `/printers/${id}/firmware/detect`
    );
    return response.data;
  }

  async applyModelTemplate(id: string): Promise<void> {
    await this.client.post(`/printers/${id}/apply-template`);
  }

  async applyAllModelTemplates(): Promise<{ updated: number; total: number }> {
    const response = await this.client.post<{ updated: number; total: number }>('/printers/apply-templates');
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

  async registerDiscoveredPrinter(
    sessionId: string,
    request: RegisterDiscoveredPrinterRequest
  ): Promise<Printer> {
    const response = await this.client.post<Printer>(
      `/printers/discover/${encodeURIComponent(sessionId)}/register`,
      request
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

  async cancelPrint(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/cancel`
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

  async loadFilament(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/filament-load`
    );
    return response.data;
  }

  async unloadFilament(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/filament-unload`
    );
    return response.data;
  }

  async changeFilament(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/filament-change`
    );
    return response.data;
  }

  async extrudeFilament(
    printerId: string,
    distanceMm: number,
    feedrateMmPerMinute: number
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/extrude`,
      { distanceMm, feedrateMmPerMinute }
    );
    return response.data;
  }

  // ── MMU (Multi-Material Unit) commands ──

  /** Change to a specific MMU tool/gate (loads filament). */
  async mmuChangeTool(printerId: string, tool: number): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/change-tool/${tool}`
    );
    return response.data;
  }

  /** Eject/unload filament from the MMU. */
  async mmuEject(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/eject`
    );
    return response.data;
  }

  /** Load filament from the currently selected gate into the extruder. */
  async mmuLoad(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/load`
    );
    return response.data;
  }

  /** Home the MMU unit. */
  async mmuHome(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/home`
    );
    return response.data;
  }

  /** Pre-select an MMU tool without loading filament. */
  async mmuSelectTool(printerId: string, tool: number): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/select-tool/${tool}`
    );
    return response.data;
  }

  /** Recover the MMU from an error state. */
  async mmuRecover(printerId: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/recover`
    );
    return response.data;
  }

  async mmuGateAction(
    printerId: string,
    request: {
      protocol: 'Qidibox' | 'Afc';
      action: 'Load' | 'Unload' | 'Eject';
      gateIndex?: number;
      laneName?: string;
    }
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/mmu/gate-action`,
      request
    );
    return response.data;
  }

  async excludePrintJobObject(printerId: string, name: string): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/printjob/objects/exclude`,
      { name }
    );
    return response.data;
  }

  /**
   * Save calibrated Z-offset to the printer and optionally to firmware.
   * @param printerId The printer's GUID
   * @param request The Z-offset save payload
   */
  async saveZOffset(
    printerId: string,
    request: ZOffsetSaveRequest,
    reviewedRowVersion: string
  ): Promise<CommandResult> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/z-offset`,
      request,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    return response.data;
  }

  /**
   * Set the active spool on a printer via Spoolman.
   * @param printerId The printer's GUID
   * @param spoolId The Spoolman spool ID to activate
   */
  async setActiveSpool(
    printerId: string,
    spoolId: number,
    reviewedRowVersion: string
  ): Promise<string> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/active-spool`,
      { spoolId },
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    if (!response.data.success) {
      throw new Error(response.data.message ?? 'Failed to set active spool');
    }
    return this.responseEtag(response.headers, "The active spool mutation");
  }

  /**
   * Clear the active spool on a printer via Spoolman.
   * @param printerId The printer's GUID
   */
  async clearActiveSpool(
    printerId: string,
    reviewedRowVersion: string
  ): Promise<string> {
    const response = await this.client.post<CommandResult>(
      `/printers/${printerId}/active-spool`,
      { spoolId: null },
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    if (!response.data.success) {
      throw new Error(response.data.message ?? 'Failed to clear active spool');
    }
    return this.responseEtag(response.headers, "The active spool mutation");
  }

  /**
   * Get spools available on a printer's Spoolman instance (via Moonraker proxy).
   * Each printer may use a different Spoolman server.
   * @param printerId The printer's GUID
   */
  async getPrinterSpools(printerId: string): Promise<SpoolmanSpool[]> {
    try {
      const response = await this.client.get(`/printers/${printerId}/spoolman/spools`);
      const data = response.data;
      return Array.isArray(data) ? data : [];
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'response' in err) {
        const axiosErr = err as { response?: { status?: number } };
        if (axiosErr.response?.status === 404) {
          // Backend doesn't support per-printer Spoolman — fall back to central inventory
          const result = await this.getSpools({ limit: 500 });
          return result.items;
        }
      }
      throw err;
    }
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

    const response = await this.client.get<{ count: number; jobs: HistoryJobWire[] }>(
      `/printers/${printerId}/history`,
      { params }
    );
    return {
      count: response.data.count,
      jobs: response.data.jobs.map(normalizeHistoryJob),
    };
  }

  async getPrinterHistoryJob(
    printerId: string,
    jobId: string
  ): Promise<HistoryJob> {
    const response = await this.client.get<HistoryJobWire>(
      `/printers/${printerId}/history/${jobId}`
    );
    return normalizeHistoryJob(response.data);
  }

  async getPrinterHistoryThumbnail(
    printerId: string,
    jobId: string,
    signal?: AbortSignal
  ): Promise<Blob> {
    const response = await this.client.get<Blob>(
      `/printers/${printerId}/history/${encodeURIComponent(jobId)}/thumbnail`,
      {
        responseType: "blob",
        signal,
      }
    );
    return response.data;
  }

  async getPrinterHistoryTotals(printerId: string): Promise<HistoryTotals> {
    const response = await this.client.get<HistoryTotalsWire>(
      `/printers/${printerId}/history/totals`
    );
    return normalizeHistoryTotals(response.data);
  }

  // ============ Printer Files API methods ============

  async getPrinterFileList(printerId: string): Promise<PrinterFileDto[]> {
    const response = await this.client.get<PrinterFileDto[]>(
      `/printers/${printerId}/files`
    );
    return response.data;
  }

  /**
   * Fetches a printer file's thumbnail as an authenticated blob.
   *
   * `thumbnailUrl` is the same-origin proxy path returned by the backend on
   * `PrinterFileDto.thumbnailUrl` (e.g. `/api/printers/{id}/files/thumbnail?filename=...`).
   * The leading `/api` is stripped because `this.client` already has that prefix as its
   * baseURL - see `NewSliceJobPage.tsx`'s identical `serverModel.url.replace(/^\/api/, '')`
   * pattern. A bare `<img src>` cannot be used here because auth is JWT-bearer only (no
   * auth cookie), so the thumbnail must be fetched with the Authorization header and
   * rendered via an object URL. See issue #1650.
   */
  async getPrinterFileThumbnail(
    thumbnailUrl: string,
    signal?: AbortSignal
  ): Promise<Blob> {
    const path = thumbnailUrl.replace(/^\/api/, "");
    const response = await this.client.get<Blob>(path, {
      responseType: "blob",
      signal,
    });
    return response.data;
  }

  // ============ Printer Groups API methods ============

  async getPrinterGroups(): Promise<PrinterGroup[]> {
    const response = await this.client.get<PrinterGroup[]>(
      "/printer-groups"
    );
    return response.data;
  }

  async getPrinterGroup(id: string): Promise<PrinterGroupDetail> {
    const response = await this.client.get<PrinterGroupDetail>(
      `/printer-groups/${id}`
    );
    return response.data;
  }

  async createPrinterGroup(dto: CreatePrinterGroupRequest): Promise<PrinterGroup> {
    const response = await this.client.post<PrinterGroup>(
      "/printer-groups",
      dto
    );
    return response.data;
  }

  async updatePrinterGroup(
    id: string,
    dto: UpdatePrinterGroupRequest
  ): Promise<PrinterGroup> {
    const response = await this.client.put<PrinterGroup>(
      `/printer-groups/${id}`,
      dto
    );
    return response.data;
  }

  async deletePrinterGroup(id: string): Promise<void> {
    await this.client.delete(`/printer-groups/${id}`);
  }

  async assignPrinterToGroup(groupId: string, printerId: string): Promise<void> {
    await this.client.put(`/printer-groups/${groupId}/printers/${printerId}`);
  }

  async removePrinterFromGroup(groupId: string, printerId: string): Promise<void> {
    await this.client.delete(`/printer-groups/${groupId}/printers/${printerId}`);
  }

  async getPrinterGroupAccessRules(groupId: string): Promise<PrinterGroupAccessRule[]> {
    const response = await this.client.get<PrinterGroupAccessRule[]>(
      `/printer-groups/${groupId}/access`
    );
    return response.data;
  }

  async setPrinterGroupAccessRules(
    groupId: string,
    dto: SetAccessRulesRequest
  ): Promise<PrinterGroupAccessRule[]> {
    const response = await this.client.put<PrinterGroupAccessRule[]>(
      `/printer-groups/${groupId}/access`,
      dto
    );
    return response.data;
  }

  async getRoles(): Promise<RoleDto[]> {
    const response = await this.client.get<RoleDto[]>("/users/roles");
    return response.data;
  }

  // ============ Role Management API methods (#1455) ============
  // Distinct from getRoles()/`/users/roles` above (the thin access-control role list) —
  // these consume the richer admin role-management + permission-catalog APIs (#1446/#1448/#1449).

  async getAdminRoles(): Promise<RoleSummary[]> {
    const response = await this.client.get<RoleSummary[]>("/admin/roles");
    return response.data ?? [];
  }

  async getAdminRole(roleId: string): Promise<RoleDetail> {
    const response = await this.client.get<RoleDetail>(`/admin/roles/${roleId}`);
    return response.data;
  }

  async createAdminRole(dto: CreateCustomRoleRequest): Promise<RoleDetail> {
    const response = await this.client.post<RoleDetail>("/admin/roles", dto);
    return response.data;
  }

  async updateAdminRole(roleId: string, dto: UpdateCustomRoleRequest): Promise<RoleDetail> {
    const response = await this.client.put<RoleDetail>(`/admin/roles/${roleId}`, dto);
    return response.data;
  }

  /**
   * Deletes a custom role. If the role still has members, the server rejects with a
   * structured 409 `{ error, memberCount }` (surfaced via `ApiError.data`) unless
   * `reassignTo` or `cascade` is provided.
   */
  async deleteAdminRole(
    roleId: string,
    options?: { reassignTo?: string; cascade?: boolean },
  ): Promise<void> {
    await this.client.delete(`/admin/roles/${roleId}`, {
      params: {
        reassignTo: options?.reassignTo,
        cascade: options?.cascade,
      },
    });
  }

  /** Full enforced-permission catalog, used to render the permission matrix. */
  async getPermissionCatalog(): Promise<PermissionCatalog> {
    const response = await this.client.get<PermissionCatalog>("/admin/permissions/catalog");
    return response.data;
  }

  /** A role's current permission grants joined against the derived catalog. */
  async getRolePermissions(roleId: string): Promise<RolePermissions> {
    const response = await this.client.get<RolePermissions>(`/admin/roles/${roleId}/permissions`);
    return response.data;
  }

  /**
   * Full-replacement update of a role's permission grants. `dto.updatedAt` must equal the
   * role's last-observed `updatedAt`, or the server rejects with a structured 409
   * `{ error }` concurrency conflict (surfaced via `ApiError.message`).
   */
  async updateRolePermissions(
    roleId: string,
    dto: UpdateRolePermissionsRequest,
  ): Promise<UpdateRolePermissionsResponse> {
    const response = await this.client.put<UpdateRolePermissionsResponse>(
      `/admin/roles/${roleId}/permissions`,
      dto,
    );
    return response.data;
  }

  // ============ Bed Type API methods ============

  async getBedTypes(): Promise<BedType[]> {
    const response = await this.client.get<BedType[]>("/bed-types");
    return response.data;
  }

  async createBedType(dto: CreateBedTypeRequest): Promise<BedType> {
    const response = await this.client.post<BedType>("/bed-types", dto);
    return response.data;
  }

  async updateBedType(id: string, dto: UpdateBedTypeRequest): Promise<BedType> {
    const response = await this.client.put<BedType>(`/bed-types/${id}`, dto);
    return response.data;
  }

  async deleteBedType(id: string): Promise<void> {
    await this.client.delete(`/bed-types/${id}`);
  }

  // ============ Custom Fields API methods ============

  async getCustomFieldDefinitions(entityType: CustomFieldEntityType): Promise<CustomFieldDefinition[]> {
    const response = await this.client.get<CustomFieldDefinition[]>('/custom-fields/definitions', { params: { entityType } });
    return response.data;
  }

  async createCustomFieldDefinition(dto: CreateCustomFieldDefinitionRequest): Promise<CustomFieldDefinition> {
    const response = await this.client.post<CustomFieldDefinition>('/custom-fields/definitions', dto);
    return response.data;
  }

  async updateCustomFieldDefinition(id: string, dto: UpdateCustomFieldDefinitionRequest): Promise<CustomFieldDefinition> {
    const response = await this.client.put<CustomFieldDefinition>(`/custom-fields/definitions/${id}`, dto);
    return response.data;
  }

  async deleteCustomFieldDefinition(id: string): Promise<void> {
    await this.client.delete(`/custom-fields/definitions/${id}`);
  }

  async getCustomFieldValues(entityType: CustomFieldEntityType, entityId: string): Promise<CustomFieldValue[]> {
    const response = await this.client.get<CustomFieldValue[]>(`/custom-fields/values/${entityType}/${entityId}`);
    return response.data;
  }

  async setCustomFieldValues(entityType: CustomFieldEntityType, entityId: string, values: Record<string, string | null>): Promise<void> {
    await this.client.put(`/custom-fields/values/${entityType}/${entityId}`, { values });
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

  async setModelDispatchDefaults(
    modelId: string,
    request: SetModelDispatchDefaultsRequest
  ): Promise<PrinterModelDto> {
    const response = await this.client.put<PrinterModelDto>(
      `/catalog/printer-models/${modelId}/dispatch-defaults`,
      request
    );
    return response.data;
  }

  async applyModelDefaults(modelId: string): Promise<ApplyModelDefaultsResult> {
    const response = await this.client.post<ApplyModelDefaultsResult>(
      `/catalog/printer-models/${modelId}/apply-defaults`
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

  // ============ Component Model API methods ============

  async getHotendModels(): Promise<HotendModelDefinition[]> {
    const response = await this.client.get<HotendModelDefinition[]>(
      "/catalog/hotends"
    );
    return response.data;
  }

  async getExtruderModels(): Promise<ExtruderModelDefinition[]> {
    const response = await this.client.get<ExtruderModelDefinition[]>(
      "/catalog/extruders"
    );
    return response.data;
  }

  async getToolheadModels(): Promise<ToolheadModelDefinition[]> {
    const response = await this.client.get<ToolheadModelDefinition[]>(
      "/catalog/toolheads"
    );
    return response.data;
  }

  async getNozzleModels(): Promise<NozzleModelDefinition[]> {
    const response = await this.client.get<NozzleModelDefinition[]>(
      "/catalog/nozzles"
    );
    return response.data;
  }

  // ============ Component Model CRUD methods ============

  // Hotend CRUD
  async createHotendModel(
    dto: CreateHotendModelDto
  ): Promise<HotendModelDefinition> {
    const response = await this.client.post<HotendModelDefinition>(
      "/catalog/hotends",
      dto
    );
    return response.data;
  }

  async updateHotendModel(
    id: string,
    dto: UpdateHotendModelDto
  ): Promise<HotendModelDefinition | null> {
    try {
      const response = await this.client.put<HotendModelDefinition>(
        `/catalog/hotends/${id}`,
        dto
      );
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null;
      }
      throw error;
    }
  }

  async deleteHotendModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/hotends/${id}`);
  }

  // Extruder CRUD
  async createExtruderModel(
    dto: CreateExtruderModelDto
  ): Promise<ExtruderModelDefinition> {
    const response = await this.client.post<ExtruderModelDefinition>(
      "/catalog/extruders",
      dto
    );
    return response.data;
  }

  async updateExtruderModel(
    id: string,
    dto: UpdateExtruderModelDto
  ): Promise<ExtruderModelDefinition | null> {
    try {
      const response = await this.client.put<ExtruderModelDefinition>(
        `/catalog/extruders/${id}`,
        dto
      );
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null;
      }
      throw error;
    }
  }

  async deleteExtruderModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/extruders/${id}`);
  }

  // Toolhead CRUD
  async createToolheadModel(
    dto: CreateToolheadModelDto
  ): Promise<ToolheadModelDefinition> {
    const response = await this.client.post<ToolheadModelDefinition>(
      "/catalog/toolheads",
      dto
    );
    return response.data;
  }

  async updateToolheadModel(
    id: string,
    dto: UpdateToolheadModelDefDto
  ): Promise<ToolheadModelDefinition | null> {
    try {
      const response = await this.client.put<ToolheadModelDefinition>(
        `/catalog/toolheads/${id}`,
        dto
      );
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null;
      }
      throw error;
    }
  }

  async deleteToolheadModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/toolheads/${id}`);
  }

  // Nozzle CRUD
  async createNozzleModel(
    dto: CreateNozzleModelDto
  ): Promise<NozzleModelDefinition> {
    const response = await this.client.post<NozzleModelDefinition>(
      "/catalog/nozzles",
      dto
    );
    return response.data;
  }

  async updateNozzleModel(
    id: string,
    dto: UpdateNozzleModelDto
  ): Promise<NozzleModelDefinition | null> {
    try {
      const response = await this.client.put<NozzleModelDefinition>(
        `/catalog/nozzles/${id}`,
        dto
      );
      return response.data;
    } catch (error) {
      if (axios.isAxiosError(error) && error.response?.status === 404) {
        return null;
      }
      throw error;
    }
  }

  async deleteNozzleModel(id: string): Promise<void> {
    await this.client.delete(`/catalog/nozzles/${id}`);
  }

  // Contextual Manufacturer Query
  async getManufacturersByContext(
    context: CatalogContext
  ): Promise<ManufacturersByContext> {
    const response = await this.client.get<ManufacturersByContext>(
      `/catalog/manufacturers/by-context/${context}`
    );
    return response.data;
  }

  // ============ File type API methods ============

  // ============ Filament Type API methods ============

  async getFilamentTypes(): Promise<FilamentTypeDto[]> {
    const response = await this.client.get<FilamentTypeDto[]>(
      "/filament-types"
    );
    return response.data;
  }

  async getFilamentTypesPaged(page = 1, pageSize = 50, search?: string): Promise<PagedResponse<FilamentTypeDto>> {
    const params: Record<string, string | number> = {};
    params.page = page;
    params.pageSize = pageSize;
    if (search) params.search = search;

    const response = await this.client.get<PagedResponse<FilamentTypeDto>>(
      "/filament-types",
      { params }
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

  async exportFilamentTypesCsv(): Promise<Blob> {
    const response = await this.client.get("/filament-types/export", {
      responseType: "blob",
    });
    return response.data;
  }

  async importFilamentTypesCsv(file: File): Promise<FilamentCsvImportResult> {
    const formData = new FormData();
    formData.append("file", file);
    const response = await this.client.post<FilamentCsvImportResult>(
      "/filament-types/import",
      formData,
      { headers: { "Content-Type": "multipart/form-data" } }
    );
    return response.data;
  }

  async getSpoolmanDbFilaments(): Promise<SpoolmanDbFilamentEntry[]> {
    const response = await this.client.get<SpoolmanDbFilamentEntry[]>(
      "/filament-types/spoolmandb/filaments"
    );
    return response.data;
  }

  async getSpoolmanDbMaterials(): Promise<SpoolmanDbMaterialEntry[]> {
    const response = await this.client.get<SpoolmanDbMaterialEntry[]>(
      "/filament-types/spoolmandb/materials"
    );
    return response.data;
  }

  async importFromSpoolmanDb(request: SpoolmanDbImportRequest): Promise<SpoolmanDbImportResult> {
    const response = await this.client.post<SpoolmanDbImportResult>(
      "/filament-types/spoolmandb/import",
      request
    );
    return response.data;
  }

  async syncExternalMaterials(): Promise<SpoolmanDbImportResult> {
    const response = await this.client.post<SpoolmanDbImportResult>(
      "/filament-types/spoolmandb/sync-materials"
    );
    return response.data;
  }

  // ─── Open Filament Database ──────────────────────────────────────────

  async getOfdBrands(): Promise<OfdBrand[]> {
    const response = await this.client.get<OfdBrand[]>(
      "/filament-types/openfilamentdb/brands"
    );
    return response.data;
  }

  async getOfdBrandMaterials(brandSlug: string): Promise<OfdBrandDetail> {
    const response = await this.client.get<OfdBrandDetail>(
      `/filament-types/openfilamentdb/brands/${brandSlug}/materials`
    );
    return response.data;
  }

  async getOfdFilaments(
    brandSlug: string,
    materialSlug: string,
    brandName: string,
    materialName: string
  ): Promise<OfdFlattenedEntry[]> {
    const response = await this.client.get<OfdFlattenedEntry[]>(
      `/filament-types/openfilamentdb/brands/${brandSlug}/materials/${materialSlug}/filaments`,
      { params: { brandName, materialName } }
    );
    return response.data;
  }

  async importFromOfd(request: OfdImportRequest): Promise<OfdImportResult> {
    const response = await this.client.post<OfdImportResult>(
      "/filament-types/openfilamentdb/import",
      request
    );
    return response.data;
  }

  async scanNetworkForSpoolman(): Promise<SpoolmanDiscoveryResult[]> {
    const response = await this.client.post<SpoolmanDiscoveryResult[]>(
      "/spoolman/scan-network"
    );
    return response.data;
  }

  // ============ Material Clusters ============

  async getMaterialClusters(): Promise<MaterialClusterDto[]> {
    const response = await this.client.get<MaterialClusterDto[]>(
      "/material-clusters"
    );
    return response.data;
  }

  async getMaterialCluster(id: string): Promise<MaterialClusterDto> {
    const response = await this.client.get<MaterialClusterDto>(
      `/material-clusters/${id}`
    );
    return response.data;
  }

  async createMaterialCluster(
    request: CreateMaterialClusterRequest
  ): Promise<MaterialClusterDto> {
    const response = await this.client.post<MaterialClusterDto>(
      "/material-clusters",
      request
    );
    return response.data;
  }

  async updateMaterialCluster(
    id: string,
    request: UpdateMaterialClusterRequest
  ): Promise<MaterialClusterDto> {
    const response = await this.client.put<MaterialClusterDto>(
      `/material-clusters/${id}`,
      request
    );
    return response.data;
  }

  async deleteMaterialCluster(id: string): Promise<void> {
    await this.client.delete(`/material-clusters/${id}`);
  }

  async addMaterialClusterMember(
    clusterId: string,
    filamentTypeId: string
  ): Promise<MaterialClusterDto> {
    const response = await this.client.post<MaterialClusterDto>(
      `/material-clusters/${clusterId}/members/${filamentTypeId}`
    );
    return response.data;
  }

  async removeMaterialClusterMember(
    clusterId: string,
    filamentTypeId: string
  ): Promise<void> {
    await this.client.delete(
      `/material-clusters/${clusterId}/members/${filamentTypeId}`
    );
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
    const response = await this.client.get<GcodeFileApiResponse[]>("/gcode-files", {
      params: { page, pageSize },
    });
    return response.data.map(ApiClient.mapGcodeFile);
  }

  async getGcodeFile(id: string): Promise<GcodeFile> {
    const response = await this.client.get<GcodeFileApiResponse>(`/gcode-files/${id}`);
    return ApiClient.mapGcodeFile(response.data);
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

    const response = await this.client.post<GcodeFileApiResponse>(
      "/gcode-files/upload",
      formData,
      {
        headers: {
          "Content-Type": "multipart/form-data",
        },
      }
    );
    return ApiClient.mapGcodeFile(response.data);
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
    
    const response = await this.client.get<GetGcodeFilesApiResponse>(
      "/gcode-files",
      { params }
    );
    return ApiClient.mapGcodeFilesResponse(response.data);
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
    
    const response = await this.client.get<GetGcodeFilesApiResponse>(
      "/gcode-files/query",
      { params }
    );
    return ApiClient.mapGcodeFilesResponse(response.data);
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

  async getUnifiedFiles(
    request: import("@/types/api").UnifiedFilesQueryRequest,
    signal?: AbortSignal
  ): Promise<import("@/types/api").UnifiedFilesQueryResponse> {
    const response = await this.client.post<import("@/types/api").UnifiedFilesQueryResponse>(
      "/3d-models/files/query",
      request,
      { signal }
    );
    return response.data;
  }

  async getPrintablesUserCollections(
    username: string,
    options?: { cursor?: string; limit?: number }
  ): Promise<PrintablesPagedResponse<PrintablesCollectionSummary>> {
    const normalizedUsername = ApiClient.normalizePrintablesUsername(username);
    if (!normalizedUsername) {
      throw new Error("Printables username is required.");
    }

    const response = await this.client.get<PrintablesPagedResponse<PrintablesCollectionSummary> & { collections?: PrintablesCollectionSummary[] }>(
      `/3d-models/printables/users/${encodeURIComponent(normalizedUsername)}/collections`,
      {
        params: {
          cursor: options?.cursor,
          limit: options?.limit,
        },
      }
    );
    const items = Array.isArray(response.data.items)
      ? response.data.items
      : Array.isArray(response.data.collections)
        ? response.data.collections
        : [];

    return {
      items,
      nextCursor: typeof response.data.nextCursor === "string" ? response.data.nextCursor : null,
    };
  }

  async getPrintablesCollectionModels(
    collectionId: string,
    options?: { cursor?: string; limit?: number; query?: string; ordering?: string }
  ): Promise<PrintablesPagedResponse<PrintablesModelSummary>> {
    const response = await this.client.get<PrintablesCursorApiResponse<PrintablesModelSummaryApiDto>>(
      `/3d-models/printables/collections/${encodeURIComponent(collectionId)}/models`,
      {
        params: {
          cursor: options?.cursor,
          limit: options?.limit,
          query: options?.query,
          ordering: options?.ordering,
        },
      }
    );
    return ApiClient.normalizePrintablesCursorPage(response.data);
  }

  async getPrintablesUserModels(
    username: string,
    options?: { cursor?: string; limit?: number }
  ): Promise<PrintablesPagedResponse<PrintablesModelSummary>> {
    const normalizedUsername = ApiClient.normalizePrintablesUsername(username);
    if (!normalizedUsername) {
      throw new Error("Printables username is required.");
    }

    const response = await this.client.get<PrintablesCursorApiResponse<PrintablesModelSummaryApiDto>>(
      `/3d-models/printables/users/${encodeURIComponent(normalizedUsername)}/models`,
      {
        params: {
          cursor: options?.cursor,
          limit: options?.limit,
        },
      }
    );
    return ApiClient.normalizePrintablesCursorPage(response.data);
  }

  async searchPrintablesModels(
    query: string,
    options?: { offset?: number; limit?: number }
  ): Promise<PrintablesPagedResponse<PrintablesModelSummary>> {
    const response = await this.client.get<PrintablesSearchApiResponse<PrintablesModelSummaryApiDto>>(
      `/3d-models/printables/search`,
      {
        params: {
          query,
          offset: options?.offset ?? 0,
          limit: options?.limit,
        },
      }
    );
    return ApiClient.normalizePrintablesSearchPage(response.data);
  }

  async getPrintablesOAuthStatus(): Promise<PrintablesOAuthStatus> {
    const response = await this.client.get<PrintablesOAuthStatus>(
      "/3d-models/printables/oauth/status"
    );
    return response.data;
  }

  async getPrintablesOAuthAuthorizeUrl(): Promise<{ authorizationUrl: string }> {
    const response = await this.client.post<{ authorizationUrl: string }>(
      "/3d-models/printables/oauth/connect"
    );
    return response.data;
  }

  async completePrintablesOAuthCallback(code: string, state: string): Promise<PrintablesOAuthStatus> {
    const response = await this.client.get<PrintablesOAuthStatus>(
      "/3d-models/printables/oauth/callback",
      {
        params: { code, state },
      }
    );
    return response.data;
  }

  async disconnectPrintablesOAuth(): Promise<void> {
    await this.client.post("/3d-models/printables/oauth/disconnect");
  }

  async getPrintablesLikedModels(
    options?: { cursor?: string; limit?: number }
  ): Promise<PrintablesPagedResponse<PrintablesModelSummary>> {
    const response = await this.client.get<PrintablesCursorApiResponse<PrintablesModelSummaryApiDto>>(
      "/3d-models/printables/liked",
      {
        params: {
          cursor: options?.cursor,
          limit: options?.limit,
        },
      }
    );
    return ApiClient.normalizePrintablesCursorPage(response.data);
  }

  async getPrintablesDownloadHistory(
    options?: { cursor?: string; limit?: number }
  ): Promise<PrintablesPagedResponse<PrintablesDownloadHistoryItem>> {
    const response = await this.client.get<PrintablesCursorApiResponse<PrintablesModelSummaryApiDto & { downloadedAt?: string | null }>>(
      "/3d-models/printables/history",
      {
        params: {
          cursor: options?.cursor,
          limit: options?.limit,
        },
      }
    );
    return ApiClient.normalizePrintablesHistoryPage(response.data);
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
    const response = await this.client.get<Blob>(`/gcode-files/download`, {
      params: { path: filePath },
      responseType: "blob",
    });

    const fileName = originalName || filePath.split("/").pop() || "file.gcode";
    this.triggerBrowserDownload(response.data, fileName);
  }

  async downloadGcodeFileById(id: string, originalName: string): Promise<void> {
    const response = await this.client.get<Blob>(`/gcode-files/file/${id}`, {
      responseType: "blob",
    });

    this.triggerBrowserDownload(response.data, originalName || "file.gcode");
  }

  async downloadModel3dFile(id: string, originalName: string): Promise<void> {
    const response = await this.client.get<Blob>(`/3d-models/file/${id}`, {
      responseType: "blob",
    });

    this.triggerBrowserDownload(response.data, originalName || "model");
  }

  private triggerBrowserDownload(blob: Blob, fileName: string): void {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
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
      xhr.open("POST", `${getApiBaseUrl()}/gcode-files/upload?${params.toString()}`);

      // Set auth header if available
      const token = localStorage.getItem("auth-token");
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
      xhr.open("POST", `${getApiBaseUrl()}/3d-models/upload?${params.toString()}`);

      // Set auth header if available
      const token = localStorage.getItem("auth-token");
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

  private queueJobIfMatch(rowVersion: string): string {
    const value = rowVersion.trim();
    if (!value) {
      throw new Error("The reviewed queue job does not have an ETag");
    }
    return `"${this.normalizeQueueJobEtagForBody(value)}"`;
  }

  private normalizeQueueJobEtagForBody(etag: string): string {
    return etag.trim().replace(/^W\//, "").replace(/^"|"$/g, "");
  }

  async getQueueChanges(
    afterSequence = 0,
    limit = 100
  ): Promise<QueueChangeFeed> {
    const response = await this.client.get<QueueChangeFeed>("/job-queue/changes", {
        params: { afterSequence, limit },
    });
    return response.data;
  }

  async getQueueSubscriptionResources(): Promise<QueueSubscriptionResources> {
    const response = await this.client.get<QueueSubscriptionResources>(
      "/job-queue/subscription-resources"
    );
    return response.data;
  }

  /**
   * Get queue overview for available printers with compatibility filtering.
   * All filtering is done server-side for consistency with auto-assign.
   * @param model Optional printer model name or slicer alias (e.g., "COREONEL", "Prusa MK4")
   * @param nozzle Optional required nozzle diameter in mm (e.g., 0.4)
   * @param material Optional required material type (e.g., "PLA", "PCTG")
   */
  async getQueueOverview(model?: string, nozzle?: number, material?: string): Promise<QueueOverviewDto[]> {
    const params: Record<string, string | number> = {};
    if (model) params.model = model;
    if (nozzle !== undefined) params.nozzle = nozzle;
    if (material) params.material = material;
    const response = await this.client.get<QueueOverviewDto[]>("/job-queue", {
      params,
    });
    return response.data;
  }

  /**
   * Get all queued print jobs with file metadata.
   * Uses the job-queue-analytics endpoint which returns actual job data.
   * @param printerId Optional printer ID to filter jobs by printer
   */
  async getJobQueue(printerId?: string): Promise<QueuedPrintJobWithFileMetaDto[]> {
    const params: Record<string, string | number> = { limit: 100 };
    if (printerId) {
      // Use the printer-specific endpoint
      const response = await this.client.get<QueuedPrintJobWithFileMetaDto[]>(
        `/job-queue-analytics/printer/${printerId}`,
        { params: { limit: 100 } }
      );
      return response.data;
    }
    // Use the global queue endpoint
    const response = await this.client.get<QueuedPrintJobWithFileMetaDto[]>(
      "/job-queue-analytics",
      { params }
    );
    return response.data;
  }

  /**
   * Batched fleet queue-summary read (#1146 item 9). One flat call replaces
   * the N per-printer `getJobQueue(printerId)` round trips the compact
   * printer grid previously made only to derive its "X of Y" queue label.
   * Printers with no active (queued or printing) job are simply absent from
   * the response.
   */
  async getPrinterQueueSummaries(signal?: AbortSignal): Promise<PrinterQueueSummaryDto[]> {
    const response = await this.client.get<PrinterQueueSummaryDto[]>(
      "/job-queue-analytics/printer-summaries",
      { signal }
    );
    return response.data ?? [];
  }

  async queuePrintJob(
    printerId: string,
    gcodeFileId: string,
    priority: PrintJobPriority = PrintJobPriority.Normal
  ): Promise<JobQueuePrintJob> {
    const response = await this.client.post<JobQueuePrintJob>("/job-queue", {
      printerId,
      gcodeFileId,
      priority,
    });
    return response.data;
  }

  /**
   * Delete a print queue job from the queue.
   * Cannot delete jobs that are currently printing.
   */
  async deletePrintQueueJob(jobId: string, reviewedRowVersion: string): Promise<void> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    await this.client.delete(`/job-queue/${jobId}`, {
      headers: { "If-Match": etag },
    });
  }

  /**
   * Dispatch a queued/assigned job to its printer to start printing.
   * The job must have an assigned printer and be in Queued or Assigned status.
   * @param jobId - The ID of the job to dispatch
   * @returns The updated job with Starting/Printing status
   */
  async dispatchPrintQueueJob(
    jobId: string,
    reviewedRowVersion: string
  ): Promise<DispatchClientResult> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    // Dispatch can take longer than the global Axios timeout due to G-code upload time.
    const response = await this.client.post<
      QueuedPrintJobDto | {
        error?: string;
        detail?: string;
        job?: QueuedPrintJobDto;
      }
    >(
      `/job-queue/${jobId}/dispatch`,
      undefined,
      {
        timeout: 0,
        headers: { "If-Match": etag },
        validateStatus: (status) => [200, 202, 409, 412, 503].includes(status),
      }
    );
    if (response.status === 200 || response.status === 202) {
      const job = response.data as QueuedPrintJobDto;
      if (!job.dispatchResult) {
        return {
          kind: 'unavailable',
          httpStatus: 503,
          errorCode: 'dispatch_outcome_unavailable',
        };
      }
      return {
        kind: response.status === 200 ? 'accepted' : 'reconciliation',
        httpStatus: response.status,
        job,
        dispatch: job.dispatchResult,
      };
    }

    const body = response.data as {
      error?: string;
      detail?: string;
      job?: QueuedPrintJobDto;
      dispatchResult?: QueuedPrintJobDto['dispatchResult'];
    };
    const rejectedJob =
      body.job ??
      ('id' in body ? (body as unknown as QueuedPrintJobDto) : undefined);
    return {
      kind:
        response.status === 409
          ? 'conflict'
          : response.status === 412
            ? 'stale'
            : 'unavailable',
      httpStatus: response.status as 409 | 412 | 503,
      errorCode:
        body.error ??
        rejectedJob?.dispatchResult?.errorCode ??
        'dispatch_request_failed',
      detail: body.detail ?? rejectedJob?.dispatchResult?.errorDetail ?? undefined,
      job: rejectedJob,
    };
  }

  /**
   * Dispatch a job to a specific printer, bypassing the scorer's material compatibility check.
   * Used when the operator explicitly overrides a material mismatch.
   */
  async dispatchJobToPrinter(
    jobId: string,
    printerId: string,
    reviewedRowVersion: string
  ): Promise<DispatchClientResult> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.post<
      QueuedPrintJobDto | {
        error?: string;
        detail?: string;
        job?: QueuedPrintJobDto;
      }
    >(
      `/job-queue/${jobId}/dispatch-to`,
      { printerId },
      {
        timeout: 0,
        headers: { "If-Match": etag },
        validateStatus: (status) => [200, 202, 409, 412, 503].includes(status),
      }
    );
    if (response.status === 200 || response.status === 202) {
      const job = response.data as QueuedPrintJobDto;
      if (!job.dispatchResult) {
        return {
          kind: 'unavailable',
          httpStatus: 503,
          errorCode: 'dispatch_outcome_unavailable',
        };
      }
      return {
        kind: response.status === 200 ? 'accepted' : 'reconciliation',
        httpStatus: response.status,
        job,
        dispatch: job.dispatchResult,
      };
    }

    const body = response.data as {
      error?: string;
      detail?: string;
      job?: QueuedPrintJobDto;
    };
    const rejectedJob =
      body.job ??
      ('id' in body ? (body as unknown as QueuedPrintJobDto) : undefined);
    return {
      kind:
        response.status === 409
          ? 'conflict'
          : response.status === 412
            ? 'stale'
            : 'unavailable',
      httpStatus: response.status as 409 | 412 | 503,
      errorCode:
        body.error ??
        rejectedJob?.dispatchResult?.errorCode ??
        'dispatch_request_failed',
      detail:
        body.detail ??
        rejectedJob?.dispatchResult?.errorDetail ??
        undefined,
      job: rejectedJob,
    };
  }

  // ============ Dispatch history ============

  async getDispatchHistory(page: number = 1, pageSize: number = 20, dateFrom?: Date, dateTo?: Date): Promise<DispatchHistoryPageDto> {
    const response = await this.client.get<DispatchHistoryPageDto>('/dispatch/history', {
      params: {
        page,
        pageSize,
        ...(dateFrom && { dateFrom: dateFrom.toISOString() }),
        ...(dateTo && { dateTo: dateTo.toISOString() }),
      },
    });
    return response.data;
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

  // ============ System capabilities ============

  async getSystemCapabilities(): Promise<SystemCapabilities> {
    const response = await this.client.get<SystemCapabilities>('/system/capabilities');
    return response.data;
  }

  async getSystemInfo(): Promise<SystemInfo> {
    const response = await this.client.get<SystemInfo>('/system/info');
    return response.data;
  }

  async getFeatureFlags(): Promise<Record<string, boolean>> {
    const response = await this.client.get<Record<string, boolean>>('/system/feature-flags');
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
      payload,
      { skipAuthRedirect: true },
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

  async request<T>(config: PfRequestConfig): Promise<T> {
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
    >("/file-consistency/health/summary");
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
    >("/file-consistency/audits/history", {
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
    >("/file-consistency/issues");
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
    >(`/file-consistency/model3d/${id}/health`);
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
    >(`/file-consistency/gcode/${id}/health`);
    return response.data;
  }

  // ============ Location API methods ============
  async getAllLocations(): Promise<import("@/types/api").Location[]> {
    const response = await this.client.get<import("@/types/api").Location[]>('/locations');
    return response.data;
  }

  async getLocationById(id: string): Promise<import("@/types/api").Location> {
    const response = await this.client.get<import("@/types/api").Location>(`/locations/${id}`);
    return response.data;
  }

  async getLocationTree(): Promise<import("@/types/api").LocationTreeNode[]> {
    const response = await this.client.get<import("@/types/api").LocationTreeNode[]>('/locations/tree');
    return response.data;
  }

  async getLocationAncestors(id: string): Promise<import("@/types/api").LocationBreadcrumbItem[]> {
    const response = await this.client.get<import("@/types/api").LocationBreadcrumbItem[]>(`/locations/${id}/ancestors`);
    return response.data;
  }

  async getLocationDescendants(id: string): Promise<import("@/types/api").Location[]> {
    const response = await this.client.get<import("@/types/api").Location[]>(`/locations/${id}/descendants`);
    return response.data;
  }

  async createLocation(request: import("@/types/api").CreateLocationRequest): Promise<import("@/types/api").Location> {
    const response = await this.client.post<import("@/types/api").Location>('/locations', request);
    return response.data;
  }

  async updateLocation(id: string, request: import("@/types/api").UpdateLocationRequest): Promise<import("@/types/api").Location> {
    const response = await this.client.put<import("@/types/api").Location>(`/locations/${id}`, request);
    return response.data;
  }

  async moveLocation(id: string, request: import("@/types/api").MoveLocationRequest): Promise<import("@/types/api").Location> {
    const response = await this.client.post<import("@/types/api").Location>(`/locations/${id}/move`, request);
    return response.data;
  }

  async deleteLocation(id: string): Promise<void> {
    await this.client.delete(`/locations/${id}`);
  }

  async getLocationSubtreePrinters(locationId: string): Promise<import("@/types/api").LocationSubtreePrinter[]> {
    const response = await this.client.get<import("@/types/api").LocationSubtreePrinter[]>(`/locations/${locationId}/printers/subtree`);
    return response.data;
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
    const response = await this.client.delete(`/printers/${printerId}/location`);
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

  /**
   * Get a single tag by id with its revision/concurrencyToken, typed for the
   * optimistic-concurrency edit flow (#844/#846). Delegates to `getTagById` so there's
   * a single place wiring the `/tags/{id}` request; this method only adds the stronger
   * `TagOption` return type needed by the revision-conflict flow.
   */
  async getTag(tagId: string): Promise<TagOption | null> {
    const data = await this.getTagById(tagId);
    return (data as TagOption | null) || null;
  }

  /**
   * Updates a tag's metadata using optimistic concurrency (#844). `dto.expectedRevision`
   * must equal the tag's current revision or the server rejects the write with a
   * structured HTTP 409 conflict (`{ error, expectedRevision, actualRevision }`), which the
   * response interceptor surfaces via `ApiError.data`.
   */
  async updateTag(tagId: string, dto: UpdateTagRequest): Promise<TagOption> {
    const response = await this.client.put(`/tags/${tagId}`, dto);
    return response.data as TagOption;
  }

  async assignTagToObject(objectId: string, tagId: string, objectType: 'Model3D' | 'GcodeFile' | 'Printer'): Promise<void> {
    await this.client.post(`/tags/${objectId}/${tagId}/assign`, null, {
      params: { objectType }
    });
  }

  async removeTagFromObject(objectId: string, tagId: string, objectType: 'Model3D' | 'GcodeFile' | 'Printer'): Promise<void> {
    await this.client.delete(`/tags/${objectId}/${tagId}/remove`, {
      params: { objectType }
    });
  }

  async getObjectTags(objectId: string, objectType: 'Model3D' | 'GcodeFile' | 'Printer'): Promise<Record<string, unknown>[]> {
    const response = await this.client.get(`/tags/object/${objectId}`, {
      params: { objectType }
    });
    return response.data || [];
  }

  /**
   * Batched fleet tag-read (#1146 item 1). One entry per accessible object of
   * `objectType`, replacing N per-object `getObjectTags` calls. Powers grid
   * surfaces (e.g. the compact printer grid) that would otherwise issue one
   * tag request per card.
   */
  async getObjectsTags(
    objectType: 'Model3D' | 'GcodeFile' | 'Printer',
    signal?: AbortSignal
  ): Promise<ObjectTagsDto[]> {
    const response = await this.client.get<ObjectTagsDto[]>('/tags/objects', {
      params: { objectType },
      signal,
    });
    return response.data ?? [];
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
    const response = await this.client.get('/system-logs', { params });
    return response.data || [];
  }

  /**
   * Get system logs with advanced query string search
   */
  async getSystemLogsQuery(query: string): Promise<Record<string, unknown>[]> {
    const response = await this.client.get(`/system-logs?query=${encodeURIComponent(query)}`);
    return response.data || [];
  }

  /**
   * Export system logs as JSON blob with optional filtering
   */
  async exportSystemLogs(params: Record<string, string>): Promise<Blob> {
    const response = await this.client.get('/system-logs/export', {
      params,
      responseType: 'blob'
    });
    return response.data;
  }

  async getSystemLogStats(): Promise<{ rowCount: number }> {
    const response = await this.client.get('/system-logs/stats');
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
    
    const response = await this.client.post('/3d-models', form, {
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
    const response = await this.client.get('/3d-models');
    return response.data || [];
  }

  /**
   * Upload raw STL geometry (e.g. cut model pieces) to the server.
   * Returns a server-side URL that slicer workers can HTTP-fetch.
   */
  async uploadGeometry(stlBlob: Blob, fileName: string): Promise<GeometryUploadResultDto> {
    const formData = new FormData();
    formData.append('geometryFile', new File([stlBlob], fileName, { type: 'application/octet-stream' }));
    const response = await this.client.post<GeometryUploadResultDto>(
      '/3d-models/upload-geometry',
      formData,
      { headers: { 'Content-Type': 'multipart/form-data' } },
    );
    return response.data;
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

  /**
   * Delete a user
   */
  async deleteUser(userId: string): Promise<void> {
    await this.client.delete(`/users/${userId}`);
  }

  /**
   * Admin: change another user's password.
   */
  async adminChangeUserPassword(
    userId: string,
    newPassword: string,
    confirmNewPassword: string
  ): Promise<{ message: string }> {
    const response = await this.client.post<{ message: string }>(`/users/${userId}/change-password`, {
      newPassword,
      confirmNewPassword,
    });
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
   * Get non-secret deployment defaults while first-run setup is required.
   */
  async getSetupBootstrap(signal?: AbortSignal): Promise<import("@/types/api").SetupBootstrapResponse> {
    const response = await this.client.get<import("@/types/api").SetupBootstrapResponse>(
      '/setup/bootstrap',
      { signal },
    );
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
  async testSpoolmanConnection(baseUrl: string): Promise<Record<string, unknown>> {
    const response = await this.client.post('/spoolman/test', { baseUrl });
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
   * Get paginated spools from Spoolman with server-side filtering and sorting.
   */
  async getSpools(params?: {
    limit?: number;
    offset?: number;
    sort?: string;
    search?: string;
    material?: string;
    vendor?: string;
    location?: string;
    allowArchived?: boolean;
    signal?: AbortSignal;
  }): Promise<{ items: SpoolmanSpool[]; totalCount: number }> {
    const queryParams: Record<string, string | number | boolean> = {};
    if (params?.limit && params.limit > 0) queryParams.limit = params.limit;
    if (params?.offset && params.offset > 0) queryParams.offset = params.offset;
    if (params?.sort) queryParams.sort = params.sort;
    if (params?.search) queryParams.search = params.search;
    if (params?.material) queryParams.material = params.material;
    if (params?.vendor) queryParams.vendor = params.vendor;
    if (params?.location) queryParams.location = params.location;
    if (params?.allowArchived) queryParams.allowArchived = true;

    const response = await this.client.get('/spoolman/spools', {
      params: Object.keys(queryParams).length > 0 ? queryParams : undefined,
      signal: params?.signal,
    });
    const data = response.data;

    // Handle the new paginated response format { items, totalCount }
    if (data && typeof data === 'object' && !Array.isArray(data) && 'items' in data) {
      const result = data as { items: SpoolmanSpool[]; totalCount: number };
      return {
        items: Array.isArray(result.items) ? result.items : [],
        totalCount: typeof result.totalCount === 'number' ? result.totalCount : 0,
      };
    }

    // Fallback for plain array response (backward compatibility)
    const items = Array.isArray(data) ? (data as SpoolmanSpool[]) : [];
    const offset = params?.offset ?? 0;
    return { items, totalCount: Math.max(items.length, offset + items.length) };
  }

  /**
   * Get distinct material, vendor, and location values across all spools.
   * Used to populate filter dropdowns without relying on paginated data.
   */
  async getSpoolFilterOptions(): Promise<SpoolFilterOptions> {
    const response = await this.client.get<SpoolFilterOptions>('/spoolman/filter-options');
    return response.data;
  }

  /**
   * Get filament types/products from Spoolman (product definitions, not physical spools)
   */
  async getFilaments(): Promise<SpoolmanFilament[]> {
    const response = await this.client.get('/spoolman/filaments');
    const data = response.data;
    return Array.isArray(data) ? data : (data as Record<string, unknown>).items as SpoolmanFilament[] || [];
  }

  /**
   * Get paginated filament types/products from Spoolman with server-side filtering and sorting.
   */
  async getFilamentsPaged(params?: {
    limit?: number;
    offset?: number;
    sort?: string;
    search?: string;
    material?: string;
    vendor?: string;
    signal?: AbortSignal;
  }): Promise<{ items: SpoolmanFilament[]; totalCount: number }> {
    const queryParams: Record<string, string | number> = {};
    if (params?.limit && params.limit > 0) queryParams.limit = params.limit;
    if (params?.offset && params.offset > 0) queryParams.offset = params.offset;
    if (params?.sort) queryParams.sort = params.sort;
    if (params?.search) queryParams.search = params.search;
    if (params?.material) queryParams.material = params.material;
    if (params?.vendor) queryParams.vendor = params.vendor;

    const response = await this.client.get('/spoolman/filaments', {
      params: Object.keys(queryParams).length > 0 ? queryParams : undefined,
      signal: params?.signal,
    });
    const data = response.data;

    // Handle paginated response format { items, totalCount }
    if (data && typeof data === 'object' && !Array.isArray(data) && 'items' in data) {
      const result = data as { items: SpoolmanFilament[]; totalCount: number };
      return {
        items: Array.isArray(result.items) ? result.items : [],
        totalCount: typeof result.totalCount === 'number' ? result.totalCount : 0,
      };
    }

    // Fallback for plain array response (backward compatibility)
    const items = Array.isArray(data) ? (data as SpoolmanFilament[]) : [];
    const offset = params?.offset ?? 0;
    return { items, totalCount: Math.max(items.length, offset + items.length) };
  }

  /**
   * Get distinct material and vendor values across all filament types.
   * Used to populate filter dropdowns on the Filaments tab.
   */
  async getFilamentFilterOptions(): Promise<FilamentFilterOptions> {
    const response = await this.client.get<FilamentFilterOptions>('/spoolman/filaments/filter-options');
    return response.data;
  }

  /**
   * Create a new filament in Spoolman.
   */
  async createFilament(request: SpoolmanUpdateFilamentRequest): Promise<SpoolmanFilament> {
    const response = await this.client.post<SpoolmanFilament>('/spoolman/filaments', request);
    return response.data;
  }

  /**
   * Get all vendors from Spoolman
   */
  async getVendors(): Promise<SpoolmanVendor[]> {
    const response = await this.client.get('/spoolman/vendors');
    const data = response.data;
    return Array.isArray(data) ? data : (data as Record<string, unknown>).items as SpoolmanVendor[] || [];
  }

  /**
   * Get all material types from Spoolman (e.g. PLA, PETG, ASA)
   */
  async getMaterials(): Promise<SpoolmanMaterial[]> {
    const response = await this.client.get('/spoolman/materials');
    const data = response.data;
    return Array.isArray(data) ? data : (data as Record<string, unknown>).items as SpoolmanMaterial[] || [];
  }

  /**
   * Gets material names that have at least one non-empty spool in inventory.
   * Used by the spool picker to show only selectable materials.
   */
  async getAvailableMaterials(): Promise<string[]> {
    const response = await this.client.get('/spoolman/materials/available');
    return Array.isArray(response.data) ? response.data : [];
  }

  /**
   * Bulk-update multiple filaments in Spoolman.
   * Only non-null fields are applied.
   */
  async bulkUpdateFilaments(request: SpoolmanBulkUpdateFilamentsRequest): Promise<SpoolmanBulkUpdateResult> {
    const response = await this.client.patch<SpoolmanBulkUpdateResult>('/spoolman/filaments/bulk', request);
    return response.data;
  }

  /**
   * Update a single filament in Spoolman by ID.
   * Only non-null fields are applied (PATCH semantics).
   */
  async updateFilament(id: number, request: SpoolmanUpdateFilamentRequest): Promise<SpoolmanFilament> {
    const response = await this.client.patch<SpoolmanFilament>(`/spoolman/filaments/${id}`, request);
    return response.data;
  }

  /**
   * Delete a single filament from Spoolman by ID.
   */
  async deleteFilament(id: number): Promise<void> {
    await this.client.delete(`/spoolman/filaments/${id}`);
  }

  /**
   * Bulk-delete multiple filaments from Spoolman.
   */
  async bulkDeleteFilaments(filamentIds: number[]): Promise<SpoolmanBulkUpdateResult> {
    const response = await this.client.delete<SpoolmanBulkUpdateResult>('/spoolman/filaments/bulk', {
      data: { filamentIds },
    });
    return response.data;
  }

  // ---- Spool CRUD ----

  /**
   * Create a new spool in Spoolman.
   */
  async createSpool(request: SpoolmanUpdateSpoolRequest): Promise<SpoolmanSpool> {
    const response = await this.client.post<SpoolmanSpool>('/spoolman/spools', request);
    return response.data;
  }

  /**
   * Update a single spool in Spoolman by ID.
   * Only non-null fields are applied (PATCH semantics).
   */
  async updateSpool(id: number, request: SpoolmanUpdateSpoolRequest): Promise<SpoolmanSpool> {
    const response = await this.client.patch<SpoolmanSpool>(`/spoolman/spools/${id}`, request);
    return response.data;
  }

  /**
   * Delete a single spool from Spoolman by ID.
   */
  async deleteSpool(id: number): Promise<void> {
    await this.client.delete(`/spoolman/spools/${id}`);
  }

  /**
   * Bulk-update multiple spools in Spoolman.
   * Only non-null fields are applied.
   */
  async bulkUpdateSpools(request: SpoolmanBulkUpdateSpoolsRequest): Promise<SpoolmanBulkUpdateResult> {
    const response = await this.client.patch<SpoolmanBulkUpdateResult>('/spoolman/spools/bulk', request);
    return response.data;
  }

  /**
   * Bulk-delete multiple spools from Spoolman.
   */
  async bulkDeleteSpools(spoolIds: number[]): Promise<SpoolmanBulkUpdateResult> {
    const response = await this.client.delete<SpoolmanBulkUpdateResult>('/spoolman/spools/bulk', {
      data: { spoolIds },
    });
    return response.data;
  }

  /**
   * Export all Spoolman filaments as a CSV file download.
   */
  async exportSpoolmanFilamentsCsv(): Promise<Blob> {
    const response = await this.client.get('/spoolman/filaments/export', {
      responseType: 'blob',
    });
    return response.data;
  }

  /**
   * Import filaments from a CSV file into Spoolman.
   */
  async importSpoolmanFilamentsCsv(file: File): Promise<SpoolmanBulkUpdateResult> {
    const formData = new FormData();
    formData.append('file', file);
    const response = await this.client.post<SpoolmanBulkUpdateResult>(
      '/spoolman/filaments/import',
      formData,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    );
    return response.data;
  }

  /**
   * Import spools from a CSV file into Spoolman.
   */
  async importSpoolmanSpoolsCsv(file: File): Promise<SpoolmanBulkUpdateResult> {
    const formData = new FormData();
    formData.append('file', file);
    const response = await this.client.post<SpoolmanBulkUpdateResult>(
      '/spoolman/spools/import',
      formData,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    );
    return response.data;
  }

  // ============ Settings API methods ============

  /**
   * Get password policy settings
   */
  async getPasswordPolicy(): Promise<AxiosResponse> {
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

  /**
   * Get connection health diagnostics for all printers
   */
  async getConnectionDiagnostics(): Promise<ConnectionDiagnosticsResponse> {
    const response = await this.client.get('/diagnostics/connections');
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
  async updatePrinterMaintenance(
    printerId: string,
    maintenance: Record<string, unknown>,
    reviewedRowVersion: string
  ): Promise<Record<string, unknown>> {
    const response = await this.client.put(
      `/printers/${printerId}/maintenance`,
      maintenance,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
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
   * Set printer maintenance mode
   * @param printerId - The printer ID
   * @param inMaintenance - Boolean indicating if printer should be in maintenance mode
   */
  async setPrinterMaintenance(
    printerId: string,
    inMaintenance: boolean,
    reviewedRowVersion: string
  ): Promise<Record<string, unknown>> {
    const response = await this.client.put(
      `/printers/${printerId}/maintenance`,
      inMaintenance,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
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

  /**
   * Get all profile schemas (process, machine, filament).
   *
   * When `engineVersion` is provided (e.g. `"2.3.1"`, `"2.4.1"`), the backend
   * filters fields to those applicable to that OrcaSlicer engine and renames
   * fields to that engine's key convention where needed (issue #578).
   */
  async getProfileSchemas(engineVersion?: string): Promise<ProfileSchemasResponse> {
    const params = engineVersion ? { engineVersion } : undefined;
    const response = await this.client.get<ProfileSchemasResponse>('/slicer/profiles/schemas', { params });
    return response.data;
  }

  /**
   * Get process profile schema, optionally scoped to an OrcaSlicer engine version.
   */
  async getProcessProfileSchema(engineVersion?: string): Promise<ProfileTypeSchema> {
    const params = engineVersion ? { engineVersion } : undefined;
    const response = await this.client.get<ProfileTypeSchema>('/slicer/profiles/schema/process', { params });
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
  async updatePasswordPolicy(policy: Record<string, unknown>): Promise<AxiosResponse> {
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
   * Get extracted 3MF metadata for a model
   */
  async getModel3DMetadata(modelId: string): Promise<ThreeMfMetadata | null> {
    const response = await this.client.get(`/3d-models/${modelId}/metadata`);
    return response.data as ThreeMfMetadata | null;
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
    sortBy: "priority" | "deadline" | "deadline_desc" = "priority",
    limit: number = 50,
    offset: number = 0,
    queuedFrom?: Date,
    queuedTo?: Date
  ): Promise<unknown[]> {
    const params = new URLSearchParams();
    if (filterStatus) params.append("filterStatus", filterStatus);
    if (filterModel) params.append("filterModel", filterModel);
    if (filterMaterial) params.append("filterMaterial", filterMaterial);
    params.append("sortBy", sortBy);
    params.append("limit", limit.toString());
    params.append("offset", offset.toString());
    if (queuedFrom) params.append("queuedFrom", queuedFrom.toISOString());
    if (queuedTo) params.append("queuedTo", queuedTo.toISOString());

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
  async getAnalyticsQueueStats(): Promise<QueueStatsDto> {
    const response = await this.client.get(`/job-queue-analytics/stats`);
    return response.data as QueueStatsDto;
  }
  /**
   * Get queue statistics by model (analytics)
   */
  async getAnalyticsQueueModelStats(): Promise<unknown[]> {
    const response = await this.client.get(`/job-queue-analytics/stats/models`);
    return response.data;
  }

  /**
   * Get queue history with pagination and filtering (analytics)
   * @param limit Maximum number of results (default 50)
   * @param offset Number of results to skip (default 0)
   * @param sortBy Field to sort by (newest, oldest, duration, model)
   * @param statuses Array of statuses to filter by (completed, failed, cancelled)
   * @param dateStart Start date filter (ISO string)
   * @param dateEnd End date filter (ISO string)
   */
  async getAnalyticsQueueHistory(
    limit: number = 50,
    offset: number = 0,
    sortBy: string = "newest",
    statuses?: string[],
    dateStart?: string | null,
    dateEnd?: string | null
  ): Promise<QueueHistoryPageDto> {
    const params: Record<string, unknown> = { limit, offset, sortBy };
    
    // Add statuses as comma-separated string if provided
    if (statuses && statuses.length > 0) {
      params.statuses = statuses.join(',');
    }
    
    // Add date filters if provided
    if (dateStart) {
      params.dateStart = dateStart;
    }
    if (dateEnd) {
      params.dateEnd = dateEnd;
    }
    
    const response = await this.client.get(`/job-queue-analytics/history`, { params });
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
  async updateJob(
    jobId: string,
    request: unknown,
    reviewedRowVersion: string
  ): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.put(
      `/job-queue/${jobId}`,
      request,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Update job priority
   */
  async updateJobPriority(
    jobId: string,
    newPriority: PrintJobPriority,
    reviewedRowVersion: string
  ): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.put(
      `/job-queue-analytics/jobs/${jobId}/priority`,
      { newPriority },
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Pause a print job
   */
  async pauseJob(jobId: string, reviewedRowVersion: string): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.post(
      `/job-queue-analytics/jobs/${jobId}/pause`,
      undefined,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Resume a print job
   */
  async resumeJob(jobId: string, reviewedRowVersion: string): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.post(
      `/job-queue-analytics/jobs/${jobId}/resume`,
      undefined,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Cancel a print job - stops the print if currently printing.
   * Sends a cancel command to the printer if the job is actively printing.
   */
  async cancelPrintQueueJob(
    jobId: string,
    reviewedRowVersion: string
  ): Promise<void> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    await this.client.post(
      `/job-queue/${jobId}/cancel`,
      undefined,
      { headers: { "If-Match": etag } }
    );
  }

  /**
   * Abort the current print attempt but keep the job in the queue.
   * Only works when the job is actively printing (Printing, Starting, or Paused).
   */
  async abortPrint(jobId: string, reviewedRowVersion: string): Promise<void> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    await this.client.post(
      `/job-queue/${jobId}/abort-print`,
      undefined,
      { headers: { "If-Match": etag } }
    );
  }

  /**
   * Rerun a completed print queue job (add it back to queue)
   */
  async rerunPrintQueueJob(
    jobId: string,
    reviewedRowVersion: string
  ): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.post(
      `/job-queue/${jobId}/rerun`,
      undefined,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Bulk cancel multiple print jobs
   */
  async bulkCancelJobs(request: {
    jobs: Array<{ jobId: string; rowVersion: string }>;
  }): Promise<unknown> {
    const jobIds = request.jobs.map((job) => job.jobId);
    const etags = request.jobs.map((job) => [
      job.jobId,
      this.normalizeQueueJobEtagForBody(job.rowVersion),
    ] as const);
    const response = await this.client.post(`/job-queue-analytics/bulk/cancel`, {
      jobIds,
      jobETags: Object.fromEntries(etags),
    });
    return response.data;
  }

  /**
   * Seed history from printer APIs.
   * Fetches all available history and uses deduplication to prevent duplicates.
   * Safe to call multiple times.
   */
  async seedHistory(printerIds?: string[]): Promise<void> {
    await this.client.post(`/job-queue/history/seed`, {
      printerIds,
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
  async updateJobDetails(
    jobId: string,
    updates: unknown,
    reviewedRowVersion: string
  ): Promise<unknown> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    const response = await this.client.put(
      `/job-queue/${jobId}`,
      updates,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  /**
   * Update job notes only
   */
  async updateJobNotes(
    jobId: string,
    notes: string,
    reviewedRowVersion: string
  ): Promise<void> {
    const etag = this.queueJobIfMatch(reviewedRowVersion);
    await this.client.put(
      `/job-queue-analytics/jobs/${jobId}/notes`,
      { notes: notes || null },
      { headers: { "If-Match": etag } }
    );
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
  ): Promise<TimelineEventDto[]> {
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
  async getAnalyticsJobStateHistory(jobId: string): Promise<JobStateHistoryDto> {
    const response = await this.client.get<JobStateHistoryDto>(
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

  // ===== User API Keys methods =====
  async listUserApiKeys(userId: string): Promise<unknown[]> {
    const response = await this.client.get(`/users/${userId}/apikeys`);
    return response.data;
  }

  async createUserApiKey(userId: string, request: unknown): Promise<unknown> {
    const response = await this.client.post(`/users/${userId}/apikeys`, request);
    return response.data;
  }

  async toggleUserApiKey(userId: string, keyId: string): Promise<unknown> {
    const response = await this.client.patch(`/users/${userId}/apikeys/${keyId}/toggle`);
    return response.data;
  }

  async deleteUserApiKey(userId: string, keyId: string): Promise<void> {
    await this.client.delete(`/users/${userId}/apikeys/${keyId}`);
  }

  async rotateUserApiKey(userId: string, keyId: string): Promise<unknown> {
    const response = await this.client.post(`/users/${userId}/apikeys/${keyId}/rotate`);
    return response.data;
  }

  async revealUserApiKey(userId: string, keyId: string): Promise<unknown> {
    const response = await this.client.get(`/users/${userId}/apikeys/${keyId}/reveal`);
    return response.data;
  }

  async getApiKeySettings(): Promise<unknown> {
    const response = await this.client.get('/apikeys/settings');
    return response.data;
  }

  // ============ Camera API methods ============
  
  /**
   * Get all standalone cameras
   */
  async getAllCameras(): Promise<import('@/types/api').CameraDto[]> {
    const response = await this.client.get('/cameras');
    return response.data;
  }

  /**
   * Get all enabled cameras (standalone only)
   */
  async getEnabledCameras(): Promise<import('@/types/api').CameraDto[]> {
    const response = await this.client.get('/cameras/enabled');
    return response.data;
  }

  /**
   * Get combined display cameras (standalone + printer-attached)
   * This is what should be shown in the Camera View
   */
  async getDisplayCameras(): Promise<import('@/types/api').DisplayCameraDto[]> {
    const response = await this.client.get('/cameras/display');
    return response.data;
  }

  /**
   * Get a specific camera by ID
   */
  async getCameraById(id: string): Promise<import('@/types/api').CameraDto> {
    const response = await this.client.get(`/cameras/${id}`);
    return response.data;
  }

  /**
   * Create a new standalone camera
   */
  async createCamera(request: import('@/types/api').CreateCameraDto): Promise<import('@/types/api').CameraDto> {
    const response = await this.client.post('/cameras', request);
    return response.data;
  }

  /**
   * Update an existing camera
   */
  async updateCamera(id: string, request: import('@/types/api').UpdateCameraDto): Promise<import('@/types/api').CameraDto> {
    const response = await this.client.put(`/cameras/${id}`, request);
    return response.data;
  }

  /**
   * Delete a camera
   */
  async deleteCamera(id: string): Promise<void> {
    await this.client.delete(`/cameras/${id}`);
  }

  /**
   * Toggle camera enabled status
   */
  async toggleCamera(id: string, isEnabled: boolean): Promise<import('@/types/api').CameraDto> {
    const response = await this.client.patch(`/cameras/${id}/toggle`, { isEnabled });
    return response.data;
  }

  /**
   * Get cameras linked to a specific printer
   */
  async getCamerasByPrinter(printerId: string): Promise<import('@/types/api').CameraDto[]> {
    const response = await this.client.get(`/cameras/by-printer/${printerId}`);
    return response.data;
  }

  /**
   * Detect camera endpoints for a printer backend.
   */
  async detectCameraEndpoints(
    request: import('@/types/api').DetectCameraEndpointsRequest
  ): Promise<import('@/types/api').DetectCameraEndpointsResponse> {
    // TODO: Confirm Lambert's backend contract remains POST /api/cameras/detect-endpoints with { printerId }.
    const response = await this.client.post('/cameras/detect-endpoints', request);
    return response.data;
  }

  // ====== NFC Devices ======

  async getNfcDevices(): Promise<import('@/types/api').NfcDeviceDto[]> {
    const response = await this.client.get('/nfc-devices');
    return response.data;
  }

  async getNfcDevice(id: string): Promise<import('@/types/api').NfcDeviceDto> {
    const response = await this.client.get(`/nfc-devices/${id}`);
    return response.data;
  }

  async createNfcDevice(dto: import('@/types/api').CreateNfcDeviceDto): Promise<import('@/types/api').NfcDeviceDto> {
    const response = await this.client.post('/nfc-devices', dto);
    return response.data;
  }

  async updateNfcDevice(id: string, dto: import('@/types/api').UpdateNfcDeviceDto): Promise<import('@/types/api').NfcDeviceDto> {
    const response = await this.client.put(`/nfc-devices/${id}`, dto);
    return response.data;
  }

  async deleteNfcDevice(id: string): Promise<void> {
    await this.client.delete(`/nfc-devices/${id}`);
  }

  async linkNfcTag(request: import('@/features/nfc/types').NfcLinkRequest): Promise<void> {
    await this.client.post('/nfc/link', request);
  }

  async getNfcBindings(): Promise<import('@/features/nfc/types').NfcBindingDto[]> {
    const response = await this.client.get('/nfc/bindings');
    return response.data;
  }

  async deleteNfcBinding(id: string): Promise<void> {
    await this.client.delete(`/nfc/bindings/${id}`);
  }

  async getNfcDeviceScanHistory(id: string, limit = 50): Promise<import('@/types/api').NfcScanHistoryDto[]> {
    const response = await this.client.get(`/nfc-devices/${id}/history`, { params: { limit } });
    return response.data;
  }

  // Monitoring
  async createMonitoringSession(): Promise<{ success: boolean; expiresAt: string }> {
    const response = await this.client.post('/monitoring/session');
    return response.data;
  }

  async getMonitoringStatus(): Promise<import('@/types/api').MonitoringStatusDto> {
    const response = await this.client.get('/monitoring/status');
    return response.data;
  }

  async getFailureDetectionStatus(): Promise<import('@/types/api').FailureDetectionMonitorStatusDto> {
    const response = await this.client.get('/failure-detection/status');
    return response.data;
  }

  async getFailureDetectionHistory(
    options?: {
      printerId?: string;
      take?: number;
    }
  ): Promise<FailureDetectionEvent[]> {
    const response = await this.client.get('/failure-detection/history', {
      params: {
        printerId: options?.printerId,
        take: options?.take,
      },
    });
    return response.data;
  }

  async getMonitoringMetricsSummary(): Promise<import('@/types/api').MonitoringMetricsSummaryDto> {
    const response = await this.client.get('/monitoring/metrics/summary');
    return response.data;
  }

  // ── Analytics Exports ─────────────────────────────────────────────────

  async exportPdfReport(days?: number): Promise<Blob> {
    const params = days ? `?days=${days}` : '';
    const response = await this.client.get(`/statistics/export/pdf${params}`, { responseType: 'blob' });
    return response.data;
  }

  async exportJobHistoryCsv(days?: number): Promise<Blob> {
    const params = days ? `?days=${days}` : '';
    const response = await this.client.get(`/statistics/export/jobs-csv${params}`, { responseType: 'blob' });
    return response.data;
  }

  async exportCostCsv(days?: number): Promise<Blob> {
    const params = days ? `?days=${days}` : '';
    const response = await this.client.get(`/statistics/export/cost-csv${params}`, { responseType: 'blob' });
    return response.data;
  }

  async exportUtilizationCsv(days?: number): Promise<Blob> {
    const params = days ? `?days=${days}` : '';
    const response = await this.client.get(`/statistics/export/utilization-csv${params}`, { responseType: 'blob' });
    return response.data;
  }

  // ============ Cost Tracking API methods ============
  async getCostSummary(days?: number, startDate?: string, endDate?: string): Promise<import("@/types/api").CostSummary> {
    const params: Record<string, string | number> = {};
    if (startDate && endDate) {
      params.startDate = startDate;
      params.endDate = endDate;
    } else if (days !== undefined) {
      params.days = days;
    }
    const response = await this.client.get('/statistics/costs/summary', {
      params: Object.keys(params).length > 0 ? params : undefined,
    });
    return response.data;
  }

  async getCosts(): Promise<import("@/types/api").CostSummary> {
    const response = await this.client.get('/statistics/costs');
    return response.data;
  }

  async getCostsByPrinter(days?: number, startDate?: string, endDate?: string): Promise<import("@/types/api").CostByPrinter[]> {
    const params: Record<string, string | number> = {};
    if (startDate && endDate) {
      params.startDate = startDate;
      params.endDate = endDate;
    } else if (days !== undefined) {
      params.days = days;
    }
    const response = await this.client.get('/statistics/costs/by-printer', {
      params: Object.keys(params).length > 0 ? params : undefined,
    });
    return response.data;
  }

  async getCostsByMaterial(days?: number, startDate?: string, endDate?: string): Promise<import("@/types/api").CostByMaterial[]> {
    const params: Record<string, string | number> = {};
    if (startDate && endDate) {
      params.startDate = startDate;
      params.endDate = endDate;
    } else if (days !== undefined) {
      params.days = days;
    }
    const response = await this.client.get('/statistics/costs/by-material', {
      params: Object.keys(params).length > 0 ? params : undefined,
    });
    return response.data;
  }

  async getCostsByJob(days?: number, startDate?: string, endDate?: string): Promise<import("@/types/api").CostByJob[]> {
    const params: Record<string, string | number> = {};
    if (startDate && endDate) {
      params.startDate = startDate;
      params.endDate = endDate;
    } else if (days !== undefined) {
      params.days = days;
    }
    const response = await this.client.get('/statistics/costs/by-job', {
      params: Object.keys(params).length > 0 ? params : undefined,
    });
    return response.data;
  }

  async getCostOverTime(): Promise<import("@/types/api").CostOverTime[]> {
    const response = await this.client.get('/statistics/cost-over-time');
    return response.data;
  }

  // ============ Notification API methods ============
  async getNotifications(limit?: number): Promise<NotificationDto[]> {
    const params = limit ? `?limit=${limit}` : '';
    const response = await this.client.get(`/notifications${params}`);
    return response.data || [];
  }

  async getUnreadNotifications(): Promise<NotificationDto[]> {
    const response = await this.client.get('/notifications/unread');
    return response.data || [];
  }

  async getUnreadCount(): Promise<number> {
    const response = await this.client.get<UnreadCountResponse>('/notifications/unread/count');
    return response.data.unreadCount;
  }

  async markNotificationAsRead(notificationId: string): Promise<void> {
    await this.client.put(`/notifications/${notificationId}/mark-read`);
  }

  async markMultipleNotificationsAsRead(notificationIds: string[]): Promise<void> {
    await this.client.put('/notifications/mark-read-batch', { notificationIds });
  }

  async deleteNotification(notificationId: string): Promise<void> {
    await this.client.delete(`/notifications/${notificationId}`);
  }

  async getNotificationPreferences(): Promise<NotificationPreferencesDto> {
    const response = await this.client.get('/notifications/preferences');
    return response.data;
  }

  async updateNotificationPreferences(preferences: UpdateNotificationPreferencesRequest): Promise<NotificationPreferencesDto> {
    const response = await this.client.put('/notifications/preferences', preferences);
    return response.data;
  }

  /**
   * Capability probe for the notification preferences enum. Introduced by #708.
   * Legacy servers respond 404; the client treats that as "supportedEventTypes
   * = the classic four job tokens only" via the preferences adapter, so
   * anticipatory operator tokens are never sent on the outbound PUT.
   */
  async getNotificationCapabilities(): Promise<NotificationCapabilitiesResponse | null> {
    try {
      const response = await this.client.get('/notifications/preferences/capabilities');
      return response.data as NotificationCapabilitiesResponse;
    } catch (err: unknown) {
      // The axios response interceptor above unwraps errors into ApiError
      // shapes carrying `statusCode`, not raw AxiosError. Treat 404 as the
      // legacy-server signal per the #708 contract; rethrow everything else
      // (network, 5xx) so callers/react-query can surface a proper failure
      // instead of misclassifying an outage as "legacy" and silently
      // stripping operator tokens on save.
      const status =
        typeof err === 'object' && err !== null && 'statusCode' in err
          ? (err as { statusCode: unknown }).statusCode
          : undefined;
      if (status === 404) return null;
      throw err;
    }
  }

  async getTelegramSettings(): Promise<TelegramSettingsDto> {
    const response = await this.client.get('/admin/integrations/telegram/settings');
    return response.data;
  }

  async updateTelegramSettings(settings: UpdateTelegramSettingsRequest): Promise<TelegramSettingsDto> {
    const response = await this.client.put('/admin/integrations/telegram/settings', settings);
    return response.data;
  }

  async sendTelegramTestMessage(): Promise<TelegramTestResult> {
    const response = await this.client.post('/admin/integrations/telegram/test');
    return response.data;
  }

  // ============ Auto-Dispatch API methods ============
  private reviewedEtag(value: string, label: string): string {
    const reviewed = value.trim();
    if (!reviewed) {
      throw new Error(`${label} does not have a reviewed ETag`);
    }
    return reviewed.startsWith('"') ? reviewed : `"${reviewed}"`;
  }

  private responseEtag(
    headers: unknown,
    label: string
  ): string {
    const etag = (headers as { etag?: unknown } | undefined)?.etag;
    if (typeof etag !== 'string' || !etag.trim()) {
      throw new Error(`${label} did not return a successor ETag`);
    }
    return etag.trim().replace(/^W\//, '').replace(/^"|"$/g, '');
  }

  private autoDispatchIfMatch(dispatchStateETag: string): string {
    const value = dispatchStateETag.trim();
    if (!value) {
      throw new Error("The reviewed auto-dispatch status does not have an ETag");
    }
    return value.startsWith('"') ? value : `"${value}"`;
  }

  async getAutoDispatchStatus(): Promise<AutoDispatchGlobalStatus> {
    const response = await this.client.get(`${AUTO_DISPATCH_API_BASE}/status`);
    return response.data;
  }

  async getAutoDispatchPrinterStatus(printerId: string): Promise<AutoDispatchDetailedStatus> {
    const response = await this.client.get(`${AUTO_DISPATCH_API_BASE}/${printerId}/status`);
    return response.data;
  }

  async confirmAutoDispatchReady(
    printerId: string,
    dispatchStateETag: string,
    confirmFilamentOverride = false,
    overrideJobETag?: string | null,
    filamentCheckETag?: string | null
  ): Promise<AutoDispatchReadyResult> {
    const etag = this.autoDispatchIfMatch(dispatchStateETag);
    const overrideQuery = confirmFilamentOverride
      ? "?confirmFilamentOverride=true"
      : "";
    const response = await this.client.post(
      `${AUTO_DISPATCH_API_BASE}/${printerId}/ready${overrideQuery}`,
      undefined,
      {
        headers: {
          "If-Match": etag,
          ...(confirmFilamentOverride
            ? {
                "X-Job-If-Match": this.reviewedEtag(
                  overrideJobETag,
                  "The reviewed filament override job"
                ),
                "X-Filament-Check-If-Match": this.reviewedEtag(
                  filamentCheckETag,
                  "The reviewed filament check"
                ),
              }
            : {}),
        },
        validateStatus: (status) =>
          status === 200 || status === 202 || status === 409,
      }
    );
    if (
      response.status === 409 &&
      (
        (
          response.data?.requiresFilamentOverride !== true &&
          response.data?.filamentCheckChanged !== true
        ) ||
        typeof response.data?.status !== "object" ||
        response.data?.status === null
      )
    ) {
      const data = response.data as { detail?: string; error?: string } | undefined;
      throw Object.assign(
        new Error(data?.detail ?? data?.error ?? "The ready request conflicted with the current queue state."),
        {
          statusCode: response.status,
          data: response.data,
        }
      );
    }
    return response.data;
  }

  async skipAutoDispatchJob(
    printerId: string,
    dispatchStateETag: string,
    jobETag: string
  ): Promise<void> {
    const etag = this.autoDispatchIfMatch(dispatchStateETag);
    const jobEtag = this.reviewedEtag(jobETag, "The reviewed next job");
    await this.client.post(
      `${AUTO_DISPATCH_API_BASE}/${printerId}/skip`,
      undefined,
      {
        headers: {
          "If-Match": etag,
          "X-Job-If-Match": jobEtag,
        },
      }
    );
  }

  async cancelAutoDispatch(
    printerId: string,
    dispatchStateETag: string
  ): Promise<void> {
    const etag = this.autoDispatchIfMatch(dispatchStateETag);
    await this.client.post(
      `${AUTO_DISPATCH_API_BASE}/${printerId}/cancel`,
      undefined,
      { headers: { "If-Match": etag } }
    );
  }

  async setAutoDispatchEnabled(
    printerId: string,
    enabled: boolean,
    dispatchStateETag: string,
    printerETag: string
  ): Promise<void> {
    const etag = this.autoDispatchIfMatch(dispatchStateETag);
    const printerEtag = this.reviewedEtag(
      printerETag,
      "The reviewed printer"
    );
    await this.client.put(
      `${AUTO_DISPATCH_API_BASE}/${printerId}/enabled`,
      { enabled },
      {
        headers: {
          "If-Match": etag,
          "X-Printer-If-Match": printerEtag,
        },
      }
    );
  }

  async setAutoDispatchGlobalEnabled(
    enabled: boolean,
    statuses: AutoDispatchStatus[]
  ): Promise<void> {
    const expectedVersions = Object.fromEntries(
      statuses.map((status) => {
        if (!status.dispatchStateETag || !status.printerETag) {
          throw new Error(
            `Printer ${status.printerId} does not have reviewed ETags`
          );
        }
        return [
          status.printerId,
          {
            dispatchStateETag: status.dispatchStateETag,
            printerETag: status.printerETag,
          },
        ];
      })
    );
    await this.client.put(`${AUTO_DISPATCH_API_BASE}/enabled`, {
      enabled,
      expectedVersions,
    });
  }

  async preClearAutoDispatchBed(
    printerId: string,
    dispatchStateETag: string
  ): Promise<AutoDispatchStatus> {
    const etag = this.autoDispatchIfMatch(dispatchStateETag);
    const response = await this.client.post(
      `${AUTO_DISPATCH_API_BASE}/${printerId}/pre-clear`,
      undefined,
      { headers: { "If-Match": etag } }
    );
    return response.data;
  }

  async acknowledgeCalibrationBedClearAndStart(input: {
    jobId: string;
    printerId: string;
    jobETag: string;
    dispatchStateETag: string;
    expectedPrinterConfigRevision?: number | null;
    idempotencyKey: string;
  }): Promise<BedClearAcknowledgementResult> {
    const response = await this.client.post<
      {
        message?: string;
        jobETag?: string | null;
        dispatchStateETag?: string | null;
        error?: string;
        detail?: string;
      }
    >(
      `/job-queue/${input.jobId}/acknowledge-bed-clear-and-start`,
      {
        printerId: input.printerId,
        expectedPrinterConfigRevision:
          input.expectedPrinterConfigRevision ?? null,
      },
      {
        headers: {
          "Idempotency-Key": input.idempotencyKey,
          "If-Match": this.reviewedEtag(input.jobETag, "The reviewed job"),
          "X-Dispatch-State-If-Match": this.reviewedEtag(
            input.dispatchStateETag,
            "The reviewed dispatch state"
          ),
        },
        validateStatus: (status) =>
          [200, 202, 409, 412, 422, 503].includes(status),
      }
    );
    if (response.status === 200 || response.status === 202) {
      return {
        kind: response.status === 202 ? 'accepted' : 'replayed',
        httpStatus: response.status,
        message: response.data.message,
        jobETag: response.data.jobETag,
        dispatchStateETag: response.data.dispatchStateETag,
      };
    }
    return {
      kind:
        response.status === 409
          ? 'conflict'
          : response.status === 412
            ? 'stale'
            : response.status === 422
              ? 'incompatible'
              : 'unavailable',
      httpStatus: response.status as 409 | 412 | 422 | 503,
      errorCode: response.data.error ?? 'bed_clear_acknowledgement_failed',
      detail: response.data.detail,
    };
  }

  // ============ Job Scheduling API methods ============
  async getScheduledJobs(dateFrom?: Date, dateTo?: Date): Promise<ScheduledJob[]> {
    const response = await this.client.get('/job-scheduling/scheduled', {
      params: {
        dateFrom: dateFrom?.toISOString(),
        dateTo: dateTo?.toISOString(),
      },
    });
    return response.data || [];
  }

  async getScheduledJob(jobId: string): Promise<ScheduledJob> {
    const response = await this.client.get(`/job-scheduling/${jobId}`);
    return response.data;
  }

  async scheduleJob(jobId: string, request: ScheduleJobRequest): Promise<ScheduledJob> {
    const response = await this.client.post(`/job-scheduling/${jobId}/schedule`, request);
    return response.data;
  }

  async rescheduleJob(jobId: string, request: RescheduleJobRequest): Promise<ScheduledJob> {
    const response = await this.client.put(`/job-scheduling/${jobId}/reschedule`, request);
    return response.data;
  }

  async cancelSchedule(jobId: string): Promise<void> {
    await this.client.delete(`/job-scheduling/${jobId}/schedule`);
  }

  async pauseSchedule(jobId: string): Promise<void> {
    await this.client.post(`/job-scheduling/${jobId}/pause`);
  }

  async resumeSchedule(jobId: string): Promise<void> {
    await this.client.post(`/job-scheduling/${jobId}/resume`);
  }

  async getJobExecutions(jobId: string): Promise<JobExecution[]> {
    const response = await this.client.get(`/job-scheduling/${jobId}/executions`);
    return response.data || [];
  }

  async getTimezones(): Promise<TimezoneInfo[]> {
    const response = await this.client.get('/job-scheduling/timezones');
    return response.data || [];
  }

  // ============ Obico ML Server Management API methods ============
  
  /**
   * Get all configured Obico ML servers
   */
  async getObicoServers(): Promise<ObicoServer[]> {
    const response = await this.client.get('/obico-servers');
    return response.data || [];
  }

  /**
   * Create a new Obico ML server
   */
  async createObicoServer(data: CreateObicoServerRequest): Promise<ObicoServer> {
    const response = await this.client.post<ObicoServer>('/obico-servers', data);
    return response.data;
  }

  /**
   * Update an existing Obico ML server
   */
  async updateObicoServer(id: string, data: UpdateObicoServerRequest): Promise<ObicoServer> {
    const response = await this.client.put<ObicoServer>(`/obico-servers/${id}`, data);
    return response.data;
  }

  /**
   * Delete an Obico ML server
   */
  async deleteObicoServer(id: string): Promise<void> {
    await this.client.delete(`/obico-servers/${id}`);
  }

  /**
   * Test connectivity and health of an Obico ML server
   */
  async testObicoServerHealth(id: string): Promise<ObicoServerHealthResponse> {
    const response = await this.client.get<ObicoServerHealthResponse>(`/obico-servers/${id}/health`);
    return response.data;
  }

  // ============ Multi-Toolhead Filament Tracking API methods ============

  /**
   * Assign a spool to a specific toolhead
   */
  async setToolheadSpool(
    printerId: string,
    toolheadIndex: number,
    spoolId: number,
    reviewedRowVersion: string
  ): Promise<string> {
    const response = await this.client.put(
      `/printers/${printerId}/toolheads/${toolheadIndex}/spool`,
      { spoolId },
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    return this.responseEtag(response.headers, "The toolhead spool mutation");
  }

  /**
   * Clear spool assignment from a specific toolhead
   */
  async clearToolheadSpool(
    printerId: string,
    toolheadIndex: number,
    reviewedRowVersion: string
  ): Promise<string> {
    const response = await this.client.delete(
      `/printers/${printerId}/toolheads/${toolheadIndex}/spool`,
      { headers: { "If-Match": this.reviewedEtag(reviewedRowVersion, "The reviewed printer") } }
    );
    return this.responseEtag(response.headers, "The toolhead spool mutation");
  }

  // ============ Model Collections API methods (#843/#846) ============
  // Personal/shared collections that group 3D models. Owner-or-administrator may mutate;
  // shared collections are readable by any authenticated user. See ModelCollectionsController.

  /** Lists collections visible to the current user (owned + shared; admins see all). */
  async getModelCollections(): Promise<ModelCollection[]> {
    const response = await this.client.get("/model-collections");
    return response.data || [];
  }

  /** Gets a single collection by id. */
  async getModelCollection(id: string): Promise<ModelCollection> {
    const response = await this.client.get(`/model-collections/${id}`);
    return response.data;
  }

  /** Creates a new collection owned by the current user. */
  async createModelCollection(dto: CreateModelCollectionRequest): Promise<ModelCollection> {
    const response = await this.client.post("/model-collections", dto);
    return response.data;
  }

  /** Updates a collection's name/description. */
  async updateModelCollection(id: string, dto: UpdateModelCollectionRequest): Promise<ModelCollection> {
    const response = await this.client.put(`/model-collections/${id}`, dto);
    return response.data;
  }

  /** Deletes a collection and its memberships. */
  async deleteModelCollection(id: string): Promise<void> {
    await this.client.delete(`/model-collections/${id}`);
  }

  /** Shares a collection so any authenticated user can read it. */
  async shareModelCollection(id: string): Promise<ModelCollection> {
    const response = await this.client.post(`/model-collections/${id}/share`);
    return response.data;
  }

  /** Unshares a collection so only the owner and administrators can read it. */
  async unshareModelCollection(id: string): Promise<ModelCollection> {
    const response = await this.client.post(`/model-collections/${id}/unshare`);
    return response.data;
  }

  /** Lists the memberships (model references) of a collection. */
  async listModelCollectionMembers(id: string): Promise<ModelCollectionMembership[]> {
    const response = await this.client.get(`/model-collections/${id}/members`);
    return response.data || [];
  }

  /** Adds a single model to a collection. Idempotent when already present. */
  async addModelCollectionMember(id: string, modelId: string): Promise<ModelCollectionMembership> {
    const response = await this.client.post(`/model-collections/${id}/members`, { modelId });
    return response.data;
  }

  /** Removes a single model from a collection. Idempotent when already absent. */
  async removeModelCollectionMember(id: string, modelId: string): Promise<void> {
    await this.client.delete(`/model-collections/${id}/members/${modelId}`);
  }

  /** Replaces a collection's entire membership set. */
  async replaceModelCollectionMembers(id: string, modelIds: string[]): Promise<ModelCollection> {
    const response = await this.client.put(`/model-collections/${id}/members`, { modelIds });
    return response.data;
  }

  // ── Quotas & Balances ───────────────────────────────────────────────

  async getQuotas(): Promise<QuotaDto[]> {
    const { data } = await this.client.get<QuotaDto[]>('/quotas');
    return data;
  }

  async getQuotasForUser(userId: string): Promise<QuotaDto[]> {
    const { data } = await this.client.get<QuotaDto[]>(`/quotas/user/${userId}`);
    return data;
  }

  async getQuotasForGroup(groupName: string): Promise<QuotaDto[]> {
    const { data } = await this.client.get<QuotaDto[]>(`/quotas/group/${encodeURIComponent(groupName)}`);
    return data;
  }

  async getQuota(id: string): Promise<QuotaDto> {
    const { data } = await this.client.get<QuotaDto>(`/quotas/${id}`);
    return data;
  }

  async createQuota(request: CreateQuotaRequest): Promise<QuotaDto> {
    const { data } = await this.client.post<QuotaDto>('/quotas', request);
    return data;
  }

  async updateQuota(id: string, request: UpdateQuotaRequest): Promise<QuotaDto> {
    const { data } = await this.client.put<QuotaDto>(`/quotas/${id}`, request);
    return data;
  }

  async deleteQuota(id: string): Promise<void> {
    await this.client.delete(`/quotas/${id}`);
  }

  async checkQuota(request: CheckQuotaRequest): Promise<QuotaCheckResult> {
    const { data } = await this.client.post<QuotaCheckResult>('/quotas/check', request);
    return data;
  }

  async resetExpiredQuotas(): Promise<{ resetCount: number }> {
    const { data } = await this.client.post<{ resetCount: number }>('/quotas/reset-expired');
    return data;
  }

  async getBalance(userId: string): Promise<UserBalanceDto> {
    const { data } = await this.client.get<UserBalanceDto>(`/quotas/balance/${userId}`);
    return data;
  }

  async creditBalance(userId: string, request: BalanceAdjustRequest): Promise<UserBalanceDto> {
    const { data } = await this.client.post<UserBalanceDto>(`/quotas/balance/${userId}/credit`, request);
    return data;
  }

  async debitBalance(userId: string, request: BalanceAdjustRequest): Promise<UserBalanceDto> {
    const { data } = await this.client.post<UserBalanceDto>(`/quotas/balance/${userId}/debit`, request);
    return data;
  }

  async getBalanceTransactions(userId: string, take = 50): Promise<BalanceTransactionDto[]> {
    const { data } = await this.client.get<BalanceTransactionDto[]>(`/quotas/balance/${userId}/transactions`, { params: { take } });
    return data;
  }
}

// Export singleton instance
export const apiClient = new ApiClient();

// ── Domain service re-exports ───────────────────────────────────────────
// Phase 2: focused service modules. Import directly for new code,
// or continue importing from api.ts (barrel) for backward compat.
export { printerService } from './printerService';
export { jobQueueService } from './jobQueueService';
export { catalogService } from './catalogService';
