import React, { useState, useEffect } from 'react';
import { X, Check, Loader2 } from 'lucide-react';
import { usePrinterDetails, useUpdatePrinter, useManufacturers, useModels, useFilamentTypes, useModelDefaultCapabilities } from '@/hooks/useApi';
import { UpdatePrinterDto, PrinterBackend } from '@/types/api';
import { toast } from 'sonner';
import { FilamentTypeSelector } from './FilamentTypeSelector';
import { BackendSelector } from './BackendSelector';
import { Button, Input, Select, Textarea, FormField, Alert, Checkbox } from '@/components/ui';

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
        backendPort: printerDetails.backendPort ?? undefined,
        frontendPort: printerDetails.frontendPort ?? undefined,
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
            <Button
              variant="subtle"
              size="sm"
              onClick={handleClose}
              aria-label="Close edit printer modal"
              title="Close"
              className="!p-1 !h-auto"
            >
              <X className="w-6 h-6" />
            </Button>
          </div>

            {error && (
              <Alert type="error" title="Update Failed" className="mb-4">
                {error}
              </Alert>
            )}

          <form onSubmit={handleSubmit} className="space-y-5">
            <FormField
              label="Name"
              required
              error={validationErrors.name?.[0]}
            >
              <Input
                type="text"
                value={formData.name}
                onChange={e => handleInputChange('name', e.target.value)}
                placeholder="Printer name"
                title="Printer name"
              />
            </FormField>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <FormField
                label="Server URL"
                required
                error={validationErrors.serverUrl?.[0]}
              >
                <Input
                  type="text"
                  value={formData.serverUrl}
                  onChange={e => handleInputChange('serverUrl', e.target.value)}
                  placeholder="https://printer.local"
                  title="Printer server URL"
                />
              </FormField>
              <FormField label="Backend">
                <BackendSelector
                  value={formData.backend}
                  onChange={(backend) => handleInputChange('backend', backend)}
                  ariaLabel="Printer backend"
                />
              </FormField>
            {/* Moonraker/PrusaLink port/API key fields */}
            {formData.backend === PrinterBackend.Moonraker && (
              <div className="col-span-2 grid grid-cols-1 md:grid-cols-2 gap-4">
                <FormField label="Backend Port (API)">
                  <Input
                    type="number"
                    value={formData.backendPort ?? ''}
                    onChange={e => handleInputChange('backendPort', e.target.value ? parseInt(e.target.value, 10) : undefined)}
                    placeholder="7125"
                    min={1}
                    max={65535}
                  />
                </FormField>
                <FormField label="Frontend Port (UI)">
                  <Input
                    type="number"
                    value={formData.frontendPort ?? ''}
                    onChange={e => handleInputChange('frontendPort', e.target.value ? parseInt(e.target.value, 10) : undefined)}
                    placeholder="80"
                    min={1}
                    max={65535}
                  />
                </FormField>
              </div>
            )}
            {formData.backend === PrinterBackend.PrusaLink && (
              <div className="col-span-2">
                <FormField label="API Key (PrusaLink)">
                  <Input
                    type="text"
                    value={formData.apiKey || ''}
                    onChange={e => handleInputChange('apiKey', e.target.value)}
                    placeholder="Enter PrusaLink API Key"
                    title="PrusaLink API Key"
                  />
                </FormField>
              </div>
            )}
            </div>
            <FormField label="Notes">
              <Textarea
                value={formData.notes || ''}
                onChange={e => handleInputChange('notes', e.target.value)}
                rows={3}
                placeholder="Optional notes"
                title="Printer notes"
              />
            </FormField>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <FormField label="Manufacturer">
                <Select
                  value={formData.manufacturerId || ''}
                  onChange={e => { const val = e.target.value || undefined; handleInputChange('manufacturerId', val); setSelectedManufacturer(val); }}
                  title="Manufacturer"
                >
                  <option value="">(none)</option>
                  {manufacturers?.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
                </Select>
              </FormField>
              <FormField label="Model">
                <Select
                  value={formData.modelId || ''}
                  onChange={e => handleInputChange('modelId', e.target.value || undefined)}
                  title="Model"
                >
                  <option value="">(none)</option>
                  {filteredModels.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
                </Select>
              </FormField>
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
                  <FormField label="Max X (mm)">
                    <Input
                      type="number"
                      value={formData.maxBuildVolumeX || ''}
                      onChange={e => handleInputChange('maxBuildVolumeX', parseFloat(e.target.value) || undefined)}
                      placeholder={printerDetails?.modelMaxX?.toString() || '220'}
                      title="Maximum X axis travel"
                      className="text-sm"
                    />
                  </FormField>
                  <FormField label="Max Y (mm)">
                    <Input
                      type="number"
                      value={formData.maxBuildVolumeY || ''}
                      onChange={e => handleInputChange('maxBuildVolumeY', parseFloat(e.target.value) || undefined)}
                      placeholder={printerDetails?.modelMaxY?.toString() || '220'}
                      title="Maximum Y axis travel"
                      className="text-sm"
                    />
                  </FormField>
                  <FormField label="Max Z (mm)">
                    <Input
                      type="number"
                      value={formData.maxBuildVolumeZ || ''}
                      onChange={e => handleInputChange('maxBuildVolumeZ', parseFloat(e.target.value) || undefined)}
                      placeholder={printerDetails?.modelMaxZ?.toString() || '250'}
                      title="Maximum Z axis travel"
                      className="text-sm"
                    />
                  </FormField>
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
                  <FormField label="Nozzle Diameter (mm)" htmlFor="nozzle-diameter">
                    <Input
                      id="nozzle-diameter"
                      type="number"
                      step="0.1"
                      value={formData.nozzleDiameter?.toString() || ''}
                      onChange={e => {
                        const value = e.target.value;
                        handleInputChange('nozzleDiameter', value ? parseFloat(value) : undefined);
                      }}
                      placeholder="0.4"
                      title="Nozzle diameter"
                    />
                  </FormField>
                  <FormField label="Number of Extruders" htmlFor="num-extruders">
                    <Input
                      id="num-extruders"
                      type="number"
                      min="1"
                      max="8"
                      value={formData.numberOfExtruders || 1}
                      onChange={e => handleInputChange('numberOfExtruders', parseInt(e.target.value, 10) || 1)}
                      title="Number of extruders"
                    />
                  </FormField>
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
                  <FormField label="Min Hotend °C">
                    <Input
                      type="number"
                      value={formData.minHotendTemp || ''}
                      onChange={e => handleInputChange('minHotendTemp', parseInt(e.target.value, 10) || undefined)}
                      placeholder="180"
                      title="Minimum hotend temperature"
                      className="text-sm"
                    />
                  </FormField>
                  <FormField label="Max Hotend °C">
                    <Input
                      type="number"
                      value={formData.maxHotendTemp || ''}
                      onChange={e => handleInputChange('maxHotendTemp', parseInt(e.target.value, 10) || undefined)}
                      placeholder="300"
                      title="Maximum hotend temperature"
                      className="text-sm"
                    />
                  </FormField>
                  <FormField label="Min Bed °C">
                    <Input
                      type="number"
                      value={formData.minBedTemp || ''}
                      onChange={e => handleInputChange('minBedTemp', parseInt(e.target.value, 10) || undefined)}
                      placeholder="0"
                      title="Minimum bed temperature"
                      className="text-sm"
                    />
                  </FormField>
                  <FormField label="Max Bed °C">
                    <Input
                      type="number"
                      value={formData.maxBedTemp || ''}
                      onChange={e => handleInputChange('maxBedTemp', parseInt(e.target.value, 10) || undefined)}
                      placeholder="120"
                      title="Maximum bed temperature"
                      className="text-sm"
                    />
                  </FormField>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div className="space-y-3">
                    <Checkbox
                      id="hasHeatedBed"
                      label="Heated bed"
                      checked={formData.hasHeatedBed ?? true}
                      onChange={e => handleInputChange('hasHeatedBed', e.target.checked)}
                    />
                    <Checkbox
                      id="hasEnclosure"
                      label="Enclosure"
                      checked={formData.hasEnclosure ?? false}
                      onChange={e => handleInputChange('hasEnclosure', e.target.checked)}
                    />
                  </div>
                  <div className="space-y-3">
                    <Checkbox
                      id="multiMaterial"
                      label="Multi-material"
                      checked={formData.multiMaterial ?? false}
                      onChange={e => handleInputChange('multiMaterial', e.target.checked)}
                    />
                    <Checkbox
                      id="supportsAutoLeveling"
                      label="Auto-leveling"
                      checked={formData.supportsAutoLeveling ?? false}
                      onChange={e => handleInputChange('supportsAutoLeveling', e.target.checked)}
                    />
                  </div>
                </div>

                <FormField label="Max Print Speed (mm/s)">
                  <Input
                    type="number"
                    value={formData.maxPrintSpeed || ''}
                    onChange={e => handleInputChange('maxPrintSpeed', parseInt(e.target.value, 10) || undefined)}
                    placeholder="150"
                    title="Maximum print speed"
                  />
                </FormField>
              </div>
            </div>
            <div className="flex items-center justify-end space-x-3 pt-2">
              <Button
                type="button"
                variant="secondary"
                onClick={handleClose}
              >
                Cancel
              </Button>
              <Button
                type="submit"
                variant="primary"
                disabled={updateMutation.status === 'pending'}
              >
                <Check className="w-4 h-4 mr-1" />
                {updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes'}
              </Button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}