import React, { useCallback, useEffect, useState } from 'react';
import { Button } from '@/common/components/ui/Button';
import { printQueueService, QueuedPrintJobDto } from '../../../services/printQueueService';
import JobDetailsSection from './JobDetailsSection';
import JobNotesEditor from './JobNotesEditor';
import JobTagsEditor from './JobTagsEditor';
import '../styles/JobDetailsModal.css';

export interface JobDetailsModalProps {
  jobId: string;
  isOpen: boolean;
  onClose: () => void;
  onSave?: (updatedJob: QueuedPrintJobDto) => void;
}

interface JobDetails {
  id: string;
  name: string;
  status: string;
  priority: number;
  queuePosition: number;
  gcodeFileId: string;
  fileName?: string;
  printerId: string;
  printerName: string;
  printerModel: string;
  notes: string;
  tags: string[];
  materialType?: string;
  nozzleDiameter?: number;
  estimatedPrintTimeSeconds: number;
  estimatedFilamentUsage?: string;
  createdAt: string;
  queuedAt?: string;
  startedAt?: string;
  completedAt?: string;
}

type TabType = 'overview' | 'details' | 'timing' | 'history';

const JobDetailsModal: React.FC<JobDetailsModalProps> = ({
  jobId,
  isOpen,
  onClose,
  onSave,
}) => {
  const [jobDetails, setJobDetails] = useState<JobDetails | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isEditing, setIsEditing] = useState(false);
  const [hasChanges, setHasChanges] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [activeTab, setActiveTab] = useState<TabType>('overview');
  const [editedDetails, setEditedDetails] = useState<JobDetails | null>(null);

  // Load job details when modal opens
  useEffect(() => {
    if (!isOpen || !jobId) return;

    const loadJobDetails = async () => {
      try {
        setLoading(true);
        setError(null);
        
        const response = await printQueueService.getJobDetailsAsync(jobId);
        const jobDetailsData = response as unknown as JobDetails;
        setJobDetails(jobDetailsData);
        setEditedDetails(jobDetailsData);
        setIsEditing(false);
        setHasChanges(false);
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to load job details';
        setError(errorMessage);
        console.error('Failed to load job details:', err);
      } finally {
        setLoading(false);
      }
    };

    loadJobDetails();
  }, [isOpen, jobId]);

  const handleEditClick = useCallback(() => {
    setIsEditing(true);
    setHasChanges(false);
  }, []);

  const handleCancelEdit = useCallback(() => {
    setEditedDetails(jobDetails);
    setIsEditing(false);
    setHasChanges(false);
    setError(null);
  }, [jobDetails]);

  const handleFieldChange = useCallback((field: keyof JobDetails, value: string | number | undefined) => {
    if (!editedDetails) return;

    const updated = { ...editedDetails, [field]: value };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleTagsChange = useCallback((tags: string[]) => {
    if (!editedDetails) return;

    const updated = { ...editedDetails, tags };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleNotesChange = useCallback((notes: string) => {
    if (!editedDetails) return;

    const updated = { ...editedDetails, notes };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleSave = useCallback(async () => {
    if (!editedDetails || !hasChanges) {
      setIsEditing(false);
      return;
    }

    try {
      setIsSaving(true);
      setError(null);

      // Call update endpoint with changed fields
      const updatedJob = await printQueueService.updateJobDetailsAsync(
        jobId,
        editedDetails
      );

      const jobDetailsData = updatedJob as unknown as JobDetails;
      setJobDetails(jobDetailsData);
      setEditedDetails(jobDetailsData);
      setIsEditing(false);
      setHasChanges(false);

      // Call callback if provided
      if (onSave) {
        onSave(updatedJob);
      }

      // Show success message
      if (window.PrintFarmerDebug?.utilities) console.log('Job updated successfully');
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to update job';
      setError(errorMessage);
      console.error('Failed to save job details:', err);
    } finally {
      setIsSaving(false);
    }
  }, [jobId, editedDetails, hasChanges, onSave]);

  const handleClose = useCallback(() => {
    if (hasChanges) {
      const confirmed = window.confirm(
        'You have unsaved changes. Are you sure you want to close?'
      );
      if (!confirmed) return;
    }

    setIsEditing(false);
    setHasChanges(false);
    setError(null);
    onClose();
  }, [hasChanges, onClose]);

  if (!isOpen) return null;

  const displayDetails = isEditing ? editedDetails : jobDetails;

  return (
    <div className="job-details-modal-overlay" onClick={handleClose}>
      <div
        className="job-details-modal-container"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="modal-title"
      >
        {/* Header */}
        <div className="job-details-modal-header">
          <div className="header-content">
            <h2 id="modal-title" className="modal-title">
              Job Details
            </h2>
            {displayDetails && (
              <div className="modal-subtitle">
                {displayDetails.name}
                <span className="status-badge" data-status={displayDetails.status.toLowerCase()}>
                  {displayDetails.status}
                </span>
              </div>
            )}
          </div>

          <Button
            className="modal-close-button"
            onClick={handleClose}
            aria-label="Close modal"
            variant="subtle"
          >
            ✕
          </Button>
        </div>

        {/* Error Message */}
        {error && (
          <div className="modal-error-message" role="alert">
            <strong>Error:</strong> {error}
            <Button
              className="error-dismiss"
              onClick={() => setError(null)}
              aria-label="Dismiss error"
              variant="subtle"
              size="sm"
            >
              ✕
            </Button>
          </div>
        )}

        {/* Loading State */}
        {loading && (
          <div className="modal-loading">
            <div className="spinner"></div>
            <p>Loading job details...</p>
          </div>
        )}

        {/* Content */}
        {!loading && displayDetails && (
          <>
            {/* Tabs */}
            <div className="modal-tabs">
              <Button
                className={`tab-button ${activeTab === 'overview' ? 'active' : ''}`}
                onClick={() => setActiveTab('overview')}
                role="tab"
                aria-selected={activeTab === 'overview'}
                aria-controls="tab-overview"
                variant="tab"
              >
                Overview
              </Button>
              <Button
                className={`tab-button ${activeTab === 'details' ? 'active' : ''}`}
                onClick={() => setActiveTab('details')}
                role="tab"
                aria-selected={activeTab === 'details'}
                aria-controls="tab-details"
                variant="tab"
              >
                Details
              </Button>
              <Button
                className={`tab-button ${activeTab === 'timing' ? 'active' : ''}`}
                onClick={() => setActiveTab('timing')}
                role="tab"
                aria-selected={activeTab === 'timing'}
                aria-controls="tab-timing"
                variant="tab"
              >
                Timing
              </Button>
              <Button
                className={`tab-button ${activeTab === 'history' ? 'active' : ''}`}
                onClick={() => setActiveTab('history')}
                role="tab"
                aria-selected={activeTab === 'history'}
                aria-controls="tab-history"
                variant="tab"
              >
                History
              </Button>
            </div>

            {/* Tab Content */}
            <div className="modal-content">
              {/* Overview Tab */}
              {activeTab === 'overview' && (
                <div id="tab-overview" role="tabpanel">
                  <JobDetailsSection
                    jobDetails={displayDetails}
                    isEditing={isEditing}
                    onFieldChange={handleFieldChange}
                  />
                  <div className="section-divider"></div>
                  <div className="notes-and-tags-section">
                    <div className="notes-editor-wrapper">
                      <h3>Notes</h3>
                      <JobNotesEditor
                        notes={displayDetails.notes || ''}
                        isEditing={isEditing}
                        onNotesChange={handleNotesChange}
                      />
                    </div>
                    <div className="tags-editor-wrapper">
                      <h3>Tags</h3>
                      <JobTagsEditor
                        tags={displayDetails.tags || []}
                        isEditing={isEditing}
                        onTagsChange={handleTagsChange}
                      />
                    </div>
                  </div>
                </div>
              )}

              {/* Details Tab */}
              {activeTab === 'details' && (
                <div id="tab-details" role="tabpanel" className="details-grid">
                  <div className="detail-item">
                    <label>Printer</label>
                    <p>{displayDetails.printerName}</p>
                  </div>
                  <div className="detail-item">
                    <label>Model</label>
                    <p>{displayDetails.printerModel}</p>
                  </div>
                  <div className="detail-item">
                    <label>Material Type</label>
                    <p>{displayDetails.materialType || 'Not specified'}</p>
                  </div>
                  <div className="detail-item">
                    <label>Nozzle Diameter</label>
                    <p>{displayDetails.nozzleDiameter ? `${displayDetails.nozzleDiameter}mm` : 'Not specified'}</p>
                  </div>
                  <div className="detail-item">
                    <label>Priority</label>
                    <p>{displayDetails.priority}</p>
                  </div>
                  <div className="detail-item">
                    <label>Queue Position</label>
                    <p>{displayDetails.queuePosition}</p>
                  </div>
                  <div className="detail-item full-width">
                    <label>File Name</label>
                    <p>{displayDetails.fileName || 'Unknown'}</p>
                  </div>
                  <div className="detail-item full-width">
                    <label>Estimated Filament</label>
                    <p>{displayDetails.estimatedFilamentUsage || 'Not available'}</p>
                  </div>
                </div>
              )}

              {/* Timing Tab */}
              {activeTab === 'timing' && (
                <div id="tab-timing" role="tabpanel" className="timing-info">
                  <div className="timing-item">
                    <label>Estimated Print Time</label>
                    <p className="timing-value">
                      {Math.round(displayDetails.estimatedPrintTimeSeconds / 60)} minutes
                      ({Math.round(displayDetails.estimatedPrintTimeSeconds / 3600)} hours)
                    </p>
                  </div>
                  <div className="timing-item">
                    <label>Created</label>
                    <p>{new Date(displayDetails.createdAt).toLocaleString()}</p>
                  </div>
                  {displayDetails.queuedAt && (
                    <div className="timing-item">
                      <label>Queued</label>
                      <p>{new Date(displayDetails.queuedAt).toLocaleString()}</p>
                    </div>
                  )}
                  {displayDetails.startedAt && (
                    <div className="timing-item">
                      <label>Started</label>
                      <p>{new Date(displayDetails.startedAt).toLocaleString()}</p>
                    </div>
                  )}
                  {displayDetails.completedAt && (
                    <div className="timing-item">
                      <label>Completed</label>
                      <p>{new Date(displayDetails.completedAt).toLocaleString()}</p>
                    </div>
                  )}
                </div>
              )}

              {/* History Tab */}
              {activeTab === 'history' && (
                <div id="tab-history" role="tabpanel" className="history-info">
                  <div className="history-event">
                    <span className="event-type">Created</span>
                    <span className="event-time">{new Date(displayDetails.createdAt).toLocaleString()}</span>
                  </div>
                  {displayDetails.queuedAt && (
                    <div className="history-event">
                      <span className="event-type">Queued</span>
                      <span className="event-time">{new Date(displayDetails.queuedAt).toLocaleString()}</span>
                    </div>
                  )}
                  {displayDetails.startedAt && (
                    <div className="history-event">
                      <span className="event-type">Started</span>
                      <span className="event-time">{new Date(displayDetails.startedAt).toLocaleString()}</span>
                    </div>
                  )}
                  {displayDetails.completedAt && (
                    <div className="history-event">
                      <span className="event-type">Completed</span>
                      <span className="event-time">{new Date(displayDetails.completedAt).toLocaleString()}</span>
                    </div>
                  )}
                </div>
              )}
            </div>
          </>
        )}

        {/* Footer */}
        <div className="modal-footer">
          {isEditing ? (
            <>
              <Button
                className="btn btn-secondary"
                onClick={handleCancelEdit}
                disabled={isSaving}
                variant="secondary"
              >
                Cancel
              </Button>
              <Button
                className="btn btn-primary"
                onClick={handleSave}
                disabled={!hasChanges || isSaving}
                variant="primary"
              >
                {isSaving ? 'Saving...' : 'Save Changes'}
              </Button>
            </>
          ) : (
            <>
              <Button
                className="btn btn-secondary"
                onClick={handleClose}
                variant="secondary"
              >
                Close
              </Button>
              <Button
                className="btn btn-primary"
                onClick={handleEditClick}
                variant="primary"
              >
                Edit Details
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  );
};

export default JobDetailsModal;
