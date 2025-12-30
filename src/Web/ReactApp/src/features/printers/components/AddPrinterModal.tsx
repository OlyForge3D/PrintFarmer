// ...existing code...
import React, { useState, useEffect } from 'react';
import styles from './AddPrinterModal.module.css';
import { LoadingIcon, CloseIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
import type { PrinterModelDto, CreatePrinterDto } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { BackendSelector } from '@/common/components/BackendSelector';
import { Button, Input, Select, Textarea, FormField, Alert } from '@/common/components/ui';

interface ManufacturerDto {
  id: string;
  name: string;
}


interface AddPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

export function AddPrinterModal({ isOpen, onClose, onSuccess }: AddPrinterModalProps) {
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
  const [manufacturers, setManufacturers] = useState<ManufacturerDto[]>([]);
  const [models, setModels] = useState<PrinterModelDto[]>([]);
  const [filteredModels, setFilteredModels] = useState<PrinterModelDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<Record<string, string[]>>({});
  
  // Load manufacturers on mount
  useEffect(() => {
    if (isOpen) {
      loadManufacturers();
      loadModels();
    }
  }, [isOpen]);

  // Filter models when manufacturer changes
  useEffect(() => {
    if (formData.manufacturerId) {
      setFilteredModels(models.filter(m => m.manufacturerId === formData.manufacturerId));
    } else {
      setFilteredModels([]);
    }
    // Reset model selection when manufacturer changes
    setFormData(prev => ({ ...prev, modelId: undefined }));
  }, [formData.manufacturerId, models]);

  const loadManufacturers = async () => {
    try {
      const response = await fetch(`${getApiBaseUrl()}/catalog/manufacturers`, {
        headers: getAuthHeaders(),
      });
      if (response.ok) {
        const data = await response.json();
        setManufacturers(data);
      }
    } catch (err) {
      console.error('Failed to load manufacturers:', err);
    }
  };

  const loadModels = async () => {
    try {
      const response = await fetch(`${getApiBaseUrl()}/catalog/printer-models`, {
        headers: getAuthHeaders(),
      });
      if (response.ok) {
        const data = await response.json();
        setModels(data);
      }
    } catch (err) {
      console.error('Failed to load models:', err);
    }
  };

  const handleInputChange = (field: keyof typeof formData, value: unknown) => {
    setFormData(prev => ({
      ...prev,
      [field]: value
    }));
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
      const response = await fetch(`${getApiBaseUrl()}/printers`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...getAuthHeaders(),
        },
        body: JSON.stringify(formData),
      });

      if (response.ok) {
        onSuccess();
        handleClose();
      } else {
        const errorData = await response.json().catch(() => null);
        if (response.status === 400 && errorData?.errors) {
          // Handle validation errors from the server
          setValidationErrors(errorData.errors);
        } else {
          setError(errorData?.message || `Failed to add printer (${response.status})`);
        }
      }
    } catch (err) {
      setError('Network error. Please check your connection and try again.');
      console.error('Add printer error:', err);
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
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
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto">
      <div className="flex items-center justify-center min-h-screen pt-4 px-4 pb-20 text-center sm:block sm:p-0">
        {/* Overlay */}
        <div 
          className="fixed inset-0 bg-pf-bg-0 bg-opacity-75 transition-opacity" 
          onClick={handleClose}
        />
        
        {/* Modal */}
        <div className="inline-block align-bottom bg-pf-bg-1 rounded-xl px-6 pt-6 pb-6 text-left shadow-xl transform transition-all sm:my-8 sm:align-middle sm:max-w-2xl sm:w-full border border-pf-border relative overflow-visible">
          {/* Header */}
          <div className="flex items-center justify-between mb-6">
            <h3 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">
              Add New Printer
            </h3>
            <Button
              variant="subtle"
              size="sm"
              onClick={handleClose}
              aria-label="Close add printer dialog"
              title="Close"
              className="!p-1 !h-auto"
            >
              <CloseIcon className="w-6 h-6" />
            </Button>
          </div>

          {/* Error Message */}
          {error && (
            <Alert type="error" className="mb-4">
              {error}
            </Alert>
          )}

          {/* Form */}
          <form onSubmit={handleSubmit} className="space-y-4 max-h-[70vh] overflow-y-auto pr-2">
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

            {/* Form Actions */}
            <div className="flex gap-3 pt-4">
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
                variant="success"
                disabled={isLoading}
                className="flex-1"
              >
                {isLoading ? (
                  <>
                    <LoadingIcon className="w-4 h-4 mr-2" />
                    Adding...
                  </>
                ) : (
                  <>
                    <CheckIcon className="w-4 h-4 mr-2" />
                    Add Printer
                  </>
                )}
              </Button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}