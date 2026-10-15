import { Button } from '@/common/components/ui';
import type { QueueMovePlacement } from '@/features/queue/utils/queueReordering';
import type { QueueMoveNeighbors } from '@/features/queue/utils/queueReordering';

export interface QueueReorderInteractions {
  canReorder?: boolean;
  isReordering?: boolean;
  draggedJobId?: string | null;
  onMoveJob?: (movedJobId: string, neighborJobId: string, placement: QueueMovePlacement) => void;
  onDragStartJob?: (jobId: string) => void;
  onDragEndJob?: () => void;
  canDropOnJob?: (movedJobId: string, neighborJobId: string) => boolean;
  reorderNeighbors?: Map<string, QueueMoveNeighbors>;
}

interface QueueJobReorderControlsProps {
  fileName: string;
  enabled: boolean;
  busy: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
}

export function QueueJobReorderControls({
  fileName,
  enabled,
  busy,
  canMoveUp,
  canMoveDown,
  onMoveUp,
  onMoveDown,
}: QueueJobReorderControlsProps) {
  if (!enabled) return null;

  return (
    <div className="flex gap-1" role="group" aria-label={`Reorder ${fileName}`}>
      <Button
        type="button"
        variant="subtle"
        size="sm"
        disabled={busy || !canMoveUp}
        aria-label={`Move ${fileName} up`}
        title="Move up in this printer queue"
        onClick={(event) => {
          event.stopPropagation();
          onMoveUp?.();
        }}
      >
        Move up
      </Button>
      <Button
        type="button"
        variant="subtle"
        size="sm"
        disabled={busy || !canMoveDown}
        aria-label={`Move ${fileName} down`}
        title="Move down in this printer queue"
        onClick={(event) => {
          event.stopPropagation();
          onMoveDown?.();
        }}
      >
        Move down
      </Button>
    </div>
  );
}
