import React, { useState, useCallback, useEffect } from 'react';
import styles from './AddPrinterModal.module.css';
import { LoadingIcon, CheckIcon, WiFiIcon, EyeIcon, EyeOffIcon } from '@/common/components/icons/MdiIcons';
import type { PrinterModelDto, CreatePrinterDto, ManufacturerDto } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { BackendSelector } from '@/common/components/BackendSelector';
import { Button, Input, Select, Textarea, FormField, Alert } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useManufacturers, useModels } from '@/common/hooks/useApi';

interface AddPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

/**
 * Content component for the Add Printer modal
 */
function AddPrinterModalContent({ 
  manufacturers, 
  models,
  isOpen,
  onClose,
  onSuccess
}: {
  manufacturers: ManufacturerDto[];
  models: PrinterModelDto[];
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}) {
  const [formData, setFormData] = useState<CreatePrinterDto>({
    name: '',
    serverUrl: '',
    backend: PrinterBackend.Moonraker,
    notes: '',
    dateAcquired: new Date(), // Default to today's date
    manufacturerId: undefined,
    modelId: undefined,
    apiKey: undefined,
    username: undefined,
    password: undefined,
    cameraStreamUrl: '',
    cameraSnapshotUrl: '',
    backendPort: 7125,
    frontendPort: 80,
  });
  const [filteredModels, setFilteredModels] = useState<PrinterModelDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});
  
  // Test connection state
  const [isTesting, setIsTesting] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [showApiKey, setShowApiKey] = useState(false);

  const handleInputChange = (field: keyof typeof formData, value: unknown) => {
    setFormData(prev => ({
      ...prev,
      [field]: value
    }));
    // Filter models when manufacturer changes
    if (field === 'manufacturerId' && value) {
      const filtered = models.filter(m => m.manufacturerId === value);
      setFilteredModels(filtered);
    }
    // Clear validation error when user starts typing
    if (validationErrors[field]) {
      setValidationErrors(prev => {
        const newErrors = { ...prev };
        delete newErrors[field];
        return newErrors;
      });
    }
    // Clear test result when server URL or backend type changes
    if (field === 'serverUrl' || field === 'backend' || field === 'apiKey' || field === 'backendPort') {
      toast.dismiss();
    }
  };

  /**
   * Tests connectivity to the printer before adding it.
   * Uses backend-specific test methods (Moonraker, PrusaLink, OctoPrint).
   */
  const handleTestConnection = async () => {
    // Validate required fields for test
    const errors: Record<string, string[]> = {};

    if (!formData.serverUrl.trim()) {
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
        serverUrl: formData.serverUrl,
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

  const validateForm = (): boolean => {
    const errors: Record<string, string[]> = {};
    
    if (!formData.name.trim()) {
      errors.name = ['Printer name is required'];
    }
    
    if (!formData.serverUrl.trim()) {
      errors.serverUrl = ['Server URL is required'];
    } else {
      try {
        new URL(formData.serverUrl);
      } catch {
        errors.serverUrl = ['Please enter a valid HTTP/HTTPS URL'];
      }
    }
    
    // Validate authentication per backend
    if (formData.backend === PrinterBackend.PrusaLink && !formData.password?.trim()) {
      errors.password = ['Password is required for PrusaLink printers'];
    }
    
    if (formData.backend === PrinterBackend.OctoPrint && !formData.apiKey?.trim()) {
      errors.apiKey = ['API Key is required for OctoPrint printers'];
    }

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    
    if (!validateForm()) {
      return;
    }

    setIsLoading(true);
    setError('');

    try {
      await apiClient.createPrinter(formData);
      onSuccess();
      handleClose();
    } catch (err: unknown) {
      const error = err as Record<string, unknown>;
      if (error?.response && (error.response as Record<string, unknown>)?.status === 400 && ((error.response as Record<string, unknown>)?.data as Record<string, unknown>)?.errors) {
        // Handle validation errors from the server
        setValidationErrors((error.response as Record<string, unknown>).data as Record<string, string[]>);
      } else {
        setError(((error.response as Record<string, unknown>)?.data as Record<string, unknown>)?.message as string || 'Failed to add printer');
      }
      console.error('Add printer error:', err);
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = useCallback(() => {
    setFormData({
      name: '',
      serverUrl: '',
      backend: PrinterBackend.Moonraker,
      notes: '',
      dateAcquired: new Date(), // Reset to today's date
      manufacturerId: undefined,
      modelId: undefined,
      apiKey: undefined,
      username: undefined,
      password: undefined,
      cameraStreamUrl: '',
      cameraSnapshotUrl: '',
    });
    setValidationErrors({});
    setError('');
    setIsTesting(false);
    onClose();
  }, [onClose]);

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

  if (!isOpen) return null;

  const modalFooter = (
    <div className="space-y-3">
      {/* Buttons */}
      <div className="flex gap-3">
        <Button
          type="button"
          variant="secondary"
          onClick={handleClose}
          className="flex-1"
        >
          Cancel
        </Button>
        <Button
          type="button"
          variant="secondary"
          onClick={handleTestConnection}
          disabled={isTesting || !formData.serverUrl.trim()}
          iconLeft={isTesting ? <LoadingIcon className="w-4 h-4 animate-spin" /> : <WiFiIcon className="w-4 h-4" />}
        >
          {isTesting ? 'Testing...' : 'Test'}
        </Button>
        <Button
          type="submit"
          form="add-printer-form"
          variant="success"
          disabled={isLoading}
          className="flex-1"
          iconLeft={isLoading ? <LoadingIcon className="w-4 h-4 animate-spin" /> : <CheckIcon className="w-4 h-4" />}
        >
          {isLoading ? 'Adding...' : 'Add Printer'}
        </Button>
      </div>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Add New Printer"
      width="max-w-2xl"
      footer={modalFooter}
    >
      {/* Error Message */}
      {error && (
        <Alert type="error" className="mb-4">
          {error}
        </Alert>
      )}

      {/* Form */}
      <form id="add-printer-form" onSubmit={handleSubmit} className="space-y-4">
            {/* Basic Info */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* Printer Name */}
              <FormField
                label="Printer Name"
                required
                error={validationErrors.name?.[0]}
              >
                <Input
                  type="text"
                  value={formData.name}
                  onChange={(e) => handleInputChange('name', e.target.value)}
                  placeholder="My 3D Printer"
                  aria-label="Printer name"
                />
              </FormField>

              {/* Backend Type */}
              <FormField
                label="Backend Type"
                required
              >
                <BackendSelector
                  value={formData.backend}
                  onChange={(backend) => handleInputChange('backend', backend)}
                  required
                />
              </FormField>
            </div>

            {/* Server URL */}
            <FormField
              label="Server URL"
              required
              error={validationErrors.serverUrl?.[0]}
            >
              <Input
                type="url"
                value={formData.serverUrl}
                onChange={(e) => handleInputChange('serverUrl', e.target.value)}
                placeholder="http://192.168.1.100 or http://printer.local"
                aria-label="Server URL"
              />
            </FormField>

            {/* PrusaLink Authentication (password with fixed "maker" username) */}
            {formData.backend === PrinterBackend.PrusaLink && (
              <FormField
                label="Password"
                required
                error={validationErrors.password?.[0]}
                helper="Get this from printer: Settings → Network → Credentials"
              >
                <div className="relative">
                  <Input
                    type={showPassword ? 'text' : 'password'}
                    value={formData.password || ''}
                    onChange={(e) => handleInputChange('password', e.target.value)}
                    placeholder="Enter password from printer"
                    aria-label="Password for PrusaLink"
                    className="pr-10"
                  />
                  <Button
                    type="button"
                    variant="subtle"
                    onClick={() => setShowPassword(!showPassword)}
                    className="absolute right-2 top-1/2 -translate-y-1/2 !p-1 !h-auto"
                    aria-label={showPassword ? 'Hide password' : 'Show password'}
                    iconCenter={showPassword ? <EyeOffIcon className="w-5 h-5" /> : <EyeIcon className="w-5 h-5" />}
                  />
                </div>
              </FormField>
            )}

            {/* OctoPrint Authentication (API Key) */}
            {formData.backend === PrinterBackend.OctoPrint && (
              <FormField
                label="API Key"
                required
                error={validationErrors.apiKey?.[0]}
              >
                <div className="relative">
                  <Input
                    type={showApiKey ? 'text' : 'password'}
                    value={formData.apiKey || ''}
                    onChange={(e) => handleInputChange('apiKey', e.target.value)}
                    placeholder="Enter OctoPrint API key"
                    aria-label="API Key"
                    className="pr-10"
                  />
                  <Button
                    type="button"
                    variant="subtle"
                    onClick={() => setShowApiKey(!showApiKey)}
                    className="absolute right-2 top-1/2 -translate-y-1/2 !p-1 !h-auto"
                    aria-label={showApiKey ? 'Hide API key' : 'Show API key'}
                    iconCenter={showApiKey ? <EyeOffIcon className="w-5 h-5" /> : <EyeIcon className="w-5 h-5" />}
                  />
                </div>
              </FormField>
            )}

            {/* Show backend/frontend port fields for Moonraker and FlashForge */}
            {(formData.backend === PrinterBackend.Moonraker || formData.backend === PrinterBackend.FlashForge) && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <FormField label="Backend Port (API)">
                  <Input
                    type="number"
                    value={formData.backendPort ?? (formData.backend === PrinterBackend.FlashForge ? 8899 : 7125)}
                    onChange={e => handleInputChange('backendPort', parseInt(e.target.value, 10) || (formData.backend === PrinterBackend.FlashForge ? 8899 : 7125))}
                    placeholder={formData.backend === PrinterBackend.FlashForge ? '8899' : '7125'}
                    min={1}
                    max={65535}
                    aria-label="Backend port"
                  />
                </FormField>
                <FormField label="Frontend Port (UI)">
                  <Input
                    type="number"
                    value={formData.frontendPort ?? 80}
                    onChange={e => handleInputChange('frontendPort', parseInt(e.target.value, 10) || 80)}
                    placeholder="80"
                    min={1}
                    max={65535}
                    aria-label="Frontend port"
                  />
                </FormField>
              </div>
            )}

            {/* Camera URLs (for OctoPrint) */}
            {formData.backend === PrinterBackend.OctoPrint && (
              <>
                <FormField label="Camera Stream URL">
                  <Input
                    type="url"
                    value={formData.cameraStreamUrl || ''}
                    onChange={(e) => handleInputChange('cameraStreamUrl', e.target.value)}
                    placeholder="http://octoprint.local/webcam/?action=stream"
                    aria-label="Camera stream URL"
                  />
                </FormField>
                <FormField label="Camera Snapshot URL">
                  <Input
                    type="url"
                    value={formData.cameraSnapshotUrl || ''}
                    onChange={(e) => handleInputChange('cameraSnapshotUrl', e.target.value)}
                    placeholder="http://octoprint.local/webcam/?action=snapshot"
                    aria-label="Camera snapshot URL"
                  />
                </FormField>
              </>
            )}
            {/* Manufacturer & Model */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* Manufacturer */}
              <FormField label="Manufacturer">
                <Select
                  value={formData.manufacturerId || ''}
                  onChange={(e) => handleInputChange('manufacturerId', e.target.value)}
                  aria-label="Manufacturer"
                >
                  <option value="">Select manufacturer...</option>
                  {manufacturers.map((mfg) => (
                    <option key={mfg.id} value={mfg.id}>{mfg.name}</option>
                  ))}
                </Select>
              </FormField>

              {/* Model */}
              <FormField label="Model">
                <Select
                  value={formData.modelId || ''}
                  onChange={(e) => handleInputChange('modelId', e.target.value)}
                  disabled={!formData.manufacturerId}
                  aria-label="Printer model"
                >
                  <option value="">Select model...</option>
                  {filteredModels.map((model) => (
                    <option key={model.id} value={model.id}>{model.name}</option>
                  ))}
                </Select>
              </FormField>
            </div>

            {/* Date Acquired */}
            <div className="relative z-20">
              <FormField
                label="Date Acquired (click the calendar icon →)"
                error={validationErrors.dateAcquired?.[0]}
                helper="Try clicking inside the input field or on the right edge"
              >
                <Input
                  type="date"
                  value={formData.dateAcquired ? (typeof formData.dateAcquired === 'string' ? formData.dateAcquired : formData.dateAcquired.toISOString().split('T')[0]) : ''}
                  onChange={(e) => handleInputChange('dateAcquired', e.target.value ? new Date(e.target.value) : undefined)}
                  max={new Date().toISOString().split('T')[0]}
                  title="Click to open date picker"
                  aria-label="Date acquired"
                  className={styles.dateInputDark}
                />
              </FormField>
              {formData.dateAcquired && (
                <p className="mt-1 text-xs text-pf-text-secondary">✅ Selected: {typeof formData.dateAcquired === 'string' ? formData.dateAcquired : formData.dateAcquired.toISOString().split('T')[0]}</p>
              )}
            </div>

            {/* Notes */}
            <FormField label="Notes">
              <Textarea
                value={formData.notes || ''}
                onChange={(e) => handleInputChange('notes', e.target.value)}
                rows={3}
                placeholder="Optional notes about this printer..."
                aria-label="Printer notes"
              />
            </FormField>

            {/* Cost Settings */}
            <div className="border-t pt-5 mt-5">
              <h4 className="text-lg font-medium text-pf-text-primary mb-4">Cost Settings</h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <FormField
                  label="Wattage (W)"
                  helper="Power consumption in watts. Leave blank to use model default or global setting."
                >
                  <Input
                    type="number"
                    value={formData.wattage ?? ''}
                    onChange={(e) => handleInputChange('wattage', e.target.value ? parseFloat(e.target.value) : undefined)}
                    placeholder="e.g. 250"
                    min={0}
                    step={1}
                    aria-label="Wattage"
                  />
                </FormField>
                <FormField
                  label="Machine Hourly Rate ($)"
                  helper="Hourly operating cost. Leave blank to use the global default."
                >
                  <Input
                    type="number"
                    value={formData.machineHourlyRate ?? ''}
                    onChange={(e) => handleInputChange('machineHourlyRate', e.target.value ? parseFloat(e.target.value) : undefined)}
                    placeholder="e.g. 0.50"
                    min={0}
                    step={0.01}
                    aria-label="Machine hourly rate"
                  />
                </FormField>
              </div>
            </div>
          </form>
    </Modal>
  );
}

/**
 * Wrapper component that uses React Query hooks for data loading
 */
export function AddPrinterModal({ isOpen, onClose, onSuccess }: AddPrinterModalProps) {
  const { data: manufacturers = [], isLoading: loadingMfg, error: mfgError } = useManufacturers();
  const { data: models = [], isLoading: loadingModels, error: modelsError } = useModels();

  const isLoadingData = loadingMfg || loadingModels;
  const dataError = (mfgError || modelsError) as Error | null;

  if (!isOpen) return null;

  // Show loading state
  if (isLoadingData) {
    return (
      <Modal
        isOpen={isOpen}
        onClose={onClose}
        title="Add New Printer"
        width="max-w-2xl"
      >
        <div className="flex items-center justify-center py-12">
          <div className="text-center">
            <LoadingIcon className="w-8 h-8 animate-spin mx-auto mb-2" />
            <p className="text-pf-text-secondary">Loading printers...</p>
          </div>
        </div>
      </Modal>
    );
  }

  // Show error state
  if (dataError) {
    return (
      <Modal
        isOpen={isOpen}
        onClose={onClose}
        title="Add New Printer"
        width="max-w-2xl"
      >
        <div className="p-4">
          <Alert type="error" title="Failed to load printer data">
            <p>{dataError.message || 'Unable to load manufacturers and models'}</p>
          </Alert>
          <div className="mt-4 flex justify-end">
            <Button onClick={onClose} className="bg-pf-bg-2 hover:bg-pf-bg-1">
              Close
            </Button>
          </div>
        </div>
      </Modal>
    );
  }

  return (
    <AddPrinterModalContent 
      manufacturers={manufacturers}
      models={models}
      isOpen={isOpen}
      onClose={onClose}
      onSuccess={onSuccess}
    />
  );
}