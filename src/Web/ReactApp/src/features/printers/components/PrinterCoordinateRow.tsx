import { useId, useRef, useState } from 'react';
import { toast } from 'sonner';
import { Button, MovementInput } from '@/common/components/ui';
import type { MoveRequest } from '@/types/api';
import { useMotionAction } from '@/features/printers/hooks/use-motion-action';
import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { mutationErrorMessage } from '@/common/utils/mutationError';

type Axis = 'X' | 'Y' | 'Z';
type Coordinate = number | string;
type CoordinateMode = 'relative' | 'absolute';
const axes: Axis[] = ['X', 'Y', 'Z'];
const isEntered = (value: Coordinate) => typeof value === 'number' || value.trim() !== '';
const isFiniteCoordinate = (value: Coordinate) => isEntered(value) && Number.isFinite(Number(value));

interface PrinterCoordinateRowProps {
  values: Record<Axis, Coordinate>;
  positions?: Partial<Record<Axis, number | null>>;
  homedAxes?: string;
  onChange: (axis: Axis, value: number | '') => void;
  onMove: (axis: Axis, distance: number) => void | Promise<void>;
  onMoveTo?: (position: MoveRequest) => void | Promise<void>;
  disabled: boolean;
}

export function PrinterCoordinateRow({ values, positions, onChange, onMove, onMoveTo, disabled }: PrinterCoordinateRowProps) {
  const { mode: helpMode } = usePrinterControlsMode();
  const { hasPermission } = useAuth();
  const action = useMotionAction();
  const messageId = useId();
  const [mode, setMode] = useState<CoordinateMode>(onMoveTo ? 'absolute' : 'relative');
  const [invalid, setInvalid] = useState<Axis | null>(null);
  const dirty = useRef<Partial<Record<Axis, boolean>>>({});
  const initial = useRef<Partial<Record<Axis, Coordinate>>>({});
  const blocked = disabled || action.pending || !hasPermission('queue', 'start');
  const coordinateBlocked = blocked;

  const cancel = (axis: Axis) => {
    dirty.current[axis] = false;
    initial.current[axis] = '';
    onChange(axis, '');
    setInvalid(null);
  };

  const submit = (axis: Axis) => {
    if (!dirty.current[axis]) return;
    // Consume this edit synchronously: Enter, disabled-input blur and Tab cannot replay it.
    dirty.current[axis] = false;
    if (coordinateBlocked) { cancel(axis); return; }
    const value = values[axis];
    if (!isFiniteCoordinate(value)) {
      setInvalid(axis);
      return;
    }
    if (isFiniteCoordinate(initial.current[axis] ?? '') && Number(initial.current[axis]) === Number(value)) return;
    const amount = Number(value);
    cancel(axis);
    if ((mode === 'relative' && amount === 0) || (mode === 'absolute' && !onMoveTo)) return;
    const position: MoveRequest = axis === 'X' ? { x: amount } : axis === 'Y' ? { y: amount } : { z: amount };
    void action.run(() => mode === 'absolute' ? onMoveTo?.(position) : onMove(axis, amount))
      .catch(error => toast.error(mutationErrorMessage(error, 'Movement status uncertain. Check the printer before another command.')));
  };

  const changeMode = (next: CoordinateMode) => {
    if (blocked || (next === 'absolute' && !onMoveTo)) return;
    axes.forEach(cancel);
    setMode(next);
  };

  return (
    <div className="@container mt-3 w-full min-w-0 max-w-[24rem]" data-printer-coordinate-row aria-busy={action.pending}>
      <div role="group" aria-label="Coordinate movement mode" className="flex gap-1 mb-2">
        {(['relative', 'absolute'] as const).map(next => (
          <Button key={next} type="button" size="sm" variant={mode === next ? 'primary' : 'secondary'}
            data-coordinate-mode={next} aria-pressed={mode === next}
            disabled={blocked || (next === 'absolute' && !onMoveTo)}
            onClick={() => changeMode(next)}>
            {next === 'relative' ? 'Relative' : 'Absolute'}
          </Button>
        ))}
      </div>
      <div className="grid grid-cols-1 gap-2 @[16rem]:grid-cols-3">
        {axes.map(axis => (
          <div key={`${mode}-${axis}`} className="min-w-0">
            <MovementInput axis={axis} coordinateMode={mode} currentPosition={positions?.[axis]}
              disabled={coordinateBlocked} value={values[axis]} step={0.1}
              onFocus={() => { initial.current[axis] = values[axis]; }}
              onChange={event => {
                if (coordinateBlocked) return;
                dirty.current[axis] = true;
                setInvalid(null);
                onChange(axis, event.target.value === '' ? '' : Number(event.target.value));
              }}
              onBlur={event => {
                if (event.relatedTarget instanceof Element && event.relatedTarget.closest('[data-coordinate-mode]')) cancel(axis);
                else submit(axis);
              }}
              onKeyDown={event => {
                if (event.key === 'Enter') { event.preventDefault(); submit(axis); }
                else if (event.key === 'Escape') { event.preventDefault(); cancel(axis); }
              }}
              invalid={invalid === axis} aria-invalid={invalid === axis || undefined}
              aria-describedby={invalid || helpMode === 'guided' ? messageId : undefined}
            />
          </div>
        ))}
      </div>
      {action.pending && <p role="status" className="mt-1 text-xs text-pf-text-secondary">Sending movement command…</p>}
      {invalid ? <p id={messageId} role="alert" className="mt-1 text-xs text-pf-error">Enter a finite number for {invalid}, or press Escape to cancel.</p>
        : helpMode === 'guided' ? <p id={messageId} className="mt-1 text-xs text-pf-text-secondary">
          {mode === 'absolute' ? 'Absolute: move the edited axis to its target (mm).' : 'Relative: move the edited axis by its amount (mm).'} Tab or Enter sends one move.
        </p> : null}
    </div>
  );
}
