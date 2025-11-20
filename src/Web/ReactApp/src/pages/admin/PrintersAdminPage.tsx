import React from 'react';
import { usePrintersWithCameraUrls, queryKeys } from '@/hooks/useApi';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { printerHubService } from '@/services/printerHubService';
import { getPrinterBackendName } from '@/utils/enumHelpers';
import { PrinterDiscoveryModal } from '@/components/PrinterDiscoveryModal';
import { EditPrinterModal } from '@/components/EditPrinterModal';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { PageTemplate } from '@/components/PageTemplate';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { toast } from 'sonner';
import type { Printer } from '@/types/api';

function downloadJson(filename: string, data: unknown) {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

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
  const [retryingIndex, setRetryingIndex] = React.useState<number | null>(null);
  const [selectedIds, setSelectedIds] = React.useState<string[]>([]);
  const [showExportOptions, setShowExportOptions] = React.useState(false);
  const [exportProgress, setExportProgress] = React.useState<number | null>(null);
  const [exporting, setExporting] = React.useState<boolean>(false);
  const [showDiscovery, setShowDiscovery] = React.useState(false);
  const [discoveryAvailable, setDiscoveryAvailable] = React.useState(false);
  const [bulkManufacturerId, setBulkManufacturerId] = React.useState<string>('');
  const [bulkModelId, setBulkModelId] = React.useState<string>('');
  const [bulkIsEnabled, setBulkIsEnabled] = React.useState<boolean | null>(null);
  const [bulkOperating, setBulkOperating] = React.useState(false);
  const [editPrinterId, setEditPrinterId] = React.useState<string | null>(null);
  const [isEditModalOpen, setIsEditModalOpen] = React.useState(false);
  const [togglingEnabledId, setTogglingEnabledId] = React.useState<string | null>(null);

  // Check if discovery service is available
  React.useEffect(() => {
    const checkDiscoveryAvailability = async () => {
      try {
        // Fetch network discovery settings
        const settings = await apiClient.getSettings<import('@/types/NetworkDiscoverySettings').NetworkDiscoverySettings>('NetworkDiscovery');
        console.log('Discovery settings:', settings);
        
        // Discovery is available if:
        // 1. EnableDiscovery is true AND
        // 2. LastHeartbeat is recent (within 60 seconds) - confirms service is actually running
        const isEnabled = settings?.enableDiscovery === true;
        const hasRecentHeartbeat = settings?.lastHeartbeat 
          ? new Date().getTime() - new Date(settings.lastHeartbeat).getTime() < 60000 // 60 seconds
          : false;
        
        console.log('Discovery check:', { isEnabled, lastHeartbeat: settings?.lastHeartbeat, hasRecentHeartbeat });
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

  const exportSelectedAsJson = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const ids = selectedIds.length > 0 ? selectedIds : printers.map(p => p.id);
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.json`;
    try {
      setExporting(true);
      const exported = await apiClient.exportPrintersByIds(ids);
      downloadJson(filename, exported);
      toast.success('Printers exported (JSON)');
    } catch (err) {
      console.error('Export JSON failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
      setExporting(false);
    }
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
      toast.success('Printers exported (server JSON)');
    } catch (err) {
      console.error('Server export failed', err);
      toast.error('Server export failed');
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
      toast.success('Printers exported (server CSV)');
    } catch (err) {
      console.error('Server CSV export failed', err);
      toast.error('Server CSV export failed');
    } finally {
      setShowExportOptions(false);
      setExportProgress(null);
      setExporting(false);
    }
  };

  const toCsv = (rows: Record<string, unknown>[], headers: string[]) => {
    const escape = (v: unknown) => {
      if (v === null || v === undefined) return '';
      const s = String(v);
      if (s.includes('"')) return `"${s.replace(/"/g, '""')}"`;
      if (s.includes(',') || s.includes('\n') || s.includes('\r')) return `"${s}"`;
      return s;
    };
    const sb: string[] = [];
    sb.push(headers.join(','));
    for (const r of rows) {
      const line = headers.map(h => escape((r as Record<string, unknown>)[h] ?? '')).join(',');
      sb.push(line);
    }
    return sb.join('\n');
  };

  const exportSelectedAsCsv = async () => {
    if (!printers || printers.length === 0) return toast('No printers to export');
    const ids = selectedIds.length > 0 ? selectedIds : printers.map(p => p.id);
    try {
      const exported = await apiClient.exportPrintersByIds(ids);
      // Normalize and pick columns per requirement
      type MinimalExport = { name?: string; manufacturerName?: string; modelName?: string; backend?: number | string; ipAddress?: string };
      const rows = (exported as MinimalExport[]).map((p) => ({
        Name: p.name ?? '',
        ManufacturerName: p.manufacturerName ?? '',
        ModelName: p.modelName ?? '',
        Backend: p.backend !== undefined ? String(p.backend) : '',
        IpAddress: p.ipAddress ?? ''
      }));
      const csv = toCsv(rows, ['Name', 'ManufacturerName', 'ModelName', 'Backend', 'IpAddress']);
      const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.csv`;
      const blob = new Blob([csv], { type: 'text/csv' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      toast.success('Printers exported (CSV)');
    } catch (err) {
      console.error('Export CSV failed', err);
      toast.error('Export failed');
    } finally {
      setShowExportOptions(false);
    }
  };

  const fileInputRef = React.useRef<HTMLInputElement | null>(null);

  const handleImportClick = () => {
    fileInputRef.current?.click();
  };

  const handleFile = async (file?: File) => {
    try {
      const f = file || (fileInputRef.current?.files ? fileInputRef.current.files[0] : undefined);
      if (!f) return;
      
      // Send file directly to backend - it handles CSV/JSON parsing
      const formData = new FormData();
      formData.append('file', f);
      formData.append('duplicateHandling', duplicateHandling);
      
      const response = await fetch(`${getApiBaseUrl()}/printers/import`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: formData
      });
      
      if (!response.ok) {
        const errorData = await response.json().catch(() => null);
        throw new Error(errorData?.message || `Import failed: ${response.statusText}`);
      }
      
      const result = await response.json();
      const resultsArray = Array.isArray(result?.results) ? result.results : [];
      
      // Map results for display
      setImportResults(resultsArray);
      setPreviewItems(null);
      
      // Refresh printers table so imported printers can be edited
      await refetch();
      
      toast.success(`Import complete: ${resultsArray.length} items processed`);
    } catch (err) {
      console.error('Import failed', err);
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
      
      // Invalidate printer queries globally so all pages (admin + main printers page) see new printers immediately
      queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      
      // Also refetch for immediate local update
      await refetch();
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
      
      if (singleResult && singleResult.status === 'Imported') {
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

    if (!bulkManufacturerId && !bulkModelId && bulkIsEnabled === null) {
      toast('Select at least one field to update');
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

          await apiClient.updatePrinter(id, {
            name: printer.name,
            serverUrl: printer.serverUrl || '',
            notes: printer.notes,
            manufacturerId: bulkManufacturerId ? (bulkManufacturerId as unknown as string) : printer.manufacturerId,
            modelId: bulkModelId ? (bulkModelId as unknown as string) : printer.modelId,
            backend: printer.backend,
            isEnabled: bulkIsEnabled !== null ? bulkIsEnabled : undefined
          });
          updated++;
        } catch (err) {
          console.error(`Failed to update printer ${id}:`, err);
          failed++;
        }
      }

      toast.success(`Updated ${updated} printer${updated !== 1 ? 's' : ''}${failed > 0 ? ` (${failed} failed)` : ''}`);
      
      // Clear selections and reset form
      setSelectedIds([]);
      setBulkManufacturerId('');
      setBulkModelId('');
      setBulkIsEnabled(null);
      
      // Refetch printers to show updates
      if (refetch) await refetch();
    } catch (err) {
      console.error('Bulk update failed', err);
      toast.error('Bulk update failed');
    } finally {
      setBulkOperating(false);
    }
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

  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Admin: Printers" subtitle="Import and export printers" maxWidth="max-w-4xl">
        <div className="gap-md flex flex-col">
          <div className="flex items-center gap-3">
            {discoveryAvailable && (
              <button
                type="button"
                aria-label="Trigger network discovery to find printers on local network"
                onClick={() => setShowDiscovery(true)}
                className="btn-base btn-md btn-primary"
              >
                Discover Printers
              </button>
            )}
            <button type="button" aria-label="Export printers as JSON" onClick={handleExport} className="btn-base btn-md btn-primary" disabled={exporting}>
              {exporting ? (
                <span className="flex items-center gap-2">
                  <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" /><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"/></svg>
                  Exporting...
                </span>
              ) : 'Export printers'}
            </button>
            <button type="button" aria-label="Open file picker to import printers" onClick={handleImportClick} className="btn-base btn-md btn-primary">Import printers</button>
            <input aria-label="Import printers CSV or JSON file" ref={fileInputRef} type="file" accept=".csv,.json,text/csv,application/json" className="hidden" onChange={(e) => handleFile(e.target.files?.[0])} />
          </div>

          <div className="card">
            <div className="card-header">
              <div className="card-header-title">Available printers</div>
            </div>
            <div className="card-body gap-md">
              {isLoading ? (
                <div className="text-sm text-pf-text-secondary">Loading...</div>
              ) : error ? (
                <div className="alert-base alert-error">
                  <div className="alert-title">Error</div>
                  <div>Failed to load printers</div>
                </div>
              ) : (!printers || printers.length === 0) ? (
                <div className="text-sm text-pf-text-secondary">No printers found</div>
              ) : (
                <div className="gap-md flex flex-col">
                  <div className="flex items-center justify-between">
                    <div className="text-sm text-pf-text-secondary">{printers.length} printers</div>
                    <div className="flex items-center gap-2">
                      <button type="button" onClick={() => { setSelectedIds(printers.map(p => p.id)); }} className="btn-base btn-sm btn-secondary">Select all</button>
                      <button type="button" onClick={() => { setSelectedIds([]); }} className="btn-base btn-sm btn-secondary">Select none</button>
                      <button type="button" onClick={handleExport} className="btn-base btn-sm btn-primary">Export</button>
                    </div>
                  </div>
                  {showExportOptions && (
                    <div className="gap-md flex flex-col">
                      <div className="flex gap-2 flex-wrap">
                        <button type="button" onClick={exportSelectedAsJson} className="btn-base btn-sm btn-secondary" disabled={exporting}>Export JSON</button>
                        <button type="button" onClick={exportSelectedServerJson} className="btn-base btn-sm btn-secondary" disabled={exporting}>Export (server JSON)</button>
                        <button type="button" onClick={exportSelectedServerCsv} className="btn-base btn-sm btn-secondary" disabled={exporting}>Export (server CSV)</button>
                        <button type="button" onClick={exportSelectedAsCsv} className="btn-base btn-sm btn-secondary" disabled={exporting}>Export CSV</button>
                        <button type="button" onClick={() => setShowExportOptions(false)} className="btn-base btn-sm btn-secondary" disabled={exporting}>Cancel</button>
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

                  <div className="overflow-x-auto">
                    <table>
                      <thead>
                        <tr>
                          <th>
                            <input
                              aria-label="Select all printers"
                              type="checkbox"
                              checked={selectedIds.length === printers.length && printers.length > 0}
                              onChange={(e) => {
                                if (e.target.checked) setSelectedIds(printers.map(p => p.id));
                                else setSelectedIds([]);
                              }}
                            />
                          </th>
                          <th>Printer Name</th>
                          <th>Backend</th>
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
                              <input
                                aria-label={`Select printer ${p.name}`}
                                type="checkbox"
                                checked={selectedIds.includes(p.id)}
                                onChange={(e) => {
                                  if (e.target.checked) setSelectedIds(prev => Array.from(new Set([...prev, p.id])));
                                  else setSelectedIds(prev => prev.filter(id => id !== p.id));
                                }}
                              />
                            </td>
                            <td className="text-pf-text-primary font-medium">{p.name}</td>
                            <td className="text-pf-text-secondary text-xs">{getPrinterBackendName(p.backend) || '-'}</td>
                            <td className="text-pf-text-secondary">{p.manufacturerName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{p.modelName || <span className="text-pf-warning-text">-</span>}</td>
                            <td className="text-pf-text-secondary">{p.ipAddress ?? p.serverUrl ?? ''}</td>
                            <td className="text-center">
                              <input
                                type="checkbox"
                                checked={p.isEnabled ?? true}
                                disabled={togglingEnabledId === p.id}
                                aria-label={`Toggle ${p.name} enabled status`}
                                onChange={() => handleToggleEnabled(p)}
                                title={p.isEnabled ? 'Disable printer' : 'Enable printer'}
                              />
                            </td>
                            <td className="text-right">
                              <button
                                type="button"
                                onClick={() => handleEditClick(p)}
                                className="btn-base btn-sm btn-secondary"
                              >
                                Edit
                              </button>
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
                          <label className="text-sm text-pf-text-secondary mb-1 block" htmlFor="bulk-manufacturer">Set Manufacturer</label>
                          <select
                            id="bulk-manufacturer"
                            value={bulkManufacturerId}
                            onChange={(e) => setBulkManufacturerId(e.target.value)}
                            className="input-base input-sm w-full"
                          >
                            <option value="">-- Keep unchanged --</option>
                            {/* Manufacturers would be populated from API - placeholder for now */}
                            <option value="">No manufacturers loaded</option>
                          </select>
                        </div>
                        <div className="form-group">
                          <label className="text-sm text-pf-text-secondary mb-1 block" htmlFor="bulk-model">Set Model</label>
                          <select
                            id="bulk-model"
                            value={bulkModelId}
                            onChange={(e) => setBulkModelId(e.target.value)}
                            className="input-base input-sm w-full"
                          >
                            <option value="">-- Keep unchanged --</option>
                            {/* Models would be populated from API - placeholder for now */}
                            <option value="">No models loaded</option>
                          </select>
                        </div>
                      </div>
                      <div className="grid grid-cols-2 gap-4 mb-4">
                        <div className="form-group">
                          <label className="text-sm text-pf-text-secondary mb-2 block">Enabled Status</label>
                          <div className="flex gap-2">
                            <button
                              type="button"
                              onClick={() => setBulkIsEnabled(true)}
                              className={`btn-base btn-sm flex-1 ${bulkIsEnabled === true ? 'btn-primary' : 'btn-secondary'}`}
                            >
                              Enable
                            </button>
                            <button
                              type="button"
                              onClick={() => setBulkIsEnabled(false)}
                              className={`btn-base btn-sm flex-1 ${bulkIsEnabled === false ? 'btn-primary' : 'btn-secondary'}`}
                            >
                              Disable
                            </button>
                            <button
                              type="button"
                              onClick={() => setBulkIsEnabled(null)}
                              className={`btn-base btn-sm flex-1 ${bulkIsEnabled === null ? 'btn-primary' : 'btn-secondary'}`}
                            >
                              Skip
                            </button>
                          </div>
                        </div>
                        <div className="flex items-end">
                          <button
                            type="button"
                            onClick={handleBulkUpdate}
                            disabled={bulkOperating}
                            className="btn-base btn-sm btn-primary w-full"
                          >
                            {bulkOperating ? 'Updating...' : 'Apply to selected'}
                          </button>
                        </div>
                      </div>
                      <button
                        type="button"
                        onClick={() => setSelectedIds([])}
                        className="btn-base btn-sm btn-secondary w-full"
                      >
                        Clear selection
                      </button>
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
                                  {importResult.status === 'Imported' && <span className="text-pf-success-text font-semibold">Imported</span>}
                                  {importResult.status === 'Skipped' && <span className="text-pf-warning-text font-semibold">Skipped</span>}
                                  {importResult.status === 'Failed' && (
                                    <div className="flex flex-col gap-1">
                                      <span className="text-pf-error-text font-semibold">Failed</span>
                                      {importResult.reason && <span className="text-pf-error-text text-xs">{importResult.reason}</span>}
                                    </div>
                                  )}
                                </>
                              ) : (
                                <span className="text-pf-text-tertiary">-</span>
                              )}
                            </td>
                            <td className="text-center">
                              <button
                                disabled={retryingIndex !== null || importResult?.status !== 'Failed'}
                                onClick={() => handleRetryRow(item)}
                                className="btn-base btn-sm btn-secondary disabled:opacity-50 disabled:cursor-not-allowed"
                                aria-label={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Retry (only available for failed imports)'}
                                title={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Only failed imports can be retried'}
                              >
                                {retryingIndex === item.__index ? '↻' : '↻'}
                              </button>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>

                <div className="flex gap-md flex-wrap items-center">
                  <label className="form-group inline">
                    <span className="text-pf-text-secondary">Duplicate handling:</span>
                    <select value={duplicateHandling} onChange={e => setDuplicateHandling(e.target.value as 'skip' | 'overwrite' | 'rename')} className="input-base input-sm">
                      <option value="skip">Skip</option>
                      <option value="overwrite">Overwrite</option>
                      <option value="rename">Rename</option>
                    </select>
                  </label>
                  <button type="button" disabled={importing} aria-label="Confirm import of previewed printers" onClick={handleConfirmImport} className="btn-base btn-sm btn-primary disabled:opacity-50">{importing ? 'Importing...' : 'Confirm Import'}</button>
                  <button type="button" disabled={importing} aria-label="Return to main printers table to see imported printers" onClick={() => { setPreviewItems(null); setImportResults(null); }} className="btn-base btn-sm btn-secondary disabled:opacity-50">{importing ? 'Importing...' : 'Close'}</button>
                  <button type="button" disabled={importing || !importResults?.some(r => r.status === 'Failed')} aria-label="Retry all failed imports" title={importing ? 'Cannot retry during import' : !importResults?.some(r => r.status === 'Failed') ? 'No failed imports to retry' : 'Retry all failed imports'} onClick={handleRetryAllFailed} className="btn-base btn-sm btn-secondary disabled:opacity-50">Retry All</button>
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
      </PageTemplate>
    </ProtectedRoute>
  );
}

export default PrintersAdminPage;
