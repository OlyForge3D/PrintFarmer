import React from 'react';
import { usePrintersWithCameraUrls, queryKeys } from '@/hooks/useApi';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { printerHubService } from '@/services/printerHubService';
import { getPrinterBackendName } from '@/utils/enumHelpers';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { PrinterDiscoveryModal } from '@/components/PrinterDiscoveryModal';
import { EditPrinterModal } from '@/components/EditPrinterModal';
import { DeleteConfirmationModal } from '@/components/DeleteConfirmationModal';
import { ImportResultsModal } from '@/components/ImportResultsModal';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { PageTemplate } from '@/components/PageTemplate';
import { toast } from 'sonner';
import { DeleteIcon, EditIcon, CheckCircleIcon, CircleIcon } from '@/components/icons/MdiIcons';
import { Alert, Button, Checkbox, Label, Select, Tooltip } from '@/components/ui';
import type { Printer, UpdatePrinterDto } from '@/types/api';

export function PrintersAdminPage() {
  const queryClient = useQueryClient();
  const { data: printers, isLoading, error, refetch } = usePrintersWithCameraUrls(true);
  type PreviewItem = {
    __index: number;
    raw: Record<string, unknown>;
    name: string;
    serverUrl: string;
    backend: number;
    apiKey?: string | undefined;
    notes?: string | undefined;
    manufacturerId?: string | undefined;
    modelId?: string | undefined;
    manufacturerName?: string | undefined;
    modelName?: string | undefined;
    valid: boolean;
  };

  const [previewItems, setPreviewItems] = React.useState<PreviewItem[] | null>(null);
  const [importing, setImporting] = React.useState<boolean>(false);
  const [duplicateHandling, setDuplicateHandling] = React.useState<'skip' | 'overwrite' | 'rename'>('skip');
  const [importResults, setImportResults] = React.useState<import('@/types/api').BulkImportResultItem[] | null>(null);
  const [importStats, setImportStats] = React.useState<{ imported: number; skipped: number; failed: number }>({ imported: 0, skipped: 0, failed: 0 });
  const [showImportResults, setShowImportResults] = React.useState(false);
  const [retryingIndex, setRetryingIndex] = React.useState<number | null>(null);
  const [selectedIds, setSelectedIds] = React.useState<string[]>([]);
  const [showExportOptions, setShowExportOptions] = React.useState(false);
  const [exportProgress, setExportProgress] = React.useState<number | null>(null);
  const [exporting, setExporting] = React.useState<boolean>(false);
  const [showDiscovery, setShowDiscovery] = React.useState(false);
  const [discoveryAvailable, setDiscoveryAvailable] = React.useState(false);
  const [bulkManufacturerId, setBulkManufacturerId] = React.useState<string>('');
  const [bulkModelId, setBulkModelId] = React.useState<string>('');
  const [bulkOperation, setBulkOperation] = React.useState<'none' | 'enable' | 'disable' | 'delete'>('none');
  const [bulkOperating, setBulkOperating] = React.useState(false);
  const [editPrinterId, setEditPrinterId] = React.useState<string | null>(null);
  const [isEditModalOpen, setIsEditModalOpen] = React.useState(false);
  const [togglingEnabledId, setTogglingEnabledId] = React.useState<string | null>(null);
  const [isRefreshingCapabilities, setIsRefreshingCapabilities] = React.useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = React.useState<{
    isOpen: boolean;
    printers: Printer[];
  }>({ isOpen: false, printers: [] });

  // Check if discovery service is available
  React.useEffect(() => {
    const checkDiscoveryAvailability = async () => {
      try {
        // Fetch network discovery settings
        const settings = await apiClient.getSettings<import('@/types/NetworkDiscoverySettings').NetworkDiscoverySettings>('NetworkDiscovery');
        if (window.PrintFarmerDebug?.discovery) {
          console.log('Discovery settings:', settings);
        }
        
        // Discovery is available if:
        // 1. EnableDiscovery is true AND
        // 2. LastHeartbeat is recent (within 60 seconds) - confirms service is actually running
        const isEnabled = settings?.enableDiscovery === true;
        const hasRecentHeartbeat = settings?.lastHeartbeat 
          ? new Date().getTime() - new Date(settings.lastHeartbeat).getTime() < 60000 // 60 seconds
          : false;
        
        if (window.PrintFarmerDebug?.discovery) {
          console.log('Discovery check:', { isEnabled, lastHeartbeat: settings?.lastHeartbeat, hasRecentHeartbeat });
        }
        setDiscoveryAvailable(isEnabled && hasRecentHeartbeat);
        
        if (isEnabled && !hasRecentHeartbeat) {
          console.warn('Discovery is enabled but service is not responding to heartbeats');
        }
      } catch (error) {
        // If error, discovery is likely not available
        console.error('Failed to check discovery availability:', error);
        setDiscoveryAvailable(false);
      }
    };

    // Check immediately on mount
    checkDiscoveryAvailability();
    
    // Re-check every 30 seconds to catch service state changes
    const interval = setInterval(checkDiscoveryAvailability, 30000);
    
    return () => clearInterval(interval);
  }, []);
  React.useEffect(() => {
    let unsubscribe: (() => void) | null = null;

    const setupSignalR = async () => {
      try {
        if (!printerHubService.isConnected()) {
          await printerHubService.start();
        }

        // Subscribe to import progress updates
        unsubscribe = printerHubService.onPrinterImportProgress((progress) => {
          setImportResults(prev => {
            if (!prev) return [progress];
            // Update existing result or add new one
            const existing = prev.findIndex(r => r.index === progress.index);
            if (existing >= 0) {
              const updated = [...prev];
              updated[existing] = progress;
              return updated;
            }
            return [...prev, progress];
          });
        });
      } catch (error) {
        console.error('Failed to set up PrinterHub:', error);
      }
    };

    setupSignalR();

    return () => {
      if (unsubscribe) {
        unsubscribe();
      }
    };
  }, []);

  const handleExport = () => {
    if (!printers || !printers.length) {
      toast('No printers to export');
      return;
    }
    // Show export option buttons (JSON/CSV)
    setShowExportOptions(true);
  };

  const exportSelectedServerJson = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const ids = selectedIds.length > 0 ? selectedIds : printers.map(p => p.id);
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.json`;
    try {
      setExporting(true);
      setExportProgress(0);
      await apiClient.streamExportFile(ids, 'json', filename, (loaded, total) => {
        if (total && total > 0) setExportProgress(Math.round((loaded / total) * 100));
        else setExportProgress(null);
      });
      toast.success('Printers exported (JSON)');
    } catch (err) {
      console.error('Export failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
      setExportProgress(null);
      setExporting(false);
    }
  };

  const exportSelectedServerCsv = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const ids = selectedIds.length > 0 ? selectedIds : printers.map(p => p.id);
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.csv`;
    try {
      setExporting(true);
      setExportProgress(0);
      await apiClient.streamExportFile(ids, 'csv', filename, (loaded, total) => {
        if (total && total > 0) setExportProgress(Math.round((loaded / total) * 100));
        else setExportProgress(null);
      });
      toast.success('Printers exported (CSV)');
    } catch (err) {
      console.error('Export CSV failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
      setExportProgress(null);
      setExporting(false);
    }
  };

  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const fileInputId = React.useId();

  const handleImportClick = () => {
    fileInputRef.current?.click();
  };

  const handleFile = async (file?: File) => {
    try {
      const f = file || (fileInputRef.current?.files ? fileInputRef.current.files[0] : undefined);
      if (!f) return;

      // Validate file type
      const extension = f.name.split('.').pop()?.toLowerCase();
      if (!['csv', 'json'].includes(extension || '')) {
        toast.error('File must be CSV or JSON format');
        return;
      }

      // Send file to backend for parsing and import
      const formData = new FormData();
      formData.append('file', f);

      setImporting(true);
      const response = await fetch(`${getApiBaseUrl()}/printers/import`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: 'Unknown error' }));
        throw new Error(error.message || `HTTP ${response.status}`);
      }

      const result = await response.json();
      if (window.PrintFarmerDebug?.import) {
        console.log('[Import] Backend response:', result);
      }

      setImporting(false);
      
      // Refresh printers list to show newly imported printers
      await queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      
      const { importedCount, skippedCount, failureCount, results } = result;
      
      // Show results modal if there are any results
      if (results && results.length > 0) {
        const stats = {
          imported: importedCount,
          skipped: skippedCount,
          failed: failureCount
        };
        setImportStats(stats);
        setImportResults(results);
        setShowImportResults(true);
      }
      
      // Show toast notification
      if (failureCount > 0) {
        toast.error(`Import completed with ${failureCount} error${failureCount === 1 ? '' : 's'} - See modal for details`);
      } else if (importedCount > 0) {
        const summary = `Successfully imported ${importedCount} printer${importedCount === 1 ? '' : 's'}${skippedCount > 0 ? `, skipped ${skippedCount}` : ''}`;
        toast.success(summary);
      } else if (skippedCount > 0 && failureCount === 0) {
        toast.info(`No new printers imported (${skippedCount} skipped due to duplicates)`);
      }
    } catch (err) {
      console.error('[Import] Import failed', err);
      setImporting(false);
      toast.error(err instanceof Error ? err.message : 'Failed to import file');
    }
  };

  const handleConfirmImport = async () => {
  if (!previewItems || previewItems.length === 0) return;
    const toImport = previewItems.filter(i => i.valid);
    if (toImport.length === 0) {
      toast.error('No valid printers to import');
      return;
    }
    
    // Initialize all items with "Pending" status
    const pendingResults = toImport.map((item) => ({
      index: item.__index,
      name: item.name,
      status: 'Pending' as const
    }));
    setImportResults(pendingResults);
    setImporting(true);
    
    try {
      // Prefer server-side bulk endpoint for better validation and per-item errors
      const dtos = toImport.map(i => ({
        name: i.name,
        serverUrl: i.serverUrl,
        backend: i.backend,
        apiKey: i.apiKey,
        notes: i.notes,
        manufacturerId: i.manufacturerId,
        modelId: i.modelId,
        newManufacturerName: i.manufacturerName,
        newModelName: i.modelName
      }));

      const resp = await apiClient.bulkCreatePrinters(dtos, { duplicateHandling });
      // Map results back to preview item indices
      const mappedResults = resp.results?.map((r, idx) => ({ ...r, index: toImport[idx].__index })) || [];
      setImportResults(mappedResults);
      // Keep previewItems so admin can review results
      
      // Show detailed feedback based on results
      const { importedCount = 0, skippedCount = 0, failureCount = 0 } = resp;
      
      setImportStats({ imported: importedCount, skipped: skippedCount, failed: failureCount });
      
      // Always show the results modal for visibility
      setShowImportResults(true);
      
      if (failureCount > 0) {
        toast.error(`Import completed with ${failureCount} error${failureCount === 1 ? '' : 's'} - see details below`);
      }
      
      if (importedCount > 0) {
        const summary = `Imported ${importedCount} printer${importedCount === 1 ? '' : 's'}${skippedCount > 0 ? `, skipped ${skippedCount}` : ''}`;
        toast.success(summary);
      } else if (skippedCount > 0 && failureCount === 0) {
        toast.info(`No new printers imported (${skippedCount} skipped due to duplicates)`);
      }
      
      // Invalidate printer queries globally so all pages (admin + main printers page) see new printers immediately
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      
      // Also refetch for immediate local update
      if (refetch) {
        await refetch();
      }
    } catch (err) {
      console.error('Batch import failed', err);
      toast.error('Import encountered errors');
    } finally {
      setImporting(false);
    }
  };

  const handleRetryRow = async (item: PreviewItem) => {
    // Retry a single row by sending a single-element bulk request so server returns same result shape
    setRetryingIndex(item.__index);
    try {
      const dto = {
        name: item.name,
        serverUrl: item.serverUrl,
        backend: item.backend,
        apiKey: item.apiKey,
        notes: item.notes,
        manufacturerId: item.manufacturerId,
        modelId: item.modelId,
        newManufacturerName: item.manufacturerName,
        newModelName: item.modelName
      };
  const resp = await apiClient.bulkCreatePrinters([dto], { duplicateHandling });
      // resp.results is array; server returns index relative to input (0). Map it back to the preview item's original index
      const singleResult = resp.results && resp.results.length > 0 ? { ...resp.results[0], index: item.__index } : undefined;
      setImportResults(prev => {
        const next = (prev || []).filter(r => r.index !== item.__index);
        return singleResult ? [...next, singleResult] : next;
      });
      
      // Invalidate printer queries globally so all pages see retried printer immediately
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      
      if (singleResult && singleResult.status === 'Success') {
        toast.success(`Imported ${singleResult.name}`);
      } else if (singleResult && singleResult.status === 'Skipped') {
        toast(`Skipped ${singleResult.name || 'row'}`);
      } else {
        toast.error(`Failed to import ${item.name || 'row'}`);
      }
    } catch (err) {
      console.error('Retry failed', err);
      toast.error('Retry failed');
    } finally {
      setRetryingIndex(null);
    }
  };

  const handleRetryAllFailed = async () => {
    if (!importResults || importResults.length === 0) {
      toast('No previous import results to retry');
      return;
    }
    // Find failed result indices and map back to preview items
    const failed = importResults.filter(r => r.status === 'Failed');
    if (failed.length === 0) {
      toast('No failed rows to retry');
      return;
    }

    const failedItems: PreviewItem[] = (previewItems || []).filter(pi => failed.some(f => f.index === pi.__index));
    if (failedItems.length === 0) {
      toast.error('Failed rows not present in current preview');
      return;
    }

    setImporting(true);
    try {
      const dtos = failedItems.map(i => ({
        name: i.name,
        serverUrl: i.serverUrl,
        backend: i.backend,
        apiKey: i.apiKey,
        notes: i.notes,
        manufacturerId: i.manufacturerId,
        modelId: i.modelId,
        newManufacturerName: i.manufacturerName,
        newModelName: i.modelName
      }));
  const resp = await apiClient.bulkCreatePrinters(dtos, { duplicateHandling });
      // Merge results: map resp.results (0..n) back to original indices
      const mapped = resp.results?.map((r, idx) => ({ ...r, index: failedItems[idx].__index })) || [];
      setImportResults(prev => {
        const others = (prev || []).filter(p => !mapped.some(m => m.index === p.index));
        return [...others, ...mapped];
      });
      toast.success(`Retried ${mapped.length} failed rows`);
      
      // Invalidate printer queries globally so all pages see retried printers immediately
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    } catch (err) {
      console.error('Retry all failed failed', err);
      toast.error('Retry all failed encountered an error');
    } finally {
      setImporting(false);
    }
  };

  const handleBulkUpdate = async () => {
    if (selectedIds.length === 0) {
      toast('No printers selected');
      return;
    }

    // For manufacturer/model updates, require at least one field to be set
    if (bulkOperation === 'none' && !bulkManufacturerId && !bulkModelId) {
      toast('Select an operation or at least one field to update');
      return;
    }

    // If delete is selected, show confirmation modal
    if (bulkOperation === 'delete') {
      const printersToDelete = (printers || []).filter(p => selectedIds.includes(p.id));
      setDeleteConfirmation({ isOpen: true, printers: printersToDelete });
      return;
    }

    setBulkOperating(true);
    try {
      let updated = 0;
      let failed = 0;

      for (const id of selectedIds) {
        try {
          const printer = printers?.find(p => p.id === id);
          if (!printer) continue;

          // Build update object with only the fields we need
          const updateData: UpdatePrinterDto = {
            backend: printer.backend,
            // Optional fields - only include if they have values
            ...(printer.notes !== undefined && { notes: printer.notes }),
            ...(bulkManufacturerId && { manufacturerId: bulkManufacturerId as unknown as string }),
            ...(!bulkManufacturerId && printer.manufacturerId && { manufacturerId: printer.manufacturerId }),
            ...(bulkModelId && { modelId: bulkModelId as unknown as string }),
            ...(!bulkModelId && printer.modelId && { modelId: printer.modelId }),
            ...(printer.apiKey && { apiKey: printer.apiKey }),
            ...(printer.originalServerUrl && { originalServerUrl: printer.originalServerUrl }),
          };

          // Add enabled status if changing
          if (bulkOperation === 'enable') {
            updateData.isEnabled = true;
          } else if (bulkOperation === 'disable') {
            updateData.isEnabled = false;
          }

          await apiClient.updatePrinter(id, updateData);
          updated++;
        } catch (err) {
          console.error(`Failed to update printer ${id}:`, err);
          failed++;
        }
      }

      const operationDesc = bulkOperation === 'enable' ? 'enabled' : bulkOperation === 'disable' ? 'disabled' : 'updated';
      toast.success(`${updated} printer${updated !== 1 ? 's' : ''} ${operationDesc}${failed > 0 ? ` (${failed} failed)` : ''}`);
      
      // Clear selections and reset form
      setSelectedIds([]);
      setBulkManufacturerId('');
      setBulkModelId('');
      setBulkOperation('none');
      
      // Refetch printers to show updates
      if (refetch) await refetch();
    } catch (err) {
      console.error('Bulk update failed', err);
      toast.error('Bulk update failed');
    } finally {
      setBulkOperating(false);
    }
  };

  const handleConfirmDelete = async () => {
    setBulkOperating(true);
    try {
      let deleted = 0;
      let failed = 0;

      for (const printer of deleteConfirmation.printers) {
        try {
          await apiClient.deletePrinter(printer.id);
          deleted++;
        } catch (err) {
          console.error(`Failed to delete printer ${printer.id}:`, err);
          failed++;
        }
      }

      toast.success(`Deleted ${deleted} printer${deleted !== 1 ? 's' : ''}${failed > 0 ? ` (${failed} failed)` : ''}`);
      
      // Clear selections
      setSelectedIds([]);
      setDeleteConfirmation({ isOpen: false, printers: [] });
      setBulkOperation('none');
      
      // Refetch printers to show updates
      if (refetch) await refetch();
    } catch (err) {
      console.error('Bulk delete failed', err);
      toast.error('Bulk delete failed');
    } finally {
      setBulkOperating(false);
    }
  };

  const handleCancelDelete = () => {
    setDeleteConfirmation({ isOpen: false, printers: [] });
  };

  const handleEditClick = (printer: Printer) => {
    setEditPrinterId(printer.id);
    setIsEditModalOpen(true);
  };

  const handleEditModalClose = () => {
    setIsEditModalOpen(false);
    setEditPrinterId(null);
  };

  const handleEditSuccess = async () => {
    setIsEditModalOpen(false);
    setEditPrinterId(null);
    await refetch?.();
  };

  const handleDeletePrinter = async (printer: Printer) => {
    if (!confirm(`Are you sure you want to delete "${printer.name}"?`)) return;
    try {
      await apiClient.deletePrinter(printer.id);
      toast.success(`Deleted ${printer.name}`);
      await refetch?.();
    } catch (error) {
      console.error('Failed to delete printer', error);
      toast.error('Failed to delete printer');
    }
  };

  const handleToggleEnabled = async (printer: Printer) => {
    const currentlyEnabled = printer.isEnabled ?? true;
    setTogglingEnabledId(printer.id);
    try {
      await apiClient.updatePrinter(printer.id, {
        name: printer.name,
        serverUrl: printer.serverUrl,
        notes: printer.notes,
        manufacturerId: printer.manufacturerId,
        modelId: printer.modelId,
        backend: printer.backend,
        apiKey: printer.apiKey,
        originalServerUrl: printer.originalServerUrl,
        isEnabled: !currentlyEnabled,
      });
      toast.success(`${printer.name || 'Printer'} ${currentlyEnabled ? 'disabled' : 'enabled'}`);
      await refetch?.();
    } catch (error) {
      console.error('Failed to toggle printer enabled state', error);
      toast.error('Failed to update printer enabled state');
    } finally {
      setTogglingEnabledId(null);
    }
  };

  const handleRefreshCapabilities = async () => {
    if (!printers || printers.length === 0) {
      toast.info('No printers to refresh');
      return;
    }

    setIsRefreshingCapabilities(true);
    const successCount: number[] = [];
    const failedCount: number[] = [];

    try {
      // Refresh cameras for all printers that support it
      for (const printer of printers) {
        try {
          await apiClient.refreshCameraUrls(printer.id);
          if (printer.cameraStreamUrl || printer.cameraSnapshotUrl) {
            successCount.push(1);
          }
        } catch (error) {
          failedCount.push(1);
          console.warn(`Failed to refresh cameras for ${printer.name}:`, error);
        }
      }

      const total = printers.length;
      const withCameras = successCount.length;
      
      toast.success(`Refreshed capabilities for ${total} printer${total === 1 ? '' : 's'} (${withCameras} with cameras detected)`);
      
      // Refetch to get updated camera URLs
      await refetch?.();
    } catch (error) {
      console.error('Failed to refresh capabilities:', error);
      toast.error('Failed to refresh printer capabilities');
    } finally {
      setIsRefreshingCapabilities(false);
    }
  };

  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Admin: Printers" subtitle="Import and export printers" maxWidth="max-w-4xl">
        <div className="gap-md flex flex-col">
          <div className="flex items-center gap-3">
            {discoveryAvailable && (
              <Button
                variant="primary"
                aria-label="Trigger network discovery to find printers on local network"
                onClick={() => setShowDiscovery(true)}
              >
                Discover Printers
              </Button>
            )}
            <Button variant="primary" aria-label="Export printers as JSON" onClick={handleExport} disabled={exporting}>
              {exporting ? (
                <span className="flex items-center gap-2">
                  <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" /><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"/></svg>
                  Exporting...
                </span>
              ) : 'Export printers'}
            </Button>
            <Button variant="primary" aria-label="Open file picker to import printers" onClick={handleImportClick}>Import printers</Button>
            <Button 
              variant="secondary" 
              aria-label="Refresh printer capabilities (cameras, features, etc.)" 
              onClick={handleRefreshCapabilities}
              disabled={isRefreshingCapabilities}
            >
              {isRefreshingCapabilities ? (
                <span className="flex items-center gap-2">
                  <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" /><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"/></svg>
                  Refreshing...
                </span>
              ) : 'Refresh Capabilities'}
            </Button>
            {/* Hidden file input for import - using standard HTML hidden input pattern */}
            <input
              ref={fileInputRef}
              type="file"
              id={fileInputId}
              accept=".csv,.json"
              onChange={(e) => handleFile(e.target.files?.[0])}
              style={{ display: 'none' }}
              aria-hidden="true"
            />
          </div>

          {showExportOptions && (
            <div className="gap-md flex flex-col">
              <div className="flex gap-2 flex-wrap">
                <Button size="sm" variant="secondary" onClick={exportSelectedServerJson} disabled={exporting}>Export JSON</Button>
                <Button size="sm" variant="secondary" onClick={exportSelectedServerCsv} disabled={exporting}>Export CSV</Button>
                <Button size="sm" variant="secondary" onClick={() => setShowExportOptions(false)} disabled={exporting}>Cancel</Button>
              </div>
              {exportProgress !== null && (
                <div className="w-full bg-pf-bg-1 rounded overflow-hidden h-3">
                  {(() => {
                    const pct = Math.max(0, Math.min(100, exportProgress ?? 0));
                    const nearest = Math.round(pct / 5) * 5; // nearest 5%
                    const cls = `bg-pf-accent h-3 pf-export-progress-bar pf-export-progress-var-${nearest}`;
                    return <div className={cls} />;
                  })()}
                  <div className="text-xs text-pf-text-tertiary mt-1">{typeof exportProgress === 'number' ? `${exportProgress}%` : 'Downloading...'}</div>
                </div>
              )}
            </div>
          )}

          <div className="card">
            <div className="card-header">
              <div className="card-header-title">Available printers</div>
            </div>
            <div className="card-body gap-md">
              {isLoading ? (
                <div className="text-sm text-pf-text-secondary">Loading...</div>
              ) : error ? (
                <Alert type="error" title="Error">
                  Failed to load printers
                </Alert>
              ) : (!printers || printers.length === 0) ? (
                <div className="text-sm text-pf-text-secondary">No printers found</div>
              ) : (
                <div className="gap-md flex flex-col">
                  <div className="flex items-center justify-between">
                    <div className="text-sm text-pf-text-secondary">{printers.length} printers</div>
                    <div className="flex items-center gap-1">
                      <Tooltip content="Select all">
                        <Button size="sm" variant="secondary" onClick={() => { setSelectedIds(printers.map(p => p.id)); }}>
                          <CheckCircleIcon className="w-4 h-4" />
                        </Button>
                      </Tooltip>
                      <Tooltip content="Select none">
                        <Button size="sm" variant="secondary" onClick={() => { setSelectedIds([]); }}>
                          <CircleIcon className="w-4 h-4" />
                        </Button>
                      </Tooltip>
                    </div>
                  </div>

                  <div className="overflow-x-auto">
                    <table>
                      <thead>
                        <tr>
                          <th>
                            <Checkbox
                              aria-label="Select all printers"
                              checked={selectedIds.length === printers.length && printers.length > 0}
                              onChange={(e) => {
                                if (e.target.checked) setSelectedIds(printers.map(p => p.id));
                                else setSelectedIds([]);
                              }}
                            />
                          </th>
                          <th>Printer Name</th>
                          <th>Backend</th>
                          <th>Port</th>
                          <th>Manufacturer</th>
                          <th>Model</th>
                          <th>Server URL</th>
                          <th>Enabled</th>
                          <th>Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {printers.map(p => (
                          <tr key={p.id}>
                            <td>
                              <Checkbox
                                aria-label={`Select printer ${p.name}`}
                                checked={selectedIds.includes(p.id)}
                                onChange={(e) => {
                                  if (e.target.checked) setSelectedIds(prev => Array.from(new Set([...prev, p.id])));
                                  else setSelectedIds(prev => prev.filter(id => id !== p.id));
                                }}
                              />
                            </td>
                            <td className="text-pf-text-primary font-medium">{p.name}</td>
                            <td className="text-pf-text-secondary text-xs">{getPrinterBackendName(p.backend) || '-'}</td>
                            <td className="text-pf-text-secondary text-xs">{p.backendPort ?? <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{p.manufacturerName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{p.modelName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{p.ipAddress ?? p.serverUrl ?? ''}</td>
                            <td className="text-center">
                              <Tooltip content={p.isEnabled ? 'Disable printer' : 'Enable printer'}>
                                <Checkbox
                                  checked={p.isEnabled ?? true}
                                  disabled={togglingEnabledId === p.id}
                                  aria-label={`Toggle ${p.name} enabled status`}
                                  onChange={() => handleToggleEnabled(p)}
                                />
                              </Tooltip>
                            </td>
                            <td className="px-4 py-4">
                              <div className="flex items-center justify-center space-x-1">
                                <Tooltip content="Edit printer">
                                  <Button
                                    size="sm"
                                    variant="subtle"
                                    onClick={() => handleEditClick(p)}
                                    className="!p-2 text-pf-text-tertiary hover:text-pf-accent hover:bg-pf-bg-2"
                                  >
                                    <EditIcon className="w-4 h-4" />
                                  </Button>
                                </Tooltip>
                                <Tooltip content="Delete printer">
                                  <Button
                                    size="sm"
                                    variant="subtle"
                                    onClick={() => handleDeletePrinter(p)}
                                    className="!p-2 text-pf-text-tertiary hover:text-pf-error-text hover:bg-pf-error-bg"
                                  >
                                    <DeleteIcon className="w-4 h-4" />
                                  </Button>
                                </Tooltip>
                              </div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>

                  {selectedIds.length > 0 && (
                    <div className="card bg-pf-bg-secondary border border-pf-border rounded p-4 mt-4">
                      <div className="text-sm font-semibold mb-3">Bulk operations ({selectedIds.length} selected)</div>
                      <div className="grid grid-cols-2 gap-4 mb-4">
                        <div className="form-group">
                          <Label htmlFor="bulk-manufacturer" className="mb-1">Set Manufacturer</Label>
                          <Select
                            id="bulk-manufacturer"
                            value={bulkManufacturerId}
                            onChange={(e) => setBulkManufacturerId(e.target.value)}
                            className="w-full"
                          >
                            <option value="">-- Keep unchanged --</option>
                            {/* Manufacturers would be populated from API - placeholder for now */}
                            <option value="">No manufacturers loaded</option>
                          </Select>
                        </div>
                        <div className="form-group">
                          <Label htmlFor="bulk-model" className="mb-1">Set Model</Label>
                          <Select
                            id="bulk-model"
                            value={bulkModelId}
                            onChange={(e) => setBulkModelId(e.target.value)}
                            className="w-full"
                          >
                            <option value="">-- Keep unchanged --</option>
                            {/* Models would be populated from API - placeholder for now */}
                            <option value="">No models loaded</option>
                          </Select>
                        </div>
                      </div>
                      <div className="grid grid-cols-2 gap-4 mb-4">
                        <div className="form-group">
                          <Label htmlFor="bulk-operation" className="mb-1">Bulk Operation</Label>
                          <Select
                            id="bulk-operation"
                            value={bulkOperation}
                            onChange={(e) => setBulkOperation(e.target.value as 'none' | 'enable' | 'disable' | 'delete')}
                            className="w-full"
                          >
                            <option value="none">-- Select operation --</option>
                            <option value="enable">Enable</option>
                            <option value="disable">Disable</option>
                            <option value="delete">Delete</option>
                          </Select>
                        </div>
                      </div>
                      <div className="flex gap-2 mb-4">
                        <Button
                          size="sm"
                          variant={bulkOperation === 'delete' ? 'danger' : 'primary'}
                          onClick={handleBulkUpdate}
                          disabled={bulkOperating}
                          className="flex-1"
                        >
                          {bulkOperating ? 'Processing...' : 'Apply to selected'}
                        </Button>
                        <Button
                          size="sm"
                          variant="secondary"
                          onClick={() => {
                            setSelectedIds([]);
                            setBulkManufacturerId('');
                            setBulkModelId('');
                            setBulkOperation('none');
                          }}
                          className="flex-1"
                        >
                          Clear selection
                        </Button>
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          </div>

          {previewItems && (
            <div className="card">
              <div className="card-header">
                {(() => {
                  const totalItems = previewItems.length;
                  const completedItems = importResults?.length ?? 0;
                  const progressPercent = totalItems > 0 ? Math.round((completedItems / totalItems) * 100) : 0;
                  return (
                    <div className="flex items-center justify-between w-full">
                      <div className="card-header-title">Import preview ({previewItems.length})</div>
                      {importing && (
                        <div className="flex items-center gap-3">
                          <div className="text-sm text-pf-text-secondary">
                            <span className="font-semibold">{completedItems}/{totalItems}</span> processed • <span className="font-semibold">{progressPercent}%</span>
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })()}
              </div>
              <div className="card-body gap-md">
                <div className="overflow-x-auto">
                  <table>
                    <thead>
                      <tr>
                        <th>Printer Name</th>
                        <th>Manufacturer</th>
                        <th>Model</th>
                        <th>Server URL</th>
                        <th>Status</th>
                        <th>Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {previewItems.map(item => {
                        const importResult = importResults?.find(r => r.index === item.__index);
                        return (
                          <tr key={item.__index}>
                            <td className="text-pf-text-primary font-medium">{item.name || <i className="text-pf-text-tertiary">(missing)</i>}</td>
                            <td className="text-pf-text-secondary">{item.manufacturerName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{item.modelName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{item.serverUrl || <span className="text-pf-error-text">(missing)</span>}</td>
                            <td className="text-center text-xs">
                              {importResult ? (
                                <>
                                  {importResult.status === 'Pending' && (
                                    <div className="flex items-center justify-center gap-1">
                                      <svg className="animate-spin h-3 w-3 text-pf-text-secondary" viewBox="0 0 24 24">
                                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z" />
                                      </svg>
                                      <span className="text-pf-text-secondary">Pending</span>
                                    </div>
                                  )}
                                  {importResult.status === 'Success' && (
                                    <div className="flex flex-col items-start gap-1">
                                      <span className="text-pf-success-text font-semibold">Success</span>
                                      {importResult.id && (
                                        <a href={`/printers/${importResult.id}`} className="text-pf-accent underline text-xs">Open</a>
                                      )}
                                    </div>
                                  )}
                                  {importResult.status === 'Skipped' && <span className="text-pf-warning-text font-semibold">Skipped</span>}
                                  {importResult.status === 'Failed' && (
                                    <div className="flex flex-col gap-1">
                                      <span className="text-pf-error-text font-semibold">Failed{importResult.reason ? ':' : ''}</span>
                                      {importResult.reason && <span className="text-pf-error-text text-xs">{importResult.reason}</span>}
                                    </div>
                                  )}
                                </>
                              ) : (
                                <span className="text-pf-text-tertiary">-</span>
                              )}
                            </td>
                            <td className="text-center">
                              <Tooltip content={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Only failed imports can be retried'}>
                                <Button
                                  size="sm"
                                  variant="secondary"
                                  disabled={retryingIndex !== null || importResult?.status !== 'Failed'}
                                  onClick={() => handleRetryRow(item)}
                                  aria-label={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Retry (only available for failed imports)'}
                                >
                                  {retryingIndex === item.__index ? 'Retrying...' : 'Retry'}
                                </Button>
                              </Tooltip>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>

                <div className="flex gap-md flex-wrap items-center">
                  <div className="form-group inline flex items-center gap-2">
                    <Label>Duplicate handling:</Label>
                    <Select value={duplicateHandling} onChange={e => setDuplicateHandling(e.target.value as 'skip' | 'overwrite' | 'rename')}>
                      <option value="skip">Skip</option>
                      <option value="overwrite">Overwrite</option>
                      <option value="rename">Rename</option>
                    </Select>
                  </div>
                  <Button size="sm" variant="primary" disabled={importing} aria-label="Confirm import of previewed printers" onClick={handleConfirmImport}>{importing ? 'Importing...' : 'Confirm Import'}</Button>
                  <Button size="sm" variant="secondary" disabled={importing} aria-label="Return to main printers table to see imported printers" onClick={() => { setPreviewItems(null); setImportResults(null); }}>{importing ? 'Importing...' : 'Close'}</Button>
                  <Tooltip content={importing ? 'Cannot retry during import' : !importResults?.some(r => r.status === 'Failed') ? 'No failed imports to retry' : 'Retry all failed imports'}>
                    <Button size="sm" variant="secondary" disabled={importing || !importResults?.some(r => r.status === 'Failed')} aria-label="Retry all failed imports" onClick={handleRetryAllFailed}>Retry all failed</Button>
                  </Tooltip>
                </div>
              </div>
            </div>
          )}
          <EditPrinterModal
            printerId={editPrinterId}
            isOpen={isEditModalOpen}
            onClose={handleEditModalClose}
            onSuccess={handleEditSuccess}
          />
        </div>

        <PrinterDiscoveryModal
          isOpen={showDiscovery}
          onClose={() => setShowDiscovery(false)}
          onSuccess={() => {
            setShowDiscovery(false);
            // Refetch the printers list to show newly discovered printers
            // Note: usePrintersWithCameraUrls will automatically refetch when needed
          }}
        />

        <DeleteConfirmationModal
          isOpen={deleteConfirmation.isOpen}
          printers={deleteConfirmation.printers}
          onConfirm={handleConfirmDelete}
          onCancel={handleCancelDelete}
        />

        <ImportResultsModal
          isOpen={showImportResults}
          results={importResults?.map(r => ({
            index: r.index,
            name: r.name,
            status: r.status,
            reason: r.reason
          })) || null}
          importedCount={importStats.imported}
          skippedCount={importStats.skipped}
          failureCount={importStats.failed}
          onClose={() => setShowImportResults(false)}
        />
      </PageTemplate>
    </ProtectedRoute>
  );
}

export default PrintersAdminPage;
