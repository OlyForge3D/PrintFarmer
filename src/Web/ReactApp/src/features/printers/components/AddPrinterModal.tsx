import React, { useState, use, Suspense, useCallback, useEffect } from 'react';
import styles from './AddPrinterModal.module.css';
import { LoadingIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
import type { PrinterModelDto, CreatePrinterDto } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { apiClient } from '@/services/api';
import { BackendSelector } from '@/common/components/BackendSelector';
import { Button, Input, Select, Textarea, FormField, Alert } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';

interface ManufacturerDto {
  id: string;
  name: string;
}

interface AddPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

/**
 * React 19 async functions for loading catalog data
 */
async function fetchManufacturers(): Promise<ManufacturerDto[]> {
  return apiClient.getManufacturers();
}

async function fetchModels(): Promise<PrinterModelDto[]> {
  return apiClient.getModels();
}

/**
 * Content component using React 19 use() hook for async data
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
    cameraStreamUrl: '',
    cameraSnapshotUrl: '',
    backendPort: 7125,
    frontendPort: 80,
  });
  const [filteredModels, setFilteredModels] = useState<PrinterModelDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});

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
    
    if ((formData.backend === PrinterBackend.PrusaLink || formData.backend === PrinterBackend.OctoPrint) && !formData.apiKey?.trim()) {
      errors.apiKey = [
        formData.backend === PrinterBackend.OctoPrint
          ? 'API Key is required for OctoPrint printers'
          : 'API Key is required for PrusaLink printers'
      ];
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
      cameraStreamUrl: '',
      cameraSnapshotUrl: '',
    });
    setValidationErrors({});
    setError('');
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
        type="submit"
        form="add-printer-form"
        variant="success"
        disabled={isLoading}
        className="flex-1"
        iconLeft={isLoading ? <LoadingIcon className="w-4 h-4" /> : <CheckIcon className="w-4 h-4" />}
      >
        {isLoading ? 'Adding...' : 'Add Printer'}
      </Button>
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

            {/* API Key (for PrusaLink and OctoPrint) */}
            {(formData.backend === PrinterBackend.PrusaLink || formData.backend === PrinterBackend.OctoPrint) && (
              <FormField
                label="API Key"
                required
                error={validationErrors.apiKey?.[0]}
              >
                <Input
                  type="text"
                  value={formData.apiKey || ''}
                  onChange={(e) => handleInputChange('apiKey', e.target.value)}
                  placeholder={formData.backend === PrinterBackend.OctoPrint ? "Enter OctoPrint API key" : "Enter PrusaLink API key"}
                  aria-label="API Key"
                />
              </FormField>
            )}

            {/* Show backend/frontend port fields for Moonraker */}
            {formData.backend === PrinterBackend.Moonraker && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <FormField label="Backend Port (API)">
                  <Input
                    type="number"
                    value={formData.backendPort ?? 7125}
                    onChange={e => handleInputChange('backendPort', parseInt(e.target.value, 10) || 7125)}
                    placeholder="7125"
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
          </form>
    </Modal>
  );
}

/**
 * Wrapper component with Suspense boundary for async data loading
 */
export function AddPrinterModal({ isOpen, onClose, onSuccess }: AddPrinterModalProps) {
  if (!isOpen) return null;

  return (
    <Suspense fallback={
      <Modal
        isOpen={isOpen}
        onClose={onClose}
        title="Add New Printer"
        width="max-w-2xl"
      >
        <div className="flex items-center justify-center py-12">
          <div className="text-center">
            <LoadingIcon className="w-8 h-8 animate-spin mx-auto mb-2" />
            <p className="text-gray-500">Loading printers...</p>
          </div>
        </div>
      </Modal>
    }>
      <AddPrinterModalAsync isOpen={isOpen} onClose={onClose} onSuccess={onSuccess} />
    </Suspense>
  );
}

/**
 * Inner component that uses the use() hook
 */
function AddPrinterModalAsync({ isOpen, onClose, onSuccess }: AddPrinterModalProps) {
  const [manufacturersPromise] = useState(fetchManufacturers());
  const [modelsPromise] = useState(fetchModels());

  const manufacturers = use(manufacturersPromise);
  const models = use(modelsPromise);

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