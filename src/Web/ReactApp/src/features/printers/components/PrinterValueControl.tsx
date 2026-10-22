import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { toast } from 'sonner';
import { Button, Input } from '@/common/components/ui';
import { RefreshIcon } from '@/common/components/icons/MdiIcons';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import type { CommandResult } from '@/types/api';

interface PrinterValueControlProps {
  label: string;
  current: number | undefined;
  min: number;
  max?: number;
  step?: number | null;
  unit: string;
  disabled: boolean;
  pending?: boolean;
  mode?: 'number' | 'slider' | 'switch';
  children?: ReactNode;
  status?: PrinterValueStatus;
  onSubmit: (value: number) => Promise<CommandResult>;
}

export interface PrinterValueStatus {
  id: string;
  onChange: (message: string, error: boolean) => void;
}

const SLIDER_KEYS = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown']);

export function PrinterValueControl({
  label, current, min, max, step, unit, disabled, pending = false, mode = 'number', children, status, onSubmit,
}: PrinterValueControlProps) {
  const id = useId();
  const [draft, setDraft] = useState<string | null>(null);
  const [request, setRequest] = useState<{ value: number; baseline: number | undefined; accepted: boolean } | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState(false);
  const [sending, setSending] = useState(false);
  const busy = useRef(false);
  const dirty = useRef(false);
  const pointer = useRef(false);
  const replaceNumberBlur = useRef(false);
  const interrupted = useRef(false);
  const shownRequest = request && request.baseline === current ? request : null;
  const setting = shownRequest?.value ?? current;
  const shown = draft ?? (setting === undefined ? '' : String(setting));
  const parsed = shown.trim() === '' ? NaN : Number(shown);
  const validValue = (value: number) => Number.isFinite(value) && value >= min && (max === undefined || value <= max) &&
    (!step || step <= 0 || Math.abs((value - min) / step - Math.round((value - min) / step)) <= 1e-6);
  const locked = disabled || pending || sending;
  const messageId = status?.id ?? `${id}-message`;
  const actionLabel = label.startsWith('Set ') ? label.slice(4) : label;

  useEffect(() => { status?.onChange(message, error); }, [message, error, status]);

  useEffect(() => {
    if (request && current !== request.baseline) {
      setRequest(null);
      if (!busy.current) setMessage('');
    }
  }, [current, request]);

  useEffect(() => {
    if (disabled) {
      dirty.current = false;
      pointer.current = false;
      setDraft(null);
      setRequest(null);
      if (busy.current) {
        interrupted.current = true;
        setMessage('Connection or access changed; outcome unknown. Check telemetry before retrying.');
      } else {
        setMessage('');
      }
    }
  }, [disabled]);

  const discard = () => {
    dirty.current = false;
    pointer.current = false;
    setDraft(null);
    setMessage('');
    setError(false);
  };

  const send = async (value: number) => {
    if (disabled || pending || busy.current) return;
    dirty.current = false;
    if (!validValue(value)) {
      setError(true);
      setMessage(`Enter ${min}${max === undefined ? ' or higher' : `–${max}`}${unit}${step && step > 0 ? ` in steps of ${step}` : ''}. Nothing sent.`);
      return;
    }
    if (value === setting) {
      discard();
      return;
    }
    busy.current = true;
    setSending(true);
    interrupted.current = false;
    setDraft(null);
    setError(false);
    setRequest({ value, baseline: current, accepted: false });
    setMessage('Sending…');
    try {
      const result = await onSubmit(value);
      if (interrupted.current) return;
      if (!result.success) throw new Error(result.error || result.message || `Failed to set ${label}`);
      setRequest({ value, baseline: current, accepted: true });
      setMessage(`Accepted ${value}${unit} · awaiting telemetry`);
    } catch (cause) {
      if (interrupted.current) return;
      const detail = mutationErrorMessage(cause, `Failed to set ${label}`);
      setRequest(null);
      setDraft(mode === 'switch' ? null : String(value));
      setError(true);
      setMessage(`${detail}. ${mode === 'switch' ? 'Toggle again' : 'Press Enter or adjust the control'} to retry.`);
      toast.error(detail);
    } finally {
      busy.current = false;
      setSending(false);
    }
  };

  const commitNumber = () => {
    if (draft !== null) void send(parsed);
  };
  const preview = (value: string) => {
    dirty.current = true;
    setDraft(value);
    setError(false);
    setMessage('');
  };
  const numeric = (
    <label data-value-field className={`relative mx-0! my-0.5! flex h-7 shrink-0 items-center @[441px]/sensors:w-[5.5rem] ${mode === 'slider' ? 'w-[4.75rem]' : 'w-[4.5rem]'}`}>
      <Input type="number" aria-label={label} aria-describedby={`${id}-bounds ${messageId}`}
        aria-invalid={error || (draft !== null && !validValue(parsed)) || undefined}
        aria-busy={busy.current} min={min} max={max} step={step && step > 0 ? step : 'any'}
        value={shown} disabled={locked}
        className="h-7! min-h-7! py-0! border-pf-control-boundary! text-right text-xs! font-medium text-pf-text-primary! pl-0.5! pr-5! [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
        onChange={event => preview(event.target.value)}
        onBlur={() => {
          if (replaceNumberBlur.current) { replaceNumberBlur.current = false; return; }
          if (dirty.current) commitNumber();
        }}
        onKeyDown={event => {
          if (event.key === 'Enter') { event.preventDefault(); commitNumber(); }
          if (event.key === 'Escape') { event.preventDefault(); discard(); }
        }} />
      <span className="pointer-events-none absolute right-1 top-1/2 -translate-y-1/2 text-xs text-pf-text-secondary" aria-hidden="true">{unit}</span>
    </label>
  );

  return (
    <div className="min-w-0" onPointerDownCapture={event => {
      const action = event.target instanceof Element ? event.target.closest('[data-value-action]') : null;
      // Pointer actions replace this row's draft before focus leaves the number. Tab still commits.
      replaceNumberBlur.current = Boolean(action && !action.hasAttribute('disabled'));
      if (replaceNumberBlur.current) dirty.current = false;
    }} onPointerUpCapture={() => { replaceNumberBlur.current = false; }}
      onKeyDownCapture={() => { replaceNumberBlur.current = false; }}>
      <div className="flex min-h-8 min-w-0 flex-wrap items-center justify-end gap-x-1 gap-y-1 @[441px]/sensors:gap-x-2">
        {children}
        {mode === 'switch' ? setting === undefined ? null : <Button type="button" variant="ghost" role="switch"
          aria-label={label} aria-checked={setting === 1} aria-describedby={messageId}
          disabled={locked} className="h-8! min-h-8! min-w-9 p-0!"
          onClick={() => { void send(setting === 1 ? 0 : 1); }}>
          <span data-pf-radius="full" className={`relative block h-[18px] w-8 rounded-full border border-pf-control-boundary ${setting === 1 ? 'bg-pf-accent' : 'bg-pf-bg-2'}`} aria-hidden="true">
            <span data-pf-radius="full" className={`absolute top-[3px] left-[3px] h-2.5 w-2.5 rounded-full bg-pf-text-primary ${setting === 1 ? 'translate-x-3.5' : ''}`} />
          </span>
        </Button> : <>
          {mode === 'slider' && min <= 0 && (max === undefined || max >= 0) && validValue(0) &&
            <Button type="button" variant="ghost" data-value-action aria-label={`Reset ${actionLabel} to 0${unit}`}
              title={`Reset to 0${unit}`} className="h-8! min-h-8! min-w-8 p-0!" disabled={locked}
              onClick={() => { void send(0); }}><span aria-hidden="true"><RefreshIcon /></span></Button>}
          {numeric}
        </>}
      </div>
      {mode === 'slider' && <div className="mt-1 flex min-w-0 items-center gap-1">
        {step !== undefined && step !== null && step > 0 && <Button type="button" variant="ghost" data-value-action
          aria-label={`Decrease ${actionLabel}`} className="h-8! min-h-8! min-w-8 p-0! text-2xl! font-normal! leading-none!" disabled={locked || !validValue(parsed - step)}
          onClick={() => { void send(parsed - step); }}>−</Button>}
        <Input type="range" data-value-action aria-label={`${label} slider`} aria-describedby={`${id}-bounds ${messageId}`}
          aria-valuetext={setting === undefined && draft === null ? 'Not reported' : `${shown}${unit}`}
          min={min} max={max} step={step && step > 0 ? step : 'any'} value={Number.isFinite(parsed) ? parsed : min}
          disabled={locked} className="h-8! min-h-8! min-w-0 flex-1 cursor-pointer accent-pf-accent border-0! bg-transparent! p-0!"
          onPointerDown={event => { pointer.current = true; event.currentTarget.setPointerCapture?.(event.pointerId); }}
          onChange={event => preview(event.target.value)}
          onPointerUp={event => {
            if (pointer.current && dirty.current) void send(Number(event.currentTarget.value));
            pointer.current = false;
          }}
          onPointerCancel={discard}
          onKeyUp={event => { if (SLIDER_KEYS.has(event.key) && dirty.current) void send(Number(event.currentTarget.value)); }}
          onKeyDown={event => { if (event.key === 'Escape') discard(); }}
          onBlur={() => { if (dirty.current && !pointer.current) commitNumber(); }} />
        {step !== undefined && step !== null && step > 0 && <Button type="button" variant="ghost" data-value-action
          aria-label={`Increase ${actionLabel}`} className="h-8! min-h-8! min-w-8 p-0! text-2xl! font-normal! leading-none!" disabled={locked || !validValue(parsed + step)}
          onClick={() => { void send(parsed + step); }}>+</Button>}
      </div>}
      <span id={`${id}-bounds`} className="sr-only">
        {min}{max === undefined ? ' or higher' : ` to ${max}`}{unit}{step && step > 0 ? `, step ${step}` : ''}. Enter or Tab sends.
      </span>
      {!status && <p id={messageId} role="status" className={`empty:hidden mt-1 break-words text-left text-xs ${error ? 'text-pf-error' : 'text-pf-text-secondary'}`}>
        {message}
      </p>}
    </div>
  );
}
