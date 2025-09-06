import React, { useState, useEffect } from 'react';
import { X, AlertCircle, Check } from 'lucide-react';
import { usePrinterDetails, useUpdatePrinter, useManufacturers, useModels } from '@/hooks/useApi';
import type { UpdatePrinterDto, PrinterBackend } from '@/types/api';
import { toast } from 'sonner';

interface EditPrinterModalProps {
  printerId: string | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

export function EditPrinterModal({ printerId, isOpen, onClose, onSuccess }: EditPrinterModalProps) {
  const { data: printerDetails } = usePrinterDetails(printerId || '');
  const { data: manufacturers } = useManufacturers();
  const [selectedManufacturer, setSelectedManufacturer] = useState<string | undefined>();
  const { data: models } = useModels(selectedManufacturer);
  const updateMutation = useUpdatePrinter();

  const [formData, setFormData] = useState<UpdatePrinterDto | null>(null);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    if (printerDetails) {
      setFormData({
        name: printerDetails.name,
        serverUrl: printerDetails.serverUrl,
        originalServerUrl: printerDetails.originalServerUrl,
        notes: printerDetails.notes,
        manufacturerId: printerDetails.manufacturerId,
        modelId: printerDetails.modelId,
        newManufacturerName: undefined,
        newModelName: undefined,
        dateAcquired: printerDetails.dateAcquired ? new Date(printerDetails.dateAcquired) : undefined,
        backend: printerDetails.backend,
        apiKey: printerDetails.apiKey
      });
      setSelectedManufacturer(printerDetails.manufacturerId);
    }
  }, [printerDetails]);

  const handleInputChange = (field: keyof UpdatePrinterDto, value: unknown) => {
    setFormData(prev => prev ? { ...prev, [field]: value } : prev);
    if (validationErrors[field]) {
      setValidationErrors(prev => { const clone = { ...prev }; delete clone[field]; return clone; });
    }
  };

  const validateForm = (): boolean => {
    if (!formData) return false;
    const errors: Record<string, string[]> = {};
    if (!formData.name?.trim()) errors.name = ['Name is required'];
    if (!formData.serverUrl?.trim()) errors.serverUrl = ['Server URL is required'];
    else {
      try { new URL(formData.serverUrl); } catch { errors.serverUrl = ['Invalid URL']; }
    }
    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!formData || !printerId) return;
    if (!validateForm()) return;
    setError('');
    try {
      const result = await updateMutation.mutateAsync({ id: printerId, printer: formData });
      toast.success(`Printer "${result.name}" updated`);
      onSuccess?.();
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to update printer';
      toast.error(message);
      setError(message);
    }
  };

  const handleClose = () => {
    onClose();
    setValidationErrors({});
    setError('');
  };

  if (!isOpen || !formData) return null;

  const filteredModels = models || [];

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-center justify-center min-h-screen px-4 py-8 text-center sm:block sm:p-0">
        <div className="fixed inset-0 bg-pf-bg-0 bg-opacity-75" onClick={handleClose} />
        <div className="inline-block align-bottom bg-pf-bg-1 rounded-xl px-6 pt-6 pb-6 text-left shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-2xl sm:w-full border border-pf-border relative">
          <div className="flex items-center justify-between mb-6">
            <h3 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">Edit Printer</h3>
            <button onClick={handleClose} className="text-pf-text-tertiary hover:text-pf-text-primary" aria-label="Close edit printer modal" title="Close">
              <X className="w-6 h-6" />
            </button>
          </div>

            {error && (
              <div className="mb-4 p-3 rounded bg-pf-error-bg text-pf-error-text flex items-start">
                <AlertCircle className="w-5 h-5 mr-2 mt-0.5" />
                <div>
                  <p className="font-medium">Update Failed</p>
                  <p className="text-sm opacity-90">{error}</p>
                </div>
              </div>
            )}

          <form onSubmit={handleSubmit} className="space-y-5">
            <div>
              <label className="block text-sm font-medium text-pf-text-secondary mb-1">Name</label>
              <input
                type="text"
                value={formData.name}
                onChange={e => handleInputChange('name', e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                placeholder="Printer name"
                title="Printer name"
              />
              {validationErrors.name && <p className="text-xs text-pf-error-text mt-1">{validationErrors.name[0]}</p>}
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">Server URL</label>
                <input
                  type="text"
                  value={formData.serverUrl}
                  onChange={e => handleInputChange('serverUrl', e.target.value)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  placeholder="https://printer.local"
                  title="Printer server URL"
                />
                {validationErrors.serverUrl && <p className="text-xs text-pf-error-text mt-1">{validationErrors.serverUrl[0]}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">Backend</label>
                <select
                  value={formData.backend}
                  onChange={e => handleInputChange('backend', parseInt(e.target.value, 10) as PrinterBackend)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  title="Printer backend"
                >
                  <option value={0}>Moonraker</option>
                  <option value={1}>PrusaLink</option>
                  <option value={2}>SDCP</option>
                </select>
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium text-pf-text-secondary mb-1">Notes</label>
              <textarea
                value={formData.notes || ''}
                onChange={e => handleInputChange('notes', e.target.value)}
                rows={3}
                className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                placeholder="Optional notes"
                title="Printer notes"
              />
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">Manufacturer</label>
                <select
                  value={formData.manufacturerId || ''}
                  onChange={e => { const val = e.target.value || undefined; handleInputChange('manufacturerId', val); setSelectedManufacturer(val); }}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  title="Manufacturer"
                >
                  <option value="">(none)</option>
                  {manufacturers?.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">Model</label>
                <select
                  value={formData.modelId || ''}
                  onChange={e => handleInputChange('modelId', e.target.value || undefined)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  title="Model"
                >
                  <option value="">(none)</option>
                  {filteredModels.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
                </select>
              </div>
            </div>
            {formData.backend === 1 && (
              <div>
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">API Key (PrusaLink)</label>
                <input
                  type="text"
                  value={formData.apiKey || ''}
                  onChange={e => handleInputChange('apiKey', e.target.value)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  placeholder="Enter PrusaLink API Key"
                  title="PrusaLink API Key"
                />
              </div>
            )}
            <div className="flex items-center justify-end space-x-3 pt-2">
              <button type="button" onClick={handleClose} className="px-4 py-2 text-sm rounded-lg bg-pf-text-tertiary hover:bg-pf-text-secondary text-white transition-colors">Cancel</button>
              <button type="submit" disabled={updateMutation.status === 'pending'} className="px-4 py-2 text-sm rounded-lg bg-pf-accent hover:bg-pf-accent-hover text-white flex items-center transition-colors disabled:opacity-50">
                <Check className="w-4 h-4 mr-1" />
                {updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes'}
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}