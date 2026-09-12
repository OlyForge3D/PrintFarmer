import { useCallback, useContext, useEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { registerAuthenticatedSignalRTransport } from '@/common/auth/authenticatedSignalRSession';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { queryKeys } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { printerSignalRService as printerSignalR } from '@/services/printer-signalr';
import { CONTROL_RECHECK_MS, PrinterControlTracker } from '@/services/printer-control-operations';
import type { ControlOperationSnapshot } from '@/services/printer-control-operations';
import { PrinterBackend, type CommandResult, type Printer, type PrinterControlIntent } from '@/types/api';

const trackers = new Map<string, { token: string; tracker: PrinterControlTracker }>();
let sessionGeneration = 0;
registerAuthenticatedSignalRTransport('printer-control-operations', async () => {
  sessionGeneration++;
  trackers.clear();
});
const emptySnapshot: ControlOperationSnapshot = {
  current: null, operation: null, etag: null, saved: null,
  checking: false, submitting: false, uncertain: true, missingAdmission: false, error: null,
};
const emptySubscribe = () => () => undefined;
const getEmptySnapshot = () => emptySnapshot;

export function usePrinterControlOperation(printer?: Pick<Printer, 'id' | 'backend'>) {
  const auth = useContext(AuthContext);
  const queryClient = useQueryClient();
  const isMoonraker = printer?.backend === PrinterBackend.Moonraker;
  const subject = auth?.isAuthenticated ? auth.user?.id : undefined;
  const token = subject ? localStorage.getItem('auth-token') : null;
  const printerId = printer?.id;
  const scope = subject && printerId
    ? `printfarmer:control-operation:${JSON.stringify([new URL(getApiBaseUrl(), window.location.origin).href, subject, printerId])}`
    : null;
  const tracker = useMemo(() => {
    if (!isMoonraker || !scope || !token || !printerId) return null;
    const existing = trackers.get(scope);
    if (existing?.token === token) return existing.tracker;
    const generation = sessionGeneration;
    const created = new PrinterControlTracker(printerId, scope,
      () => generation === sessionGeneration && localStorage.getItem('auth-token') === token);
    trackers.set(scope, { token, tracker: created });
    return created;
  }, [isMoonraker, scope, token, printerId]);
  const snapshot = useSyncExternalStore(tracker?.subscribe ?? emptySubscribe, tracker?.getSnapshot ?? getEmptySnapshot);
  const lifetime = useRef(new AbortController());

  useEffect(() => {
    lifetime.current = new AbortController();
    return () => lifetime.current.abort();
  }, [tracker, printerId]);

  useEffect(() => {
    if (!tracker || !printerId) return;
    const recheck = () => { void tracker.refresh().catch(() => undefined); };
    const subscribe = () => {
      void printerSignalR.subscribeToPrinter(printerId).catch(() => undefined);
    };
    const foreground = () => { if (document.visibilityState === 'visible') recheck(); };
    recheck();
    subscribe();
    const stopHints = printerSignalR.onControlOperationUpdated(event => {
      if (event.printerId === printerId) recheck();
    });
    const stopConnection = printerSignalR.onConnectionStateChange(connected => {
      if (connected) { subscribe(); recheck(); }
    });
    window.addEventListener('focus', recheck);
    document.addEventListener('visibilitychange', foreground);
    return () => {
      stopHints();
      stopConnection();
      window.removeEventListener('focus', recheck);
      document.removeEventListener('visibilitychange', foreground);
    };
  }, [tracker, printerId]);

  const pending = !!snapshot.saved || !!snapshot.current?.physicalControl.barrierHeld || !!snapshot.current?.physicalControl.requiresRecovery;
  useEffect(() => {
    if (!tracker || !pending) return;
    // Connected SignalR is still lossy. Poll REST without replaying commands.
    const timer = setInterval(() => {
      void tracker.poll().catch(() => undefined);
    }, CONTROL_RECHECK_MS);
    return () => clearInterval(timer);
  }, [tracker, pending]);

  const execute = useCallback(async (intent: PrinterControlIntent): Promise<CommandResult> => {
    if (!printerId) throw new Error('Select a printer first.');
    let result: CommandResult;
    if (isMoonraker) {
      if (!tracker) throw new Error('An authenticated session is required for durable motion.');
      result = await tracker.execute(intent, lifetime.current.signal);
    } else {
      const move = {
        ...(intent.x !== undefined ? { x: intent.x } : {}),
        ...(intent.y !== undefined ? { y: intent.y } : {}),
        ...(intent.z !== undefined ? { z: intent.z } : {}),
        ...(intent.f !== undefined ? { f: intent.f } : {}),
      };
      switch (intent.kind) {
        case 'HomeAll': result = await apiClient.homePrinter(printerId); break;
        case 'HomeXY': result = await apiClient.homeXY(printerId); break;
        case 'HomeZ': result = await apiClient.homeZ(printerId); break;
        case 'Jog': {
          result = await apiClient.movePrinter(printerId, move);
          break;
        }
        case 'MoveTo': {
          result = await apiClient.movePrinterTo(printerId, move);
          break;
        }
      }
    }
    void queryClient.invalidateQueries({ queryKey: queryKeys.printers });
    return result;
  }, [printerId, isMoonraker, tracker, queryClient]);

  const supported = snapshot.current?.physicalControl.supportedOperations;
  return {
    ...snapshot, tracker, execute, isMoonraker,
    canRetryAdmission: !!tracker?.canRetryAdmission(),
    blocked: isMoonraker && (!tracker || tracker.isBlocked() || !supported?.length),
    canRecover: !!auth?.isAuthenticated && auth.hasRole('farm_admin') && auth.hasPermission('queue', 'reconcile'),
  };
}

export type PrinterControlOperationController = ReturnType<typeof usePrinterControlOperation>;
