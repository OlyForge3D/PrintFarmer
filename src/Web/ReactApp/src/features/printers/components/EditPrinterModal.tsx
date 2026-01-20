import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { LoadingIcon, RefreshIcon, CheckIcon, PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { usePrinterDetails, useUpdatePrinter, useManufacturers, useModels, useFilamentTypes, useModelDefaultCapabilities, useHotendModels, useExtruderModels, useToolheadModels, useNozzleModels } from '@/common/hooks/useApi';
import { UpdatePrinterDto, UpdateToolheadDto, PrinterBackend, ToolheadDto, NozzleTypeStringLabels, ToolheadType, ToolheadTypeLabels } from '@/types/api';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { FilamentTypeSelector } from '@/features/catalog/components/FilamentTypeSelector';
import { BackendSelector } from '@/common/components/BackendSelector';
import { CloneProfilesModal } from '@/features/slicer/components/CloneProfilesModal';
import { Button, Input, Select, Textarea, FormField, Alert, Checkbox, AccordionButton } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { generateUUID } from '@/utils/uuid';

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
  
  // Component model hooks for toolhead hardware customization
  const { data: hotendModels } = useHotendModels();
  const { data: extruderModels } = useExtruderModels();
  const { data: toolheadModels } = useToolheadModels();
  const { data: nozzleModels } = useNozzleModels();

  const [formData, setFormData] = useState<UpdatePrinterDto | null>(null);
  const [originalFormData, setOriginalFormData] = useState<UpdatePrinterDto | null>(null);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});
  const [lastModelId, setLastModelId] = useState<string | undefined>();
  const [isRefreshingCameras, setIsRefreshingCameras] = useState(false);
  const [showCloneProfilesModal, setShowCloneProfilesModal] = useState(false);
  const [toolheads, setToolheads] = useState<UpdateToolheadDto[]>([]);
  const [originalToolheads, setOriginalToolheads] = useState<UpdateToolheadDto[]>([]);
  const [expandedToolheads, setExpandedToolheads] = useState<Set<string>>(new Set());
  
  // Fetch default capabilities for the selected model
  const { data: defaultCapabilities, isLoading: isLoadingCapabilities } = useModelDefaultCapabilities(formData?.modelId);

  useEffect(() => {
    if (printerDetails) {
      const initialFormData: UpdatePrinterDto = {
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
        cameraStreamUrl: printerDetails.cameraStreamUrl,
        cameraSnapshotUrl: printerDetails.cameraSnapshotUrl,
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
        maxHotendTemp: printerDetails.capabilities?.maxHotendTemp,
        maxBedTemp: printerDetails.capabilities?.maxBedTemp,
        supportsAutoLeveling: printerDetails.capabilities?.supportsAutoLeveling,
        maxPrintSpeed: printerDetails.capabilities?.maxPrintSpeed,
        backendPort: printerDetails.backendPort ?? undefined,
        frontendPort: printerDetails.frontendPort ?? undefined,
      };
      
      setFormData(initialFormData);
      setOriginalFormData(initialFormData);
      
      // Initialize toolheads from printer details
      let initialToolheads: UpdateToolheadDto[] = [];
      if (printerDetails.toolheads && printerDetails.toolheads.length > 0) {
        initialToolheads = printerDetails.toolheads.map((th: ToolheadDto) => ({
          id: th.id,
          name: th.name,
          index: th.index,
          nozzleDiameter: th.nozzleDiameter,
          maxHotendTemp: th.maxHotendTemp,
          supportedMaterials: th.supportedMaterials,
          isPrimary: th.isPrimary,
        }));
        setToolheads(initialToolheads);
        setOriginalToolheads(initialToolheads);
        // Expand primary toolhead by default
        const primaryId = printerDetails.toolheads.find((th: ToolheadDto) => th.isPrimary)?.id;
        if (primaryId) {
          setExpandedToolheads(new Set([primaryId]));
        }
      } else {
        setToolheads([]);
        setOriginalToolheads([]);
        setExpandedToolheads(new Set());
      }
      
      // Prevent applying model defaults immediately after loading existing printer
      setLastModelId(printerDetails.modelId);
      setSelectedManufacturer(printerDetails.manufacturerId);
    }
  }, [printerDetails]);
  
  const handleClose = useCallback(() => {
    onClose();
    setValidationErrors({});
    setError('');
  }, [onClose]);

  // Helper to compare two values, treating null/undefined/NaN as equal
  const valuesEqual = useCallback((a: unknown, b: unknown): boolean => {
    if (a === b) return true;
    if (a == null && b == null) return true;
    if (a == null || b == null) return false;
    if (typeof a === 'number' && typeof b === 'number') {
      if (isNaN(a) && isNaN(b)) return true;
    }
    // For arrays (like supportedMaterials), do a deep comparison
    if (Array.isArray(a) && Array.isArray(b)) {
      if (a.length !== b.length) return false;
      const sortedA = [...a].sort();
      const sortedB = [...b].sort();
      return sortedA.every((val, idx) => val === sortedB[idx]);
    }
    return false;
  }, []);

  // Check if form data has changed
  const hasFormChanges = useMemo(() => {
    if (!formData || !originalFormData) return false;
    
    // Compare each field
    const fields: (keyof UpdatePrinterDto)[] = [
      'name', 'serverUrl', 'notes', 'manufacturerId', 'modelId',
      'newManufacturerName', 'newModelName', 'backend', 'apiKey',
      'cameraStreamUrl', 'cameraSnapshotUrl', 'nozzleDiameter',
      'supportedMaterials', 'maxBuildVolumeX', 'maxBuildVolumeY', 'maxBuildVolumeZ',
      'hasHeatedBed', 'hasEnclosure', 'multiMaterial', 'numberOfExtruders',
      'maxHotendTemp', 'maxBedTemp', 'supportsAutoLeveling', 'maxPrintSpeed',
      'backendPort', 'frontendPort'
    ];
    
    for (const field of fields) {
      if (!valuesEqual(formData[field], originalFormData[field])) {
        return true;
      }
    }
    
    return false;
  }, [formData, originalFormData, valuesEqual]);

  // Check if toolheads have changed
  const hasToolheadChanges = useMemo(() => {
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
    
    return false;
  }, [toolheads, originalToolheads, valuesEqual]);

  // Combined dirty state
  const hasChanges = hasFormChanges || hasToolheadChanges;
  
  // Handle ESC key to close modal
  useEffect(() => {
    if (!isOpen) return;
    
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        handleClose();
      }
    };
    
    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, handleClose]);
  
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
          maxHotendTemp: defaultCapabilities.maxHotendTemp ?? prev.maxHotendTemp,
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

  const handleToolheadChange = (toolheadId: string, field: keyof UpdateToolheadDto, value: unknown) => {
    setToolheads(prev => prev.map(th => 
      th.id === toolheadId ? { ...th, [field]: value } : th
    ));
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
    const newToolhead: UpdateToolheadDto = {
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
      // Include toolheads in the update if we have any
      const updateData: UpdatePrinterDto = {
        ...formData,
        toolheads: toolheads.length > 0 ? toolheads : undefined,
      };
      const result = await updateMutation.mutateAsync({ id: printerId, printer: updateData });
      toast.success(`Printer "${result.name}" updated`);
      onSuccess?.();
      // Show clone profiles modal if printer was just created or updated
      // (user may want to clone profiles from template machine)
      setShowCloneProfilesModal(true);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to update printer';
      toast.error(message);
      setError(message);
    }
  };

  const handleCloneProfilesSuccess = () => {
    setShowCloneProfilesModal(false);
    onClose();
  };

  const handleRefreshCameraUrls = async () => {
    if (!printerId) return;
    setIsRefreshingCameras(true);
    try {
      const updated = await apiClient.refreshCameraUrls(printerId);
      if (updated.cameraStreamUrl || updated.cameraSnapshotUrl) {
        toast.success('Camera URLs detected and updated');
      } else {
        toast.info('No camera URLs found. Make sure your printer is online and configured with a camera.');
      }
      // Optionally refetch printer details to update form if needed
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to refresh camera URLs';
      toast.error(message);
      console.error('Failed to refresh camera URLs:', err);
    } finally {
      setIsRefreshingCameras(false);
    }
  };

  if (!isOpen || !formData) return null;

  const filteredModels = models || [];

  const modalFooter = (
    <div className="flex gap-2">
      <Button
        type="button"
        variant="secondary"
        onClick={handleClose}
      >
        Cancel
      </Button>
      <Button
        type="submit"
        form="edit-printer-form"
        variant="primary"
        disabled={updateMutation.status === 'pending' || !hasChanges}
        iconLeft={<CheckIcon className="w-4 h-4" />}
        title={!hasChanges ? 'No changes to save' : undefined}
      >
        {updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes'}
      </Button>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Edit Printer"
      width="max-w-2xl"
      footer={modalFooter}
    >
      {error && (
        <Alert type="error" title="Update Failed" className="mb-4">
          {error}
        </Alert>
      )}

      <form id="edit-printer-form" onSubmit={handleSubmit} className="space-y-5">
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
            {(formData.backend === PrinterBackend.PrusaLink || formData.backend === PrinterBackend.OctoPrint) && (
              <div className="col-span-2">
                <FormField label={formData.backend === PrinterBackend.PrusaLink ? "API Key (PrusaLink)" : "API Key (OctoPrint)"}>
                  <Input
                    type="text"
                    value={formData.apiKey || ''}
                    onChange={e => handleInputChange('apiKey', e.target.value)}
                    placeholder={formData.backend === PrinterBackend.PrusaLink ? "Enter PrusaLink API Key" : "Enter OctoPrint API Key"}
                    title={formData.backend === PrinterBackend.PrusaLink ? "PrusaLink API Key" : "OctoPrint API Key"}
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
                    <LoadingIcon className="w-4 h-4 mr-2" />
                    Loading defaults...
                  </div>
                )}
              </div>
              <div className="space-y-4">
                <div className="grid grid-cols-2 gap-4">
                  <FormField label="Max Bed °C">
                    <Input
                      type="number"
                      value={formData.maxBedTemp || ''}
                      onChange={e => handleInputChange('maxBedTemp', parseInt(e.target.value, 10) || undefined)}
                      placeholder="120"
                      title="Maximum bed temperature"
                    />
                  </FormField>
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

                <div className="flex flex-wrap gap-x-6 gap-y-2">
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
            </div>

            {/* Toolheads Section */}
            <div className="border-t pt-5 mt-5">
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
                        isExpanded={expandedToolheads.has(toolhead.id!)}
                        onClick={() => toggleToolheadExpanded(toolhead.id!)}
                        title={toolhead.name || `Toolhead ${index + 1}`}
                        badge={toolhead.isPrimary ? 'Primary' : undefined}
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
                              handleRemoveToolhead(toolhead.id!);
                            }}
                            className="p-1 text-pf-text-secondary hover:text-red-500"
                            title="Remove toolhead"
                          />
                        }
                      />
                      
                      {/* Toolhead Details - Expandable */}
                      {expandedToolheads.has(toolhead.id!) && (
                        <div className="p-4 bg-pf-bg-primary border-t border-pf-border space-y-4">
                          {/* Basic Info */}
                          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <FormField label="Name" htmlFor={`toolhead-name-${index}`}>
                              <Input
                                id={`toolhead-name-${index}`}
                                value={toolhead.name || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'name', e.target.value || undefined)}
                                placeholder={`Toolhead ${index + 1}`}
                              />
                            </FormField>
                            <FormField label="Toolhead Type" htmlFor={`toolhead-type-${index}`}>
                              <Select
                                id={`toolhead-type-${index}`}
                                value={toolhead.toolheadType?.toString() ?? ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'toolheadType', e.target.value ? parseInt(e.target.value, 10) as ToolheadType : undefined)}
                              >
                                <option value="">Select type...</option>
                                {Object.entries(ToolheadTypeLabels).map(([value, label]) => (
                                  <option key={value} value={value}>{label}</option>
                                ))}
                              </Select>
                            </FormField>
                            <FormField label="Toolhead Model" htmlFor={`toolhead-model-${index}`}>
                              <Select
                                id={`toolhead-model-${index}`}
                                value={toolhead.toolheadModelDefId || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'toolheadModelDefId', e.target.value || undefined)}
                              >
                                <option value="">Select toolhead model...</option>
                                {toolheadModels?.map(tm => (
                                  <option key={tm.id} value={tm.id}>
                                    {tm.manufacturerName ? `${tm.manufacturerName} - ${tm.name}` : tm.name}
                                  </option>
                                ))}
                              </Select>
                            </FormField>
                          </div>

                          {/* Extruder and Hotend */}
                          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                            <FormField label="Extruder" htmlFor={`toolhead-extruder-${index}`}>
                              <Select
                                id={`toolhead-extruder-${index}`}
                                value={toolhead.extruderModelId || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'extruderModelId', e.target.value || undefined)}
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
                                onChange={e => handleToolheadChange(toolhead.id!, 'hotendModelId', e.target.value || undefined)}
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

                          {/* Nozzle */}
                          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <FormField label="Nozzle Diameter (mm)" htmlFor={`toolhead-nozzle-${index}`}>
                              <Input
                                id={`toolhead-nozzle-${index}`}
                                type="number"
                                step="0.1"
                                value={toolhead.nozzleDiameter?.toString() || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'nozzleDiameter', e.target.value ? parseFloat(e.target.value) : undefined)}
                                placeholder="0.4"
                              />
                            </FormField>
                            <FormField label="Nozzle Type" htmlFor={`toolhead-nozzle-type-${index}`}>
                              <Select
                                id={`toolhead-nozzle-type-${index}`}
                                value={toolhead.nozzleType?.toString() ?? ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'nozzleType', e.target.value || undefined)}
                              >
                                <option value="">Select nozzle type...</option>
                                {Object.entries(NozzleTypeStringLabels).map(([value, label]) => (
                                  <option key={value} value={value}>{label}</option>
                                ))}
                              </Select>
                            </FormField>
                            <FormField label="Nozzle Model" htmlFor={`toolhead-nozzle-model-${index}`}>
                              <Select
                                id={`toolhead-nozzle-model-${index}`}
                                value={toolhead.nozzleModelId || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'nozzleModelId', e.target.value || undefined)}
                              >
                                <option value="">Select nozzle model...</option>
                                {nozzleModels?.map(nm => (
                                  <option key={nm.id} value={nm.id}>
                                    {nm.manufacturerName ? `${nm.manufacturerName} - ${nm.name}` : nm.name}
                                    {nm.isHardened ? ' (Hardened)' : ''}
                                  </option>
                                ))}
                              </Select>
                            </FormField>
                          </div>

                          {/* Performance */}
                          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                            <FormField label="Max Hotend Temp (°C)" htmlFor={`toolhead-max-temp-${index}`}>
                              <Input
                                id={`toolhead-max-temp-${index}`}
                                type="number"
                                value={toolhead.maxHotendTemp?.toString() || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'maxHotendTemp', e.target.value ? parseInt(e.target.value, 10) : undefined)}
                                placeholder="300"
                              />
                            </FormField>
                            <FormField label="Max Flow Rate (mm³/s)" htmlFor={`toolhead-max-flow-${index}`}>
                              <Input
                                id={`toolhead-max-flow-${index}`}
                                type="number"
                                step="0.1"
                                value={toolhead.maxFlowRate?.toString() || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'maxFlowRate', e.target.value ? parseFloat(e.target.value) : undefined)}
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
                              onSelectionChange={(selectedTypes) => handleToolheadChange(toolhead.id!, 'supportedMaterials', selectedTypes)}
                            />
                          </div>

                          {/* Index and Primary */}
                          <div className="flex items-center space-x-4">
                            <FormField label="Index" htmlFor={`toolhead-index-${index}`} className="w-20">
                              <Input
                                id={`toolhead-index-${index}`}
                                type="number"
                                min="0"
                                value={toolhead.index?.toString() ?? index.toString()}
                                onChange={e => handleToolheadChange(toolhead.id!, 'index', parseInt(e.target.value, 10) || 0)}
                                title="Toolhead index (T0, T1, etc.)"
                              />
                            </FormField>
                            <div className="pt-6">
                              <Checkbox
                                id={`toolhead-primary-${index}`}
                                label="Primary Toolhead"
                                checked={toolhead.isPrimary ?? false}
                                onChange={e => {
                                  // When setting a toolhead as primary, unset others
                                  if (e.target.checked) {
                                    setToolheads(prev => prev.map(th => ({
                                      ...th,
                                      isPrimary: th.id === toolhead.id
                                    })));
                                  } else {
                                    handleToolheadChange(toolhead.id!, 'isPrimary', false);
                                  }
                                }}
                              />
                            </div>
                          </div>
                        </div>
                      )}
                    </div>
                  ))}
                  </div>
                </>
              )}
            </div>

            {/* Camera URLs Section */}
            <div className="border-t pt-5 mt-5">
              <div className="flex items-center justify-between mb-4">
                <h4 className="text-lg font-medium text-pf-text-primary">Camera Configuration</h4>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={handleRefreshCameraUrls}
                  disabled={isRefreshingCameras}
                  title="Auto-detect camera URLs from the printer backend"
                  iconLeft={<RefreshIcon className={`w-4 h-4 ${isRefreshingCameras ? 'animate-spin' : ''}`} />}
                >
                  {isRefreshingCameras ? 'Detecting...' : 'Auto-Detect'}
                </Button>
              </div>
              <div className="space-y-4">
                <FormField label="Camera Stream URL" htmlFor="camera-stream-url">
                  <Input
                    id="camera-stream-url"
                    type="text"
                    value={formData.cameraStreamUrl || ''}
                    onChange={e => handleInputChange('cameraStreamUrl', e.target.value || undefined)}
                    placeholder="http://printer.local/webcam/?action=stream"
                    title="Live video stream URL (MJPEG or similar)"
                  />
                </FormField>
                <FormField label="Camera Snapshot URL" htmlFor="camera-snapshot-url">
                  <Input
                    id="camera-snapshot-url"
                    type="text"
                    value={formData.cameraSnapshotUrl || ''}
                    onChange={e => handleInputChange('cameraSnapshotUrl', e.target.value || undefined)}
                    placeholder="http://printer.local/webcam/?action=snapshot"
                    title="Static image snapshot URL (JPEG)"
                  />
                </FormField>
              </div>
            </div>
          </form>

          <CloneProfilesModal
            isOpen={showCloneProfilesModal}
            onClose={() => setShowCloneProfilesModal(false)}
            printerId={printerId || ''}
            printerName={formData?.name || ''}
            onSuccess={handleCloneProfilesSuccess}
          />
    </Modal>
  );
}