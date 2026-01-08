import React, { useEffect, useState } from 'react';
import { Button, Checkbox, Select } from '@/common/components/ui';
import { queueService } from '@/services/queueService';
import printJobQueueService, { EnqueuePrintJobRequest } from '@/services/printJobQueueService';
import { GcodeFile } from '@/types/api';

interface Props {
  file: GcodeFile;
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}

export const QueueGcodeModal: React.FC<Props> = ({ file, isOpen, onClose }) => {
  const [printers, setPrinters] = useState<Array<{ id: string; name: string; model: string; isAvailable: boolean; nozzleDiameter?: number; supportedMaterials?: string[] }>>([]);
  const [selectedPrinter, setSelectedPrinter] = useState<string | undefined>(undefined);
  const [autoAssign, setAutoAssign] = useState(true);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!isOpen) return;
    (async () => {
      try {
        const list = await queueService.getQueueOverview();
        // Map additional metadata if available (nozzle diameter, supported materials)
        setPrinters(list.map(p => {
          // queueService.QueueOverview may include optional capability fields
          const nozzle = (p as unknown as { nozzleDiameter?: number }).nozzleDiameter;
          const mats = (p as unknown as { supportedMaterials?: string[] }).supportedMaterials;
          return {
            id: p.printerId,
            name: p.printerName,
            model: p.printerModel,
            isAvailable: p.isAvailable,
            nozzleDiameter: typeof nozzle === 'number' ? nozzle : undefined,
            supportedMaterials: Array.isArray(mats) ? mats : undefined
          };
        }));
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load printers');
      }
    })();
  }, [isOpen]);

  const handleQueue = async () => {
    setLoading(true);
    setError(null);
    try {
      const req: EnqueuePrintJobRequest = {
        gcodeFileId: file.id,
        assignedPrinterId: autoAssign ? undefined : (selectedPrinter || undefined),
        priority: 'Normal'
      };

      // Support extracted fields from lightweight GcodeFile
      const requiredNozzle = file.extractedNozzleDiameter;
      const requiredMaterial = file.extractedMaterial;

      if (typeof requiredNozzle === 'number') req.requiredNozzleDiameter = requiredNozzle;
      if (typeof requiredMaterial === 'string' && requiredMaterial.length > 0) req.requiredMaterialType = requiredMaterial;

      await printJobQueueService.enqueue(req);
      setLoading(false);
      onClose(true);
    } catch (err) {
      setLoading(false);
      setError(err instanceof Error ? err.message : 'Failed to queue job');
    }
  };

  const isPrinterCompatible = (p: { nozzleDiameter?: number; supportedMaterials?: string[] }) => {
    if (!file) return true;
    const requiredNozzle = file.extractedNozzleDiameter;
    const requiredMaterial = file.extractedMaterial;

    if (typeof requiredNozzle === 'number' && (p.nozzleDiameter == null || p.nozzleDiameter < requiredNozzle)) return false;
    if (typeof requiredMaterial === 'string' && requiredMaterial.length > 0 && p.supportedMaterials && p.supportedMaterials.length > 0) {
      return p.supportedMaterials.map(s => s.toLowerCase()).includes(requiredMaterial.toLowerCase());
    }
    return true;
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-white rounded-lg shadow-lg w-full max-w-2xl p-6">
        <div className="flex items-start justify-between mb-4">
          <h3 className="text-lg font-semibold">Queue G-code for Printing</h3>
          <Button variant="secondary" size="sm" onClick={() => onClose(false)} className="text-gray-500 hover:text-gray-700">✕</Button>
        </div>

        <div className="space-y-3">
          <div>
            <div className="text-sm text-gray-600">File</div>
            <div className="font-medium">{file.name}</div>
          </div>

          <div>
            <Checkbox label="Auto-assign best available printer (recommended)" checked={autoAssign} onChange={(e) => setAutoAssign((e.target as HTMLInputElement).checked)} />
          </div>

          {!autoAssign && (
            <div>
              <div className="text-sm text-gray-600 mb-1">Select Printer</div>
              <div>
                <Select
                  aria-label={`Select printer for ${file.name}`}
                  value={selectedPrinter ?? ''}
                  onChange={(e) => setSelectedPrinter((e.target as HTMLSelectElement).value || undefined)}
                >
                  <option value="">-- Select printer --</option>
                  {printers.map(p => {
                    const compatible = isPrinterCompatible(p);
                    return (
                      <option key={p.id} value={p.id} disabled={!p.isAvailable || !compatible}>
                        {p.name} — {p.model} {p.isAvailable ? '' : '(offline)'}{!compatible ? ' (incompatible)' : ''}
                      </option>
                    );
                  })}
                </Select>
              </div>
            </div>
          )}

          {error && <div className="text-sm text-red-600">{error}</div>}
          {(file.extractedNozzleDiameter || file.extractedMaterial) && (
            <div className="text-sm text-gray-600">
              <div>Compatibility required for this file:</div>
              {file.extractedNozzleDiameter && <div>- Nozzle: {file.extractedNozzleDiameter} mm</div>}
              {file.extractedMaterial && <div>- Material: {file.extractedMaterial}</div>}
            </div>
          )}
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => onClose(false)}>Cancel</Button>
          <Button variant="primary" onClick={handleQueue} disabled={loading}>
            {loading ? 'Queueing…' : 'Queue for Print'}
          </Button>
        </div>
      </div>
    </div>
  );
};
