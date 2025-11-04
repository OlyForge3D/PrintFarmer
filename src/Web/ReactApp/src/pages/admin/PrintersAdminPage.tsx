import React from 'react';
import { usePrintersWithCameraUrls } from '@/hooks/useApi';
import { apiClient } from '@/services/api';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { PageTemplate } from '@/components/PageTemplate';
import { toast } from 'sonner';

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
  const { data: printers, isLoading, error } = usePrintersWithCameraUrls();
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
      const text = await f.text();
      const parsed = JSON.parse(text);
      const printersToCreate = Array.isArray(parsed) ? parsed : [parsed];

      // Simple validation: ensure name and at least one of serverUrl/ipAddress exist
      const validated = printersToCreate.map((p: unknown, idx: number) => {
        const rec = (p ?? {}) as Record<string, unknown>;
        // Support both formats: import format ('name', 'serverUrl') and export format ('printerName', 'serverUrl')
        const name = typeof rec.name === 'string' 
          ? rec.name 
          : (typeof rec.printerName === 'string' ? rec.printerName : '');
        const serverUrl = typeof rec.serverUrl === 'string'
          ? rec.serverUrl
          : (typeof rec.ipAddress === 'string' ? rec.ipAddress : '');
        const backend = typeof rec.backend === 'number' ? rec.backend : 0;
        const apiKey = typeof rec.apiKey === 'string' ? rec.apiKey : undefined;
        const notes = typeof rec.notes === 'string' ? rec.notes : undefined;
        const manufacturerId = typeof rec.manufacturerId === 'string' ? rec.manufacturerId : undefined;
        const modelId = typeof rec.modelId === 'string' ? rec.modelId : undefined;
        // Support multiple field name formats from different export/import sources:
        // - camelCase: manufacturerName, modelName (old format)
        // - PascalCase: Manufacturer, Model (intermediate format)
        // - API export: ManufacturerName, PrinterModel (current format)
        const manufacturerName = typeof rec.manufacturerName === 'string' 
          ? rec.manufacturerName 
          : (typeof rec.ManufacturerName === 'string' 
            ? rec.ManufacturerName 
            : (typeof rec.Manufacturer === 'string' ? rec.Manufacturer : undefined));
        const modelName = typeof rec.modelName === 'string' 
          ? rec.modelName 
          : (typeof rec.PrinterModel === 'string' 
            ? rec.PrinterModel 
            : (typeof rec.Model === 'string' ? rec.Model : undefined));

        return {
          __index: idx,
          raw: rec,
          name,
          serverUrl,
          backend,
          apiKey,
          notes,
          manufacturerId,
          modelId,
          manufacturerName,
          modelName,
          valid: Boolean(name && serverUrl)
        } as PreviewItem;
      });

      setPreviewItems(validated);
      toast.success(`Loaded ${validated.length} printers for preview`);
    } catch (err) {
      console.error('Import failed', err);
      toast.error('Failed to parse import file');
    }
  };

  const handleConfirmImport = async () => {
  if (!previewItems || previewItems.length === 0) return;
    const toImport = previewItems.filter(i => i.valid);
    if (toImport.length === 0) {
      toast.error('No valid printers to import');
      return;
    }
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
    } catch (err) {
      console.error('Retry all failed failed', err);
      toast.error('Retry all failed encountered an error');
    } finally {
      setImporting(false);
    }
  };

  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Admin: Printers" subtitle="Import and export printers" maxWidth="max-w-4xl">
        <div className="space-y-4">
          <div className="flex items-center gap-3">
            <button type="button" aria-label="Export printers as JSON" onClick={handleExport} className="px-4 py-2 bg-pf-accent text-white rounded hover:opacity-90" disabled={exporting}>
              {exporting ? (
                <span className="flex items-center gap-2">
                  <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24"><circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" /><path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"/></svg>
                  Exporting...
                </span>
              ) : 'Export printers'}
            </button>
            <button type="button" aria-label="Open file picker to import printers" onClick={handleImportClick} className="px-4 py-2 bg-pf-accent text-white rounded hover:opacity-90">Import printers</button>
            <input aria-label="Import printers JSON file" ref={fileInputRef} type="file" accept="application/json" className="hidden" onChange={(e) => handleFile(e.target.files?.[0])} />
          </div>

          <div className="p-4 bg-pf-bg-1 border border-pf-border rounded">
            <h3 className="text-lg font-semibold">Available printers</h3>
            {isLoading ? (
              <div className="text-sm text-pf-text-secondary">Loading...</div>
            ) : error ? (
              <div className="text-sm text-pf-error-text">Failed to load printers</div>
            ) : (!printers || printers.length === 0) ? (
              <div className="text-sm text-pf-text-secondary">No printers found</div>
            ) : (
              <div>
                <div className="flex items-center justify-between">
                  <div className="text-sm">{printers.length} printers</div>
                  <div className="flex items-center gap-2">
                    <button type="button" onClick={() => { setSelectedIds(printers.map(p => p.id)); }} className="px-2 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded text-sm hover:bg-pf-bg-3">Select all</button>
                    <button type="button" onClick={() => { setSelectedIds([]); }} className="px-2 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded text-sm hover:bg-pf-bg-3">Select none</button>
                    <button type="button" onClick={handleExport} className="px-2 py-1 bg-pf-accent text-white rounded text-sm hover:opacity-90">Export</button>
                  </div>
                </div>
                {showExportOptions && (
                  <div className="mt-2 flex flex-col gap-2">
                    <div className="flex gap-2">
                      <button type="button" onClick={exportSelectedAsJson} className="px-3 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-3" disabled={exporting}>Export JSON</button>
                      <button type="button" onClick={exportSelectedServerJson} className="px-3 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-3" disabled={exporting}>Export (server JSON)</button>
                      <button type="button" onClick={exportSelectedServerCsv} className="px-3 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-3" disabled={exporting}>Export (server CSV)</button>
                      <button type="button" onClick={exportSelectedAsCsv} className="px-3 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-3" disabled={exporting}>Export CSV</button>
                      <button type="button" onClick={() => setShowExportOptions(false)} className="px-3 py-1 border border-pf-border bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-3" disabled={exporting}>Cancel</button>
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

                <div className="mt-2 overflow-x-auto">
                  <table className="w-full text-sm border-collapse">
                    <thead className="bg-pf-bg-2 border-b border-pf-border">
                      <tr>
                        <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">
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
                        <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Printer Name</th>
                        <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Manufacturer</th>
                        <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Model</th>
                        <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Server URL</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-pf-border">
                      {printers.map(p => (
                        <tr key={p.id} className="hover:bg-pf-bg-2 transition-colors">
                          <td className="px-3 py-2">
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
                          <td className="px-3 py-2 text-pf-text-primary font-medium">{p.name}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{p.manufacturerName || <span className="text-pf-warning-text">-</span>}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{p.modelName || <span className="text-pf-warning-text">-</span>}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{p.ipAddress ?? p.serverUrl ?? ''}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>

          {previewItems && (
            <div className="p-4 bg-pf-bg-2 border border-pf-border rounded">
              <h3 className="text-lg font-semibold">Import preview ({previewItems.length})</h3>
              <div className="mt-2 overflow-x-auto">
                <table className="w-full text-sm border-collapse">
                  <thead className="bg-pf-bg-2 border-b border-pf-border">
                    <tr>
                      <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Printer Name</th>
                      <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Manufacturer</th>
                      <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Model</th>
                      <th className="text-left px-3 py-2 font-semibold text-pf-text-primary">Server URL</th>
                      <th className="text-center px-3 py-2 font-semibold text-pf-text-primary">Status</th>
                      <th className="text-center px-3 py-2 font-semibold text-pf-text-primary">Action</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-pf-border">
                    {previewItems.map(item => {
                      const importResult = importResults?.find(r => r.index === item.__index);
                      return (
                        <tr key={item.__index} className="hover:bg-pf-bg-2 transition-colors">
                          <td className="px-3 py-2 text-pf-text-primary font-medium">{item.name || <i className="text-pf-text-tertiary">(missing)</i>}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{item.manufacturerName || <span className="text-pf-warning-text">-</span>}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{item.modelName || <span className="text-pf-warning-text">-</span>}</td>
                          <td className="px-3 py-2 text-pf-text-secondary">{item.serverUrl || <span className="text-pf-error-text">(missing)</span>}</td>
                          <td className="px-3 py-2 text-center text-xs">
                            {importResult ? (
                              <>
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
                          <td className="px-3 py-2 text-center space-x-2">
                            <button
                              disabled={retryingIndex !== null || importResult?.status !== 'Failed'}
                              onClick={() => handleRetryRow(item)}
                              className="px-1.5 py-1 text-sm bg-pf-accent text-white rounded hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed"
                              aria-label={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Retry (only available for failed imports)'}
                              title={retryingIndex === item.__index ? 'Retrying...' : importResult?.status === 'Failed' ? 'Retry import' : 'Only failed imports can be retried'}
                            >
                              {retryingIndex === item.__index ? '↻' : '↻'}
                            </button>
                            {importResult?.id && (
                              <a href={`/printers/${importResult.id}`} className="text-xs text-pf-accent underline hover:opacity-80" target="_blank" rel="noreferrer" aria-label={`Open printer ${importResult.name}`}>Open</a>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>

              <div className="mt-3 flex gap-2">
                <label className="flex items-center gap-2 text-sm">
                  <span className="text-pf-text-secondary">Duplicate handling:</span>
                  <select value={duplicateHandling} onChange={e => setDuplicateHandling(e.target.value as 'skip' | 'overwrite' | 'rename')} className="px-2 py-1 rounded border border-pf-border bg-pf-bg-1 text-pf-text-primary text-sm">
                    <option value="skip">Skip</option>
                    <option value="overwrite">Overwrite</option>
                    <option value="rename">Rename</option>
                  </select>
                </label>
                <button type="button" disabled={importing} aria-label="Confirm import of previewed printers" onClick={handleConfirmImport} className="px-3 py-1 bg-pf-accent text-white rounded hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed">{importing ? 'Importing...' : 'Confirm Import'}</button>
                <button type="button" disabled={importing} aria-label="Cancel import preview" onClick={() => setPreviewItems(null)} className="px-3 py-1 bg-pf-accent text-white rounded hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed">Cancel</button>
                <button type="button" disabled={importing || !importResults?.some(r => r.status === 'Failed')} aria-label="Retry all failed imports" title={importing ? 'Cannot retry during import' : !importResults?.some(r => r.status === 'Failed') ? 'No failed imports to retry' : 'Retry all failed imports'} onClick={handleRetryAllFailed} className="px-3 py-1 bg-pf-accent text-white rounded hover:opacity-90 disabled:opacity-50 disabled:cursor-not-allowed">Retry All</button>
              </div>
            </div>
          )}
          {(!previewItems || previewItems.length === 0) && importResults && importResults.length > 0 && (
            <div className="p-4 bg-pf-bg-2 border border-pf-border rounded">
              <h3 className="text-lg font-semibold">Import results ({importResults.length})</h3>
              <ul className="mt-2 space-y-2 text-sm text-pf-text-secondary">
                {importResults.map(r => (
                  <li key={r.index} className="flex justify-between items-center">
                    <div>
                      <div className="font-medium text-pf-text-primary">{r.name}</div>
                      {r.reason && <div className="text-xs text-pf-error-text">{r.reason}</div>}
                    </div>
                    <div className="flex items-center gap-3">
                      <div className="text-xs">
                        {r.status === 'Imported' ? <span className="text-pf-success-text">Imported</span> : r.status === 'Skipped' ? <span className="text-pf-warning-text">Skipped</span> : <span className="text-pf-error-text">Failed</span>}
                      </div>
                      {r.id && (
                        <a href={`/printers/${r.id}`} className="text-xs text-pf-accent underline" target="_blank" rel="noreferrer">Open</a>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      </PageTemplate>
    </ProtectedRoute>
  );
}

export default PrintersAdminPage;
