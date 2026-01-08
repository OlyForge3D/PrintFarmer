import React, { useState, useCallback } from 'react';

export interface JobDetailsSectionProps {
  jobDetails: {
    id: string;
    name: string;
    status: string;
    priority: number;
    queuePosition: number;
    printerName: string;
    materialType?: string;
  };
  isEditing: boolean;
  onFieldChange: (field: string, value: any) => void;
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

  const handleMaterialChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value.trim();
      onFieldChange('materialType', value || undefined);
    },
    [onFieldChange]
  );

  if (isEditing) {
    return (
      <div className="job-details-section editing">
        <fieldset>
          <legend>Basic Information</legend>

          <div className="form-group">
            <label htmlFor="job-name">
              Job Name <span className="required" aria-label="required">*</span>
            </label>
            <input
              id="job-name"
              type="text"
              value={jobDetails.name}
              onChange={handleNameChange}
              maxLength={255}
              placeholder="Enter job name"
              aria-invalid={!!errors.name}
              aria-describedby={errors.name ? 'name-error' : undefined}
            />
            {errors.name && (
              <span id="name-error" className="error-message" role="alert">
                {errors.name}
              </span>
            )}
          </div>

          <div className="form-group">
            <label htmlFor="job-priority">
              Priority
              <span className="help-text">(0-100, higher = more urgent)</span>
            </label>
            <input
              id="job-priority"
              type="number"
              min="0"
              max="100"
              value={jobDetails.priority}
              onChange={handlePriorityChange}
              placeholder="Priority (0-100)"
              aria-invalid={!!errors.priority}
              aria-describedby={errors.priority ? 'priority-error' : undefined}
            />
            {errors.priority && (
              <span id="priority-error" className="error-message" role="alert">
                {errors.priority}
              </span>
            )}
          </div>

          <div className="form-group">
            <label htmlFor="job-material">Material Type</label>
            <input
              id="job-material"
              type="text"
              value={jobDetails.materialType || ''}
              onChange={handleMaterialChange}
              placeholder="e.g., PLA, PETG, ABS"
              list="material-suggestions"
            />
            <datalist id="material-suggestions">
              <option value="PLA" />
              <option value="PETG" />
              <option value="ABS" />
              <option value="TPU" />
              <option value="Nylon" />
            </datalist>
          </div>

          <div className="form-group read-only">
            <label htmlFor="job-printer">Printer</label>
            <input
              id="job-printer"
              type="text"
              value={jobDetails.printerName}
              disabled
              readOnly
            />
            <span className="read-only-hint">Cannot change printer after queuing</span>
          </div>

          <div className="form-group read-only">
            <label htmlFor="job-status">Status</label>
            <input
              id="job-status"
              type="text"
              value={jobDetails.status}
              disabled
              readOnly
            />
            <span className="read-only-hint">Status managed by system</span>
          </div>

          <div className="form-group read-only">
            <label htmlFor="job-position">Queue Position</label>
            <input
              id="job-position"
              type="text"
              value={jobDetails.queuePosition}
              disabled
              readOnly
            />
            <span className="read-only-hint">Use drag-and-drop to reorder</span>
          </div>
        </fieldset>
      </div>
    );
  }

  return (
    <div className="job-details-section view-only">
      <div className="details-grid">
        <div className="detail-row">
          <span className="detail-label">Name</span>
          <span className="detail-value">{jobDetails.name}</span>
        </div>
        <div className="detail-row">
          <span className="detail-label">Status</span>
          <span className="detail-value">
            <span className="status-badge" data-status={jobDetails.status.toLowerCase()}>
              {jobDetails.status}
            </span>
          </span>
        </div>
        <div className="detail-row">
          <span className="detail-label">Priority</span>
          <span className="detail-value">{jobDetails.priority}</span>
        </div>
        <div className="detail-row">
          <span className="detail-label">Queue Position</span>
          <span className="detail-value">{jobDetails.queuePosition}</span>
        </div>
        <div className="detail-row">
          <span className="detail-label">Printer</span>
          <span className="detail-value">{jobDetails.printerName}</span>
        </div>
        <div className="detail-row">
          <span className="detail-label">Material Type</span>
          <span className="detail-value">{jobDetails.materialType || 'Not specified'}</span>
        </div>
      </div>
    </div>
  );
};

export default JobDetailsSection;
