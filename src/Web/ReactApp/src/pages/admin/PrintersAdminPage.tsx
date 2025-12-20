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
import { toast } from 'sonner';
import { DeleteIcon, EditIcon } from '@/components/icons/MdiIcons';
import { CheckSquare, Square } from 'lucide-react';
import { Alert, Button, Checkbox, FileUpload, Label, Select, Tooltip } from '@/components/ui';
import type { Printer } from '@/types/api';

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

      const text = await f.text();
      let parsed: unknown = [];
      try {
        parsed = JSON.parse(text);
      } catch (parseErr) {
        console.error('Failed to parse import file', parseErr);
        toast.error('Invalid file format. Expected JSON array.');
        return;
      }

      if (!Array.isArray(parsed)) {
        toast.error('Import file must contain a JSON array');
        return;
      }

      const mapped: PreviewItem[] = parsed.map((item, idx) => {
        const rec = (item ?? {}) as Record<string, unknown>;
        return {
          __index: idx,
          raw: rec,
          name: typeof rec.name === 'string' ? rec.name : '',
          serverUrl: typeof rec.serverUrl === 'string' ? rec.serverUrl : '',
          backend: typeof rec.backend === 'number' ? rec.backend : 1,
          apiKey: typeof rec.apiKey === 'string' ? rec.apiKey : undefined,
          notes: typeof rec.notes === 'string' ? rec.notes : undefined,
          manufacturerId: typeof rec.manufacturerId === 'string' ? rec.manufacturerId : undefined,
          modelId: typeof rec.modelId === 'string' ? rec.modelId : undefined,
          manufacturerName: typeof rec.manufacturerName === 'string' ? rec.manufacturerName : undefined,
          modelName: typeof rec.modelName === 'string' ? rec.modelName : undefined,
          valid: Boolean(rec.name) && Boolean(rec.serverUrl)
        };
      });

      setPreviewItems(mapped);
      setImportResults(null);
      setImporting(false);
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
            <FileUpload
              id={fileInputId}
              label="Import printers JSON file"
              ref={fileInputRef}
              accept=".csv,.json,text/csv,application/json"
              onChange={(files) => handleFile(files?.[0])}
              className=""
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
                          <CheckSquare className="w-4 h-4" />
                        </Button>
                      </Tooltip>
                      <Tooltip content="Select none">
                        <Button size="sm" variant="secondary" onClick={() => { setSelectedIds([]); }}>
                          <Square className="w-4 h-4" />
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
                          <Label className="mb-2">Enabled Status</Label>
                          <div className="flex gap-2">
                            <Button
                              size="sm"
                              variant={bulkIsEnabled === true ? 'primary' : 'secondary'}
                              onClick={() => setBulkIsEnabled(true)}
                              className="flex-1"
                            >
                              Enable
                            </Button>
                            <Button
                              size="sm"
                              variant={bulkIsEnabled === false ? 'primary' : 'secondary'}
                              onClick={() => setBulkIsEnabled(false)}
                              className="flex-1"
                            >
                              Disable
                            </Button>
                            <Button
                              size="sm"
                              variant={bulkIsEnabled === null ? 'primary' : 'secondary'}
                              onClick={() => setBulkIsEnabled(null)}
                              className="flex-1"
                            >
                              Skip
                            </Button>
                          </div>
                        </div>
                        <div className="flex items-end">
                          <Button
                            size="sm"
                            variant="primary"
                            onClick={handleBulkUpdate}
                            disabled={bulkOperating}
                            className="w-full"
                          >
                            {bulkOperating ? 'Updating...' : 'Apply to selected'}
                          </Button>
                        </div>
                      </div>
                      <Button
                        size="sm"
                        variant="secondary"
                        onClick={() => setSelectedIds([])}
                        className="w-full"
                      >
                        Clear selection
                      </Button>
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
                                  {importResult.status === 'Imported' && (
                                    <div className="flex flex-col items-start gap-1">
                                      <span className="text-pf-success-text font-semibold">Imported</span>
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
      </PageTemplate>
    </ProtectedRoute>
  );
}

export default PrintersAdminPage;
