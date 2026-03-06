import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { apiClient } from '@/services/api';
import { Select, Spinner } from '@/common/components/ui';
import type { SpoolmanFilament } from '@/types/api';

export interface JobDetailsSectionProps {
  jobDetails: {
    id: string;
    name: string;
    status: string;
    priority: number;
    queuePosition: number;
    printerName?: string;
    printerModel?: string;
    // Material and nozzle from backend (requiredMaterialType/requiredNozzleDiameter)
    materialType?: string;
    requiredMaterialType?: string;
    nozzleDiameter?: number;
    requiredNozzleDiameter?: number;
    // Spoolman filament assignment
    spoolmanFilamentId?: number;
    filamentName?: string;
    filamentVendor?: string;
    filamentColor?: string;
  };
  isEditing: boolean;
  onFieldChange: (field: keyof JobDetailsSectionProps['jobDetails'], value: string | number | undefined) => void;
}

const JobDetailsSection: React.FC<JobDetailsSectionProps> = ({
  jobDetails,
  isEditing,
  onFieldChange,
}) => {
  const [errors, setErrors] = useState<Record<string, string>>({});

  const validatePriority = useCallback((value: number) => {
    if (value < 0 || value > 100) {
      setErrors((prev) => ({
        ...prev,
        priority: 'Priority must be between 0 and 100',
      }));
      return false;
    }
    setErrors((prev) => {
      const newErrors = { ...prev };
      delete newErrors.priority;
      return newErrors;
    });
    return true;
  }, []);

  const handlePriorityChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = parseInt(e.target.value, 10);
      if (!isNaN(value) && validatePriority(value)) {
        onFieldChange('priority', value);
      }
    },
    [validatePriority, onFieldChange]
  );

  const handleNameChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value.trim();
      if (value.length > 0 && value.length <= 255) {
        onFieldChange('name', value);
        setErrors((prev) => {
          const newErrors = { ...prev };
          delete newErrors.name;
          return newErrors;
        });
      } else if (value.length === 0) {
        setErrors((prev) => ({
          ...prev,
          name: 'Job name is required',
        }));
      } else {
        setErrors((prev) => ({
          ...prev,
          name: 'Job name must be 255 characters or less',
        }));
      }
    },
    [onFieldChange]
  );

  // Filament picker state (edit mode)
  const [filaments, setFilaments] = useState<SpoolmanFilament[]>([]);
  const [filamentsLoaded, setFilamentsLoaded] = useState(false);

  useEffect(() => {
    if (!isEditing || filamentsLoaded) return;
    let cancelled = false;
    apiClient.getFilaments()
      .then(data => { if (!cancelled) { setFilaments(data); setFilamentsLoaded(true); } })
      .catch(() => { if (!cancelled) setFilamentsLoaded(true); });
    return () => { cancelled = true; };
  }, [isEditing, filamentsLoaded]);

  // Filter filaments by the job's required material type (same as QueueGcodeModal/CreateProjectModal)
  const materialForFilter = jobDetails.requiredMaterialType || jobDetails.materialType;
  const filteredFilaments = useMemo(() => {
    if (!materialForFilter) return filaments;
    const needle = materialForFilter.toLowerCase();
    return filaments.filter(f => f.material?.toLowerCase() === needle);
  }, [filaments, materialForFilter]);

  const handleFilamentChange = useCallback(
    (e: React.ChangeEvent<HTMLSelectElement>) => {
      const val = e.target.value;
      if (!val) {
        // Clear filament — use 0 to signal clearing to backend
        onFieldChange('spoolmanFilamentId', 0);
        onFieldChange('filamentName', undefined);
        onFieldChange('filamentVendor', undefined);
        onFieldChange('filamentColor', undefined);
      } else {
        const filament = filaments.find(f => f.id === parseInt(val, 10));
        if (filament) {
          onFieldChange('spoolmanFilamentId', filament.id);
          onFieldChange('filamentName', filament.name || undefined);
          onFieldChange('filamentVendor', filament.vendor || undefined);
          onFieldChange('filamentColor', filament.colorHex || undefined);
        }
      }
    },
    [onFieldChange, filaments]
  );

  if (isEditing) {
    return (
      <div className="space-y-4">
        <fieldset className="space-y-4">
          <legend className="text-sm font-semibold text-pf-text-primary mb-3">Basic Information</legend>

          <div className="space-y-1.5">
            <label htmlFor="job-name" className="block text-sm font-medium text-pf-text-secondary">
              Job Name <span className="text-pf-error" aria-label="required">*</span>
            </label>
            <input
              id="job-name"
              type="text"
              value={jobDetails.name}
              onChange={handleNameChange}
              maxLength={255}
              placeholder="Enter job name"
              className="w-full px-3 py-2 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-transparent"
              aria-invalid={!!errors.name}
              aria-describedby={errors.name ? 'name-error' : undefined}
            />
            {errors.name && (
              <span id="name-error" className="text-xs text-pf-error" role="alert">
                {errors.name}
              </span>
            )}
          </div>

          <div className="space-y-1.5">
            <label htmlFor="job-priority" className="block text-sm font-medium text-pf-text-secondary">
              Priority
              <span className="ml-1 text-xs text-pf-text-muted">(0-100, higher = more urgent)</span>
            </label>
            <input
              id="job-priority"
              type="number"
              min="0"
              max="100"
              value={jobDetails.priority}
              onChange={handlePriorityChange}
              placeholder="Priority (0-100)"
              className="w-full px-3 py-2 text-sm border border-pf-border rounded-sm bg-pf-bg-0 text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-transparent"
              aria-invalid={!!errors.priority}
              aria-describedby={errors.priority ? 'priority-error' : undefined}
            />
            {errors.priority && (
              <span id="priority-error" className="text-xs text-pf-error" role="alert">
                {errors.priority}
              </span>
            )}
          </div>

          <div className="space-y-1.5">
            <label htmlFor="job-filament" className="block text-sm font-medium text-pf-text-secondary">
              Spoolman Filament
              {jobDetails.filamentColor && (
                <span
                  className="inline-block w-3 h-3 rounded-full ml-1 align-middle border border-pf-border"
                  style={{ backgroundColor: jobDetails.filamentColor }}
                  aria-hidden="true"
                />
              )}
            </label>
            {!filamentsLoaded ? (
              <div className="flex items-center gap-2 text-sm text-pf-text-tertiary py-2">
                <Spinner size="sm" />
                Loading filaments...
              </div>
            ) : filaments.length > 0 ? (
              <Select
                id="job-filament"
                aria-label="Select Spoolman filament"
                value={jobDetails.spoolmanFilamentId?.toString() ?? ''}
                onChange={handleFilamentChange}
              >
                <option value="">-- Not assigned --</option>
                {filteredFilaments.map(f => (
                  <option key={f.id} value={f.id.toString()}>
                    {f.vendor ? `${f.vendor} — ` : ''}{f.name || 'Unnamed'}{f.material ? ` (${f.material})` : ''}
                  </option>
                ))}
              </Select>
            ) : (
              <p className="text-xs text-pf-text-muted italic">No Spoolman filaments available</p>
            )}
          </div>


        </fieldset>
      </div>
    );
  }

  // Get material and nozzle from either new or legacy field names
  const materialType = jobDetails.requiredMaterialType || jobDetails.materialType;
  const nozzleDiameter = jobDetails.requiredNozzleDiameter || jobDetails.nozzleDiameter;

  return (
    <div className="space-y-1">
      <dl className="grid grid-cols-1 gap-3">
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Name</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">{jobDetails.name}</dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Status</dt>
          <dd className="text-sm mt-1 sm:mt-0">
            <span 
              className={`inline-flex px-2 py-0.5 text-xs font-semibold rounded-full ${
                jobDetails.status.toLowerCase() === 'completed' ? 'bg-pf-success/20 text-pf-success' :
                jobDetails.status.toLowerCase() === 'printing' ? 'bg-pf-accent/20 text-pf-accent' :
                jobDetails.status.toLowerCase() === 'queued' ? 'bg-pf-warning/20 text-pf-warning' :
                jobDetails.status.toLowerCase() === 'failed' ? 'bg-pf-error/20 text-pf-error' :
                'bg-pf-bg-2 text-pf-text-secondary'
              }`}
            >
              {jobDetails.status.toUpperCase()}
            </span>
          </dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Printer</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">{jobDetails.printerName || <span className="text-pf-text-muted italic">Not assigned</span>}</dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Printer Model</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">{jobDetails.printerModel || <span className="text-pf-text-muted italic">Not specified</span>}</dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Material Type</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">{materialType || <span className="text-pf-text-muted italic">Not specified</span>}</dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2 border-b border-pf-border">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Filament</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">
            {jobDetails.filamentName ? (
              <span className="inline-flex items-center gap-1.5">
                {jobDetails.filamentColor && (
                  <span
                    className="inline-block w-3 h-3 rounded-full border border-pf-border shrink-0"
                    style={{ backgroundColor: jobDetails.filamentColor }}
                    aria-hidden="true"
                  />
                )}
                <span>{jobDetails.filamentVendor ? `${jobDetails.filamentVendor} — ` : ''}{jobDetails.filamentName}</span>
              </span>
            ) : (
              <span className="text-pf-text-muted italic">Not assigned</span>
            )}
          </dd>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center py-2">
          <dt className="text-sm font-medium text-pf-text-secondary w-full sm:w-40 shrink-0">Nozzle Diameter</dt>
          <dd className="text-sm text-pf-text-primary mt-1 sm:mt-0">{nozzleDiameter ? `${nozzleDiameter}mm` : <span className="text-pf-text-muted italic">Not specified</span>}</dd>
        </div>
      </dl>
    </div>
  );
};

export default JobDetailsSection;
