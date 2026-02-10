import React, { useCallback, use, Suspense, useState, useOptimistic, useTransition, useMemo } from 'react';
import { Button } from '@/common/components/ui/Button';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { apiClient } from '@/services/api';
import JobDetailsSection from './JobDetailsSection';
import JobNotesEditor from './JobNotesEditor';
import JobTagsEditor from './JobTagsEditor';
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

  return (
    <>
      <Modal
        isOpen={isOpen}
        onClose={handleClose}
        title="Job Details"
        size="xl"
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

        {/* Tabs */}
        <div className="flex border-b border-pf-border mb-4" role="tablist">
          <Button
            variant="tab"
            active={activeTab === 'overview'}
            className="px-4 py-2 text-sm font-medium"
            onClick={() => setActiveTab('overview')}
            role="tab"
            aria-selected={activeTab === 'overview'}
            aria-controls="tab-overview"
          >
            Overview
          </Button>
          <Button
            variant="tab"
            active={activeTab === 'details'}
            className="px-4 py-2 text-sm font-medium"
            onClick={() => setActiveTab('details')}
            role="tab"
            aria-selected={activeTab === 'details'}
            aria-controls="tab-details"
          >
            Details
          </Button>

        </div>

        {/* Tab Content */}
        <div className="min-h-[300px]">
              {/* Overview Tab */}
              {activeTab === 'overview' && (
                <div id="tab-overview" role="tabpanel">
                  <JobDetailsSection
                    jobDetails={displayDetails}
                    isEditing={isEditing}
                    onFieldChange={handleFieldChange}
                  />
                  <div className="border-t border-pf-border my-4"></div>
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
              )}

              {/* Details Tab */}
              {activeTab === 'details' && (() => {
                // Helper to format seconds to human-readable duration
                const formatDuration = (seconds: number | undefined | null): string => {
                  if (!seconds) return '—';
                  const hours = Math.floor(seconds / 3600);
                  const mins = Math.floor((seconds % 3600) / 60);
                  if (hours > 0) return `${hours}h ${mins}m`;
                  return `${mins}m`;
                };
                // Helper for formatting dates safely
                const formatDate = (date: string | undefined | null): string => {
                  if (!date) return '—';
                  return new Date(date).toLocaleString();
                };

                // Get material and nozzle from either new or legacy field names
                const materialType = displayDetails.requiredMaterialType || displayDetails.materialType;
                const nozzleDiameter = displayDetails.requiredNozzleDiameter || displayDetails.nozzleDiameter;
                
                // Get dates from either new or legacy field names
                const createdAt = displayDetails.createdAtUtc || displayDetails.createdAt;
                const queuedAt = displayDetails.queuedAtUtc || displayDetails.queuedAt;
                const startedAt = displayDetails.actualStartTimeUtc || displayDetails.startedAt;
                const completedAt = displayDetails.actualEndTimeUtc || displayDetails.completedAt;

                return (
                <div id="tab-details" role="tabpanel" className="space-y-6">
                  {/* Two-column layout for main content */}
                  <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                    {/* Left Column - Printer & File Info */}
                    <div className="space-y-6">
                      {/* Printer Info Section */}
                      <div className="bg-pf-bg-1 rounded-lg p-4">
                        <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3 flex items-center gap-2">
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 17h2a2 2 0 002-2v-4a2 2 0 00-2-2H5a2 2 0 00-2 2v4a2 2 0 002 2h2m2 4h6a2 2 0 002-2v-4a2 2 0 00-2-2H9a2 2 0 00-2 2v4a2 2 0 002 2zm8-12V5a2 2 0 00-2-2H9a2 2 0 00-2 2v4h10z" />
                          </svg>
                          Printer
                        </h3>
                        <dl className="grid grid-cols-2 gap-x-4 gap-y-3">
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

                      {/* File Info Section */}
                      <div className="bg-pf-bg-1 rounded-lg p-4">
                        <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3 flex items-center gap-2">
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                          </svg>
                          File
                        </h3>
                        <p className="text-sm font-mono text-pf-text-primary break-all">{displayDetails.name || <span className="italic text-pf-text-muted">Unknown</span>}</p>
                      </div>
                    </div>

                    {/* Right Column - Duration & Filament */}
                    <div className="space-y-6">
                      {/* Duration Section */}
                      <div className="bg-pf-bg-1 rounded-lg p-4">
                        <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3 flex items-center gap-2">
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
                          </svg>
                          Print Duration
                        </h3>
                        <div className="grid grid-cols-2 gap-4">
                          <div className="text-center p-3 bg-pf-bg-0 rounded-md">
                            <p className="text-xs text-pf-text-muted mb-1">Estimated</p>
                            <p className="text-xl font-bold text-pf-text-primary">{formatDuration(displayDetails.estimatedPrintTimeSeconds)}</p>
                          </div>
                          <div className="text-center p-3 bg-pf-bg-0 rounded-md">
                            <p className="text-xs text-pf-text-muted mb-1">Actual</p>
                            <p className="text-xl font-bold text-pf-text-primary">{formatDuration(displayDetails.actualPrintTimeSeconds)}</p>
                          </div>
                        </div>
                      </div>

                      {/* Filament Usage Section */}
                      <div className="bg-pf-bg-1 rounded-lg p-4">
                        <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3 flex items-center gap-2">
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 21a4 4 0 01-4-4V5a2 2 0 012-2h4a2 2 0 012 2v12a4 4 0 01-4 4zm0 0h12a2 2 0 002-2v-4a2 2 0 00-2-2h-2.343M11 7.343l1.657-1.657a2 2 0 012.828 0l2.829 2.829a2 2 0 010 2.828l-8.486 8.485M7 17h.01" />
                          </svg>
                          Filament Usage
                        </h3>
                        <div className="grid grid-cols-2 gap-4">
                          <div className="text-center p-3 bg-pf-bg-0 rounded-md">
                            <p className="text-xs text-pf-text-muted mb-1">Estimated</p>
                            <p className="text-xl font-bold text-pf-text-primary">
                              {displayDetails.estimatedFilamentUsageGrams != null ? `${displayDetails.estimatedFilamentUsageGrams.toFixed(2)}g` : '—'}
                            </p>
                          </div>
                          <div className="text-center p-3 bg-pf-bg-0 rounded-md">
                            <p className="text-xs text-pf-text-muted mb-1">Actual</p>
                            <p className="text-xl font-bold text-pf-text-primary">
                              {displayDetails.actualFilamentUsageGrams != null ? `${displayDetails.actualFilamentUsageGrams.toFixed(2)}g` : '—'}
                            </p>
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>

                  {/* Timeline Section - Full Width */}
                  <div className="bg-pf-bg-1 rounded-lg p-4">
                    <h3 className="text-sm font-semibold text-pf-text-secondary uppercase tracking-wide mb-3 flex items-center gap-2">
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
                      </svg>
                      Timeline
                    </h3>
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
                </div>
                );
              })()}


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
      <Modal isOpen={true} onClose={onClose} title="Job Details" size="xl">
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
