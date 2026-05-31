import { useMemo } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { GCodeViewer } from '@/features/models3d/components/3d/GCodeViewer3D';

interface GcodePreviewModalProps {
  isOpen: boolean;
  onClose: () => void;
  jobId: string;
}

/**
 * Modal that displays the G-code preview viewer for a completed slice job.
 * Uses the job-level artifact download endpoint directly.
 */
export function GcodePreviewModal({ isOpen, onClose, jobId }: GcodePreviewModalProps) {
  const gcodeUrl = useMemo(() => {
    if (!jobId) return null;
    return `/api/artifacts/job/${jobId}`;
  }, [jobId]);

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="G-code Preview"
      size="xl"
    >
      {gcodeUrl && (
        <GCodeViewer gcodeUrl={gcodeUrl} className="h-[70vh] w-full" />
      )}
    </Modal>
  );
}
