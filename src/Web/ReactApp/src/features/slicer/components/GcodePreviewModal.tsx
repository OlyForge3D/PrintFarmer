import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Modal } from '@/common/components/modals/Modal';
import { GCodeViewer } from '@/features/models3d/components/3d/GCodeViewer3D';
import { Spinner } from '@/common/components/ui/Spinner';
import { Button } from '@/common/components/ui/Button';
import { getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import { sliceJobService } from '@/services/sliceJobService';
import { resolveGcodeArtifact } from '@/features/slicer/utils/sliceArtifactActions';

interface GcodePreviewModalProps {
  isOpen: boolean;
  onClose: () => void;
  artifactsRoute: string;
}

/**
 * Modal that displays the G-code preview viewer for a completed slice job.
 * Resolves the artifact ID from the job's artifact list, then fetches
 * GET /api/artifacts/{id} (the PhysicalFile endpoint) for raw G-code content.
 * Only G-code artifacts are considered; non-G-code files are never sent to the viewer.
 */
export function GcodePreviewModal({ isOpen, onClose, artifactsRoute }: GcodePreviewModalProps) {
  const requestHeaders = useMemo(
    () => (isOpen ? getAuthHeaders() : {}),
    [isOpen],
  );
  const {
    data: gcodeUrl,
    isLoading,
    isFetching,
    error,
    refetch,
  } = useQuery({
    queryKey: ['gcode-preview-url', artifactsRoute],
    queryFn: async () => {
      const gcode = await resolveGcodeArtifact(artifactsRoute);
      if (!gcode) return null;
      return sliceJobService.getArtifactDownloadUrl(gcode.id);
    },
    enabled: isOpen && !!artifactsRoute,
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
      {!isLoading && error && (
        <div className="flex h-[70vh] flex-col items-center justify-center gap-3 text-pf-error">
          <p>{error instanceof Error ? error.message : 'Failed to load the G-code artifact.'}</p>
          <Button variant="secondary" size="sm" loading={isFetching} onClick={() => refetch()}>
            Retry
          </Button>
        </div>
      )}
      {!isLoading && !error && !gcodeUrl && (
        <div className="flex items-center justify-center h-[70vh] text-pf-text-secondary">
          <p>No G-code artifact available for this job.</p>
        </div>
      )}
      {gcodeUrl && (
        <GCodeViewer
          gcodeUrl={gcodeUrl}
          requestHeaders={requestHeaders}
          className="h-[70vh] w-full"
        />
      )}
    </Modal>
  );
}
