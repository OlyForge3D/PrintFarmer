import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { LoadingIcon, RefreshIcon, CheckIcon, PlusIcon, DeleteIcon, WiFiIcon, EyeIcon, EyeOffIcon } from '@/common/components/icons/MdiIcons';
import { usePrinterDetails, useUpdatePrinter, useManufacturers, useModels, useFilamentTypes, useModelDefaultCapabilities, useHotendModels, useExtruderModels, useToolheadModels, useNozzleModels } from '@/common/hooks/useApi';
import { UpdatePrinterDto, UpdateToolheadDto, PrinterBackend, ToolheadDto, PrinterBackendString, NozzleTypeStringLabels } from '@/types/api';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { FilamentTypeSelector } from '@/features/catalog/components/FilamentTypeSelector';
import { BackendSelector } from '@/common/components/BackendSelector';
import { CloneProfilesModal } from '@/features/slicer/components/CloneProfilesModal';
import { Button, Input, Select, Textarea, FormField, Alert, Checkbox, Toggle, AccordionButton } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { generateUUID } from '@/utils/uuid';
import { printerBackendStringToEnum } from '@/common/utils/enumHelpers';
import { useSlicer } from '@/hooks/useSlicer';

interface EditPrinterModalProps {
  printerId: string | null;
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

export function EditPrinterModal({ printerId, isOpen, onClose, onSuccess }: EditPrinterModalProps) {
  const { data: printerDetails } = usePrinterDetails(printerId || '');
  const { data: manufacturers } = useManufacturers();
  const { isSlicerAvailable } = useSlicer();
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
  
  // Test connection state
  const [isTesting, setIsTesting] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [showApiKey, setShowApiKey] = useState(false);
  
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
        // Backend comes from API as string enum ("Moonraker"), convert to numeric for frontend
        backend: typeof printerDetails.backend === 'string'
          ? (printerBackendStringToEnum(printerDetails.backend as unknown as PrinterBackendString) ?? PrinterBackend.Unknown)
          : printerDetails.backend,
        apiKey: printerDetails.apiKey,
        username: printerDetails.username,
        // PrusaLink stores credential as password (username is fixed to "maker")
        password: printerDetails.password,
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
        maxHotendTemp: printerDetails.capabilities?.maxHotendTemp,
        maxBedTemp: printerDetails.capabilities?.maxBedTemp,
        supportsAutoLeveling: printerDetails.capabilities?.supportsAutoLeveling,
        maxPrintSpeed: printerDetails.capabilities?.maxPrintSpeed,
        backendPort: printerDetails.backendPort ?? undefined,
        frontendPort: printerDetails.frontendPort ?? undefined,
        obicoEnabled: printerDetails.obicoEnabled ?? false,
        useModelDispatchDefaults: printerDetails.useModelDispatchDefaults ?? true,
        wattage: printerDetails.wattage ?? undefined,
        machineHourlyRate: printerDetails.machineHourlyRate ?? undefined,
        buddyCameraIp: printerDetails.buddyCameraIp ?? undefined,
      };
      
      setFormData(initialFormData);
      setOriginalFormData(initialFormData);
      
      // Initialize toolheads from printer details
      if (printerDetails.toolheads && printerDetails.toolheads.length > 0) {
        const initialToolheads = printerDetails.toolheads.map((th: ToolheadDto) => ({
          id: th.id,
          name: th.name,
          index: th.index,
          nozzleDiameter: th.nozzleDiameter,
          // Component model references
          hotendModelId: th.hotendModelId,
          extruderModelId: th.extruderModelId,
          toolheadModelDefId: th.toolheadModelDefId,
          nozzleModelId: th.nozzleModelId,
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
    setShowPassword(false);
    setShowApiKey(false);
  }, [onClose]);

  // Reset password/API key visibility when modal opens or printer changes
  useEffect(() => {
    if (isOpen) {
      setShowPassword(false);
      setShowApiKey(false);
    }
  }, [isOpen, printerId]);

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
      'newManufacturerName', 'newModelName', 'backend', 'apiKey', 'username', 'password',
      'cameraStreamUrl', 'cameraSnapshotUrl', 'nozzleDiameter',
      'supportedMaterials', 'maxBuildVolumeX', 'maxBuildVolumeY', 'maxBuildVolumeZ',
      'hasHeatedBed', 'hasEnclosure', 'multiMaterial',
      'maxHotendTemp', 'maxBedTemp', 'supportsAutoLeveling', 'maxPrintSpeed',
      'backendPort', 'frontendPort', 'obicoEnabled', 'useModelDispatchDefaults',
      'wattage', 'machineHourlyRate', 'buddyCameraIp'
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
      // Component model IDs (database-backed) - nozzle type comes from nozzle model
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
          maxHotendTemp: defaultCapabilities.maxHotendTemp ?? prev.maxHotendTemp,
          maxBedTemp: defaultCapabilities.maxBedTemp ?? prev.maxBedTemp,
          supportsAutoLeveling: defaultCapabilities.supportsAutoLeveling,
          maxPrintSpeed: defaultCapabilities.maxPrintSpeed ?? prev.maxPrintSpeed,
        };
      });
      setLastModelId(formData.modelId);
    }
  }, [formData?.modelId, defaultCapabilities, lastModelId]);

  // Auto-populate toolhead components when model changes and has toolhead templates
  useEffect(() => {
    // Skip if no model selected or model hasn't changed
    if (!formData?.modelId || formData.modelId === lastModelId) return;
    
    // Find the selected model in the models list
    const selectedModel = models?.find(m => m.id === formData.modelId);
    if (!selectedModel?.toolheads?.length) return;
    
    // Map model toolhead templates to printer toolhead updates
    const modelToolheads = selectedModel.toolheads;
    
    setToolheads(prev => {
      // If printer has no toolheads, create them from model templates
      if (prev.length === 0) {
        return modelToolheads.map(mt => ({
          id: generateUUID(),
          name: mt.name,
          index: mt.index,
          nozzleDiameter: mt.nozzleDiameter ?? 0.4,
          hotendModelId: mt.hotendModelId,
          extruderModelId: mt.extruderModelId,
          toolheadModelDefId: mt.toolheadModelDefId,
          nozzleModelId: mt.nozzleModelId,
          supportedMaterials: mt.supportedMaterials ?? selectedModel.supportedFilamentTypes,
          isPrimary: mt.isPrimary,
        }));
      }
      
      // If printer already has toolheads, update their component references from model templates
      return prev.map((th, idx) => {
        // Find matching template by index or use primary
        const template = modelToolheads.find(mt => mt.index === idx) 
          ?? modelToolheads.find(mt => mt.isPrimary) 
          ?? modelToolheads[0];
        
        if (!template) return th;
        
        return {
          ...th,
          // Update component references from model template
          hotendModelId: template.hotendModelId ?? th.hotendModelId,
          extruderModelId: template.extruderModelId ?? th.extruderModelId,
          toolheadModelDefId: template.toolheadModelDefId ?? th.toolheadModelDefId,
          nozzleModelId: template.nozzleModelId ?? th.nozzleModelId,
          nozzleDiameter: template.nozzleDiameter ?? th.nozzleDiameter,
          supportedMaterials: template.supportedMaterials ?? selectedModel.supportedFilamentTypes ?? th.supportedMaterials,
        };
      });
    });
  }, [formData?.modelId, lastModelId, models]);

  // Expand toolheads when they change (so user can see auto-populated values)
  useEffect(() => {
    if (toolheads.length > 0) {
      setExpandedToolheads(new Set(toolheads.map(th => th.id!)));
    }
  }, [toolheads]);

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

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
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
      // Show clone profiles modal only if slicing is enabled
      // (user may want to clone profiles from template machine)
      if (isSlicerAvailable) {
        setShowCloneProfilesModal(true);
      } else {
        onClose();
      }
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

  /**
   * Tests connectivity to the printer using current form credentials.
   * Uses backend-specific test methods (Moonraker, PrusaLink, OctoPrint).
   */
  const handleTestConnection = async () => {
    if (!formData) return;
    
    // Validate required fields for test
    const errors: Record<string, string[]> = {};

    if (!formData.serverUrl?.trim()) {
      errors.serverUrl = ['Server URL is required'];
    } else {
      try {
        new URL(formData.serverUrl);
      } catch {
        errors.serverUrl = ['Please enter a valid HTTP/HTTPS URL'];
      }
    }

    // Check authentication requirements per backend
    if (formData.backend === PrinterBackend.PrusaLink && !formData.password?.trim()) {
      errors.password = ['Password is required for PrusaLink (Settings → Network → Credentials)'];
    }
    
    if (formData.backend === PrinterBackend.OctoPrint && !formData.apiKey?.trim()) {
      errors.apiKey = ['API Key is required for OctoPrint'];
    }

    if (Object.keys(errors).length > 0) {
      setValidationErrors(prev => ({ ...prev, ...errors }));
      return;
    }

    setIsTesting(true);

    try {
      const result = await apiClient.testConnection({
        serverUrl: formData.serverUrl!,
        backend: formData.backend,
        apiKey: formData.apiKey,
        username: formData.username,
        password: formData.password,
        backendPort: formData.backendPort,
      });
      if (result.success) {
        toast.success(result.message || 'Connection successful', { duration: 5000 });
      } else {
        toast.error(result.message || 'Connection failed', { duration: 8000 });
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Connection test failed';
      toast.error(message, { duration: 8000 });
    } finally {
      setIsTesting(false);
    }
  };

  if (!isOpen || !formData) return null;

  const filteredModels = models || [];

  // Direct submit handler for the Save button (avoids form attribute issues with portals)
  const handleSaveClick = () => {
    const form = document.getElementById('edit-printer-form') as HTMLFormElement;
    if (form) {
      form.requestSubmit();
    }
  };

  const modalFooter = (
    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-2">
      <div className="flex items-center gap-2">
        <Button
          type="button"
          variant="secondary"
          onClick={handleClose}
        >
          Cancel
        </Button>
        <Button
          type="button"
          variant="secondary"
          onClick={handleTestConnection}
          disabled={isTesting || !formData?.serverUrl?.trim()}
          iconLeft={isTesting ? <LoadingIcon className="w-4 h-4 animate-spin" /> : <WiFiIcon className="w-4 h-4" />}
        >
          {isTesting ? 'Testing...' : 'Test'}
        </Button>
      </div>

      <div className="sm:ml-auto">
        <Button
          type="button"
          onClick={handleSaveClick}
          variant="primary"
          disabled={updateMutation.status === 'pending' || !hasChanges}
          iconLeft={<CheckIcon className="w-4 h-4" />}
          title={!hasChanges ? 'No changes to save' : undefined}
        >
          {updateMutation.status === 'pending' ? 'Saving...' : 'Save Changes'}
        </Button>
      </div>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Edit Printer"
      width="max-w-4xl"
      footer={modalFooter}
    >
      {error && (
        <Alert type="error" title="Update Failed" className="mb-4">
          {error}
        </Alert>
      )}

      <form id="edit-printer-form" onSubmit={handleSubmit} className="space-y-5">
            {/* Basic Info Grid */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
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
              <FormField label="Backend">
                <BackendSelector
                  value={formData.backend}
                  onChange={(backend) => handleInputChange('backend', backend)}
                  ariaLabel="Printer backend"
                />
              </FormField>
            </div>

            {/* Connection Details Grid */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
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
            </div>

            {/* Moonraker/FlashForge port fields */}
            {(formData.backend === PrinterBackend.Moonraker || formData.backend === PrinterBackend.FlashForge) && (
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
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

            {/* PrusaLink uses password authentication (username is fixed to "maker") */}
            {formData.backend === PrinterBackend.PrusaLink && (
              <FormField label="Password" error={validationErrors.password?.[0]}>
                <div className="relative">
                  <Input
                    type={showPassword ? 'text' : 'password'}
                    value={formData.password || ''}
                    onChange={e => handleInputChange('password', e.target.value)}
                    placeholder="From printer Settings → Network → Credentials"
                    title="PrusaLink password from printer settings"
                    className="pr-10"
                  />
                  <Button
                    type="button"
                    variant="subtle"
                    onClick={() => setShowPassword(!showPassword)}
                    className="absolute right-2 top-1/2 -translate-y-1/2 p-1! h-auto!"
                    aria-label={showPassword ? 'Hide password' : 'Show password'}
                    iconCenter={showPassword ? <EyeOffIcon className="w-5 h-5" /> : <EyeIcon className="w-5 h-5" />}
                  />
                </div>
              </FormField>
            )}

            {/* OctoPrint uses API Key authentication */}
            {formData.backend === PrinterBackend.OctoPrint && (
              <FormField label="API Key (OctoPrint)" error={validationErrors.apiKey?.[0]}>
                <div className="relative">
                  <Input
                    type={showApiKey ? 'text' : 'password'}
                    value={formData.apiKey || ''}
                    onChange={e => handleInputChange('apiKey', e.target.value)}
                    placeholder="Enter OctoPrint API Key"
                    title="OctoPrint API Key"
                    className="pr-10"
                  />
                  <Button
                    type="button"
                    variant="subtle"
                    onClick={() => setShowApiKey(!showApiKey)}
                    className="absolute right-2 top-1/2 -translate-y-1/2 p-1! h-auto!"
                    aria-label={showApiKey ? 'Hide API key' : 'Show API key'}
                    iconCenter={showApiKey ? <EyeOffIcon className="w-5 h-5" /> : <EyeIcon className="w-5 h-5" />}
                  />
                </div>
              </FormField>
            )}

            {/* Catalog Selection Grid */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
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

            {/* Notes - Full Width */}
            <FormField label="Notes">
              <Textarea
                value={formData.notes || ''}
                onChange={e => handleInputChange('notes', e.target.value)}
                rows={3}
                placeholder="Optional notes"
                title="Printer notes"
              />
            </FormField>

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
                    {toolheads.map((toolhead, index) => {
                      // Get derived values from original ToolheadDto for display
                      const originalToolhead = printerDetails?.toolheads?.find((th: ToolheadDto) => th.id === toolhead.id);
                      return (
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
                          originalToolhead?.nozzleType && NozzleTypeStringLabels[String(originalToolhead.nozzleType)],
                          originalToolhead?.maxTemp && `${originalToolhead.maxTemp}°C`,
                          originalToolhead?.maxFlowRate && `${originalToolhead.maxFlowRate}mm³/s`,
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
                            className="p-1 text-pf-text-secondary hover:text-pf-error"
                            title="Remove toolhead"
                          />
                        }
                      />
                      
                      {/* Toolhead Details - Expandable */}
                      {expandedToolheads.has(toolhead.id!) && (
                        <div className="p-4 bg-pf-bg-primary border-t border-pf-border space-y-4">
                          {/* Basic Info */}
                          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                            <FormField label="Name" htmlFor={`toolhead-name-${index}`}>
                              <Input
                                id={`toolhead-name-${index}`}
                                value={toolhead.name || ''}
                                onChange={e => handleToolheadChange(toolhead.id!, 'name', e.target.value || undefined)}
                                placeholder={`Toolhead ${index + 1}`}
                              />
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
                          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
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
                      );
                    })}
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
                {formData.backend === PrinterBackend.PrusaLink && (
                  <FormField label="Buddy Camera IP" htmlFor="buddy-camera-ip">
                    <Input
                      id="buddy-camera-ip"
                      type="text"
                      value={formData.buddyCameraIp || ''}
                      onChange={e => handleInputChange('buddyCameraIp', e.target.value || undefined)}
                      placeholder="192.168.1.100"
                      title="IP address of the Prusa Buddy board camera"
                    />
                    {formData.buddyCameraIp && (
                      <p className="mt-1 text-xs text-pf-text-secondary">
                        RTSP URL: <code className="bg-pf-surface-secondary px-1 rounded">rtsp://{formData.buddyCameraIp}:554/live/</code>
                      </p>
                    )}
                  </FormField>
                )}
              </div>
            </div>
            
            {/* Cost Settings */}
            <div className="border-t pt-5 mt-5">
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Cost Settings</h4>
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                <FormField
                  label="Wattage (W)"
                  htmlFor="printer-wattage"
                  helper="Power consumption in watts. Leave blank to use model default or global setting."
                >
                  <Input
                    id="printer-wattage"
                    type="number"
                    value={formData.wattage ?? ''}
                    onChange={e => handleInputChange('wattage', e.target.value ? parseFloat(e.target.value) : undefined)}
                    placeholder="e.g. 250"
                    min={0}
                    step={1}
                    title="Printer power consumption in watts"
                  />
                </FormField>
                <FormField
                  label="Machine Hourly Rate ($)"
                  htmlFor="printer-hourly-rate"
                  helper="Hourly operating cost. Leave blank to use the global default."
                >
                  <Input
                    id="printer-hourly-rate"
                    type="number"
                    value={formData.machineHourlyRate ?? ''}
                    onChange={e => handleInputChange('machineHourlyRate', e.target.value ? parseFloat(e.target.value) : undefined)}
                    placeholder="e.g. 0.50"
                    min={0}
                    step={0.01}
                    title="Machine hourly operating rate"
                  />
                </FormField>
              </div>
            </div>

            {/* Auto-Dispatch Defaults */}
            <FormField
              label="Model Dispatch Defaults"
              htmlFor="use-model-dispatch-defaults"
              helper="When enabled, this printer inherits dispatch settings from its model"
            >
              <Toggle
                id="use-model-dispatch-defaults"
                checked={formData.useModelDispatchDefaults ?? true}
                onChange={e => handleInputChange('useModelDispatchDefaults', e.target.checked)}
                label="Use model dispatch defaults"
              />
            </FormField>

            {/* Obico AI Failure Detection */}
            <FormField 
              label="Obico AI Monitoring" 
              htmlFor="obico-enabled"
              helper={
                !formData.cameraStreamUrl && !formData.cameraSnapshotUrl
                  ? "Configure a camera URL above to enable failure detection."
                  : "Enable AI-powered print failure detection. Requires a camera. The app uses the best available Obico ML server when one is configured, or falls back to the global Obico Failure Detection settings."
              }
            >
              <Checkbox
                id="obico-enabled"
                checked={formData.obicoEnabled || false}
                disabled={!formData.cameraStreamUrl && !formData.cameraSnapshotUrl}
                onChange={e => {
                  const enabling = e.target.checked;
                  if (enabling && !formData.cameraStreamUrl && !formData.cameraSnapshotUrl) {
                    toast.error('Obico monitoring requires a camera. Configure a camera URL first.');
                    return;
                  }
                  handleInputChange('obicoEnabled', enabling);
                }}
                label="Enable Obico monitoring for this printer"
              />
            </FormField>
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
