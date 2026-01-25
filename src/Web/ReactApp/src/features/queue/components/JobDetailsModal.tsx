import React, { useCallback, use, Suspense, useState, useOptimistic, useTransition } from 'react';
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
        title={displayDetails ? `${displayDetails.name}` : 'Job Details'}
        size="lg"
        footer={footerContent}
        closeOnBackdrop={false}
        closeOnEscape={!hasChanges}
      >
        {/* Status Badge */}
        {displayDetails && (
          <div className="mb-4">
            <span 
              className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${
                displayDetails.status.toLowerCase() === 'completed' ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200' :
                displayDetails.status.toLowerCase() === 'printing' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200' :
                displayDetails.status.toLowerCase() === 'queued' ? 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200' :
                displayDetails.status.toLowerCase() === 'failed' ? 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200' :
                'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-200'
              }`}
            >
              {displayDetails.status}
            </span>
          </div>
        )}

        {/* Error Message */}
        {error && (
          <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg dark:bg-red-900/20 dark:border-red-800" role="alert">
            <div className="flex items-center justify-between">
              <span className="text-red-800 dark:text-red-200">
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
        <div className="flex border-b border-gray-200 dark:border-gray-700 mb-4" role="tablist">
          <Button
            variant={activeTab === 'overview' ? 'tab' : 'subtle'}
            className="px-4 py-2 text-sm font-medium rounded-none border-b-2 border-transparent"
            onClick={() => setActiveTab('overview')}
            role="tab"
            aria-selected={activeTab === 'overview'}
            aria-controls="tab-overview"
          >
            Overview
          </Button>
          <Button
            variant={activeTab === 'details' ? 'tab' : 'subtle'}
            className="px-4 py-2 text-sm font-medium rounded-none border-b-2 border-transparent"
            onClick={() => setActiveTab('details')}
            role="tab"
            aria-selected={activeTab === 'details'}
            aria-controls="tab-details"
          >
            Details
          </Button>
          <Button
            variant={activeTab === 'timing' ? 'tab' : 'subtle'}
            className="px-4 py-2 text-sm font-medium rounded-none border-b-2 border-transparent"
            onClick={() => setActiveTab('timing')}
            role="tab"
            aria-selected={activeTab === 'timing'}
            aria-controls="tab-timing"
          >
            Timing
          </Button>
          <Button
            variant={activeTab === 'history' ? 'tab' : 'subtle'}
            className="px-4 py-2 text-sm font-medium rounded-none border-b-2 border-transparent"
            onClick={() => setActiveTab('history')}
            role="tab"
            aria-selected={activeTab === 'history'}
            aria-controls="tab-history"
          >
            History
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
                  <div className="border-t border-gray-200 dark:border-gray-700 my-4"></div>
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                    <div>
                      <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-300 mb-2">Notes</h3>
                      <JobNotesEditor
                        notes={displayDetails.notes || ''}
                        isEditing={isEditing}
                        onNotesChange={handleNotesChange}
                      />
                    </div>
                    <div>
                      <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-300 mb-2">Tags</h3>
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
                <div id="tab-details" role="tabpanel" className="grid grid-cols-2 gap-4">
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Printer</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.printerName}</p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Model</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.printerModel}</p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Material Type</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.materialType || 'Not specified'}</p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Nozzle Diameter</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.nozzleDiameter ? `${displayDetails.nozzleDiameter}mm` : 'Not specified'}</p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Priority</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.priority}</p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Queue Position</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.queuePosition}</p>
                  </div>
                  <div className="space-y-1 col-span-2">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">File Name</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.fileName || 'Unknown'}</p>
                  </div>
                  <div className="space-y-1 col-span-2">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Estimated Filament</label>
                    <p className="text-gray-900 dark:text-gray-100">{displayDetails.estimatedFilamentUsage || 'Not available'}</p>
                  </div>
                </div>
              )}

              {/* Timing Tab */}
              {activeTab === 'timing' && (
                <div id="tab-timing" role="tabpanel" className="space-y-4">
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Estimated Print Time</label>
                    <p className="text-lg font-semibold text-gray-900 dark:text-gray-100">
                      {Math.round(displayDetails.estimatedPrintTimeSeconds / 60)} minutes
                      <span className="text-sm font-normal text-gray-500 dark:text-gray-400 ml-2">
                        ({Math.round(displayDetails.estimatedPrintTimeSeconds / 3600)} hours)
                      </span>
                    </p>
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Created</label>
                    <p className="text-gray-900 dark:text-gray-100">{new Date(displayDetails.createdAt).toLocaleString()}</p>
                  </div>
                  {displayDetails.queuedAt && (
                    <div className="space-y-1">
                      <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Queued</label>
                      <p className="text-gray-900 dark:text-gray-100">{new Date(displayDetails.queuedAt).toLocaleString()}</p>
                    </div>
                  )}
                  {displayDetails.startedAt && (
                    <div className="space-y-1">
                      <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Started</label>
                      <p className="text-gray-900 dark:text-gray-100">{new Date(displayDetails.startedAt).toLocaleString()}</p>
                    </div>
                  )}
                  {displayDetails.completedAt && (
                    <div className="space-y-1">
                      <label className="text-sm font-medium text-gray-500 dark:text-gray-400">Completed</label>
                      <p className="text-gray-900 dark:text-gray-100">{new Date(displayDetails.completedAt).toLocaleString()}</p>
                    </div>
                  )}
                </div>
              )}

              {/* History Tab */}
              {activeTab === 'history' && (
                <div id="tab-history" role="tabpanel" className="space-y-2">
                  <div className="flex justify-between py-2 border-b border-gray-100 dark:border-gray-700">
                    <span className="font-medium text-gray-700 dark:text-gray-300">Created</span>
                    <span className="text-gray-600 dark:text-gray-400">{new Date(displayDetails.createdAt).toLocaleString()}</span>
                  </div>
                  {displayDetails.queuedAt && (
                    <div className="flex justify-between py-2 border-b border-gray-100 dark:border-gray-700">
                      <span className="font-medium text-gray-700 dark:text-gray-300">Queued</span>
                      <span className="text-gray-600 dark:text-gray-400">{new Date(displayDetails.queuedAt).toLocaleString()}</span>
                    </div>
                  )}
                  {displayDetails.startedAt && (
                    <div className="flex justify-between py-2 border-b border-gray-100 dark:border-gray-700">
                      <span className="font-medium text-gray-700 dark:text-gray-300">Started</span>
                      <span className="text-gray-600 dark:text-gray-400">{new Date(displayDetails.startedAt).toLocaleString()}</span>
                    </div>
                  )}
                  {displayDetails.completedAt && (
                    <div className="flex justify-between py-2 border-b border-gray-100 dark:border-gray-700">
                      <span className="font-medium text-gray-700 dark:text-gray-300">Completed</span>
                      <span className="text-gray-600 dark:text-gray-400">{new Date(displayDetails.completedAt).toLocaleString()}</span>
                    </div>
                  )}
                </div>
              )}
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
  if (!isOpen || !jobId) return null;

  return (
    // React 19 Suspense boundary shows fallback while promise resolves
    <Suspense fallback={
      <Modal isOpen={true} onClose={onClose} title="Job Details" size="lg">
        <div className="flex flex-col items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary-500 mb-4"></div>
          <p className="text-gray-500 dark:text-gray-400">Loading job details...</p>
        </div>
      </Modal>
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
