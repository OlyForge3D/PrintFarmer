import { useId, useState } from 'react';
import { MovementInput } from '@/common/components/ui';
import type { MoveRequest } from '@/types/api';
import { MotionControlButton } from '@/features/printers/components/MotionControlButton';
import { useMotionAction } from '@/features/printers/hooks/use-motion-action';
import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';

type Axis = 'X' | 'Y' | 'Z';
type Coordinate = number | string;
const axes: Axis[] = ['X', 'Y', 'Z'];
const isEntered = (value: Coordinate) => typeof value === 'number' || value.trim() !== '';
const isFiniteCoordinate = (value: Coordinate) => isEntered(value) && Number.isFinite(Number(value));
interface PrinterCoordinateRowProps {
  values: Record<Axis, Coordinate>;
  positions?: Partial<Record<Axis, number | null>>;
  onChange: (axis: Axis, value: Coordinate) => void;
  onMove: (axis: Axis, distance: number) => void | Promise<void>;
  onMoveTo?: (position: MoveRequest) => void | Promise<void>;
  disabled: boolean;
  /** The sidebar's relative Enter moves only the focused axis. */
  perAxisEnter?: boolean;
  goTitle?: string;
}

export function PrinterCoordinateRow({ values, positions, onChange, onMove, onMoveTo, disabled, perAxisEnter = false, goTitle = 'Go to position' }: PrinterCoordinateRowProps) {
  const { mode } = usePrinterControlsMode();
  const action = useMotionAction();
  const messageId = useId();
  const [touched, setTouched] = useState<Partial<Record<Axis, boolean>>>({});
  const [submitted, setSubmitted] = useState(false);
  const absolute = !!onMoveTo;
  const invalidAxes = axes.filter(axis => !isFiniteCoordinate(values[axis]) && (absolute || isEntered(values[axis])));
  const valid = invalidAxes.length === 0 && axes.some(axis => isEntered(values[axis]));
  const showError = invalidAxes.some(axis => submitted || touched[axis]);
  const error = `Enter a finite number for ${invalidAxes.join(', ')}${absolute ? '; all XYZ targets are required for absolute movement' : ''}.`;
  const hint = absolute ? 'Enter X, Y, and Z targets in mm to enable GO.' : 'Enter movement amounts in mm; leave unused axes blank.';

  const submit = async (axis?: Axis) => {
    if (disabled || action.pending) return;
    setSubmitted(true);
    // Relative Enter deliberately ignores other fields, preserving the sidebar contract.
    if (!absolute && perAxisEnter && axis) {
      if (isFiniteCoordinate(values[axis])) await action.run(() => onMove(axis, Number(values[axis])));
      return;
    }
    if (!valid) return;
    await action.run(async () => {
      if (onMoveTo) {
        await onMoveTo({ x: Number(values.X), y: Number(values.Y), z: Number(values.Z) });
      } else {
        for (const axis of axes) if (isEntered(values[axis])) await onMove(axis, Number(values[axis]));
      }
    });
  };

  return (
    <div className="@container mt-3 w-full min-w-0 max-w-[24rem]" data-printer-coordinate-row>
      <div className="grid grid-cols-[minmax(0,1fr)_2.75rem] items-end gap-2 @[19rem]:grid-cols-[repeat(3,minmax(0,1fr))_2.75rem]">
        {axes.map(axis => {
          const invalid = invalidAxes.includes(axis) && (submitted || touched[axis]);
          return (
            <div key={axis} className="col-start-1 min-w-0 @[19rem]:col-start-auto">
              <MovementInput
                axis={axis}
                coordinateMode={absolute ? 'absolute' : 'relative'}
                currentPosition={positions?.[axis]}
                disabled={disabled || action.pending}
                value={values[axis]}
                onChange={event => onChange(axis, event.target.value === '' ? '' : Number(event.target.value))}
                onBlur={() => setTouched(previous => ({ ...previous, [axis]: true }))}
                onKeyDown={event => {
                  if (event.key === 'Enter') { event.preventDefault(); void submit(axis); }
                }}
                invalid={!!invalid}
                aria-invalid={invalid ? true : undefined}
                aria-required={absolute || undefined}
                aria-describedby={showError || mode === 'guided' ? messageId : undefined}
              />
            </div>
          );
        })}
        <div className="col-start-2 row-start-3 w-11 shrink-0 @[19rem]:col-start-4 @[19rem]:row-start-1">
          <MotionControlButton
            variant="success"
            padSize="small"
            className="w-11! h-8! text-xs font-semibold enabled:hover:scale-105 enabled:hover:shadow-md"
            title={goTitle}
            aria-label={absolute ? 'GO to absolute position' : 'GO by movement amounts'}
            disabled={disabled || !valid}
            pending={action.pending}
            onClick={() => submit()}
          >GO</MotionControlButton>
        </div>
      </div>
      {showError ? <p id={messageId} role="alert" className="mt-1 text-xs text-pf-error">{error}</p> : mode === 'guided' ? <p id={messageId} className="mt-1 text-xs text-pf-text-secondary">{hint}</p> : null}
    </div>
  );
}
