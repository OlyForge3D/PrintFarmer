import { apiClient } from '@/services/api';
import type {
  BulkImportResponse,
  CommandResult,
  CreatePrinterDto,
  DiscoveredPrinterDto,
  HistoryJob,
  HistoryListResponse,
  HistoryTotals,
  MoveRequest,
  Printer,
  PrinterBackendCapabilitiesDto,
  PrinterCameraUrlResult,
  PrinterCameraUrls,
  PrinterDetails,
  PrinterFast,
  PrinterFileDto,
  PrinterVersionInfo,
  PrintJobObjectListDto,
  PrintJobStatusDto,
  SpoolmanSpool,
  StartDiscoveryRequest,
  TempTargets,
  TestConnectionRequest,
  TestConnectionResponse,
  UpdatePrinterDto,
} from '@/types/api';

/**
 * Printer service — CRUD, control, discovery, history, and file operations.
 * Delegates to the apiClient singleton which handles auth, correlation IDs,
 * and error handling automatically.
 */
export const printerService = {
  // ── CRUD ──────────────────────────────────────────────────────────────

  async getPrinters(includeDisabled?: boolean): Promise<Printer[]> {
    return apiClient.getPrinters(includeDisabled);
  },

  async getPrintersFast(includeDisabled?: boolean): Promise<PrinterFast[]> {
    return apiClient.getPrintersFast(includeDisabled);
  },

  async getPrinter(id: string): Promise<Printer> {
    return apiClient.getPrinter(id);
  },

  async getPrinterDetails(id: string): Promise<PrinterDetails> {
    return apiClient.getPrinterDetails(id);
  },

  async getPrinterVersionInfo(printerId: string): Promise<PrinterVersionInfo> {
    return apiClient.getPrinterVersionInfo(printerId);
  },

  async createPrinter(printer: CreatePrinterDto): Promise<Printer> {
    return apiClient.createPrinter(printer);
  },

  async bulkCreatePrinters(
    printers: CreatePrinterDto[],
    options?: { duplicateHandling?: string }
  ): Promise<BulkImportResponse> {
    return apiClient.bulkCreatePrinters(printers, options);
  },

  async updatePrinter(id: string, printer: UpdatePrinterDto): Promise<Printer> {
    return apiClient.updatePrinter(id, printer);
  },

  async deletePrinter(id: string): Promise<void> {
    return apiClient.deletePrinter(id);
  },

  async importPrinters(printers: Record<string, unknown>[]): Promise<Record<string, unknown>> {
    return apiClient.importPrinters(printers);
  },

  async uploadPrinterImport(formData: FormData): Promise<void> {
    return apiClient.uploadPrinterImport(formData);
  },

  // ── Export ─────────────────────────────────────────────────────────────

  async exportPrintersByIds(
    ids?: string[]
  ): Promise<import('@/types/api').PrinterWithCapabilitiesDto[]> {
    return apiClient.exportPrintersByIds(ids);
  },

  async streamExportFile(
    ids?: string[],
    format: 'json' | 'csv' = 'json',
    filename?: string,
    onProgress?: (loaded: number, total?: number) => void
  ): Promise<void> {
    return apiClient.streamExportFile(ids, format, filename, onProgress);
  },

  // ── Connection & Discovery ────────────────────────────────────────────

  async testConnection(request: TestConnectionRequest): Promise<TestConnectionResponse> {
    return apiClient.testConnection(request);
  },

  async discoverPrinters(): Promise<DiscoveredPrinterDto[]> {
    return apiClient.discoverPrinters();
  },

  async startDiscoveryStream(
    request?: StartDiscoveryRequest
  ): Promise<{ sessionId: string; message: string }> {
    return apiClient.startDiscoveryStream(request);
  },

  async cancelDiscoveryStream(sessionId: string): Promise<{ message: string }> {
    return apiClient.cancelDiscoveryStream(sessionId);
  },

  // ── Camera ────────────────────────────────────────────────────────────

  async getPrinterCameraUrls(): Promise<PrinterCameraUrls[]> {
    return apiClient.getPrinterCameraUrls();
  },

  async getPrinterCameraUrl(id: string): Promise<PrinterCameraUrlResult> {
    return apiClient.getPrinterCameraUrl(id);
  },

  async getPrinterSnapshot(id: string): Promise<Blob> {
    return apiClient.getPrinterSnapshot(id);
  },

  async refreshCameraUrls(id: string): Promise<Printer> {
    return apiClient.refreshCameraUrls(id);
  },

  // ── Backend Capabilities ──────────────────────────────────────────────

  async getPrinterBackendCapabilities(): Promise<PrinterBackendCapabilitiesDto[]> {
    return apiClient.getPrinterBackendCapabilities();
  },

  async getPrinterBackendCapabilitiesSingle(printerId: string): Promise<PrinterBackendCapabilitiesDto> {
    return apiClient.getPrinterBackendCapabilitiesSingle(printerId);
  },

  // ── Temperature & Movement ────────────────────────────────────────────

  async setTemperatures(printerId: string, targets: TempTargets): Promise<CommandResult> {
    return apiClient.setTemperatures(printerId, targets);
  },

  async movePrinter(printerId: string, move: MoveRequest): Promise<CommandResult> {
    return apiClient.movePrinter(printerId, move);
  },

  async movePrinterTo(printerId: string, position: MoveRequest): Promise<CommandResult> {
    return apiClient.movePrinterTo(printerId, position);
  },

  async homePrinter(printerId: string): Promise<CommandResult> {
    return apiClient.homePrinter(printerId);
  },

  async homeXY(printerId: string): Promise<CommandResult> {
    return apiClient.homeXY(printerId);
  },

  async homeZ(printerId: string): Promise<CommandResult> {
    return apiClient.homeZ(printerId);
  },

  // ── Print Control ─────────────────────────────────────────────────────

  async pausePrint(printerId: string): Promise<CommandResult> {
    return apiClient.pausePrint(printerId);
  },

  async resumePrint(printerId: string): Promise<CommandResult> {
    return apiClient.resumePrint(printerId);
  },

  async cancelPrint(printerId: string): Promise<CommandResult> {
    return apiClient.cancelPrint(printerId);
  },

  async emergencyStop(printerId: string): Promise<CommandResult> {
    return apiClient.emergencyStop(printerId);
  },

  async firmwareRestart(printerId: string): Promise<CommandResult> {
    return apiClient.firmwareRestart(printerId);
  },

  async disableMotors(printerId: string): Promise<CommandResult> {
    return apiClient.disableMotors(printerId);
  },

  async getPrintJobObjects(printerId: string): Promise<PrintJobObjectListDto> {
    return apiClient.getPrintJobObjects(printerId);
  },

  async excludePrintJobObject(printerId: string, name: string): Promise<CommandResult> {
    return apiClient.excludePrintJobObject(printerId, name);
  },

  // ── Filament ──────────────────────────────────────────────────────────

  async loadFilament(printerId: string): Promise<CommandResult> {
    return apiClient.loadFilament(printerId);
  },

  async unloadFilament(printerId: string): Promise<CommandResult> {
    return apiClient.unloadFilament(printerId);
  },

  async changeFilament(printerId: string): Promise<CommandResult> {
    return apiClient.changeFilament(printerId);
  },

  // ── MMU Commands ──────────────────────────────────────────────────────

  async mmuChangeTool(printerId: string, tool: number): Promise<CommandResult> {
    return apiClient.mmuChangeTool(printerId, tool);
  },

  async mmuEject(printerId: string): Promise<CommandResult> {
    return apiClient.mmuEject(printerId);
  },

  async mmuLoad(printerId: string): Promise<CommandResult> {
    return apiClient.mmuLoad(printerId);
  },

  async mmuHome(printerId: string): Promise<CommandResult> {
    return apiClient.mmuHome(printerId);
  },

  async mmuSelectTool(printerId: string, tool: number): Promise<CommandResult> {
    return apiClient.mmuSelectTool(printerId, tool);
  },

  async mmuRecover(printerId: string): Promise<CommandResult> {
    return apiClient.mmuRecover(printerId);
  },

  // ── G-code & Spoolman ─────────────────────────────────────────────────

  async sendGcode(printerId: string, command: string): Promise<CommandResult> {
    return apiClient.sendGcode(printerId, command);
  },

  async setActiveSpool(printerId: string, spoolId: number): Promise<boolean> {
    return apiClient.setActiveSpool(printerId, spoolId);
  },

  async clearActiveSpool(printerId: string): Promise<boolean> {
    return apiClient.clearActiveSpool(printerId);
  },

  async getPrinterSpools(printerId: string): Promise<SpoolmanSpool[]> {
    return apiClient.getPrinterSpools(printerId);
  },

  // ── History ───────────────────────────────────────────────────────────

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
    return apiClient.getPrinterHistory(printerId, options);
  },

  async getPrinterHistoryJob(printerId: string, jobId: string): Promise<HistoryJob> {
    return apiClient.getPrinterHistoryJob(printerId, jobId);
  },

  async getPrinterHistoryTotals(printerId: string): Promise<HistoryTotals> {
    return apiClient.getPrinterHistoryTotals(printerId);
  },

  // ── Printer Files ─────────────────────────────────────────────────────

  async getPrinterFileList(printerId: string): Promise<PrinterFileDto[]> {
    return apiClient.getPrinterFileList(printerId);
  },

  async uploadGcodeToPrinter(printerId: string, file: File): Promise<boolean> {
    return apiClient.uploadGcodeToPrinter(printerId, file);
  },

  async startPrintFromFile(printerId: string, fileName: string): Promise<boolean> {
    return apiClient.startPrintFromFile(printerId, fileName);
  },

  async deletePrinterFile(printerId: string, fileName: string): Promise<boolean> {
    return apiClient.deletePrinterFile(printerId, fileName);
  },

  // ── Print Job Status ──────────────────────────────────────────────────

  async getPrintJobStatus(printerId: string): Promise<PrintJobStatusDto | null> {
    return apiClient.getPrintJobStatus(printerId);
  },

  // ── Maintenance ───────────────────────────────────────────────────────

  async getPrinterMaintenance(printerId: string): Promise<Record<string, unknown>> {
    return apiClient.getPrinterMaintenance(printerId);
  },

  async updatePrinterMaintenance(
    printerId: string,
    maintenance: Record<string, unknown>
  ): Promise<Record<string, unknown>> {
    return apiClient.updatePrinterMaintenance(printerId, maintenance);
  },

  async setPrinterMaintenance(
    printerId: string,
    inMaintenance: boolean
  ): Promise<Record<string, unknown>> {
    return apiClient.setPrinterMaintenance(printerId, inMaintenance);
  },
};
