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
    valid: boolean;
  };

  const [previewItems, setPreviewItems] = React.useState<PreviewItem[] | null>(null);
  const [importing, setImporting] = React.useState<boolean>(false);
  const [importResults, setImportResults] = React.useState<import('@/types/api').BulkImportResultItem[] | null>(null);
  const [retryingIndex, setRetryingIndex] = React.useState<number | null>(null);

  const handleExport = () => {
    if (!printers || !printers.length) {
      toast('No printers to export');
      return;
    }
    const filename = `printfarmer-printers-${new Date().toISOString().slice(0,10)}.json`;
    downloadJson(filename, printers);
    toast.success('Printers exported');
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
        const name = typeof rec.name === 'string' ? rec.name : '';
        const serverUrl = typeof rec.serverUrl === 'string'
          ? rec.serverUrl
          : (typeof rec.originalServerUrl === 'string' ? rec.originalServerUrl : (typeof rec.ipAddress === 'string' ? rec.ipAddress : ''));
        const backend = typeof rec.backend === 'number' ? rec.backend : 0;
        const apiKey = typeof rec.apiKey === 'string' ? rec.apiKey : undefined;
        const notes = typeof rec.notes === 'string' ? rec.notes : undefined;

        return {
          __index: idx,
          raw: rec,
          name,
          serverUrl,
          backend,
          apiKey,
          notes,
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
        notes: i.notes
      }));

  const resp = await apiClient.bulkCreatePrinters(dtos);
  setImportResults(resp.results ?? null);
  toast.success(`Imported ${resp.importedCount} printers${resp.skippedCount ? `, ${resp.skippedCount} skipped` : ''}`);
      // Keep previewItems so admin can review results; clear preview only when all imported
      const allImported = (resp.results || []).every(r => r.status === 'Imported' || r.status === 'Skipped');
      if (allImported) setPreviewItems(null);
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
      };
      const resp = await apiClient.bulkCreatePrinters([dto]);
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
      }));
      const resp = await apiClient.bulkCreatePrinters(dtos);
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
            <button type="button" aria-label="Export printers as JSON" onClick={handleExport} className="px-4 py-2 bg-pf-accent text-white rounded">Export printers</button>
            <button type="button" aria-label="Open file picker to import printers" onClick={handleImportClick} className="px-4 py-2 border rounded">Import printers</button>
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
              <ul className="mt-2 space-y-2 text-sm text-pf-text-secondary">
                {printers.map(p => (
                  <li key={p.id} className="flex justify-between items-center">
                    <div>
                      <div className="font-medium text-pf-text-primary">{p.name}</div>
                      <div className="text-xs">{p.manufacturerName ?? ''} {p.modelName ?? ''}</div>
                    </div>
                    <div className="text-xs text-pf-text-tertiary">{p.serverUrl ?? p.ipAddress ?? ''}</div>
                  </li>
                ))}
              </ul>
            )}
          </div>

          {previewItems && (
            <div className="p-4 bg-pf-bg-2 border border-pf-border rounded">
              <h3 className="text-lg font-semibold">Import preview ({previewItems.length})</h3>
              <div className="mt-2 text-sm text-pf-text-secondary">
                <ul className="space-y-2">
                  {previewItems.map(item => (
                    <li key={item.__index} className="flex justify-between items-center">
                      <div>
                        <div className="font-medium text-pf-text-primary">{item.name || <i className="text-xs text-pf-text-tertiary">(missing name)</i>}</div>
                        <div className="text-xs">{item.notes ?? ''}</div>
                      </div>
                      <div className="flex items-center gap-3">
                        <div className="text-xs text-pf-text-tertiary">{item.serverUrl || <span className="text-pf-error-text">(missing URL)</span>}</div>
                        {importResults && importResults.length > 0 && (
                          <div className="ml-2 text-xs flex items-center gap-2">
                            {(() => {
                              const r = importResults.find(rr => rr.index === item.__index);
                              if (!r) return null;
                              if (r.status === 'Imported') return <span className="text-green-600">Imported</span>;
                              if (r.status === 'Skipped') return <span className="text-yellow-600">Skipped</span>;
                              return <span className="text-red-600">Failed: {r.reason}</span>;
                            })()}
                            {(() => {
                              const r = importResults.find(rr => rr.index === item.__index && rr.id);
                              if (!r) return null;
                              return (
                                <a href={`/printers/${r.id}`} className="text-xs text-pf-accent underline" target="_blank" rel="noreferrer" aria-label={`Open printer ${r.name}`}>Open</a>
                              );
                            })()}
                          </div>
                        )}
                        <div>
                          <button
                            disabled={retryingIndex !== null}
                            onClick={() => handleRetryRow(item)}
                            className="px-2 py-1 text-xs border rounded"
                          >
                            {retryingIndex === item.__index ? 'Retrying...' : 'Retry'}
                          </button>
                        </div>
                      </div>
                    </li>
                  ))}
                </ul>
              </div>

              <div className="mt-3 flex gap-2">
                <button type="button" disabled={importing} aria-label="Confirm import of previewed printers" onClick={handleConfirmImport} className="px-3 py-1 bg-pf-accent text-white rounded">{importing ? 'Importing...' : 'Confirm Import'}</button>
                <button type="button" disabled={importing} aria-label="Cancel import preview" onClick={() => setPreviewItems(null)} className="px-3 py-1 border rounded">Cancel</button>
                <button type="button" disabled={importing} aria-label="Retry all failed imports" onClick={handleRetryAllFailed} className="px-3 py-1 border rounded">Retry all failed</button>
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
                      {r.reason && <div className="text-xs text-red-600">{r.reason}</div>}
                    </div>
                    <div className="flex items-center gap-3">
                      <div className="text-xs">
                        {r.status === 'Imported' ? <span className="text-green-600">Imported</span> : r.status === 'Skipped' ? <span className="text-yellow-600">Skipped</span> : <span className="text-red-600">Failed</span>}
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
