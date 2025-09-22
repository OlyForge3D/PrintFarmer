// ...existing code...
import React, { useState, useEffect } from 'react';
import styles from './AddPrinterModal.module.css';
import { X, AlertCircle, Check } from 'lucide-react';
import type { PrinterModelDto, CreatePrinterDto } from '@/types/api';

interface ManufacturerDto {
  id: string;
  name: string;
}


interface AddPrinterModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

const PrinterBackends = {
  Moonraker: 0,
  PrusaLink: 1,
  SDCP: 2,
  OctoPrint: 3
} as const;

export function AddPrinterModal({ isOpen, onClose, onSuccess }: AddPrinterModalProps) {
  const [formData, setFormData] = useState<CreatePrinterDto>({
    name: '',
    serverUrl: '',
    backend: PrinterBackends.Moonraker,
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
      const response = await fetch('/api/catalog/manufacturers');
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
      const response = await fetch('/api/catalog/printer-models');
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
    
    if ((formData.backend === PrinterBackends.PrusaLink || formData.backend === PrinterBackends.OctoPrint) && !formData.apiKey?.trim()) {
      errors.apiKey = [
        formData.backend === PrinterBackends.OctoPrint
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
      const response = await fetch('/api/printers', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
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
      backend: PrinterBackends.Moonraker,
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
            <button
              onClick={handleClose}
              className="text-pf-text-tertiary hover:text-pf-text-primary transition-colors"
              aria-label="Close add printer dialog"
              title="Close"
            >
              <X className="w-6 h-6" />
            </button>
          </div>

          {/* Error Message */}
          {error && (
            <div className="mb-4 p-3 bg-pf-error-bg border border-pf-error-border rounded-md flex items-center gap-2">
              <AlertCircle className="w-5 h-5 text-pf-error-text flex-shrink-0" />
              <span className="text-pf-error-text text-sm">{error}</span>
            </div>
          )}

          {/* Form */}
          <form onSubmit={handleSubmit} className="space-y-4 max-h-[70vh] overflow-y-auto pr-2">
            {/* Basic Info */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* Printer Name */}
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-1">
                  Printer Name *
                </label>
                <input
                  type="text"
                  value={formData.name}
                  onChange={(e) => handleInputChange('name', e.target.value)}
                  className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                  placeholder="My 3D Printer"
                />
                {validationErrors.name && (
                  <p className="mt-1 text-sm text-pf-error-text">{validationErrors.name[0]}</p>
                )}
              </div>

              {/* Backend Type */}
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-1">
                  Backend Type *
                </label>
                <select
                  value={formData.backend}
                  onChange={(e) => handleInputChange('backend', parseInt(e.target.value))}
                  className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                  aria-label="Backend type"
                >
                  <option value={PrinterBackends.Moonraker}>Moonraker (Klipper)</option>
                  <option value={PrinterBackends.PrusaLink}>PrusaLink (Prusa)</option>
                  <option value={PrinterBackends.SDCP}>SDCP (Generic)</option>
                  <option value={PrinterBackends.OctoPrint}>OctoPrint</option>
                </select>
              </div>
            </div>

            {/* Server URL */}
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-1">
                Server URL *
              </label>
              <input
                type="url"
                value={formData.serverUrl}
                onChange={(e) => handleInputChange('serverUrl', e.target.value)}
                className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                placeholder="http://192.168.1.100 or http://printer.local"
              />
              {validationErrors.serverUrl && (
                <p className="mt-1 text-sm text-pf-error-text">{validationErrors.serverUrl[0]}</p>
              )}
            </div>

            {/* API Key (for PrusaLink and OctoPrint) */}
            {(formData.backend === PrinterBackends.PrusaLink || formData.backend === PrinterBackends.OctoPrint) && (
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-1">
                  API Key *
                </label>
                <input
                  type="text"
                  value={formData.apiKey || ''}
                  onChange={(e) => handleInputChange('apiKey', e.target.value)}
                  className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                  placeholder={formData.backend === PrinterBackends.OctoPrint ? "Enter OctoPrint API key" : "Enter PrusaLink API key"}
                />
                {validationErrors.apiKey && (
                  <p className="mt-1 text-sm text-pf-error-text">{validationErrors.apiKey[0]}</p>
                )}
              </div>
            )}

            {/* Show backend/frontend port fields for Moonraker */}
            {formData.backend === PrinterBackends.Moonraker && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-pf-text-primary mb-1">
                    Backend Port (API)
                  </label>
                  <input
                    type="number"
                    value={formData.backendPort ?? 7125}
                    onChange={e => handleInputChange('backendPort', parseInt(e.target.value, 10) || 7125)}
                    className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                    placeholder="7125"
                    min={1}
                    max={65535}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-primary mb-1">
                    Frontend Port (UI)
                  </label>
                  <input
                    type="number"
                    value={formData.frontendPort ?? 80}
                    onChange={e => handleInputChange('frontendPort', parseInt(e.target.value, 10) || 80)}
                    className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                    placeholder="80"
                    min={1}
                    max={65535}
                  />
                </div>
              </div>
            )}

            {/* Camera URLs (for OctoPrint) */}
            {formData.backend === PrinterBackends.OctoPrint && (
              <>
                <div>
                  <label className="block text-sm font-medium text-pf-text-primary mb-1">
                    Camera Stream URL
                  </label>
                  <input
                    type="url"
                    value={formData.cameraStreamUrl || ''}
                    onChange={(e) => handleInputChange('cameraStreamUrl', e.target.value)}
                    className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                    placeholder="http://octoprint.local/webcam/?action=stream"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-pf-text-primary mb-1">
                    Camera Snapshot URL
                  </label>
                  <input
                    type="url"
                    value={formData.cameraSnapshotUrl || ''}
                    onChange={(e) => handleInputChange('cameraSnapshotUrl', e.target.value)}
                    className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                    placeholder="http://octoprint.local/webcam/?action=snapshot"
                  />
                </div>
              </>
            )}
            {/* Manufacturer & Model */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* Manufacturer */}
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-1">
                  Manufacturer
                </label>
                <select
                  value={formData.manufacturerId || ''}
                  onChange={(e) => handleInputChange('manufacturerId', e.target.value)}
                  className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent"
                  aria-label="Manufacturer"
                >
                  <option value="">Select manufacturer...</option>
                  {manufacturers.map((mfg) => (
                    <option key={mfg.id} value={mfg.id}>{mfg.name}</option>
                  ))}
                </select>
              </div>

              {/* Model */}
              <div>
                <label className="block text-sm font-medium text-pf-text-primary mb-1">
                  Model
                </label>
                <select
                  value={formData.modelId || ''}
                  onChange={(e) => handleInputChange('modelId', e.target.value)}
                  disabled={!formData.manufacturerId}
                  className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent disabled:opacity-50"
                  aria-label="Printer model"
                >
                  <option value="">Select model...</option>
                  {filteredModels.map((model) => (
                    <option key={model.id} value={model.id}>{model.name}</option>
                  ))}
                </select>
              </div>
            </div>

            {/* Date Acquired */}
            <div className="relative z-20">
              <label className="block text-sm font-medium text-pf-text-primary mb-1">
                Date Acquired (click the calendar icon →)
              </label>
              <div className="relative">
                <input
                  type="date"
                  value={formData.dateAcquired ? (typeof formData.dateAcquired === 'string' ? formData.dateAcquired : formData.dateAcquired.toISOString().split('T')[0]) : ''}
                  onChange={(e) => handleInputChange('dateAcquired', e.target.value ? new Date(e.target.value) : undefined)}
                  className={`w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent ${styles.dateInputDark}`}
                  max={new Date().toISOString().split('T')[0]}
                  title="Click to open date picker"
                  aria-label="Date acquired"
                />
              </div>
              {formData.dateAcquired && (
                <p className="mt-1 text-xs text-pf-text-secondary">✅ Selected: {typeof formData.dateAcquired === 'string' ? formData.dateAcquired : formData.dateAcquired.toISOString().split('T')[0]}</p>
              )}
              {validationErrors.dateAcquired && (
                <p className="mt-1 text-sm text-pf-error-text">{validationErrors.dateAcquired[0]}</p>
              )}
              <p className="mt-1 text-xs text-pf-text-tertiary">Try clicking inside the input field or on the right edge</p>
            </div>

            {/* Notes */}
            <div>
              <label className="block text-sm font-medium text-pf-text-primary mb-1">
                Notes
              </label>
              <textarea
                value={formData.notes || ''}
                onChange={(e) => handleInputChange('notes', e.target.value)}
                rows={3}
                className="w-full px-3 py-2 bg-pf-panel border border-pf-border-medium rounded-md text-pf-text-primary placeholder-pf-text-tertiary focus:outline-none focus:ring-2 focus:ring-pf-accent-2 focus:border-transparent resize-none"
                placeholder="Optional notes about this printer..."
              />
            </div>

            {/* Form Actions */}
            <div className="flex gap-3 pt-4">
              <button
                type="button"
                onClick={handleClose}
                className="flex-1 px-4 py-2 border border-pf-border-light rounded-md text-pf-text-primary bg-pf-panel hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent-2 transition-colors"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={isLoading}
                className="flex-1 inline-flex items-center justify-center px-4 py-2 border border-transparent rounded-md text-white bg-pf-success hover:bg-pf-success-hover focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-success disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                {isLoading ? (
                  <>
                    <div className="pf-animate-spin -ml-1 mr-2 h-4 w-4 border-2 border-white border-t-transparent rounded-full"></div>
                    Adding...
                  </>
                ) : (
                  <>
                    <Check className="w-4 h-4 mr-2" />
                    Add Printer
                  </>
                )}
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}