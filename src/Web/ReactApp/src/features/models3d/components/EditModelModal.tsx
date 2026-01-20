import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { CheckIcon, PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useFilamentTypes, useHotendModels, useExtruderModels, useToolheadModels, useNozzleModels } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import type { PrinterModelDto, UpdateModelRequest, MotionTypeString, PrinterModelToolheadDto } from '@/types/api';
import { NozzleTypeStringLabels } from '@/types/api';
import { toast } from 'sonner';
import { FilamentTypeSelector } from '@/features/catalog/components/FilamentTypeSelector';
import { ModelAliasEditor, ModelAliasEditorRef } from '@/features/catalog/components/ModelAliasEditor';
import { BackendSelector } from '@/common/components/BackendSelector';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { FormField } from '@/common/components/ui/FormField';
import { Alert } from '@/common/components/ui/Alert';
import { Select, Checkbox, AccordionButton } from '@/common/components/ui';
import { generateUUID } from '@/utils/uuid';

interface EditModelModalProps {
  model: PrinterModelDto | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
  isCloneMode?: boolean;
}

export function EditModelModal({ model, isOpen, onClose, onSuccess, isCloneMode = false }: EditModelModalProps) {
  const queryClient = useQueryClient();
  const { data: filamentTypes } = useFilamentTypes();
  
  // Component model hooks for hardware selection
  const { data: hotendModels } = useHotendModels();
  const { data: extruderModels } = useExtruderModels();
  const { data: toolheadModels } = useToolheadModels();
  const { data: nozzleModels } = useNozzleModels();
  
  const [formData, setFormData] = useState<UpdateModelRequest | null>(null);
  const [originalFormData, setOriginalFormData] = useState<UpdateModelRequest | null>(null);
  const [supportedMaterialNames, setSupportedMaterialNames] = useState<string[]>([]);
  const [originalMaterialNames, setOriginalMaterialNames] = useState<string[]>([]);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});
  const [aliasesHaveChanges, setAliasesHaveChanges] = useState(false);
  
  // Toolhead state
  const [toolheads, setToolheads] = useState<PrinterModelToolheadDto[]>([]);
  const [originalToolheads, setOriginalToolheads] = useState<PrinterModelToolheadDto[]>([]);
  const [expandedToolheads, setExpandedToolheads] = useState<Set<string>>(new Set());
  
  // Ref for alias editor to save changes on form submit
  const aliasEditorRef = useRef<ModelAliasEditorRef>(null);

  // Determine if this is add (temp ID) or edit (real ID)
  const isAddMode = model?.id?.toString().startsWith('temp-');

  const createMutation = useMutation({
    mutationFn: async (data: UpdateModelRequest) => {
      const modelData = data as unknown as Omit<PrinterModelDto, 'id'>;
      return apiClient.createModel(modelData);
    },
    onError: (error) => {
      const message = error instanceof Error ? error.message : 'Failed to create model';
      toast.error(message);
      setError(message);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, data }: { id: string; data: UpdateModelRequest }) => {
      return apiClient.updateModel(id, data);
    },
    onError: (error) => {
      const message = error instanceof Error ? error.message : 'Failed to update model';
      toast.error(message);
      setError(message);
    },
  });

  useEffect(() => {
    if (model) {
      const initialData: UpdateModelRequest = {
        name: model.name,
        motionType: model.motionType,
        maxX: model.maxX,
        maxY: model.maxY,
        maxZ: model.maxZ,
        defaultBackend: model.defaultBackend,
        supportedFilamentTypeIds: undefined, // Will be set based on supportedMaterialNames
        
        // Default capabilities (nozzle diameter and max hotend temp are now on toolheads)
        hasHeatedBed: model.hasHeatedBed,
        hasEnclosure: model.hasEnclosure,
        multiMaterial: model.multiMaterial,
        numberOfExtruders: model.numberOfExtruders,
        supportsAutoLeveling: model.supportsAutoLeveling,
        
        // Temperature ranges
        maxBedTemp: model.maxBedTemp,
        
        // Speed capabilities
        maxPrintSpeed: model.maxPrintSpeed,
      };
      
      setFormData(initialData);
      setOriginalFormData(initialData);
      
      // Initialize supported material names from the model
      const materials = model.supportedFilamentTypes || [];
      setSupportedMaterialNames(materials);
      setOriginalMaterialNames(materials);
      
      // Initialize toolheads from the model
      const modelToolheads = model.toolheads || [];
      setToolheads(modelToolheads);
      setOriginalToolheads(modelToolheads);
      setExpandedToolheads(new Set());
      
      // Reset alias tracking
      setAliasesHaveChanges(false);
    }
  }, [model]);

  // Helper to compare two values, treating null/undefined as equal
  const valuesEqual = useCallback((a: unknown, b: unknown): boolean => {
    if (a === b) return true;
    if (a == null && b == null) return true;
    if (a == null || b == null) return false;
    if (typeof a === 'number' && typeof b === 'number') {
      // Handle NaN comparison
      if (isNaN(a) && isNaN(b)) return true;
    }
    return false;
  }, []);

  // Check if form data has changed
  const hasFormChanges = useMemo(() => {
    if (!formData || !originalFormData) return false;
    
    // In add mode, always allow saving (there's no "original" to compare)
    if (isAddMode) return true;
    
    // Compare each field (nozzle diameter and max hotend temp are now on toolheads)
    const fields: (keyof UpdateModelRequest)[] = [
      'name', 'motionType', 'maxX', 'maxY', 'maxZ', 'defaultBackend',
      'hasHeatedBed', 'hasEnclosure', 'multiMaterial',
      'numberOfExtruders', 'supportsAutoLeveling', 'maxBedTemp', 'maxPrintSpeed'
    ];
    
    for (const field of fields) {
      if (!valuesEqual(formData[field], originalFormData[field])) {
        return true;
      }
    }
    
    return false;
  }, [formData, originalFormData, isAddMode, valuesEqual]);

  // Check if materials have changed
  const hasMaterialChanges = useMemo(() => {
    if (isAddMode) return true; // In add mode, always allow saving
    const currentSorted = [...supportedMaterialNames].sort();
    const originalSorted = [...originalMaterialNames].sort();
    if (currentSorted.length !== originalSorted.length) return true;
    return currentSorted.some((name, i) => name !== originalSorted[i]);
  }, [supportedMaterialNames, originalMaterialNames, isAddMode]);

  // Check if toolheads have changed
  const hasToolheadChanges = useMemo(() => {
    if (isAddMode) return true; // In add mode, always allow saving
    if (toolheads.length !== originalToolheads.length) return true;
    
    for (let i = 0; i < toolheads.length; i++) {
      const current = toolheads[i];
      const original = originalToolheads.find(th => th.id === current.id);
      if (!original) return true; // New toolhead added
      
      // Compare toolhead fields
      if (current.name !== original.name) return true;
      if (current.index !== original.index) return true;
      if (current.nozzleDiameter !== original.nozzleDiameter) return true;
      if (current.nozzleType !== original.nozzleType) return true;
      if (current.maxHotendTemp !== original.maxHotendTemp) return true;
      if (current.maxFlowRate !== original.maxFlowRate) return true;
      if (current.toolheadType !== original.toolheadType) return true;
      // Component model IDs (database-backed)
      if (current.hotendModelId !== original.hotendModelId) return true;
      if (current.extruderModelId !== original.extruderModelId) return true;
      if (current.toolheadModelDefId !== original.toolheadModelDefId) return true;
      if (current.nozzleModelId !== original.nozzleModelId) return true;
      if (current.isPrimary !== original.isPrimary) return true;
      if (!valuesEqual(current.supportedMaterials, original.supportedMaterials)) return true;
    }
    
    return false;;
  }, [toolheads, originalToolheads, isAddMode, valuesEqual]);

  // Combined dirty state - any change enables save button
  const hasChanges = hasFormChanges || hasMaterialChanges || hasToolheadChanges || aliasesHaveChanges;

  // Callback for alias editor to notify us of changes
  const handleAliasesDirtyChange = useCallback((isDirty: boolean) => {
    setAliasesHaveChanges(isDirty);
  }, []);

  const handleInputChange = (field: keyof UpdateModelRequest, value: unknown) => {
    setFormData(prev => prev ? { ...prev, [field]: value } : prev);
    if (validationErrors[field]) {
      setValidationErrors(prev => { const clone = { ...prev }; delete clone[field]; return clone; });
    }
  };

  // Toolhead handlers
  const handleToolheadChange = (toolheadId: string, field: keyof PrinterModelToolheadDto, value: unknown) => {
    setToolheads(prev => prev.map(th => 
      th.id === toolheadId ? { ...th, [field]: value } : th
    ));
  };

  // Specialized handler for toolhead model selection - auto-populates extruder/hotend/nozzle
  const handleToolheadModelSelect = (toolheadId: string, toolheadModelDefId: string | undefined) => {
    setToolheads(prev => prev.map(th => {
      if (th.id !== toolheadId) return th;
      
      if (!toolheadModelDefId) {
        // Cleared selection - just update the toolhead model ID
        return { ...th, toolheadModelDefId: undefined };
      }
      
      // Find the selected toolhead model
      const selectedToolheadModel = toolheadModels?.find(tm => tm.id === toolheadModelDefId);
      if (!selectedToolheadModel) {
        return { ...th, toolheadModelDefId };
      }
      
      // Auto-populate from toolhead defaults
      const updates: Partial<PrinterModelToolheadDto> = { toolheadModelDefId };
      
      if (selectedToolheadModel.defaultExtruderId) {
        updates.extruderModelId = selectedToolheadModel.defaultExtruderId;
      }
      if (selectedToolheadModel.defaultHotendId) {
        updates.hotendModelId = selectedToolheadModel.defaultHotendId;
      }
      if (selectedToolheadModel.defaultNozzleId) {
        updates.nozzleModelId = selectedToolheadModel.defaultNozzleId;
      }
      
      return { ...th, ...updates };
    }));
  };

  const toggleToolheadExpanded = (toolheadId: string) => {
    setExpandedToolheads(prev => {
      const next = new Set(prev);
      if (next.has(toolheadId)) {
        next.delete(toolheadId);
      } else {
        next.add(toolheadId);
      }
      return next;
    });
  };

  const handleAddToolhead = () => {
    const newId = generateUUID();
    const newIndex = toolheads.length;
    const newToolhead: PrinterModelToolheadDto = {
      id: newId,
      name: `Toolhead ${newIndex + 1}`,
      index: newIndex,
      nozzleDiameter: 0.4,
      maxHotendTemp: 300,
      supportedMaterials: ['PLA', 'PETG'],
      isPrimary: newIndex === 0, // First toolhead is primary by default
    };
    setToolheads(prev => [...prev, newToolhead]);
    setExpandedToolheads(prev => new Set([...prev, newId]));
  };

  const handleRemoveToolhead = (toolheadId: string) => {
    setToolheads(prev => {
      const filtered = prev.filter(th => th.id !== toolheadId);
      // If we removed the primary toolhead, make the first remaining one primary
      if (filtered.length > 0 && !filtered.some(th => th.isPrimary)) {
        filtered[0].isPrimary = true;
      }
      // Re-index remaining toolheads
      return filtered.map((th, idx) => ({ ...th, index: idx }));
    });
    setExpandedToolheads(prev => {
      const next = new Set(prev);
      next.delete(toolheadId);
      return next;
    });
  };

  const validateForm = (): boolean => {
    if (!formData) return false;
    const errors: Record<string, string[]> = {};
    if (!formData.name?.trim()) errors.name = ['Name is required'];
    
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
      supportedFilamentTypeIds,
      toolheads: toolheads.length > 0 ? toolheads : undefined
    };
    
    // Debug: Log what's being sent
    if (window.PrintFarmerDebug?.catalog) {
      console.log('[EditModelModal] Submitting updateData:', JSON.stringify(updateData, null, 2));
      console.log('[EditModelModal] formData.hasEnclosure:', formData.hasEnclosure);
    }
    
    try {
      if (isAddMode) {
        // For add mode, use create mutation and exclude the temp ID
        const createData = updateData as unknown as Record<string, unknown>;
        if (model.manufacturerId) {
          createData.manufacturerId = model.manufacturerId;
        }
        delete createData.id;
        await createMutation.mutateAsync(createData as unknown as UpdateModelRequest);
        // Success for create mode - force immediate refetch to bypass stale cache
        await queryClient.refetchQueries({ queryKey: ['models'] });
        toast.success(`Model "${formData?.name}" created successfully`);
        onSuccess?.();
        onClose();
      } else {
        // For edit mode, use update mutation
        await updateMutation.mutateAsync({ id: model.id, data: updateData });
        
        // Save any pending alias changes
        if (aliasEditorRef.current?.hasChanges()) {
          await aliasEditorRef.current.saveChanges();
        }
        
        // Success for edit mode - force immediate refetch to bypass stale cache
        await queryClient.refetchQueries({ queryKey: ['models'] });
        toast.success(`Model "${formData?.name}" updated successfully`);
        onSuccess?.();
        onClose();
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

  // Get the appropriate button text based on mode
  const getButtonText = () => {
    if (isAddMode) {
      if (createMutation.status === 'pending') return 'Creating...';
      return isCloneMode ? 'Clone Model' : 'Create Model';
    }
    return updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes';
  };

  const footerContent = (
    <div className="flex gap-3 justify-end">
      <Button variant="secondary" size="lg" onClick={handleClose}>
        Cancel
      </Button>
      <Button
        variant={isAddMode ? 'success' : 'primary'}
        size="lg"
        disabled={
          (isAddMode ? createMutation.status === 'pending' : updateMutation.status === 'pending') ||
          (!isAddMode && !hasChanges)
        }
        iconRight={<CheckIcon className="w-4 h-4" />}
        onClick={handleSubmit}
        title={!hasChanges && !isAddMode ? 'No changes to save' : undefined}
      >
        {getButtonText()}
      </Button>
    </div>
  );

  // Determine the title based on mode
  const getModalTitle = () => {
    if (isCloneMode) return `Clone Printer Model: ${model?.name ?? ''}`;
    if (isAddMode) return 'Add Printer Model';
    return `Edit Printer Model: ${model?.name ?? ''}`;
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={getModalTitle()}
      width="max-w-4xl"
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
              <Select
                value={formData.motionType || ''}
                onChange={e => handleInputChange('motionType', e.target.value ? e.target.value as MotionTypeString : undefined)}
              >
                {!formData.motionType && <option value="">Select type...</option>}
                <option value="Cartesian">Cartesian</option>
                <option value="CoreXY">CoreXY</option>
                <option value="Delta">Delta</option>
                <option value="Unknown">Unknown</option>
              </Select>
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
          
          {/* Number of Extruders */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
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
            <Checkbox
              id="hasHeatedBed"
              label="Heated Bed"
              checked={formData.hasHeatedBed ?? true}
              onChange={e => handleInputChange('hasHeatedBed', e.target.checked)}
            />
            <Checkbox
              id="hasEnclosure"
              label="Enclosure"
              checked={formData.hasEnclosure ?? false}
              onChange={e => handleInputChange('hasEnclosure', e.target.checked)}
            />
            <Checkbox
              id="multiMaterial"
              label="Multi-Material"
              checked={formData.multiMaterial ?? false}
              onChange={e => handleInputChange('multiMaterial', e.target.checked)}
            />
            <Checkbox
              id="supportsAutoLeveling"
              label="Auto-Leveling"
              checked={formData.supportsAutoLeveling ?? false}
              onChange={e => handleInputChange('supportsAutoLeveling', e.target.checked)}
            />
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
            <div className="grid grid-cols-2 gap-4">
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
                  valueType="string"
                  className="w-full px-3 py-2 rounded-lg bg-pf-bg-0 border border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent text-pf-text-primary text-sm"
                />
              </FormField>
            </div>
          </div>
        </div>

        {/* Toolheads Section */}
        <div className="border-t pt-5">
          <div className="flex items-center justify-between mb-4">
            <h4 className="text-lg font-medium text-pf-text-primary">
              Toolheads ({toolheads.length})
            </h4>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleAddToolhead}
              iconLeft={<PlusIcon className="w-4 h-4" />}
            >
              Add Toolhead
            </Button>
          </div>
          {toolheads.length === 0 ? (
            <p className="text-sm text-pf-text-secondary italic">No toolheads configured. Click "Add Toolhead" to add one.</p>
          ) : (
            <>
              <p className="text-xs text-pf-text-secondary mb-3">Click to expand individual toolhead settings</p>
              <div className="space-y-3">
                {toolheads.map((toolhead, index) => (
                <div 
                  key={toolhead.id} 
                  className="border border-pf-border rounded-lg overflow-hidden"
                >
                  {/* Toolhead Header - Clickable accordion */}
                  <AccordionButton
                    isExpanded={expandedToolheads.has(toolhead.id)}
                    onClick={() => toggleToolheadExpanded(toolhead.id)}
                    title={
                      <span className="flex items-center gap-2">
                        {toolhead.name || `Toolhead ${index + 1}`}
                        {toolhead.isPrimary && (
                          <span className="text-xs px-1.5 py-0.5 bg-pf-accent/20 text-pf-accent rounded font-normal">
                            Primary
                          </span>
                        )}
                      </span>
                    }
                    summary={[
                      toolhead.nozzleDiameter && `Ø${toolhead.nozzleDiameter}mm`,
                      toolhead.maxHotendTemp && `Max ${toolhead.maxHotendTemp}°C`,
                    ].filter(Boolean).join(' • ') || undefined}
                    actions={
                      <Button
                        variant="subtle"
                        size="sm"
                        iconCenter={<DeleteIcon className="w-4 h-4" />}
                        onClick={(e) => {
                          e.stopPropagation();
                          handleRemoveToolhead(toolhead.id);
                        }}
                        className="p-1 text-pf-text-secondary hover:text-red-500"
                        title="Remove toolhead"
                      />
                    }
                  />
                  
                  {/* Toolhead Details - Expandable */}
                  {expandedToolheads.has(toolhead.id) && (
                    <div className="p-4 bg-pf-bg-primary border-t border-pf-border space-y-4">
                      {/* Identity: Name + Index + Primary */}
                      <div className="grid grid-cols-1 sm:grid-cols-[1fr_80px_auto] gap-4 items-end">
                        <FormField label="Name" htmlFor={`toolhead-name-${index}`}>
                          <Input
                            id={`toolhead-name-${index}`}
                            value={toolhead.name || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'name', e.target.value || undefined)}
                            placeholder={`Toolhead ${index + 1}`}
                          />
                        </FormField>
                        <FormField label="Index" htmlFor={`toolhead-index-${index}`}>
                          <Input
                            id={`toolhead-index-${index}`}
                            type="number"
                            min="0"
                            value={toolhead.index?.toString() ?? index.toString()}
                            onChange={e => handleToolheadChange(toolhead.id, 'index', parseInt(e.target.value, 10) || 0)}
                            title="Toolhead index (T0, T1, etc.)"
                          />
                        </FormField>
                        <div className="flex items-center h-[38px]">
                          <Checkbox
                            id={`toolhead-primary-${index}`}
                            label="Primary"
                            checked={toolhead.isPrimary ?? false}
                            onChange={e => {
                              if (e.target.checked) {
                                setToolheads(prev => prev.map(th => ({
                                  ...th,
                                  isPrimary: th.id === toolhead.id
                                })));
                              } else {
                                handleToolheadChange(toolhead.id, 'isPrimary', false);
                              }
                            }}
                          />
                        </div>
                      </div>

                      {/* Toolhead Assembly: Model + Extruder + Hotend */}
                      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <FormField label="Toolhead Model" htmlFor={`toolhead-model-${index}`}>
                          <Select
                            id={`toolhead-model-${index}`}
                            value={toolhead.toolheadModelDefId || ''}
                            onChange={e => handleToolheadModelSelect(toolhead.id, e.target.value || undefined)}
                          >
                            <option value="">Select toolhead...</option>
                            {toolheadModels?.map(tm => (
                              <option key={tm.id} value={tm.id}>
                                {tm.manufacturerName ? `${tm.manufacturerName} - ${tm.name}` : tm.name}
                              </option>
                            ))}
                          </Select>
                        </FormField>
                        <FormField label="Extruder" htmlFor={`toolhead-extruder-${index}`}>
                          <Select
                            id={`toolhead-extruder-${index}`}
                            value={toolhead.extruderModelId || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'extruderModelId', e.target.value || undefined)}
                          >
                            <option value="">Select extruder...</option>
                            {extruderModels?.map(em => (
                              <option key={em.id} value={em.id}>
                                {em.manufacturerName ? `${em.manufacturerName} - ${em.name}` : em.name}
                                {em.gearRatio ? ` (${em.gearRatio})` : ''}
                              </option>
                            ))}
                          </Select>
                        </FormField>
                        <FormField label="Hotend" htmlFor={`toolhead-hotend-${index}`}>
                          <Select
                            id={`toolhead-hotend-${index}`}
                            value={toolhead.hotendModelId || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'hotendModelId', e.target.value || undefined)}
                          >
                            <option value="">Select hotend...</option>
                            {hotendModels?.map(hm => (
                              <option key={hm.id} value={hm.id}>
                                {hm.manufacturerName ? `${hm.manufacturerName} - ${hm.name}` : hm.name}
                                {hm.isHighFlow ? ' (High Flow)' : ''}
                              </option>
                            ))}
                          </Select>
                        </FormField>
                      </div>

                      {/* Nozzle Configuration */}
                      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <FormField label="Nozzle Model" htmlFor={`toolhead-nozzle-model-${index}`}>
                          <Select
                            id={`toolhead-nozzle-model-${index}`}
                            value={toolhead.nozzleModelId || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'nozzleModelId', e.target.value || undefined)}
                          >
                            <option value="">Select nozzle...</option>
                            {nozzleModels?.map(nm => (
                              <option key={nm.id} value={nm.id}>
                                {nm.manufacturerName ? `${nm.manufacturerName} - ${nm.name}` : nm.name}
                                {nm.isHardened ? ' (Hardened)' : ''}
                              </option>
                            ))}
                          </Select>
                        </FormField>
                        <FormField label="Nozzle Diameter (mm)" htmlFor={`toolhead-nozzle-${index}`}>
                          <Input
                            id={`toolhead-nozzle-${index}`}
                            type="number"
                            step="0.1"
                            value={toolhead.nozzleDiameter?.toString() || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'nozzleDiameter', e.target.value ? parseFloat(e.target.value) : undefined)}
                            placeholder="0.4"
                          />
                        </FormField>
                        <FormField label="Nozzle Type" htmlFor={`toolhead-nozzle-type-${index}`}>
                          <Select
                            id={`toolhead-nozzle-type-${index}`}
                            value={toolhead.nozzleType?.toString() ?? ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'nozzleType', e.target.value || undefined)}
                          >
                            <option value="">Select type...</option>
                            {Object.entries(NozzleTypeStringLabels).map(([value, label]) => (
                              <option key={value} value={value}>{label}</option>
                            ))}
                          </Select>
                        </FormField>
                      </div>

                      {/* Performance Specs */}
                      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                        <FormField label="Max Hotend Temp (°C)" htmlFor={`toolhead-max-temp-${index}`}>
                          <Input
                            id={`toolhead-max-temp-${index}`}
                            type="number"
                            value={toolhead.maxHotendTemp?.toString() || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'maxHotendTemp', e.target.value ? parseInt(e.target.value, 10) : undefined)}
                            placeholder="300"
                          />
                        </FormField>
                        <FormField label="Max Flow Rate (mm³/s)" htmlFor={`toolhead-max-flow-${index}`}>
                          <Input
                            id={`toolhead-max-flow-${index}`}
                            type="number"
                            step="0.1"
                            value={toolhead.maxFlowRate?.toString() || ''}
                            onChange={e => handleToolheadChange(toolhead.id, 'maxFlowRate', e.target.value ? parseFloat(e.target.value) : undefined)}
                            placeholder="15"
                          />
                        </FormField>
                      </div>

                      {/* Supported Materials */}
                      <div>
                        <label className="block text-sm font-medium text-pf-text-secondary mb-1">
                          Supported Materials
                        </label>
                        <FilamentTypeSelector
                          availableFilamentTypes={filamentTypes}
                          selectedFilamentTypes={toolhead.supportedMaterials || []}
                          onSelectionChange={(selectedTypes) => handleToolheadChange(toolhead.id, 'supportedMaterials', selectedTypes)}
                        />
                      </div>
                    </div>
                  )}
                </div>
              ))}
              </div>
            </>
          )}
        </div>

        {/* Slicer Model Aliases */}
        {!isAddMode && model && (
          <div className="border-t pt-5">
            <h4 className="text-lg font-medium text-pf-text-primary mb-4">Slicer Model Aliases</h4>
            <p className="text-sm text-pf-text-secondary mb-4">
              Configure alternative model names as they appear in different slicers. This helps automatically match gcode files
              to the correct printer model.
            </p>
            <ModelAliasEditor
              ref={aliasEditorRef}
              modelId={model.id}
              onDirtyChange={handleAliasesDirtyChange}
            />
          </div>
        )}
      </form>
    </Modal>
  );
}
