import { Button } from '@/common/components/ui';
import { ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import type { CameraViewMode } from '@/features/cameras/hooks/useCameraViewPreferences';

interface CameraModeControlProps {
  cameraName: string;
  cameraMode: CameraViewMode;
  hasStream: boolean;
  hasSnapshot: boolean;
  streamUnavailable?: boolean;
  streamIssue?: 'unsupported' | 'failed';
  onModeChange: (mode: CameraViewMode) => void;
}

export function CameraModeControl({
  cameraName,
  cameraMode,
  hasStream,
  hasSnapshot,
  streamUnavailable = false,
  streamIssue,
  onModeChange,
}: CameraModeControlProps) {
  const displayedMode = streamIssue && hasSnapshot ? 'snapshot' : cameraMode;

  if (hasStream && hasSnapshot) {
    return (
      <div className="flex flex-col items-start gap-1">
        <div
          role="group"
          aria-label={`${cameraName} preview mode`}
          title={streamIssue
            ? `${streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'} · showing snapshot`
            : undefined}
          className="flex gap-1 rounded-md border border-pf-border bg-pf-bg-2 p-1"
        >
          <Button
            type="button"
            variant={displayedMode === 'snapshot' ? 'primary' : 'ghost'}
            size="sm"
            onClick={() => onModeChange('snapshot')}
            aria-pressed={displayedMode === 'snapshot'}
            className="h-8 w-8 p-0"
            aria-label="Snapshot mode"
            title="Snapshot mode"
            iconCenter={<ImageIcon className="w-4 h-4" />}
          />
          <Button
            type="button"
            variant={displayedMode === 'stream' ? 'primary' : 'ghost'}
            size="sm"
            onClick={() => onModeChange('stream')}
            aria-pressed={displayedMode === 'stream'}
            className="h-8 w-8 p-0"
            aria-label="Stream mode"
            title="Stream mode"
            iconCenter={<VideoIcon className="w-4 h-4" />}
          />
        </div>
        {streamIssue && (
          <span
            role="status"
            aria-label={`${streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'} · showing snapshot`}
            className="sr-only"
          >
            {streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'}
            {' · showing snapshot'}
          </span>
        )}
      </div>
    );
  }

  const modeLabel = streamIssue && hasSnapshot
    ? `${streamIssue === 'unsupported' ? 'Live stream unsupported' : 'Live stream unavailable'} · showing snapshot`
    : hasSnapshot
    ? streamUnavailable
      ? 'Snapshot only · live stream unsupported'
      : 'Snapshot only'
    : hasStream
      ? streamIssue === 'unsupported'
        ? 'Live stream unsupported'
        : streamIssue === 'failed'
          ? 'Live stream unavailable'
          : 'Live stream only'
      : 'No supported preview mode';

  return (
    <span
      role="status"
      aria-label={`${cameraName}: ${modeLabel}`}
      title={modeLabel}
      className="sr-only"
    >
      {modeLabel}
    </span>
  );
}
