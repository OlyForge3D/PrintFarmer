import React from 'react';
import { usePrintersWithCameraUrls, useCreatePrinter } from '@/hooks/useApi';
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
  const createPrinter = useCreatePrinter();

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
      // Expect an array of printers or a single printer
      const printersToCreate = Array.isArray(parsed) ? parsed : [parsed];
      for (const p of printersToCreate) {
        // Map server/API shape expected by CreatePrinterDto
        const dto = {
          name: p.name,
          serverUrl: p.serverUrl || p.originalServerUrl || p.ipAddress || '',
          backend: p.backend ?? 0,
          apiKey: p.apiKey ?? undefined,
          notes: p.notes ?? undefined
        };
        await createPrinter.mutateAsync(dto);
      }
      toast.success(`Imported ${printersToCreate.length} printers`);
    } catch (err) {
      console.error('Import failed', err);
      toast.error('Failed to import printers');
    }
  };

  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Admin: Printers" subtitle="Import and export printers" maxWidth="max-w-4xl">
        <div className="space-y-4">
          <div className="flex items-center gap-3">
            <button onClick={handleExport} className="px-4 py-2 bg-pf-accent text-white rounded">Export printers</button>
            <button onClick={handleImportClick} className="px-4 py-2 border rounded">Import printers</button>
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
        </div>
      </PageTemplate>
    </ProtectedRoute>
  );
}

export default PrintersAdminPage;
