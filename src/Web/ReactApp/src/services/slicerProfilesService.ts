// Service for interacting with slicer profile API endpoints (Phase 6)
// Provides list, import, export, and set-default operations.

import { apiClient } from './api';

// Base interface for all profile types
export interface IProfileListItem {
  id: string;
  name: string;
  slicerType: string;
  isDefault: boolean;
  isSystem: boolean;
  isPublic: boolean;
  hash: string;
  profileType: 'process' | 'filament' | 'machine';
}

// Process profile list item
export interface ProcessProfileListItem extends IProfileListItem {
  profileType: 'process';
  quality: string;
  layerHeight: number;
  infillPercentage: number;
  nozzleDiameter?: number;
  material?: string;
}

// Filament profile list item
export interface FilamentProfileListItem extends IProfileListItem {
  profileType: 'filament';
  material: string;
  nozzleTemperature?: number;
  bedTemperature?: number;
  printSpeed: number;
}

// Machine profile list item
export interface MachineProfileListItem extends IProfileListItem {
  profileType: 'machine';
  manufacturer: string;
  nozzleDiameter?: number;
}

// Union type for all profile types
export type SlicerProfileListItem = ProcessProfileListItem | FilamentProfileListItem | MachineProfileListItem;

// Response structure with profiles organized by type
export interface ExtendedProfilesResponse {
  processProfiles: ProcessProfileListItem[];
  filamentProfiles: FilamentProfileListItem[];
  machineProfiles: MachineProfileListItem[];
}

export interface ImportSlicerProfileRequest {
  rawJson: string;
  name?: string;
  description?: string;
  slicerType: string; // PrusaSlicer, OrcaSlicer, etc.
  allowSystemOverride?: boolean;
  setDefault?: boolean;
  isPublic?: boolean;
}

/**
 * Response shape for `POST /slicer/profiles/import` (the only call site of this type —
 * see `slicerProfilesService.importProfile`). The backend serializes
 * `ProcessProfileExtendedDto` (`src/slicer/Farm.Slicer.Module/Dtos/ProcessProfileManagementDtos.cs`),
 * constructed by `ProfilesService.ImportProfileAsync`
 * (`src/slicer/Farm.Slicer.Module.Api/Services/ProfilesService.cs`) — this interface does NOT
 * fully mirror that DTO field-for-field; see the per-field notes below.
 *
 * `isSystem`, `hash`, and `metadata` are non-nullable on `ProcessProfileExtendedDto` and are
 * always populated by `ImportProfileAsync` (defaulting to `false`, `""`, and `{}` respectively),
 * so they remain required here — the `fixtures/wire-contracts/api/slicer-profiles/profiles.populated.json`
 * corpus fixture that omits them is captured from the unrelated `GET /api/slicer/profiles/{id}`
 * endpoint (`ProcessProfileResponseDto`), which this type does not model.
 *
 * `material`, `nozzleTemperature`, and `bedTemperature` are marked optional because
 * `ProcessProfileExtendedDto` has no such properties at all — `ImportProfileAsync` never sets
 * them on the response, even though the equivalent `ProcessProfile` entity persists them. They
 * were previously (incorrectly) declared required here; they are kept on the type as optional so
 * existing call sites that narrow on them still compile, but callers must not assume they are
 * present for the import response.
 *
 * `advancedSettings` is optional for the same reason — `ProcessProfileExtendedDto` doesn't carry
 * it either (only `ProcessProfileResponseDto`, returned by the unrelated plain CRUD `/api/slicer/profiles`
 * endpoints, does). It's declared here only as tolerant of a future backend alignment, not because
 * the import endpoint sends it today.
 */
export interface SlicerProfileExtended {
  id: string;
  name: string;
  description?: string | null;
  slicerType: string;
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  nozzleTemperature?: number;
  bedTemperature?: number;
  enableSupports: boolean;
  material?: string;
  quality: string;
  advancedSettings?: string;
  isDefault: boolean;
  isPublic: boolean;
  isSystem: boolean;
  hash: string;
  createdAt: string;
  updatedAt: string;
  metadata: Record<string, unknown>;
}

export interface SlicerProfileExportDto {
  id: string;
  name: string;
  slicerType: string;
  hash: string;
  rawJson: string;
  metadata: Record<string, unknown>;
}

// Hierarchy structures for organized profile display
export interface PrinterModelProfilesDto {
  name: string;
  modelId: string;
  machineProfiles: MachineProfileListItem[];
  filamentProfiles: FilamentProfileListItem[];
  processProfiles: ProcessProfileListItem[];
}

export interface ManufacturerProfilesDto {
  name: string;
  models: Record<string, PrinterModelProfilesDto>;
}

export interface HierarchicalProfilesResponse {
  byHierarchy: Record<string, ManufacturerProfilesDto>;
  machineProfiles: Record<string, MachineProfileListItem[]>;
  filamentProfiles: Record<string, FilamentProfileListItem[]>;
  processProfiles: Record<string, ProcessProfileListItem[]>;
}

// === Worker Hierarchy Types (Phase 3 - Hybrid Architecture) ===
// These types match the OrcaSlicer worker's AllProfilesResponseDto

/**
 * Printer model profiles structure from OrcaSlicer worker.
 * Contains associated machine, filament, and process profiles for a specific printer model.
 */
export interface WorkerPrinterModelProfilesDto {
  name: string;
  machineProfiles?: OrcaMachineProfile[];
  filamentProfiles?: OrcaFilamentProfile[];
  processProfiles?: OrcaProcessProfile[];
}

/**
 * Manufacturer profiles structure from OrcaSlicer worker.
 * Contains all models for a manufacturer with their associated profiles.
 */
export interface WorkerManufacturerProfilesDto {
  name: string;
  models: Record<string, WorkerPrinterModelProfilesDto>;
}

/**
 * Complete profile hierarchy from OrcaSlicer worker.
 * This is the response from GET /slicer/profiles/worker-hierarchy.
 */
export interface WorkerHierarchyResponse {
  byHierarchy: Record<string, WorkerManufacturerProfilesDto>;
  machineProfiles?: Record<string, OrcaMachineProfile[]>;
  filamentProfiles?: Record<string, OrcaFilamentProfile[]>;
  processProfiles?: Record<string, OrcaProcessProfile[]>;
}

/**
 * Result from deleting all system profiles.
 */
export interface DeleteSystemProfilesResult {
  machineProfilesDeleted: number;
  processProfilesDeleted: number;
  filamentProfilesDeleted: number;
  totalDeleted: number;
  message: string;
}

export interface BulkDeleteResultDto {
  machineProfilesDeleted: number;
  processProfilesDeleted: number;
  filamentProfilesDeleted: number;
  totalDeleted: number;
  notFound: number;
}

// === OrcaSlicer Worker Profile Types (System Profiles) ===
// These are returned from the OrcaSlicer worker API for incremental loading

/**
 * Machine profile from OrcaSlicer worker (system profile).
 * Contains printer-specific configuration like bed size, nozzle, etc.
 */
export interface OrcaMachineProfile {
  name: string;
  manufacturer: string;
  description?: string;
  nozzleDiameter?: number;
  nozzleType?: string;
  printer_model?: string;
  printerModel?: string;
  printerVariant?: string;
  instantiation?: boolean;
  inherits?: string;
  buildVolumeX?: number;
  buildVolumeY?: number;
  buildVolumeZ?: number;
  printableArea?: string;
  maxPrintSpeed?: number;
  motionType?: string;
  gcodeDialect?: string;
  hasHeatedBed?: boolean;
  hasHeatedChamber?: boolean;
  maxBedTemperature?: number;
  maxHotendTemperature?: number;
  extruderCount?: number;
  supportMultiMaterial?: boolean;
  retractionLength?: number;
  retractionSpeed?: number;
  retractionLiftZ?: number;
  detractionSpeed?: number;
  bedType?: string;
  bedShape?: string;
  startGcode?: string;
  endGcode?: string;
  maxAccelerationX?: number;
  maxAccelerationY?: number;
  maxFeedrateX?: number;
  maxFeedrateY?: number;
  settings?: Record<string, unknown>;
  /** High-flow (HF) hotend variant flag, derived backend-side (#1780). Some vendor
   * bundles (e.g. Prusa CORE One / CORE One L) never set nozzle_type, so this is the
   * structural signal to prefer over name parsing when it is present. */
  isHighFlowNozzle?: boolean;
}

/**
 * Filament profile from OrcaSlicer worker (system profile).
 * Contains material-specific settings like temperature, speed, etc.
 */
export interface OrcaFilamentProfile {
  name: string;
  material: string;
  manufacturer?: string;
  description?: string;
  nozzleTemperature: number;
  bedTemperature: number;
  printSpeed: number;
  compatible_printers?: string[] | null;
  instantiation?: boolean;
  inherits?: string;
  settings?: Record<string, unknown>;
}

/**
 * Process profile from OrcaSlicer worker (system profile).
 * Contains quality/speed settings like layer height, infill, supports, etc.
 */
export interface OrcaProcessProfile {
  name: string;
  quality: string;
  layerHeight: number;
  infillPercentage: number;
  printSpeed: number;
  supports: boolean;
  description?: string;
  compatible_printers?: string[] | null;
  instantiation?: boolean;
  inherits?: string;
  settings?: Record<string, unknown>;
}

/**
 * Request body for fetching profiles compatible with specific machines.
 */
export interface ForMachinesRequest {
  machineNames: string[];
}

// === Custom Profile Management Types (Phase 2) ===

/**
 * Request to clone a single profile.
 */
export interface CloneSingleProfileRequest {
  sourceProfileId: string;
  profileType: 'machine' | 'filament' | 'process';
  name?: string;
  /**
   * Optional override of the catalog PrinterModel association for the cloned profile.
   * When omitted the cloned profile inherits the source's PrinterModelId.
   * Filament profiles ignore this field.
   */
  printerModelId?: string;
  /**
   * Optional override of compatible printer names (filament only). When omitted the
   * clone inherits the source's compatible-printers list.
   */
  compatiblePrinters?: string[];
}

/**
 * Response from cloning a single profile.
 */
export interface CloneSingleProfileResponse {
  id: string;
  name: string;
  profileType: 'machine' | 'filament' | 'process';
  isSystem: boolean;
}

/**
 * Request to upload a custom profile.
 */
export interface UploadProfileRequest {
  rawJson: string;
  profileType: 'machine' | 'filament' | 'process';
  name?: string;
  /**
   * Optional explicit catalog PrinterModel association. When omitted the backend
   * attempts to resolve it from the raw JSON via the printer-model alias service.
   * Filament profiles ignore this field.
   */
  printerModelId?: string;
  /**
   * Optional explicit compatible-printer names (filament only). When omitted the
   * backend extracts them from the raw JSON's compatible_printers array.
   */
  compatiblePrinters?: string[];
}

/**
 * A user's custom profile (IsSystem=false).
 */
export interface CustomProfile {
  id: string;
  name: string;
  profileType: 'machine' | 'filament' | 'process';
  isSystem: boolean;
  createdAt: string;
  updatedAt?: string;
  description?: string;
  rawJson?: string;
  /**
   * Catalog PrinterModel association (machine and process only). Always null for
   * filament profiles, which use compatible-printer strings instead.
   */
  printerModelId?: string | null;
  /**
   * Compatible printer names (filament only). Null/undefined for machine/process
   * profiles, which use printerModelId instead.
   */
  compatiblePrinters?: string[] | null;
}

/**
 * Request to update a custom profile.
 */
export interface UpdateCustomProfileRequest {
  rawJson?: string;
  name?: string;
  description?: string;
  /**
   * New catalog PrinterModel association (machine/process only). When omitted the
   * existing association is left unchanged. Filament profiles ignore this field.
   */
  printerModelId?: string;
  /**
   * When true, clears any existing PrinterModel association on the profile.
   * Takes precedence over printerModelId when both are supplied.
   */
  clearPrinterModelId?: boolean;
  /**
   * New compatible-printer names (filament only). When omitted the existing list
   * is left unchanged. Send an empty array together with clearCompatiblePrinters
   * to detach all associations.
   */
  compatiblePrinters?: string[];
  /**
   * When true, clears the compatible-printers list on the profile. Takes precedence
   * over compatiblePrinters when both are supplied.
   */
  clearCompatiblePrinters?: boolean;
}

/**
 * Response listing user's custom profiles.
 */
export interface CustomProfilesListResponse {
  profiles: CustomProfile[];
  totalCount: number;
  machineProfileCount: number;
  processProfileCount: number;
  filamentProfileCount: number;
}

/**
 * The kind of slicer profile being resolved via `resolve-for-model` (#2443).
 * Matches the backend's `ProfileResolutionType` enum member names exactly
 * (serialized via `JsonStringEnumConverter`, e.g. "Process").
 */
export type ProfileResolutionType = 'Machine' | 'Process' | 'Filament';

/**
 * Request to resolve a catalog (library/default) profile's canonical database GUID
 * for a printer model, auto-importing it from the OrcaSlicer worker catalog if it has
 * never been imported before. Library profiles surface only a display name (no GUID) -
 * see `.github/skills/orcaslicer-profiles/SKILL.md` - so this must be called before an
 * endpoint that requires a GUID (e.g. `cloneProfile`) is used against a library profile.
 */
export interface ResolveProfileForModelRequest {
  profileType: ProfileResolutionType;
  /**
   * The profile name as reported by the catalog read endpoints (e.g.
   * `getProcessProfilesForMachines`), which carries no GUID for profiles that have
   * never been imported.
   */
  profileName: string;
}

/**
 * Result of resolving (and, if needed, auto-importing) a catalog profile's identity.
 */
export interface ResolveProfileForModelResult {
  printerModelId: string;
  profileType: ProfileResolutionType;
  profileName: string;
  /** The resolved profile's database identity. Null when resolution failed; see `error`. */
  profileId: string | null;
  imported: boolean;
  error?: string | null;
}

export interface CloneProfileFamilyRequest {
  familyName: string;
  targetPrinterModelId: string;
  sourceManufacturer: string;
  sourceMachineModelName: string;
  nozzleDiameters: number[];
  familyOverrides: Record<string, unknown>;
  slicerEngineVersion?: string;
  slicerDistribution: string;
}

export interface ProfileFamilyMachineVariant {
  id: string;
  name: string;
  nozzleDiameter: number;
  sourceSystemPresetName: string;
}

export type ProfileFamilyRenderStatus = 'NotApplicable' | 'Pending' | 'Healthy' | 'Failed' | 'Stale';

export interface CloneProfileFamilyResponse {
  familyId: string;
  familyName: string;
  targetPrinterModelId: string;
  renderStatus: ProfileFamilyRenderStatus;
  lastRenderedAt: string | null;
  machineProfiles: ProfileFamilyMachineVariant[];
  processProfileCount: number;
  filamentProfileCount: number;
}

export const slicerProfilesService = {
  async listExtended(): Promise<ExtendedProfilesResponse> {
    const res = await apiClient.get<ExtendedProfilesResponse>('/slicer/profiles/extended');
    return res.data;
  },
  async listHierarchical(machineProfileId?: string): Promise<HierarchicalProfilesResponse> {
    // Use /hierarchy endpoint which returns hierarchical profile data with byHierarchy
    // Optional machineProfileId filter to support CompatiblePrinters filtering
    const url = machineProfileId 
      ? `/slicer/profiles/hierarchy?machineProfileId=${machineProfileId}`
      : '/slicer/profiles/hierarchy';
      
    const res = await apiClient.get<HierarchicalProfilesResponse>(url);
    return res.data;
  },
  async importProfile(req: ImportSlicerProfileRequest): Promise<SlicerProfileExtended> {
    const res = await apiClient.post<SlicerProfileExtended>('/slicer/profiles/import', req);
    return res.data;
  },
  async exportProfile(id: string): Promise<SlicerProfileExportDto> {
    const res = await apiClient.get<SlicerProfileExportDto>(`/slicer/profiles/${id}/export`);
    return res.data;
  },
  async setDefault(id: string): Promise<void> {
    await apiClient.post<void>(`/slicer/profiles/${id}/set-default`);
  },
  async bulkDelete(profileIds: string[]): Promise<BulkDeleteResultDto> {
    const res = await apiClient.post<BulkDeleteResultDto>('/slicer/profiles/bulk-delete', profileIds);
    return res.data;
  },

  // === Incremental Loading Methods (Phase 1) ===
  // These methods fetch profiles on-demand from the OrcaSlicer worker
  // instead of loading all profiles upfront

  /**
   * Get machine profiles for a specific printer model ID.
   * Uses the catalog's OrcaSlicer alias to find matching profiles.
   * @param modelId - The printer model GUID from the catalog
   * @returns Machine profiles for the specified model
   */
  async getMachineProfilesForModel(modelId: string, engineVersion?: string): Promise<OrcaMachineProfile[]> {
    const qs = engineVersion ? `?slicerEngineVersion=${encodeURIComponent(engineVersion)}` : '';
    const res = await apiClient.get<OrcaMachineProfile[]>(`/slicer/profiles/machine/for-model/${modelId}${qs}`);
    return res.data;
  },

  /**
   * Get machine profiles by manufacturer and model name.
   * Direct query when you know the exact manufacturer/model strings.
   * @param manufacturer - Manufacturer name (e.g., "Prusa", "Elegoo")
   * @param model - Model name (e.g., "CORE One", "Neptune 4")
   * @param engineVersion - Optional OrcaSlicer engine version to route to.
   * @returns Machine profiles matching the manufacturer/model
   */
  async getMachineProfilesByName(manufacturer: string, model: string, engineVersion?: string): Promise<OrcaMachineProfile[]> {
    const qs = engineVersion ? `?slicerEngineVersion=${encodeURIComponent(engineVersion)}` : '';
    const res = await apiClient.get<OrcaMachineProfile[]>(
      `/slicer/profiles/machine/${encodeURIComponent(manufacturer)}/${encodeURIComponent(model)}${qs}`
    );
    return res.data;
  },

  /**
   * Get filament profiles compatible with specific machine profiles.
   * Uses OrcaSlicer's compatible_printers matching.
   * @param machineNames - Array of machine profile names (e.g., ["Prusa CORE One 0.4 nozzle"])
   * @param engineVersion - Optional OrcaSlicer engine version to route to.
   * @returns Filament profiles compatible with the specified machines
   */
  async getFilamentProfilesForMachines(
    machineNames: string[],
    engineVersion?: string,
    view?: 'summary',
  ): Promise<OrcaFilamentProfile[]> {
    const params = new URLSearchParams();
    if (engineVersion) params.set('slicerEngineVersion', engineVersion);
    if (view) params.set('view', view);
    const qs = params.size > 0 ? `?${params.toString()}` : '';
    const res = await apiClient.post<OrcaFilamentProfile[]>(
      `/slicer/profiles/filament/for-machines${qs}`,
      { machineNames } as ForMachinesRequest
    );
    return res.data;
  },

  /**
   * Get process profiles compatible with specific machine profiles.
   * Uses OrcaSlicer's compatible_printers matching.
   * @param machineNames - Array of machine profile names (e.g., ["Prusa CORE One 0.4 nozzle"])
   * @param engineVersion - Optional OrcaSlicer engine version to route to.
   * @returns Process profiles compatible with the specified machines
   */
  async getProcessProfilesForMachines(
    machineNames: string[],
    engineVersion?: string,
    view?: 'summary',
  ): Promise<OrcaProcessProfile[]> {
    const params = new URLSearchParams();
    if (engineVersion) params.set('slicerEngineVersion', engineVersion);
    if (view) params.set('view', view);
    const qs = params.size > 0 ? `?${params.toString()}` : '';
    const res = await apiClient.post<OrcaProcessProfile[]>(
      `/slicer/profiles/process/for-machines${qs}`,
      { machineNames } as ForMachinesRequest
    );
    return res.data;
  },

  /**
   * Get template filament profiles from OrcaFilamentLibrary.
   * These are universal profiles not tied to specific printers.
   * @returns Universal filament profiles
   */
  async getFilamentTemplates(): Promise<OrcaFilamentProfile[]> {
    const res = await apiClient.get<OrcaFilamentProfile[]>('/slicer/profiles/filament/templates');
    return res.data;
  },

  // === Custom Profile Management Methods (Phase 2) ===
  // These methods manage user-owned custom profiles stored in the database

  /**
   * Clone a single profile to create a user-owned custom copy.
   * @param request - Clone request with source ID, type, and optional name
   * @returns The newly created custom profile
   */
  async cloneProfile(request: CloneSingleProfileRequest): Promise<CloneSingleProfileResponse> {
    const res = await apiClient.post<CloneSingleProfileResponse>('/slicer/profiles/clone', request);
    return res.data;
  },

  /**
   * Resolve a library/default profile's canonical database GUID for a printer model,
   * auto-importing it from the OrcaSlicer worker catalog if it has never been imported
   * before (#2443). Required before calling `cloneProfile` for a profile whose only
   * client-known identifier is its display name (e.g. a default OrcaSlicer process
   * profile), since `cloneProfile`'s `sourceProfileId` must be a database GUID.
   */
  async resolveProfileForModel(
    modelId: string,
    request: ResolveProfileForModelRequest
  ): Promise<ResolveProfileForModelResult> {
    const res = await apiClient.post<ResolveProfileForModelResult>(
      `/slicer/profiles/resolve-for-model/${modelId}`,
      request
    );
    return res.data;
  },

  async cloneFamily(request: CloneProfileFamilyRequest): Promise<CloneProfileFamilyResponse> {
    const res = await apiClient.post<CloneProfileFamilyResponse>('/slicer/profiles/clone-family', request);
    return res.data;
  },

  /**
   * Upload a custom profile from raw JSON content.
   * @param request - Upload request with raw JSON, type, and optional name
   * @returns The created custom profile
   */
  async uploadProfile(request: UploadProfileRequest): Promise<CustomProfile> {
    const res = await apiClient.post<CustomProfile>('/slicer/profiles/upload', request);
    return res.data;
  },

  /**
   * List all custom profiles owned by the current user.
   * @returns List of custom profiles with summary counts
   */
  async listCustomProfiles(): Promise<CustomProfilesListResponse> {
    const res = await apiClient.get<CustomProfilesListResponse>('/slicer/profiles/custom');
    return res.data;
  },

  /**
   * Update a custom profile's properties.
   * @param id - Profile ID to update
   * @param request - Update request with optional name, rawJson, or description
   * @returns The updated custom profile
   */
  async updateCustomProfile(id: string, request: UpdateCustomProfileRequest): Promise<CustomProfile> {
    const res = await apiClient.put<CustomProfile>(`/slicer/profiles/custom/${id}`, request);
    return res.data;
  },

  /**
   * Delete a custom profile.
   * Uses the existing bulk delete endpoint with a single ID.
   * @param id - Profile ID to delete
   */
  async deleteCustomProfile(id: string): Promise<void> {
    await apiClient.post<BulkDeleteResultDto>('/slicer/profiles/bulk-delete', [id]);
  },

  // === Hybrid Architecture Methods (Phase 3) ===
  // These methods support the hybrid architecture where system profiles come from
  // OrcaSlicer worker and custom profiles come from the database.

  /**
   * Fetch the complete profile hierarchy from OrcaSlicer worker.
   * Returns system profiles directly from the worker without database storage.
   * @returns Worker hierarchy with system profiles organized by manufacturer and model
   */
  async getWorkerHierarchy(): Promise<WorkerHierarchyResponse> {
    const res = await apiClient.get<WorkerHierarchyResponse>('/slicer/profiles/worker-hierarchy');
    return res.data;
  },

  async getLibraryHierarchy(scope: 'all' | 'catalog'): Promise<WorkerHierarchyResponse> {
    const res = await apiClient.get<WorkerHierarchyResponse>(
      `/slicer/profiles/library-hierarchy?scope=${scope}`,
    );
    return res.data;
  },

  /**
   * Fetch the profile hierarchy from OrcaSlicer worker filtered to only include
   * manufacturers present in the PrintFarmer catalog.
   * Used by the Slicer Profiles management page for parity with the Slicer page.
   * @returns Worker hierarchy filtered to catalog manufacturers
   */
  async getCatalogFilteredHierarchy(): Promise<WorkerHierarchyResponse> {
    const res = await apiClient.get<WorkerHierarchyResponse>('/slicer/profiles/catalog-hierarchy');
    return res.data;
  },

  /**
   * Delete all system profiles (IsSystem=true) from the database.
   * Phase 3 cleanup: After calling this, system profiles are only served from OrcaSlicer worker.
   * Requires admin authorization.
   * @returns Counts of deleted profiles
   */
  async deleteAllSystemProfiles(): Promise<DeleteSystemProfilesResult> {
    const res = await apiClient.delete<DeleteSystemProfilesResult>('/slicer/profiles/system/cleanup');
    return res.data;
  }
};
