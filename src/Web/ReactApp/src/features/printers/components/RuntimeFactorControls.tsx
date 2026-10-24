import { useEffect, useId, useRef, useState } from 'react';
import { useIsMutating, useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button, Input } from '@/common/components/ui';
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

const allowedStates = new Set(['idle', 'ready', 'standby', 'complete', 'completed', 'cancelled', 'printing', 'paused']);
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
    : !allowedStates.has((printer.state ?? '').toLowerCase()) || !allowedStates.has((telemetry.status.state ?? '').toLowerCase())
      ? 'Printer state does not allow runtime overrides'
    : '';
  return <section aria-label="Runtime overrides" className="my-2 space-y-2 border-t border-pf-border pt-2">
    <h4 className="text-xs font-medium text-pf-text-secondary">Runtime overrides</h4>
    <p className="text-xs text-pf-text-secondary">Observed values are live readback, not command acknowledgements. 100% is normal.</p>
    {speed && <RuntimeFactorRow key={`speed-${telemetry.epoch}-${blocked}`} printerId={printer.id} kind="speed"
      observed={telemetry.speedFactorPercent} blocked={blocked} refresh={telemetry.refresh} />}
    {flow && <RuntimeFactorRow key={`flow-${telemetry.epoch}-${blocked}`} printerId={printer.id} kind="flow"
      observed={telemetry.flowFactorPercent} blocked={blocked} refresh={telemetry.refresh} />}
    {blocked && <p className="text-xs text-pf-text-secondary">{blocked}. Submit access and queue ownership are also checked by the server.</p>}
    {telemetry.error && <p role="alert" className="text-xs text-pf-error">Live readback unavailable: {telemetry.error}</p>}
  </section>;
}

function RuntimeFactorRow({ printerId, kind, observed, blocked, refresh }: {
  printerId: string; kind: 'speed' | 'flow'; observed: number | null; blocked: string; refresh: () => void;
}) {
  const label = kind === 'speed' ? 'Speed' : 'Flow';
  const id = useId();
  const [draft, setDraft] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState(false);
  const mounted = useRef(true);
  const busy = useRef(false);
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
  const send = async () => {
    if (blocked || pending || busy.current) return;
    const percent = Number(draft);
    if (!draft.trim() || !Number.isFinite(percent) || percent < 1 || percent > 200 ||
      !/^\d+(?:\.\d{1,2})?$/.test(draft.trim())) {
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
      setDraft('');
      setMessage(`Acknowledged ${percent}%. Awaiting live readback; observed value is not inferred.`);
      void queryClient.invalidateQueries({ queryKey: queryKeys.printers });
      refresh();
    } catch (cause) {
      if (!mounted.current || authEpoch !== getAuthEpoch()) return;
      const detail = mutationErrorMessage(cause, `Failed to set ${label.toLowerCase()}`);
      setError(true);
      setMessage(`${detail}. Outcome may be unknown. Check live readback and the printer before manually applying again. Nothing is retried automatically.`);
      toast.error(detail);
      refresh();
    } finally { busy.current = false; }
  };
  return <div className="space-y-1">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <label htmlFor={id} className="text-xs font-medium text-pf-text-primary">{label} %</label>
      <output aria-label={`Observed ${label.toLowerCase()}`} aria-live="polite" className="text-xs text-pf-text-secondary">
        Observed: {observed === null ? 'Unknown' : `${observed}%`}
      </output>
    </div>
    <form className="flex items-center gap-2" noValidate onSubmit={event => { event.preventDefault(); void send(); }}>
      <Input id={id} type="number" min={1} max={200} step={0.01} value={draft}
        placeholder="New %" disabled={!!blocked || pending} aria-invalid={error || undefined}
        aria-describedby={`${id}-help ${id}-status`} className="min-w-0 flex-1 text-xs!"
        onChange={event => { setDraft(event.target.value); setError(false); setMessage(''); }} />
      <Button type="submit" size="sm" variant="secondary" disabled={!!blocked || pending}
        aria-label={`Apply ${label.toLowerCase()} percentage`}>Apply</Button>
    </form>
    <p id={`${id}-help`} className="text-xs text-pf-text-secondary">New value: 1-200%, up to 2 decimals. Apply sends only {label.toLowerCase()}.</p>
    <p id={`${id}-status`} role={error ? 'alert' : 'status'} className={`empty:hidden text-xs ${error ? 'text-pf-error' : 'text-pf-text-secondary'}`}>{message}</p>
  </div>;
}
