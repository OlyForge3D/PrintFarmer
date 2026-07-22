import clsx from 'clsx';
import { Button } from '@/common/components/ui/Button';
import { CloseIcon } from '@/common/components/icons/MdiIcons';

interface MeasurementOverlayProps {
  active: boolean;
  distance: number | null;
  onClear: () => void;
  onDeactivate: () => void;
}

export function MeasurementOverlay({
  active,
  distance,
  onClear,
  onDeactivate,
}: MeasurementOverlayProps) {
  if (!active) return null;

  return (
    <div
      className={clsx(
        'absolute top-4 left-4 z-10 flex flex-col gap-2',
        'rounded-lg border border-pf-accent/40 bg-pf-bg-2/95 p-3 shadow-lg backdrop-blur-sm',
        'max-w-56 text-sm',
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="font-semibold text-pf-accent">📏 Measure</span>
        <Button
          variant="ghost"
          size="sm"
          onClick={onDeactivate}
          className="!p-0.5"
          title="Close measurement tool"
        >
          <CloseIcon className="h-4 w-4 text-pf-text-secondary" />
        </Button>
      </div>

      {distance !== null ? (
        <div className="flex flex-col gap-1">
          <span className="font-mono text-lg font-bold text-pf-text-primary">
            {distance.toFixed(2)}{' '}
            <span className="text-xs font-normal text-pf-text-secondary">mm</span>
          </span>
          <Button
            variant="link"
            size="sm"
            onClick={onClear}
            className="!p-0 text-xs text-pf-text-tertiary underline hover:text-pf-text-secondary"
          >
            Click model to start new measurement
          </Button>
        </div>
      ) : (
        <p className="text-xs text-pf-text-secondary">
          Click two points on the model to measure the distance between them.
        </p>
      )}
    </div>
  );
}
