import { useEffect, useId, useRef, useState } from 'react';
import { useIsMutating, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Input } from '@/common/components/ui';
import { getAuthEpoch } from '@/common/auth/authEpoch';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { mutationErrorMessage } from '@/common/utils/mutationError';
import { queryKeys } from '@/common/hooks/queryKeys';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useRuntimeFactors } from '@/features/printers/hooks/use-runtime-factors';
import { apiClient } from '@/services/api';
import type { Printer, PrinterBackendCapabilitiesDto } from '@/types/api';

interface RuntimeFactorControlsProps {
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
}

const MIN_PERCENT = 1;
const MAX_PERCENT = 200;
const PERCENT_STEP = 0.01;
const SLIDER_KEYS = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown']);
const inFlight = new WeakMap<QueryClient, Set<string>>();

export function RuntimeFactorControls({ printer, backendCapabilities }: RuntimeFactorControlsProps) {
  if (backendCapabilities?.printerId !== printer.id) return null;
  const speed = backendCapabilities?.supportsSpeedFactorControl === true;
  const flow = backendCapabilities?.supportsFlowFactorControl === true;
  if (!speed && !flow) return null;
  return <RuntimeFactors key={`${getApiBaseUrl()}-${printer.id}-${getAuthEpoch()}`} printer={printer} speed={speed} flow={flow} />;
}

function RuntimeFactors({ printer, speed, flow }: { printer: Printer; speed: boolean; flow: boolean }) {
  const telemetry = useRuntimeFactors(printer.id);
  const { hasPermission } = useAuth();
  const blocked = !hasPermission('queue', 'start') ? 'Queue.Start permission required'
    : !printer.isOnline || !telemetry.connected || telemetry.status?.isOnline === false ? 'Printer or live connection is offline'
    : printer.isEnabled === false ? 'Printer is disabled'
    : printer.inMaintenance ? 'Printer is in maintenance'
    : !telemetry.status ? 'Waiting for current printer status'
    : printer.state?.toLowerCase() !== 'printing' || telemetry.status.state?.toLowerCase() !== 'printing'
      ? 'Tuning is available while printing'
    : '';
  return <section aria-label="Tuning" className="my-2 space-y-2 border-t border-pf-border pt-2">
    <h4 className="text-xs font-medium text-pf-text-secondary">Tuning</h4>
    {speed && <RuntimeFactorRow key={`speed-${telemetry.epoch}-${blocked}`} printerId={printer.id} kind="speed"
      observed={telemetry.speedFactorPercent} blocked={blocked} refresh={telemetry.refresh} />}
    {flow && <RuntimeFactorRow key={`flow-${telemetry.epoch}-${blocked}`} printerId={printer.id} kind="flow"
      observed={telemetry.flowFactorPercent} blocked={blocked} refresh={telemetry.refresh} />}
    {telemetry.error && <p role="alert" className="text-xs text-pf-error">Live readback unavailable: {telemetry.error}</p>}
  </section>;
}

function RuntimeFactorRow({ printerId, kind, observed, blocked, refresh }: {
  printerId: string; kind: 'speed' | 'flow'; observed: number | null; blocked: string; refresh: () => void;
}) {
  const label = kind === 'speed' ? 'Speed' : 'Flow';
  const id = useId();
  const [draft, setDraft] = useState<number | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState(false);
  const mounted = useRef(true);
  const busy = useRef(false);
  const dirty = useRef(false);
  const pointer = useRef(false);
  const queryClient = useQueryClient();
  const mutationKey = ['printer-runtime-factor', printerId];
  const pending = useIsMutating({ mutationKey }) > 0;
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);
  const mutation = useMutation({
    mutationKey,
    retry: false,
    networkMode: 'always',
    mutationFn: async (percent: number) => {
      let active = inFlight.get(queryClient);
      if (!active) { active = new Set(); inFlight.set(queryClient, active); }
      if (active.has(printerId)) throw new Error('Another runtime override is pending. Nothing sent.');
      active.add(printerId);
      try {
        const result = kind === 'speed'
          ? await apiClient.setPrinterSpeedFactor(printerId, percent)
          : await apiClient.setPrinterFlowFactor(printerId, percent);
        if (!result.success) throw new Error(result.error || result.message || 'Command was not acknowledged');
        return result;
      } finally { active.delete(printerId); }
    },
  });
  const discard = () => {
    dirty.current = false;
    pointer.current = false;
    setDraft(null);
    setError(false);
    setMessage('');
  };
  const send = async (percent: number) => {
    dirty.current = false;
    pointer.current = false;
    if (blocked || pending || busy.current) return;
    if (!Number.isFinite(percent) || percent < MIN_PERCENT || percent > MAX_PERCENT ||
      Math.abs(percent * 100 - Math.round(percent * 100)) > 1e-6) {
      setError(true);
      setMessage('Enter 1 to 200%, with at most two decimal places. Nothing sent.');
      return;
    }
    busy.current = true;
    const authEpoch = getAuthEpoch();
    setError(false);
    setMessage('Sending...');
    try {
      await mutation.mutateAsync(percent);
      if (!mounted.current || authEpoch !== getAuthEpoch()) return;
      setDraft(null);
      setMessage(`Acknowledged ${percent}%. Awaiting live readback.`);
      void queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      refresh();
    } catch (cause) {
      if (!mounted.current || authEpoch !== getAuthEpoch()) return;
      const detail = mutationErrorMessage(cause, `Failed to set ${label.toLowerCase()}`);
      setError(true);
      setMessage(`${detail}. Outcome may be unknown. Check live readback and the printer before adjusting again. Nothing is retried automatically.`);
      toast.error(detail);
      refresh();
    } finally { busy.current = false; }
  };
  const value = Math.max(MIN_PERCENT, Math.min(MAX_PERCENT, draft ?? observed ?? MIN_PERCENT));
  return <div className="space-y-1">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <label htmlFor={id} className="text-xs font-medium text-pf-text-primary">{label} %</label>
      <div className="flex items-center gap-2">
        {draft !== null && <output aria-label={`Selected ${label.toLowerCase()}`} className="text-xs text-pf-text-primary">{draft}%</output>}
        <output aria-label={`Observed ${label.toLowerCase()}`} aria-live="polite" className="text-xs text-pf-text-secondary">
          Observed: {observed === null ? 'Unknown' : `${observed}%`}
        </output>
      </div>
    </div>
    <Input id={id} type="range" min={MIN_PERCENT} max={MAX_PERCENT} step={PERCENT_STEP} value={value}
      disabled={!!blocked || pending} title={blocked || undefined} aria-invalid={error || undefined}
      aria-valuetext={draft !== null ? `${draft}%` : observed === null ? 'Unknown' : `${observed}% observed`}
      aria-describedby={`${id}-status`}
      className="h-8! min-h-8! w-full min-w-0 cursor-pointer accent-pf-accent border-0! bg-transparent! p-0!"
      onPointerDown={event => {
        pointer.current = true;
        event.currentTarget.setPointerCapture?.(event.pointerId);
      }}
      onChange={event => {
        dirty.current = true;
        setDraft(Number(event.target.value));
        setError(false);
        setMessage('');
      }}
      onPointerUp={event => {
        if (pointer.current && dirty.current) void send(Number(event.currentTarget.value));
        pointer.current = false;
      }}
      onPointerCancel={discard}
      onLostPointerCapture={() => { if (pointer.current) discard(); }}
      onKeyUp={event => {
        if (SLIDER_KEYS.has(event.key) && dirty.current) void send(Number(event.currentTarget.value));
      }}
      onKeyDown={event => {
        if (event.key === 'Escape') { event.preventDefault(); discard(); }
      }}
      onBlur={event => {
        if (dirty.current && !pointer.current) void send(Number(event.currentTarget.value));
      }} />
    <div aria-hidden="true" className="flex justify-between text-xs text-pf-text-secondary">
      <span>{MIN_PERCENT}%</span><span>{MAX_PERCENT}%</span>
    </div>
    <p id={`${id}-status`} role={error ? 'alert' : 'status'} className={`empty:hidden text-xs ${error ? 'text-pf-error' : 'text-pf-text-secondary'}`}>{message}</p>
  </div>;
}
