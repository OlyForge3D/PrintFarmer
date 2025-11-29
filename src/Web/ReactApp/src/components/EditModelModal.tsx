import React, { useState, useEffect } from 'react';
import { Check } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useFilamentTypes } from '@/hooks/useApi';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import type { PrinterModelDto, UpdateModelRequest, MotionTypeString } from '@/types/api';
import { toast } from 'sonner';
import { FilamentTypeSelector } from './FilamentTypeSelector';
import { BackendSelector } from './BackendSelector';
import { Modal } from './ui/Modal';
import { Button } from './ui/Button';
import { Input } from './ui/Input';
import { FormField } from './ui/FormField';
import { Alert } from './ui/Alert';

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

  // Determine if this is add (temp ID) or edit (real ID)
  const isAddMode = model?.id?.toString().startsWith('temp-');

  const createMutation = useMutation({
    mutationFn: async (data: UpdateModelRequest) => {
      const response = await fetch(`${getApiBaseUrl()}/catalog/printer-models`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
        body: JSON.stringify(data),
      });
      if (!response.ok) {
        throw new Error(`Failed to create model: ${response.statusText}`);
      }
      return response.json();
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-models'] });
      toast.success(`Model "${formData?.name}" created successfully`);
      onSuccess?.();
      onClose();
    },
    onError: (error) => {
      const message = error instanceof Error ? error.message : 'Failed to create model';
      toast.error(message);
      setError(message);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, data }: { id: string; data: UpdateModelRequest }) => {
      const response = await fetch(`${getApiBaseUrl()}/catalog/printer-models/${id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
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
      if (isAddMode) {
        // For add mode, use create mutation and exclude the temp ID
        const createData: any = {
          ...updateData,
          manufacturerId: model.manufacturerId,
          // Don't include the temp ID
        };
        delete createData.id;
        await createMutation.mutateAsync(createData);
      } else {
        // For edit mode, use update mutation
        await updateMutation.mutateAsync({ id: model.id, data: updateData });
      }
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

  const footerContent = (
    <div className="flex gap-3 justify-end">
      <Button variant="secondary" size="lg" onClick={handleClose}>
        Cancel
      </Button>
      <Button
        variant={isAddMode ? 'success' : 'primary'}
        size="lg"
        disabled={isAddMode ? createMutation.status === 'pending' : updateMutation.status === 'pending'}
        iconRight={<Check className="w-4 h-4" />}
        onClick={handleSubmit}
      >
        {isAddMode 
          ? createMutation.status === 'pending' ? 'Creating...' : 'Create Model'
          : updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes'
        }
      </Button>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={`${isAddMode ? 'Add' : 'Edit'} Printer Model`}
      size="full"
      footer={footerContent}
    >
      <form onSubmit={handleSubmit} className="space-y-6">
        {error && (
          <Alert type="error" title="Update Failed">
            {error}
          </Alert>
        )}

        {/* Basic Information */}
        <div>
          <h4 className="text-lg font-medium text-pf-text-primary mb-4">Basic Information</h4>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <FormField
              label="Model Name"
              error={validationErrors.name?.[0]}
              required
            >
              <Input
                type="text"
                value={formData.name}
                onChange={e => handleInputChange('name', e.target.value)}
                placeholder="Model name"
                invalid={!!validationErrors.name}
              />
            </FormField>
            <FormField label="Printer Type">
              <select
                value={formData.motionType || ''}
                onChange={e => handleInputChange('motionType', e.target.value ? e.target.value as MotionTypeString : undefined)}
                className="w-full px-3 py-2 rounded-lg bg-pf-bg-0 border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary text-sm"
              >
                {!formData.motionType && <option value="">Select type...</option>}
                <option value="Cartesian">Cartesian</option>
                <option value="CoreXY">CoreXY</option>
                <option value="Delta">Delta</option>
                <option value="Unknown">Unknown</option>
              </select>
            </FormField>
          </div>
        </div>

        {/* Build Volume */}
        <div className="border-t pt-5">
          <h4 className="text-lg font-medium text-pf-text-primary mb-4">Build Volume</h4>
          <div className="grid grid-cols-3 gap-4">
            <FormField
              label="Max X (mm)"
              error={validationErrors.maxX?.[0]}
            >
              <Input
                type="number"
                value={formData.maxX || ''}
                onChange={e => handleInputChange('maxX', parseFloat(e.target.value) || undefined)}
                placeholder="220"
                invalid={!!validationErrors.maxX}
              />
            </FormField>
            <FormField
              label="Max Y (mm)"
              error={validationErrors.maxY?.[0]}
            >
              <Input
                type="number"
                value={formData.maxY || ''}
                onChange={e => handleInputChange('maxY', parseFloat(e.target.value) || undefined)}
                placeholder="220"
                invalid={!!validationErrors.maxY}
              />
            </FormField>
            <FormField
              label="Max Z (mm)"
              error={validationErrors.maxZ?.[0]}
            >
              <Input
                type="number"
                value={formData.maxZ || ''}
                onChange={e => handleInputChange('maxZ', parseFloat(e.target.value) || undefined)}
                placeholder="250"
                invalid={!!validationErrors.maxZ}
              />
            </FormField>
          </div>
        </div>

        {/* Printer Capabilities */}
        <div className="border-t pt-5">
          <h4 className="text-lg font-medium text-pf-text-primary mb-4">Printer Capabilities</h4>
          
          {/* Nozzle and Extruders */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
            <FormField label="Default Nozzle Diameter (mm)">
              <Input
                type="number"
                step="0.1"
                value={formData.defaultNozzleDiameter || ''}
                onChange={e => handleInputChange('defaultNozzleDiameter', parseFloat(e.target.value) || undefined)}
                placeholder="0.4"
              />
            </FormField>
            <FormField label="Number of Extruders">
              <Input
                type="number"
                min="1"
                max="8"
                value={formData.numberOfExtruders || 1}
                onChange={e => handleInputChange('numberOfExtruders', parseInt(e.target.value) || 1)}
              />
            </FormField>
          </div>

          {/* Capability Checkboxes */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                id="hasHeatedBed"
                checked={formData.hasHeatedBed ?? true}
                onChange={e => handleInputChange('hasHeatedBed', e.target.checked)}
                className="rounded"
              />
              <label htmlFor="hasHeatedBed" className="text-sm text-pf-text-primary cursor-pointer">Heated Bed</label>
            </div>
            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                id="hasEnclosure"
                checked={formData.hasEnclosure ?? false}
                onChange={e => handleInputChange('hasEnclosure', e.target.checked)}
                className="rounded"
              />
              <label htmlFor="hasEnclosure" className="text-sm text-pf-text-primary cursor-pointer">Enclosure</label>
            </div>
            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                id="multiMaterial"
                checked={formData.multiMaterial ?? false}
                onChange={e => handleInputChange('multiMaterial', e.target.checked)}
                className="rounded"
              />
              <label htmlFor="multiMaterial" className="text-sm text-pf-text-primary cursor-pointer">Multi-Material</label>
            </div>
            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                id="supportsAutoLeveling"
                checked={formData.supportsAutoLeveling ?? false}
                onChange={e => handleInputChange('supportsAutoLeveling', e.target.checked)}
                className="rounded"
              />
              <label htmlFor="supportsAutoLeveling" className="text-sm text-pf-text-primary cursor-pointer">Auto-Leveling</label>
            </div>
          </div>

          {/* Supported Materials */}
          <div className="mb-4">
            <label className="block text-sm font-medium text-pf-text-secondary mb-2">Supported Materials</label>
            <FilamentTypeSelector
              availableFilamentTypes={filamentTypes}
              selectedFilamentTypes={supportedMaterialNames}
              onSelectionChange={setSupportedMaterialNames}
            />
          </div>

          {/* Temperature Ranges */}
          <div className="mb-4">
            <h5 className="text-md font-medium text-pf-text-primary mb-3">Temperature Ranges</h5>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
              <FormField
                label="Min Hotend °C"
                error={validationErrors.minHotendTemp?.[0]}
              >
                <Input
                  type="number"
                  value={formData.minHotendTemp || ''}
                  onChange={e => handleInputChange('minHotendTemp', parseInt(e.target.value) || undefined)}
                  placeholder="0"
                  invalid={!!validationErrors.minHotendTemp}
                />
              </FormField>
              <FormField label="Max Hotend °C">
                <Input
                  type="number"
                  value={formData.maxHotendTemp || ''}
                  onChange={e => handleInputChange('maxHotendTemp', parseInt(e.target.value) || undefined)}
                  placeholder="300"
                />
              </FormField>
              <FormField
                label="Min Bed °C"
                error={validationErrors.minBedTemp?.[0]}
              >
                <Input
                  type="number"
                  value={formData.minBedTemp || ''}
                  onChange={e => handleInputChange('minBedTemp', parseInt(e.target.value) || undefined)}
                  placeholder="0"
                  invalid={!!validationErrors.minBedTemp}
                />
              </FormField>
              <FormField label="Max Bed °C">
                <Input
                  type="number"
                  value={formData.maxBedTemp || ''}
                  onChange={e => handleInputChange('maxBedTemp', parseInt(e.target.value) || undefined)}
                  placeholder="120"
                />
              </FormField>
            </div>
          </div>

          {/* Performance */}
          <div className="mb-4">
            <h5 className="text-md font-medium text-pf-text-primary mb-3">Performance</h5>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <FormField label="Max Print Speed (mm/s)">
                <Input
                  type="number"
                  value={formData.maxPrintSpeed || ''}
                  onChange={e => handleInputChange('maxPrintSpeed', parseInt(e.target.value) || undefined)}
                  placeholder="150"
                />
              </FormField>
              <FormField label="Default Backend">
                <BackendSelector
                  value={formData.defaultBackend}
                  onChange={(backend) => handleInputChange('defaultBackend', backend)}
                  className="w-full px-3 py-2 rounded-lg bg-pf-bg-0 border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary text-sm"
                />
              </FormField>
            </div>
          </div>
        </div>
      </form>
    </Modal>
  );
}
