import React, { useState, useEffect } from 'react';
import { X, AlertCircle, Check } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useFilamentTypes } from '@/hooks/useApi';
import type { PrinterModelDto, UpdateModelRequest, MotionTypeString } from '@/types/api';
import { toast } from 'sonner';
import { FilamentTypeSelector } from './FilamentTypeSelector';
import { BackendSelector } from './BackendSelector';

interface EditModelModalProps {
  model: PrinterModelDto | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

export function EditModelModal({ model, isOpen, onClose, onSuccess }: EditModelModalProps) {
  const queryClient = useQueryClient();
  const { data: filamentTypes } = useFilamentTypes();
  const [formData, setFormData] = useState<UpdateModelRequest | null>(null);
  const [supportedMaterialNames, setSupportedMaterialNames] = useState<string[]>([]);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});

  const updateMutation = useMutation({
    mutationFn: async ({ id, data }: { id: string; data: UpdateModelRequest }) => {
      const response = await fetch(`/api/catalog/printer-models/${id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data),
      });
      if (!response.ok) {
        throw new Error(`Failed to update model: ${response.statusText}`);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-models'] });
      toast.success(`Model "${formData?.name}" updated successfully`);
      onSuccess?.();
      onClose();
    },
    onError: (error) => {
      const message = error instanceof Error ? error.message : 'Failed to update model';
      toast.error(message);
      setError(message);
    },
  });

  useEffect(() => {
    if (model) {
      setFormData({
        name: model.name,
        motionType: model.motionType,
        maxX: model.maxX,
        maxY: model.maxY,
        maxZ: model.maxZ,
        defaultBackend: model.defaultBackend,
        supportedFilamentTypeIds: undefined, // Will be set based on supportedMaterialNames
        
        // Default capabilities
        defaultNozzleDiameter: model.defaultNozzleDiameter,
        hasHeatedBed: model.hasHeatedBed,
        hasEnclosure: model.hasEnclosure,
        multiMaterial: model.multiMaterial,
        numberOfExtruders: model.numberOfExtruders,
        supportsAutoLeveling: model.supportsAutoLeveling,
        
        // Temperature ranges
        minHotendTemp: model.minHotendTemp,
        maxHotendTemp: model.maxHotendTemp,
        minBedTemp: model.minBedTemp,
        maxBedTemp: model.maxBedTemp,
        
        // Speed capabilities
        maxPrintSpeed: model.maxPrintSpeed,
      });
      
      // Initialize supported material names from the model
      setSupportedMaterialNames(model.supportedFilamentTypes || []);
    }
  }, [model]);

  const handleInputChange = (field: keyof UpdateModelRequest, value: unknown) => {
    setFormData(prev => prev ? { ...prev, [field]: value } : prev);
    if (validationErrors[field]) {
      setValidationErrors(prev => { const clone = { ...prev }; delete clone[field]; return clone; });
    }
  };

  const validateForm = (): boolean => {
    if (!formData) return false;
    const errors: Record<string, string[]> = {};
    if (!formData.name?.trim()) errors.name = ['Name is required'];
    
    // Validate temperature ranges
    if (formData.minHotendTemp && formData.maxHotendTemp && formData.minHotendTemp >= formData.maxHotendTemp) {
      errors.minHotendTemp = ['Min hotend temp must be less than max'];
    }
    if (formData.minBedTemp && formData.maxBedTemp && formData.minBedTemp >= formData.maxBedTemp) {
      errors.minBedTemp = ['Min bed temp must be less than max'];
    }
    
    // Validate build volume
    if (formData.maxX && formData.maxX <= 0) errors.maxX = ['Build volume X must be positive'];
    if (formData.maxY && formData.maxY <= 0) errors.maxY = ['Build volume Y must be positive'];
    if (formData.maxZ && formData.maxZ <= 0) errors.maxZ = ['Build volume Z must be positive'];
    
    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!formData || !model) return;
    if (!validateForm()) return;
    setError('');
    
    // Convert material names to IDs for the API
    const supportedFilamentTypeIds = supportedMaterialNames
      .map(name => filamentTypes?.find(ft => ft.name === name)?.id)
      .filter((id): id is string => !!id);
    
    const updateData: UpdateModelRequest = {
      ...formData,
      supportedFilamentTypeIds
    };
    
    try {
      await updateMutation.mutateAsync({ id: model.id, data: updateData });
    } catch {
      // Error handled by mutation onError
    }
  };

  const handleClose = () => {
    onClose();
    setValidationErrors({});
    setError('');
  };

  if (!isOpen || !formData || !model) return null;

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-center justify-center min-h-screen px-4 py-8 text-center sm:block sm:p-0">
        <div className="fixed inset-0 bg-pf-bg-0 bg-opacity-75" onClick={handleClose} />
        <div className="inline-block align-bottom bg-pf-bg-1 rounded-xl px-6 pt-6 pb-6 text-left shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-4xl sm:w-full border border-pf-border relative max-h-[90vh] overflow-y-auto">
          <div className="flex items-center justify-between mb-6">
            <h3 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">Edit Printer Model</h3>
            <button onClick={handleClose} className="text-pf-text-tertiary hover:text-pf-text-primary" aria-label="Close edit model modal" title="Close">
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

          <form onSubmit={handleSubmit} className="space-y-6">
            {/* Basic Information */}
            <div>
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Basic Information</h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Model Name</label>
                  <input
                    type="text"
                    value={formData.name}
                    onChange={e => handleInputChange('name', e.target.value)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="Model name"
                    required
                  />
                  {validationErrors.name && <p className="text-xs text-pf-error-text mt-1">{validationErrors.name[0]}</p>}
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Printer Type</label>
                  <select
                    value={formData.motionType || ''}
                    onChange={e => handleInputChange('motionType', e.target.value ? e.target.value as MotionTypeString : undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    title="Printer Type"
                  >
                    <option value="">Select type...</option>
                    <option value="Cartesian">Cartesian</option>
                    <option value="CoreXY">CoreXY</option>
                    <option value="Delta">Delta</option>
                    <option value="Unknown">Unknown</option>
                  </select>
                </div>
              </div>
            </div>

            {/* Build Volume */}
            <div className="border-t pt-5">
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Build Volume</h4>
              <div className="grid grid-cols-3 gap-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Max X (mm)</label>
                  <input
                    type="number"
                    value={formData.maxX || ''}
                    onChange={e => handleInputChange('maxX', parseFloat(e.target.value) || undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="220"
                  />
                  {validationErrors.maxX && <p className="text-xs text-pf-error-text mt-1">{validationErrors.maxX[0]}</p>}
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Max Y (mm)</label>
                  <input
                    type="number"
                    value={formData.maxY || ''}
                    onChange={e => handleInputChange('maxY', parseFloat(e.target.value) || undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="220"
                  />
                  {validationErrors.maxY && <p className="text-xs text-pf-error-text mt-1">{validationErrors.maxY[0]}</p>}
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Max Z (mm)</label>
                  <input
                    type="number"
                    value={formData.maxZ || ''}
                    onChange={e => handleInputChange('maxZ', parseFloat(e.target.value) || undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="250"
                  />
                  {validationErrors.maxZ && <p className="text-xs text-pf-error-text mt-1">{validationErrors.maxZ[0]}</p>}
                </div>
              </div>
            </div>

            {/* Printer Capabilities */}
            <div className="border-t pt-5">
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Printer Capabilities</h4>
              
              {/* Nozzle and Extruders */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Default Nozzle Diameter (mm)</label>
                  <input
                    type="number"
                    step="0.1"
                    value={formData.defaultNozzleDiameter || ''}
                    onChange={e => handleInputChange('defaultNozzleDiameter', parseFloat(e.target.value) || undefined)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    placeholder="0.4"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-secondary mb-1">Number of Extruders</label>
                  <input
                    type="number"
                    min="1"
                    max="8"
                    value={formData.numberOfExtruders || 1}
                    onChange={e => handleInputChange('numberOfExtruders', parseInt(e.target.value) || 1)}
                    className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                    title="Number of Extruders"
                  />
                </div>
              </div>

              {/* Capability Checkboxes */}
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
                <div className="flex items-center">
                  <input
                    type="checkbox"
                    id="hasHeatedBed"
                    checked={formData.hasHeatedBed ?? true}
                    onChange={e => handleInputChange('hasHeatedBed', e.target.checked)}
                    className="mr-2"
                  />
                  <label htmlFor="hasHeatedBed" className="text-sm text-pf-text-primary">Heated Bed</label>
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
                <div className="flex items-center">
                  <input
                    type="checkbox"
                    id="multiMaterial"
                    checked={formData.multiMaterial ?? false}
                    onChange={e => handleInputChange('multiMaterial', e.target.checked)}
                    className="mr-2"
                  />
                  <label htmlFor="multiMaterial" className="text-sm text-pf-text-primary">Multi-Material</label>
                </div>
                <div className="flex items-center">
                  <input
                    type="checkbox"
                    id="supportsAutoLeveling"
                    checked={formData.supportsAutoLeveling ?? false}
                    onChange={e => handleInputChange('supportsAutoLeveling', e.target.checked)}
                    className="mr-2"
                  />
                  <label htmlFor="supportsAutoLeveling" className="text-sm text-pf-text-primary">Auto-Leveling</label>
                </div>
              </div>

              {/* Supported Materials */}
              <div className="mb-4">
                <label className="block text-sm font-medium text-pf-text-secondary mb-1">Supported Materials</label>
                <FilamentTypeSelector
                  availableFilamentTypes={filamentTypes}
                  selectedFilamentTypes={supportedMaterialNames}
                  onSelectionChange={setSupportedMaterialNames}
                />
              </div>

              {/* Temperature Ranges */}
              <div className="mb-4">
                <h5 className="text-md font-medium text-pf-text-primary mb-2">Temperature Ranges</h5>
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Min Hotend °C</label>
                    <input
                      type="number"
                      value={formData.minHotendTemp || ''}
                      onChange={e => handleInputChange('minHotendTemp', parseInt(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="0"
                    />
                    {validationErrors.minHotendTemp && <p className="text-xs text-pf-error-text mt-1">{validationErrors.minHotendTemp[0]}</p>}
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Hotend °C</label>
                    <input
                      type="number"
                      value={formData.maxHotendTemp || ''}
                      onChange={e => handleInputChange('maxHotendTemp', parseInt(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="300"
                    />
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Min Bed °C</label>
                    <input
                      type="number"
                      value={formData.minBedTemp || ''}
                      onChange={e => handleInputChange('minBedTemp', parseInt(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="0"
                    />
                    {validationErrors.minBedTemp && <p className="text-xs text-pf-error-text mt-1">{validationErrors.minBedTemp[0]}</p>}
                  </div>
                  <div>
                    <label className="block text-xs font-medium text-pf-text-secondary mb-1">Max Bed °C</label>
                    <input
                      type="number"
                      value={formData.maxBedTemp || ''}
                      onChange={e => handleInputChange('maxBedTemp', parseInt(e.target.value) || undefined)}
                      className="w-full px-2 py-2 text-sm rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="120"
                    />
                  </div>
                </div>
              </div>

              {/* Performance */}
              <div className="mb-4">
                <h5 className="text-md font-medium text-pf-text-primary mb-2">Performance</h5>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">Max Print Speed (mm/s)</label>
                    <input
                      type="number"
                      value={formData.maxPrintSpeed || ''}
                      onChange={e => handleInputChange('maxPrintSpeed', parseInt(e.target.value) || undefined)}
                      className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      placeholder="150"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-pf-text-secondary mb-1">Default Backend</label>
                    <BackendSelector
                      value={formData.defaultBackend}
                      onChange={(backend) => handleInputChange('defaultBackend', backend)}
                      className="w-full px-3 py-2 rounded-lg bg-pf-panel border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary"
                      title="Default Backend"
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Action Buttons */}
            <div className="flex items-center justify-end space-x-3 pt-4 border-t">
              <button 
                type="button" 
                onClick={handleClose} 
                className="px-4 py-2 text-sm rounded-lg bg-pf-text-tertiary hover:bg-pf-text-secondary text-white transition-colors"
              >
                Cancel
              </button>
              <button 
                type="submit" 
                disabled={updateMutation.status === 'pending'} 
                className="px-4 py-2 text-sm rounded-lg bg-pf-accent hover:bg-pf-accent-hover text-white flex items-center transition-colors disabled:opacity-50"
              >
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