import React, { useState, useEffect } from 'react';
import { X, AlertCircle, Check, Loader2 } from 'lucide-react';
import { usePrinterDetails, useUpdatePrinter, useManufacturers, useModels, useFilamentTypes, useModelDefaultCapabilities } from '@/hooks/useApi';
import { UpdatePrinterDto, PrinterBackend } from '@/types/api';
import { toast } from 'sonner';
import { FilamentTypeSelector } from './FilamentTypeSelector';
import { BackendSelector } from './BackendSelector';

interface EditPrinterModalProps {
  printerId: string | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

export function EditPrinterModal({ printerId, isOpen, onClose, onSuccess }: EditPrinterModalProps) {
  const { data: printerDetails } = usePrinterDetails(printerId || '');
  const { data: manufacturers } = useManufacturers();
  const { data: filamentTypes } = useFilamentTypes();
  const [selectedManufacturer, setSelectedManufacturer] = useState<string | undefined>();
  const { data: models } = useModels(selectedManufacturer);
  const updateMutation = useUpdatePrinter();

  const [formData, setFormData] = useState<UpdatePrinterDto | null>(null);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});
  const [lastModelId, setLastModelId] = useState<string | undefined>();
  
  // Fetch default capabilities for the selected model
  const { data: defaultCapabilities, isLoading: isLoadingCapabilities } = useModelDefaultCapabilities(formData?.modelId);

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
        apiKey: printerDetails.apiKey,
        // Printer capabilities
        nozzleDiameter: printerDetails.capabilities?.nozzleDiameter,
        supportedMaterials: printerDetails.capabilities?.supportedMaterials,
        maxBuildVolumeX: printerDetails.capabilities?.maxBuildVolumeX || printerDetails.modelMaxX,
        maxBuildVolumeY: printerDetails.capabilities?.maxBuildVolumeY || printerDetails.modelMaxY,
        maxBuildVolumeZ: printerDetails.capabilities?.maxBuildVolumeZ || printerDetails.modelMaxZ,
        hasHeatedBed: printerDetails.capabilities?.hasHeatedBed,
        hasEnclosure: printerDetails.capabilities?.hasEnclosure,
        multiMaterial: printerDetails.capabilities?.multiMaterial,
        numberOfExtruders: printerDetails.capabilities?.numberOfExtruders,
        minHotendTemp: printerDetails.capabilities?.minHotendTemp,
        maxHotendTemp: printerDetails.capabilities?.maxHotendTemp,
        minBedTemp: printerDetails.capabilities?.minBedTemp,
        maxBedTemp: printerDetails.capabilities?.maxBedTemp,
        supportsAutoLeveling: printerDetails.capabilities?.supportsAutoLeveling,
        maxPrintSpeed: printerDetails.capabilities?.maxPrintSpeed,
      });
      // Prevent applying model defaults immediately after loading existing printer
      setLastModelId(printerDetails.modelId);
      setSelectedManufacturer(printerDetails.manufacturerId);
    }
  }, [printerDetails]);
  
  // Update capability fields when default capabilities are fetched for a new model
  useEffect(() => {
    // Only update if modelId has changed and we have default capabilities
    if (formData?.modelId && formData.modelId !== lastModelId && defaultCapabilities) {
      setFormData(prev => {
        if (!prev) return prev;
        return {
          ...prev,
          nozzleDiameter: defaultCapabilities.nozzleDiameter ?? prev.nozzleDiameter,
          supportedMaterials: defaultCapabilities.supportedMaterials ?? prev.supportedMaterials,
          maxBuildVolumeX: defaultCapabilities.maxBuildVolumeX ?? prev.maxBuildVolumeX,
          maxBuildVolumeY: defaultCapabilities.maxBuildVolumeY ?? prev.maxBuildVolumeY,
          maxBuildVolumeZ: defaultCapabilities.maxBuildVolumeZ ?? prev.maxBuildVolumeZ,
          hasHeatedBed: defaultCapabilities.hasHeatedBed,
          hasEnclosure: defaultCapabilities.hasEnclosure,
          multiMaterial: defaultCapabilities.multiMaterial,
          numberOfExtruders: defaultCapabilities.numberOfExtruders,
          minHotendTemp: defaultCapabilities.minHotendTemp ?? prev.minHotendTemp,
          maxHotendTemp: defaultCapabilities.maxHotendTemp ?? prev.maxHotendTemp,
          minBedTemp: defaultCapabilities.minBedTemp ?? prev.minBedTemp,
          maxBedTemp: defaultCapabilities.maxBedTemp ?? prev.maxBedTemp,
          supportsAutoLeveling: defaultCapabilities.supportsAutoLeveling,
          maxPrintSpeed: defaultCapabilities.maxPrintSpeed ?? prev.maxPrintSpeed,
        };
      });
      setLastModelId(formData.modelId);
    }
  }, [formData?.modelId, defaultCapabilities, lastModelId]);

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
                <BackendSelector
                  value={formData.backend}
                  onChange={(backend) => handleInputChange('backend', backend)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                  title="Printer backend"
                />
              </div>
            {/* Moonraker/PrusaLink port/API key fields */}
            {formData.backend === PrinterBackend.Moonraker && (
              <div className="col-span-2 grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Backend Port (API)</label>
                  <input
                    type="number"
                    value={formData.backendPort ?? 7125}
                    onChange={e => handleInputChange('backendPort', parseInt(e.target.value, 10) || 7125)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="7125"
                    min={1}
                    max={65535}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Frontend Port (UI)</label>
                  <input
                    type="number"
                    value={formData.frontendPort ?? 80}
                    onChange={e => handleInputChange('frontendPort', parseInt(e.target.value, 10) || 80)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="80"
                    min={1}
                    max={65535}
                  />
                </div>
              </div>
            )}
            {formData.backend === PrinterBackend.PrusaLink && (
              <div className="col-span-2">
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

            {/* Printer Type & Build Volume Section */}
            <div className="border-t pt-5 mt-5">
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Printer Type & Build Volume</h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Printer Type</label>
                  <div className="px-3 py-2 rounded-lg bg-pf-bg-2 border border-pf-border text-pf-text-secondary">
                    {(() => {
                      // Try to get motion type from selected model first
                      if (formData.modelId && filteredModels) {
                        const selectedModel = filteredModels.find(m => m.id === formData.modelId);
                        if (selectedModel?.motionType) {
                          return selectedModel.motionType;
                        }
                      }
                      // Fallback to printer details (for initial load)
                      const modelMotionType = printerDetails?.modelMotionType;
                      if (modelMotionType !== undefined) {
                        const typeNames = ['Cartesian', 'CoreXY', 'Delta', 'Unknown'];
                        return typeNames[modelMotionType] || 'Unknown';
                      }
                      return 'Not specified';
                    })()}
                  </div>
                </div>
                <div className="grid grid-cols-3 gap-2">
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max X (mm)</label>
                    <input
                      type="number"
                      value={formData.maxBuildVolumeX || ''}
                      onChange={e => handleInputChange('maxBuildVolumeX', parseFloat(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder={printerDetails?.modelMaxX?.toString() || '220'}
                      title="Maximum X axis travel"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Y (mm)</label>
                    <input
                      type="number"
                      value={formData.maxBuildVolumeY || ''}
                      onChange={e => handleInputChange('maxBuildVolumeY', parseFloat(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder={printerDetails?.modelMaxY?.toString() || '220'}
                      title="Maximum Y axis travel"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Z (mm)</label>
                    <input
                      type="number"
                      value={formData.maxBuildVolumeZ || ''}
                      onChange={e => handleInputChange('maxBuildVolumeZ', parseFloat(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder={printerDetails?.modelMaxZ?.toString() || '250'}
                      title="Maximum Z axis travel"
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Printer Capabilities Section */}
            <div className="border-t pt-5 mt-5">
              <div className="flex items-center justify-between mb-4">
                <h4 className="text-lg font-medium text-pf-text-primary">Printer Capabilities</h4>
                {isLoadingCapabilities && formData.modelId && (
                  <div className="flex items-center text-sm text-pf-text-secondary">
                    <Loader2 className="w-4 h-4 mr-2 animate-spin" />
                    Loading defaults...
                  </div>
                )}
              </div>
              <div className="space-y-4">
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">Nozzle Diameter (mm)</label>
                    <input
                      type="number"
                      step="0.1"
                      value={formData.nozzleDiameter?.toString() || ''}
                      onChange={e => {
                        const value = e.target.value;
                        handleInputChange('nozzleDiameter', value ? parseFloat(value) : undefined);
                      }}
                      className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="0.4"
                      title="Nozzle diameter"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">Number of Extruders</label>
                    <input
                      type="number"
                      min="1"
                      max="8"
                      value={formData.numberOfExtruders || 1}
                      onChange={e => handleInputChange('numberOfExtruders', parseInt(e.target.value, 10) || 1)}
                      className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      title="Number of extruders"
                    />
                  </div>
                </div>

                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Supported Materials</label>
                  <FilamentTypeSelector
                    availableFilamentTypes={filamentTypes}
                    selectedFilamentTypes={formData.supportedMaterials || []}
                    onSelectionChange={(selectedTypes) => handleInputChange('supportedMaterials', selectedTypes)}
                  />
                </div>

                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Min Hotend °C</label>
                    <input
                      type="number"
                      value={formData.minHotendTemp || ''}
                      onChange={e => handleInputChange('minHotendTemp', parseInt(e.target.value, 10) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="180"
                      title="Minimum hotend temperature"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Hotend °C</label>
                    <input
                      type="number"
                      value={formData.maxHotendTemp || ''}
                      onChange={e => handleInputChange('maxHotendTemp', parseInt(e.target.value, 10) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="300"
                      title="Maximum hotend temperature"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Min Bed °C</label>
                    <input
                      type="number"
                      value={formData.minBedTemp || ''}
                      onChange={e => handleInputChange('minBedTemp', parseInt(e.target.value, 10) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="0"
                      title="Minimum bed temperature"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Bed °C</label>
                    <input
                      type="number"
                      value={formData.maxBedTemp || ''}
                      onChange={e => handleInputChange('maxBedTemp', parseInt(e.target.value, 10) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="120"
                      title="Maximum bed temperature"
                    />
                  </div>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div className="space-y-3">
                    <div className="flex items-center">
                      <input
                        type="checkbox"
                        id="hasHeatedBed"
                        checked={formData.hasHeatedBed ?? true}
                        onChange={e => handleInputChange('hasHeatedBed', e.target.checked)}
                        className="mr-2"
                      />
                      <label htmlFor="hasHeatedBed" className="text-sm text-pf-text-primary">Heated bed</label>
                    </div>
                    <div className="flex items-center">
                      <input
                        type="checkbox"
                        id="hasEnclosure"
                        checked={formData.hasEnclosure ?? false}
                        onChange={e => handleInputChange('hasEnclosure', e.target.checked)}
                        className="mr-2"
                      />
                      <label htmlFor="hasEnclosure" className="text-sm text-pf-text-primary">Enclosure</label>
                    </div>
                  </div>
                  <div className="space-y-3">
                    <div className="flex items-center">
                      <input
                        type="checkbox"
                        id="multiMaterial"
                        checked={formData.multiMaterial ?? false}
                        onChange={e => handleInputChange('multiMaterial', e.target.checked)}
                        className="mr-2"
                      />
                      <label htmlFor="multiMaterial" className="text-sm text-pf-text-primary">Multi-material</label>
                    </div>
                    <div className="flex items-center">
                      <input
                        type="checkbox"
                        id="supportsAutoLeveling"
                        checked={formData.supportsAutoLeveling ?? false}
                        onChange={e => handleInputChange('supportsAutoLeveling', e.target.checked)}
                        className="mr-2"
                      />
                      <label htmlFor="supportsAutoLeveling" className="text-sm text-pf-text-primary">Auto-leveling</label>
                    </div>
                  </div>
                </div>

                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Max Print Speed (mm/s)</label>
                  <input
                    type="number"
                    value={formData.maxPrintSpeed || ''}
                    onChange={e => handleInputChange('maxPrintSpeed', parseInt(e.target.value, 10) || undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="150"
                    title="Maximum print speed"
                  />
                </div>
              </div>
            </div>
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