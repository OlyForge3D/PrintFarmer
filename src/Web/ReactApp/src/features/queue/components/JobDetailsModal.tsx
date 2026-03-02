import React, { useCallback, use, Suspense, useState, useOptimistic, useTransition, useMemo } from 'react';
import { Button } from '@/common/components/ui/Button';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { apiClient } from '@/services/api';
import JobDetailsSection from './JobDetailsSection';
import JobNotesEditor from './JobNotesEditor';
import JobTagsEditor from './JobTagsEditor';
import type { JobDetails } from '@/types/queue';
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
    setEditedDetails(prev => ({ ...prev, [field]: value }));
    setHasChanges(true);
    setError(null);
  }, []);

  const handleTagsChange = useCallback((tags: string[]) => {
    setEditedDetails(prev => ({ ...prev, tags }));
    setHasChanges(true);
    setError(null);
  }, []);

  const handleNotesChange = useCallback((notes: string) => {
    setEditedDetails(prev => ({ ...prev, notes }));
    setHasChanges(true);
    setError(null);
  }, []);

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

  const doClose = useCallback(() => {
    setIsEditing(false);
    setHasChanges(false);
    setError(null);
    setShowUnsavedConfirm(false);
    onClose();
  }, [onClose]);

  const handleClose = useCallback(() => {
    if (hasChanges) {
      setShowUnsavedConfirm(true);
      return;
    }
    doClose();
  }, [hasChanges, doClose]);

  // React 19: Use optimistic details for immediate UI feedback
  const displayDetails = isEditing ? editedDetails : optimisticDetails;

  const footerContent = isEditing ? (
    <div className="flex gap-2 justify-end">
      <Button
        onClick={handleCancelEdit}
        disabled={isPending}
        variant="secondary"
      >
        Cancel
      </Button>
      <Button
        onClick={handleSave}
        disabled={!hasChanges || isPending}
        variant="primary"
      >
        {isPending ? 'Saving...' : 'Save Changes'}
      </Button>
    </div>
  ) : (
    <div className="flex gap-2 justify-end">
      <Button
        onClick={handleClose}
        variant="secondary"
      >
        Close
      </Button>
      <Button
        onClick={handleEditClick}
        variant="primary"
      >
        Edit Details
      </Button>
    </div>
  );

  // Helper to format seconds to human-readable duration
  const formatDuration = useCallback((seconds: number | undefined | null): string => {
    if (!seconds) return '—';
    const hours = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    if (hours > 0) return `${hours}h ${mins}m`;
    return `${mins}m`;
  }, []);

  // Helper for formatting dates safely
  const formatDate = useCallback((date: string | undefined | null): string => {
    if (!date) return '—';
    return new Date(date).toLocaleString();
  }, []);

  return (
    <>
      <Modal
        isOpen={isOpen}
        onClose={handleClose}
        title="Job Details"
        width="max-w-5xl"
        footer={footerContent}
        closeOnBackdrop={false}
        closeOnEscape={!hasChanges}
      >
        {/* Status Badge */}
        {displayDetails && (
          <div className="mb-4">
            <span 
              className={`inline-flex px-2.5 py-1 text-xs font-semibold rounded-full ${
                displayDetails.status.toLowerCase() === 'completed' ? 'bg-pf-success/20 text-pf-success' :
                displayDetails.status.toLowerCase() === 'printing' ? 'bg-pf-accent/20 text-pf-accent' :
                displayDetails.status.toLowerCase() === 'queued' ? 'bg-pf-warning/20 text-pf-warning' :
                displayDetails.status.toLowerCase() === 'failed' ? 'bg-pf-error/20 text-pf-error' :
                'bg-pf-bg-2 text-pf-text-secondary'
              }`}
            >
              {displayDetails.status}
            </span>
          </div>
        )}

        {/* Error Message */}
        {error && (
          <div className="mb-4 p-3 bg-pf-error/10 border border-pf-error/30 rounded-lg" role="alert">
            <div className="flex items-center justify-between">
              <span className="text-pf-error">
                <strong>Error:</strong> {error}
              </span>
              <Button
                onClick={() => setError(null)}
                aria-label="Dismiss error"
                variant="subtle"
                size="sm"
              >
                ✕
              </Button>
            </div>
          </div>
        )}

        {/* Two-column layout: left sidebar (thumbnail + overview), right (details) */}
        <div className="grid grid-cols-1 lg:grid-cols-[280px_1fr] gap-6">
          {/* Left Column — Thumbnail + Overview */}
          <div className="space-y-4">
            {/* Thumbnail */}
            <div className="bg-pf-bg-2 rounded-lg overflow-hidden aspect-square flex items-center justify-center">
              {displayDetails.thumbnailUrl ? (
                <img
                  src={displayDetails.thumbnailUrl}
                  alt={`Thumbnail for ${displayDetails.name}`}
                  className="w-full h-full object-contain"
                />
              ) : (
                <div className="text-pf-text-tertiary text-sm">No thumbnail</div>
              )}
            </div>

            {/* Overview fields */}
            <JobDetailsSection
              jobDetails={displayDetails}
              isEditing={isEditing}
              onFieldChange={handleFieldChange}
            />

            {/* Cost */}
            <div className="bg-pf-bg-1 rounded-lg p-4">
              <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">Cost</h3>
              <div className="grid grid-cols-2 gap-4">
                <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                  <p className="text-xs text-pf-text-muted mb-1">Estimated</p>
                  <p className="text-lg font-bold text-pf-text-primary">
                    {displayDetails.estimatedCost != null ? `$${displayDetails.estimatedCost.toFixed(2)}` : '—'}
                  </p>
                </div>
                <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                  <p className="text-xs text-pf-text-muted mb-1">Actual</p>
                  <p className="text-lg font-bold text-pf-text-primary">
                    {displayDetails.actualCost != null ? `$${displayDetails.actualCost.toFixed(2)}` : '—'}
                  </p>
                </div>
              </div>
            </div>
          </div>

          {/* Right Column — Details + Notes/Tags */}
          <div className="space-y-6">
            {(() => {
              const materialType = displayDetails.requiredMaterialType || displayDetails.materialType;
              const nozzleDiameter = displayDetails.requiredNozzleDiameter || displayDetails.nozzleDiameter;
              const createdAt = displayDetails.createdAtUtc || displayDetails.createdAt;
              const queuedAt = displayDetails.queuedAtUtc || displayDetails.queuedAt;
              const startedAt = displayDetails.actualStartTimeUtc || displayDetails.startedAt;
              const completedAt = displayDetails.actualEndTimeUtc || displayDetails.completedAt;

              return (
                <>
                  {/* Printer & File Info */}
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <div className="bg-pf-bg-1 rounded-lg p-4">
                      <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">Printer</h3>
                      <dl className="grid grid-cols-2 gap-x-4 gap-y-2">
                        <div>
                          <dt className="text-xs text-pf-text-muted">Name</dt>
                          <dd className="text-sm font-medium text-pf-text-primary">{displayDetails.printerName || <span className="italic text-pf-text-muted">Not assigned</span>}</dd>
                        </div>
                        <div>
                          <dt className="text-xs text-pf-text-muted">Model</dt>
                          <dd className="text-sm font-medium text-pf-text-primary">{displayDetails.printerModel || <span className="italic text-pf-text-muted">—</span>}</dd>
                        </div>
                        <div>
                          <dt className="text-xs text-pf-text-muted">Material</dt>
                          <dd className="text-sm font-medium text-pf-text-primary">{materialType || <span className="italic text-pf-text-muted">—</span>}</dd>
                        </div>
                        <div>
                          <dt className="text-xs text-pf-text-muted">Nozzle</dt>
                          <dd className="text-sm font-medium text-pf-text-primary">{nozzleDiameter ? `${nozzleDiameter}mm` : <span className="italic text-pf-text-muted">—</span>}</dd>
                        </div>
                      </dl>
                    </div>

                    <div className="bg-pf-bg-1 rounded-lg p-4">
                      <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">File</h3>
                      <p className="text-sm font-mono text-pf-text-primary break-all">{displayDetails.name || <span className="italic text-pf-text-muted">Unknown</span>}</p>
                    </div>
                  </div>

                  {/* Duration & Filament */}
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <div className="bg-pf-bg-1 rounded-lg p-4">
                      <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">Print Duration</h3>
                      <div className="grid grid-cols-2 gap-3">
                        <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                          <p className="text-xs text-pf-text-muted mb-1">Estimated</p>
                          <p className="text-lg font-bold text-pf-text-primary">{formatDuration(displayDetails.estimatedPrintTimeSeconds)}</p>
                        </div>
                        <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                          <p className="text-xs text-pf-text-muted mb-1">Actual</p>
                          <p className="text-lg font-bold text-pf-text-primary">{formatDuration(displayDetails.actualPrintTimeSeconds)}</p>
                        </div>
                      </div>
                    </div>

                    <div className="bg-pf-bg-1 rounded-lg p-4">
                      <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">Filament Usage</h3>
                      <div className="grid grid-cols-2 gap-3">
                        <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                          <p className="text-xs text-pf-text-muted mb-1">Estimated</p>
                          <p className="text-lg font-bold text-pf-text-primary">
                            {displayDetails.estimatedFilamentUsageGrams != null ? `${displayDetails.estimatedFilamentUsageGrams.toFixed(2)}g` : '—'}
                          </p>
                        </div>
                        <div className="text-center p-2 bg-pf-bg-0 rounded-md">
                          <p className="text-xs text-pf-text-muted mb-1">Actual</p>
                          <p className="text-lg font-bold text-pf-text-primary">
                            {displayDetails.actualFilamentUsageGrams != null ? `${displayDetails.actualFilamentUsageGrams.toFixed(2)}g` : '—'}
                          </p>
                        </div>
                      </div>
                    </div>
                  </div>

                  {/* Timeline */}
                  <div className="bg-pf-bg-1 rounded-lg p-4">
                    <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3">Timeline</h3>
                    <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                      <div>
                        <p className="text-xs text-pf-text-muted">Created</p>
                        <p className="text-sm font-medium text-pf-text-primary">{formatDate(createdAt)}</p>
                      </div>
                      <div>
                        <p className="text-xs text-pf-text-muted">Queued</p>
                        <p className="text-sm font-medium text-pf-text-primary">{formatDate(queuedAt)}</p>
                      </div>
                      <div>
                        <p className="text-xs text-pf-text-muted">Started</p>
                        <p className="text-sm font-medium text-pf-text-primary">{formatDate(startedAt)}</p>
                      </div>
                      <div>
                        <p className="text-xs text-pf-text-muted">Completed</p>
                        <p className="text-sm font-medium text-pf-text-primary">{formatDate(completedAt)}</p>
                      </div>
                    </div>
                  </div>
                </>
              );
            })()}

            {/* Notes & Tags */}
            <div className="border-t border-pf-border pt-4">
              <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                <div>
                  <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-2">Notes</h3>
                  <JobNotesEditor
                    notes={displayDetails.notes || ''}
                    isEditing={isEditing}
                    onNotesChange={handleNotesChange}
                  />
                </div>
                <div>
                  <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-2">Tags</h3>
                  <JobTagsEditor
                    tags={displayDetails.tags || []}
                    isEditing={isEditing}
                    onTagsChange={handleTagsChange}
                  />
                </div>
              </div>
            </div>
          </div>
        </div>
      </Modal>

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
    </>
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
  // Memoize the promise so it only changes when jobId changes
  // This prevents Suspense from re-triggering on every parent render
  const jobDetailsPromise = useMemo(() => {
    if (!jobId) return null;
    return fetchJobDetails(jobId);
  }, [jobId]);

  if (!isOpen || !jobId || !jobDetailsPromise) return null;

  return (
    // React 19 Suspense boundary shows fallback while promise resolves
    <Suspense fallback={
      <Modal isOpen={true} onClose={onClose} title="Job Details" width="max-w-5xl">
        <div className="flex flex-col items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent mb-4"></div>
          <p className="text-pf-text-muted">Loading job details...</p>
        </div>
      </Modal>
    }>
      <JobDetailsContent
        jobDetailsPromise={jobDetailsPromise}
        isOpen={isOpen}
        onClose={onClose}
        onSave={onSave}
      />
    </Suspense>
  );
};

export default JobDetailsModal;
