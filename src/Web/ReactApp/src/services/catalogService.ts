import { apiClient } from '@/services/api';
import type {
  CatalogContext,
  CreateExtruderModelDto,
  CreateFilamentTypeRequest,
  CreateHotendModelDto,
  CreateNozzleModelDto,
  CreateToolheadModelDto,
  ExtruderModelDefinition,
  FilamentCsvImportResult,
  FilamentPresets,
  FilamentTypeDto,
  HotendModelDefinition,
  ManufacturerDto,
  ManufacturersByContext,
  NozzleModelDefinition,
  OfdBrand,
  OfdBrandDetail,
  OfdFlattenedEntry,
  OfdImportRequest,
  OfdImportResult,
  PagedResponse,
  PrinterCapabilitiesDto,
  PrinterModelDto,
  SlicerModelAliasDto,
  SpoolmanDbFilamentEntry,
  SpoolmanDbImportRequest,
  SpoolmanDbImportResult,
  SpoolmanDbMaterialEntry,
  SpoolmanFilamentImportResult,
  ToolheadModelDefinition,
  UpdateExtruderModelDto,
  UpdateFilamentTypeRequest,
  UpdateHotendModelDto,
  UpdateModelAliasesRequest,
  UpdateModelRequest,
  UpdateNozzleModelDto,
  UpdateToolheadModelDefDto,
} from '@/types/api';

/**
 * Catalog service — manufacturers, printer models, component models,
 * filament types, and external material databases.
 * Delegates to the apiClient singleton which handles auth, correlation IDs,
 * and error handling automatically.
 */
export const catalogService = {
  // ── Manufacturers ─────────────────────────────────────────────────────

  async getManufacturers(): Promise<ManufacturerDto[]> {
    return apiClient.getManufacturers();
  },

  async createManufacturer(name: string, url?: string, description?: string): Promise<ManufacturerDto> {
    return apiClient.createManufacturer(name, url, description);
  },

  async updateManufacturer(id: string, name: string): Promise<ManufacturerDto> {
    return apiClient.updateManufacturer(id, name);
  },

  async deleteManufacturer(id: string): Promise<void> {
    return apiClient.deleteManufacturer(id);
  },

  async getManufacturersByContext(context: CatalogContext): Promise<ManufacturersByContext> {
    return apiClient.getManufacturersByContext(context);
  },

  // ── Printer Models ────────────────────────────────────────────────────

  async getModels(manufacturerId?: string): Promise<PrinterModelDto[]> {
    return apiClient.getModels(manufacturerId);
  },

  async createModel(model: Omit<PrinterModelDto, 'id'>): Promise<PrinterModelDto> {
    return apiClient.createModel(model);
  },

  async updateModel(id: string, request: UpdateModelRequest): Promise<PrinterModelDto> {
    return apiClient.updateModel(id, request);
  },

  async updateModelName(id: string, name: string): Promise<PrinterModelDto> {
    return apiClient.updateModelName(id, name);
  },

  async deleteModel(id: string): Promise<void> {
    return apiClient.deleteModel(id);
  },

  async getModelAliases(modelId: string): Promise<SlicerModelAliasDto[]> {
    return apiClient.getModelAliases(modelId);
  },

  async updateModelAliases(
    modelId: string,
    request: UpdateModelAliasesRequest
  ): Promise<SlicerModelAliasDto[]> {
    return apiClient.updateModelAliases(modelId, request);
  },

  async getModelDefaultCapabilities(modelId: string): Promise<PrinterCapabilitiesDto | null> {
    return apiClient.getModelDefaultCapabilities(modelId);
  },

  async getCatalogPrinterModels(manufacturerId?: string): Promise<Record<string, unknown>> {
    return apiClient.getCatalogPrinterModels(manufacturerId);
  },

  async getCatalogPrinterModel(id: string): Promise<Record<string, unknown>> {
    return apiClient.getCatalogPrinterModel(id);
  },

  // ── Component Models (Hotends, Extruders, Toolheads, Nozzles) ────────

  async getHotendModels(): Promise<HotendModelDefinition[]> {
    return apiClient.getHotendModels();
  },

  async createHotendModel(dto: CreateHotendModelDto): Promise<HotendModelDefinition> {
    return apiClient.createHotendModel(dto);
  },

  async updateHotendModel(id: string, dto: UpdateHotendModelDto): Promise<HotendModelDefinition | null> {
    return apiClient.updateHotendModel(id, dto);
  },

  async deleteHotendModel(id: string): Promise<void> {
    return apiClient.deleteHotendModel(id);
  },

  async getExtruderModels(): Promise<ExtruderModelDefinition[]> {
    return apiClient.getExtruderModels();
  },

  async createExtruderModel(dto: CreateExtruderModelDto): Promise<ExtruderModelDefinition> {
    return apiClient.createExtruderModel(dto);
  },

  async updateExtruderModel(id: string, dto: UpdateExtruderModelDto): Promise<ExtruderModelDefinition | null> {
    return apiClient.updateExtruderModel(id, dto);
  },

  async deleteExtruderModel(id: string): Promise<void> {
    return apiClient.deleteExtruderModel(id);
  },

  async getToolheadModels(): Promise<ToolheadModelDefinition[]> {
    return apiClient.getToolheadModels();
  },

  async createToolheadModel(dto: CreateToolheadModelDto): Promise<ToolheadModelDefinition> {
    return apiClient.createToolheadModel(dto);
  },

  async updateToolheadModel(id: string, dto: UpdateToolheadModelDefDto): Promise<ToolheadModelDefinition | null> {
    return apiClient.updateToolheadModel(id, dto);
  },

  async deleteToolheadModel(id: string): Promise<void> {
    return apiClient.deleteToolheadModel(id);
  },

  async getNozzleModels(): Promise<NozzleModelDefinition[]> {
    return apiClient.getNozzleModels();
  },

  async createNozzleModel(dto: CreateNozzleModelDto): Promise<NozzleModelDefinition> {
    return apiClient.createNozzleModel(dto);
  },

  async updateNozzleModel(id: string, dto: UpdateNozzleModelDto): Promise<NozzleModelDefinition | null> {
    return apiClient.updateNozzleModel(id, dto);
  },

  async deleteNozzleModel(id: string): Promise<void> {
    return apiClient.deleteNozzleModel(id);
  },

  // ── Filament Types ────────────────────────────────────────────────────

  async getFilamentTypes(): Promise<FilamentTypeDto[]> {
    return apiClient.getFilamentTypes();
  },

  async getFilamentTypesPaged(
    page?: number,
    pageSize?: number,
    search?: string
  ): Promise<PagedResponse<FilamentTypeDto>> {
    return apiClient.getFilamentTypesPaged(page, pageSize, search);
  },

  async createFilamentType(filamentType: CreateFilamentTypeRequest): Promise<FilamentTypeDto> {
    return apiClient.createFilamentType(filamentType);
  },

  async updateFilamentType(id: string, filamentType: UpdateFilamentTypeRequest): Promise<void> {
    return apiClient.updateFilamentType(id, filamentType);
  },

  async deleteFilamentType(id: string): Promise<void> {
    return apiClient.deleteFilamentType(id);
  },

  async getFilamentPresets(): Promise<FilamentPresets> {
    return apiClient.getFilamentPresets();
  },

  async saveFilamentPresets(presets: FilamentPresets): Promise<void> {
    return apiClient.saveFilamentPresets(presets);
  },

  async getFilamentTypePresets(): Promise<Record<string, unknown>> {
    return apiClient.getFilamentTypePresets();
  },

  // ── Filament Import/Export ────────────────────────────────────────────

  async importFilamentTypesFromSpoolman(): Promise<SpoolmanFilamentImportResult> {
    return apiClient.importFilamentTypesFromSpoolman();
  },

  async exportFilamentTypesCsv(): Promise<Blob> {
    return apiClient.exportFilamentTypesCsv();
  },

  async importFilamentTypesCsv(file: File): Promise<FilamentCsvImportResult> {
    return apiClient.importFilamentTypesCsv(file);
  },

  // ── SpoolmanDB ────────────────────────────────────────────────────────

  async getSpoolmanDbFilaments(): Promise<SpoolmanDbFilamentEntry[]> {
    return apiClient.getSpoolmanDbFilaments();
  },

  async getSpoolmanDbMaterials(): Promise<SpoolmanDbMaterialEntry[]> {
    return apiClient.getSpoolmanDbMaterials();
  },

  async importFromSpoolmanDb(request: SpoolmanDbImportRequest): Promise<SpoolmanDbImportResult> {
    return apiClient.importFromSpoolmanDb(request);
  },

  async syncExternalMaterials(): Promise<SpoolmanDbImportResult> {
    return apiClient.syncExternalMaterials();
  },

  // ── Open Filament Database ────────────────────────────────────────────

  async getOfdBrands(): Promise<OfdBrand[]> {
    return apiClient.getOfdBrands();
  },

  async getOfdBrandMaterials(brandSlug: string): Promise<OfdBrandDetail> {
    return apiClient.getOfdBrandMaterials(brandSlug);
  },

  async getOfdFilaments(
    brandSlug: string,
    materialSlug: string,
    brandName: string,
    materialName: string
  ): Promise<OfdFlattenedEntry[]> {
    return apiClient.getOfdFilaments(brandSlug, materialSlug, brandName, materialName);
  },

  async importFromOfd(request: OfdImportRequest): Promise<OfdImportResult> {
    return apiClient.importFromOfd(request);
  },
};
