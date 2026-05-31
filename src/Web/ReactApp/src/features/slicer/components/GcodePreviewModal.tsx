import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { GCodeViewer } from '@/features/models3d/components/3d/GCodeViewer3D';
import { Spinner } from '@/common/components/ui/Spinner';
import { sliceJobService } from '@/services/sliceJobService';

interface GcodePreviewModalProps {
  isOpen: boolean;
  onClose: () => void;
  jobId: string;
}

/**
 * Modal that displays the G-code preview viewer for a completed slice job.
 * Resolves the artifact ID from the job's artifact list, then fetches
 * GET /api/artifacts/{id} (the PhysicalFile endpoint) for raw G-code content.
 */
export function GcodePreviewModal({ isOpen, onClose, jobId }: GcodePreviewModalProps) {
  const { data: gcodeUrl, isLoading } = useQuery({
    queryKey: ['gcode-preview-url', jobId],
    queryFn: async () => {
      const artifacts = await sliceJobService.getArtifactsByJob(jobId);
      const gcode = artifacts.find(
        a => a.fileName.toLowerCase().endsWith('.gcode')
      ) ?? artifacts[0];
      if (!gcode) return null;
      return sliceJobService.getArtifactDownloadUrl(gcode.id);
    },
    enabled: isOpen && !!jobId,
  });

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="G-code Preview"
      size="xl"
    >
      {isLoading && (
        <div className="flex items-center justify-center h-[70vh]">
          <Spinner size="lg" />
        </div>
      )}
      {gcodeUrl && (
        <GCodeViewer gcodeUrl={gcodeUrl} className="h-[70vh] w-full" />
      )}
    </Modal>
  );
}
