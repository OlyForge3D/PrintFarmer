/**
 * Failure-detection SignalR alert store (#1146 item 3).
 *
 * Before this change, every mounted card called `useFailureDetectionAlert`
 * independently, each subscribing its own `printerSignalRService.onFailureDetected`
 * listener and scheduling its own `setTimeout` for the 60s alert expiry — a
 * printer grid with N cards meant N raw SignalR listeners and up to N expiry
 * timers, none of it deduplicated (unlike TanStack Query's cache, a raw
 * callback list has no built-in sharing).
 *
 * This module hoists that subscription and per-alert expiry timer to a
 * single module-level store, keyed by printer ID:
 *  - Exactly one `onFailureDetected` handler is registered regardless of how
 *    many cards are mounted (reference-counted so it tears down once the
 *    last consumer unmounts).
 *  - Timers scale with the number of *active alerts*, not the number of
 *    rendered cards — an idle grid of 100 cards has zero timers.
 *  - Each `useFailureDetectionAlert(printerId)` call is a thin, per-printer
 *    reader of the shared store, preserving the previous hook's public
 *    signature and dismissal/expiry semantics exactly.
 */
import { useCallback, useSyncExternalStore } from 'react';
import { printerSignalRService } from '@/services/printer-signalr';
import type { FailureDetectionEvent } from '@/types/api';
import { getFailureDetectionIncidentKey } from '@/features/printers/utils/failure-detection-incidents';

const ALERT_LIFETIME_MS = 60_000;
const MAX_RECENT_EVENTS = 5;

interface PrinterAlertState {
  event: FailureDetectionEvent | null;
  recentEvents: FailureDetectionEvent[];
}

const EMPTY_ALERT_STATE: PrinterAlertState = { event: null, recentEvents: [] };

type Listener = () => void;

const alertsByPrinterId = new Map<string, PrinterAlertState>();
const expiryTimersByPrinterId = new Map<string, ReturnType<typeof setTimeout>>();
const listenersByPrinterId = new Map<string, Set<Listener>>();
let signalRUnsubscribe: (() => void) | undefined;
let subscriberCount = 0;

function getAlertState(printerId: string): PrinterAlertState {
  return alertsByPrinterId.get(printerId) ?? EMPTY_ALERT_STATE;
}

function notify(printerId: string): void {
  listenersByPrinterId.get(printerId)?.forEach((listener) => listener());
}

function clearExpiryTimer(printerId: string): void {
  const existing = expiryTimersByPrinterId.get(printerId);
  if (existing) {
    clearTimeout(existing);
    expiryTimersByPrinterId.delete(printerId);
  }
}

/** Dismiss the current active alert for a printer (does not touch `recentEvents`). */
function clearPrinterEvent(printerId: string): void {
  clearExpiryTimer(printerId);
  const current = getAlertState(printerId);
  if (current.event === null) return;
  alertsByPrinterId.set(printerId, { ...current, event: null });
  notify(printerId);
}

function handleFailureDetected(nextEvent: FailureDetectionEvent): void {
  const { printerId } = nextEvent;
  clearExpiryTimer(printerId);

  const current = getAlertState(printerId);
  const nextKey = getFailureDetectionIncidentKey(nextEvent);
  const dedupedEvents = current.recentEvents.filter(
    (currentEvent) => getFailureDetectionIncidentKey(currentEvent) !== nextKey
  );
  const recentEvents = [nextEvent, ...dedupedEvents].slice(0, MAX_RECENT_EVENTS);

  alertsByPrinterId.set(printerId, { event: nextEvent, recentEvents });
  notify(printerId);

  const timer = setTimeout(() => {
    expiryTimersByPrinterId.delete(printerId);
    alertsByPrinterId.set(printerId, { ...getAlertState(printerId), event: null });
    notify(printerId);
  }, ALERT_LIFETIME_MS);
  expiryTimersByPrinterId.set(printerId, timer);
}

function ensureSignalRSubscription(): void {
  if (signalRUnsubscribe) return;
  signalRUnsubscribe = printerSignalRService.onFailureDetected(handleFailureDetected);
}

function subscribe(printerId: string, listener: Listener): () => void {
  let listeners = listenersByPrinterId.get(printerId);
  if (!listeners) {
    listeners = new Set();
    listenersByPrinterId.set(printerId, listeners);
  }
  listeners.add(listener);
  subscriberCount += 1;
  ensureSignalRSubscription();

  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) {
      listenersByPrinterId.delete(printerId);
    }
    subscriberCount -= 1;
    if (subscriberCount <= 0 && signalRUnsubscribe) {
      signalRUnsubscribe();
      signalRUnsubscribe = undefined;
    }
  };
}

/** Test-only reset for the module-level store and its single SignalR subscription. */
export function __resetFailureDetectionAlertStoreForTests(): void {
  if (signalRUnsubscribe) {
    signalRUnsubscribe();
    signalRUnsubscribe = undefined;
  }
  expiryTimersByPrinterId.forEach((timer) => clearTimeout(timer));
  expiryTimersByPrinterId.clear();
  alertsByPrinterId.clear();
  listenersByPrinterId.clear();
  subscriberCount = 0;
}

export function useFailureDetectionAlert(printerId: string): {
  event: FailureDetectionEvent | null;
  recentEvents: FailureDetectionEvent[];
  clearEvent: () => void;
} {
  // useSyncExternalStore (rather than useState+useEffect） is the correct,
  // React-approved primitive for reading a module-level external store: it
  // reads the current snapshot during render (pure — no setState call), then
  // subscribes after commit and automatically re-syncs if the store changed
  // in the window between render and subscribe, without the
  // React 19.2 purity/set-state-in-effect lint (react-hooks/set-state-in-effect)
  // that a manual "resync" `setState` call inside the effect body would trip.
  const subscribeForPrinter = useCallback(
    (listener: Listener) => subscribe(printerId, listener),
    [printerId],
  );
  const getSnapshot = useCallback(() => getAlertState(printerId), [printerId]);
  const state = useSyncExternalStore(subscribeForPrinter, getSnapshot);

  const clearEvent = useCallback(() => clearPrinterEvent(printerId), [printerId]);

  return { event: state.event, recentEvents: state.recentEvents, clearEvent };
}