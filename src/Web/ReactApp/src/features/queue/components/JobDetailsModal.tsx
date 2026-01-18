import React, { useCallback, use, Suspense, useState, useOptimistic, useTransition } from 'react';
import { Button } from '@/common/components/ui/Button';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { apiClient } from '@/services/api';
import JobDetailsSection from './JobDetailsSection';
import JobNotesEditor from './JobNotesEditor';
import JobTagsEditor from './JobTagsEditor';
import '../styles/JobDetailsModal.css';
import type { JobDetails, JobDetailsTabType } from '@/types/queue';
import type { JobDetailsModalProps } from '@/types/components';

/**
 * React 19 async data fetching: returns a promise that resolves to job details
 */
function fetchJobDetails(jobId: string): Promise<JobDetails> {
  return apiClient.getAnalyticsJobDetails(jobId).then(response => response as unknown as JobDetails);
}

/**
 * Content component that uses the use() hook to unwrap the promise
 * This is separated from the modal to use Suspense boundary
 */
interface JobDetailsContentProps {
  jobDetailsPromise: Promise<JobDetails>;
  isOpen: boolean;
  onClose: () => void;
  onSave?: (job: JobDetails) => void;
}

function JobDetailsContent({ jobDetailsPromise, isOpen, onClose, onSave }: JobDetailsContentProps) {
  // React 19: use() hook unwraps the promise and suspends rendering
  const initialJobDetails = use(jobDetailsPromise);
  
  const [jobDetails, setJobDetails] = useState<JobDetails>(initialJobDetails);
  const [error, setError] = useState<string | null>(null);
  const [isEditing, setIsEditing] = useState(false);
  const [hasChanges, setHasChanges] = useState(false);
  const [activeTab, setActiveTab] = useState<JobDetailsTabType>('overview');
  const [editedDetails, setEditedDetails] = useState<JobDetails>(initialJobDetails);
  const [showUnsavedConfirm, setShowUnsavedConfirm] = useState(false);
  
  // React 19: useTransition for managing async operations
  const [isPending, startTransition] = useTransition();
  
  // React 19: useOptimistic for immediate UI feedback on save
  const [optimisticDetails, addOptimisticUpdate] = useOptimistic<JobDetails, JobDetails>(
    jobDetails,
    (_, newDetails) => newDetails
  );

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
    const updated = { ...editedDetails, [field]: value };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleTagsChange = useCallback((tags: string[]) => {
    const updated = { ...editedDetails, tags };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleNotesChange = useCallback((notes: string) => {
    const updated = { ...editedDetails, notes };
    setEditedDetails(updated);
    setHasChanges(true);
    setError(null);
  }, [editedDetails]);

  const handleSave = useCallback(async () => {
    if (!hasChanges) {
      setIsEditing(false);
      return;
    }

    // React 19: Use startTransition for async operations
    startTransition(async () => {
      try {
        setError(null);

        // React 19: Optimistic update - show new details immediately
        addOptimisticUpdate(editedDetails);

        // Call update endpoint with changed fields
        const updatedJob = await apiClient.updateJobDetails(
          jobDetails.id,
          editedDetails
        );

        const jobDetailsData = updatedJob as unknown as JobDetails;
        setJobDetails(jobDetailsData);
        setEditedDetails(jobDetailsData);
        setIsEditing(false);
        setHasChanges(false);

        // Call callback if provided
        if (onSave) {
          onSave(jobDetailsData);
        }

        // Show success message
        if (window.PrintFarmerDebug?.utilities) console.log('Job updated successfully');
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to update job';
        setError(errorMessage);
        console.error('Failed to save job details:', err);
      }
    });
  }, [jobDetails.id, editedDetails, hasChanges, onSave, addOptimisticUpdate]);

  const handleClose = useCallback(() => {
    if (hasChanges) {
      setShowUnsavedConfirm(true);
      return;
    }
    doClose();
  }, [hasChanges]);

  const doClose = useCallback(() => {
    setIsEditing(false);
    setHasChanges(false);
    setError(null);
    setShowUnsavedConfirm(false);
    onClose();
  }, [onClose]);

  if (!isOpen) return null;

  // React 19: Use optimistic details for immediate UI feedback
  const displayDetails = isEditing ? editedDetails : optimisticDetails;

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

        {/* Content - No loading state needed, Suspense handles it */}
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

        {/* Footer */}
        <div className="modal-footer">
          {isEditing ? (
            <>
              <Button
                className="btn btn-secondary"
                onClick={handleCancelEdit}
                disabled={isPending}
                variant="secondary"
              >
                Cancel
              </Button>
              <Button
                className="btn btn-primary"
                onClick={handleSave}
                disabled={!hasChanges || isPending}
                variant="primary"
              >
                {isPending ? 'Saving...' : 'Save Changes'}
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

      {/* Unsaved changes confirmation */}
      <ConfirmationModal
        isOpen={showUnsavedConfirm}
        title="Unsaved Changes"
        message="You have unsaved changes. Are you sure you want to close?"
        confirmButtonText="Discard Changes"
        cancelButtonText="Keep Editing"
        isDangerous
        onConfirm={doClose}
        onCancel={() => setShowUnsavedConfirm(false)}
      />
    </div>
  );
}

/**
 * React 19 Modal wrapper with Suspense boundary
 * Handles async data fetching and error states
 */
const JobDetailsModal: React.FC<JobDetailsModalProps> = ({
  jobId,
  isOpen,
  onClose,
  onSave,
}) => {
  if (!isOpen || !jobId) return null;

  return (
    // React 19 Suspense boundary shows fallback while promise resolves
    <Suspense fallback={
      <div className="job-details-modal-overlay">
        <div className="job-details-modal-container" role="dialog" aria-modal="true">
          <div className="modal-loading">
            <div className="spinner"></div>
            <p>Loading job details...</p>
          </div>
        </div>
      </div>
    }>
      <JobDetailsContent
        jobDetailsPromise={fetchJobDetails(jobId)}
        isOpen={isOpen}
        onClose={onClose}
        onSave={onSave}
      />
    </Suspense>
  );
};

export default JobDetailsModal;
