// Service for admin data export/import operations
import { apiClient } from './api';
import type { 
  CatalogExportDto, 
  FullBackupExportDto, 
  ImportResponseDto, 
  ImportMode,
  ImportRequest,
  CatalogVersionDto,
  CatalogUpdateCheckResult,
  CatalogUpdateApplyResult,
} from '@/types/adminData';

/**
 * Export catalog data (manufacturers, models, components) as JSON
 */
export async function exportCatalog(): Promise<CatalogExportDto> {
  const response = await apiClient.get<CatalogExportDto>('/admin/data/export/catalog');
  return response.data;
}

/**
 * Export printer configurations only
 */
export async function exportPrinters(): Promise<unknown[]> {
  const response = await apiClient.get<unknown[]>('/admin/data/export/printers');
  return response.data;
}

/**
 * Export full backup (catalog + printers + locations)
 */
export async function exportFull(): Promise<FullBackupExportDto> {
  const response = await apiClient.get<FullBackupExportDto>('/admin/data/export/full');
  return response.data;
}

/**
 * Import catalog data with specified mode
 * @param catalog - Catalog data to import
 * @param mode - Import mode (Merge or Replace)
 */
export async function importCatalog(
  catalog: CatalogExportDto, 
  mode: ImportMode = 0
): Promise<ImportResponseDto> {
  const request: ImportRequest = { catalog, mode };
  const response = await apiClient.post<ImportResponseDto>(
    '/admin/data/import/catalog',
    request
  );
  return response.data;
}

/**
 * Import full backup with specified mode
 * @param backup - Full backup data to import
 * @param mode - Import mode (Merge or Replace)
 */
export async function importFull(
  backup: FullBackupExportDto,
  mode: ImportMode = 0
): Promise<ImportResponseDto> {
  const request: ImportRequest = { backup, mode };
  const response = await apiClient.post<ImportResponseDto>(
    '/admin/data/import/full',
    request
  );
  return response.data;
}

/**
 * Reload seed data from YAML files
 */
export async function reloadSeed(): Promise<{ success: boolean; message: string }> {
  const response = await apiClient.post<{ success: boolean; message: string }>(
    '/admin/data/seed/reload'
  );
  return response.data;
}

/**
 * Download export as JSON file
 * @param data - Data to export
 * @param filename - Filename for the download
 */
export function downloadAsJson(data: unknown, filename: string): void {
  const json = JSON.stringify(data, null, 2);
  const blob = new Blob([json], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

/**
 * Generate a timestamped filename
 * @param type - Type of export (catalog, printers, full)
 */
export function generateExportFilename(type: 'catalog' | 'printers' | 'full'): string {
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  return `printfarmer-${type}-${timestamp}.json`;
}

// --- Catalog Update API ---

/**
 * Get the currently applied catalog version
 */
export async function getCatalogVersion(): Promise<CatalogVersionDto | null> {
  const response = await apiClient.get<CatalogVersionDto | null>('/admin/catalog/version');
  return response.data;
}

/**
 * Check whether a catalog update is available from the remote repository
 */
export async function checkCatalogUpdates(): Promise<CatalogUpdateCheckResult> {
  const response = await apiClient.get<CatalogUpdateCheckResult>('/admin/catalog/updates/check');
  return response.data;
}

/**
 * Apply available catalog updates from the remote repository
 */
export async function applyCatalogUpdates(): Promise<CatalogUpdateApplyResult> {
  const response = await apiClient.post<CatalogUpdateApplyResult>('/admin/catalog/updates/apply');
  return response.data;
}
