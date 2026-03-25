import type { AutoDispatchStatus } from '@/types/api';

/**
 * Format printer state for display.
 * Backend normalizes states to PascalCase (Idle, Printing, Paused, Offline, Shutdown, etc.)
 * This function ensures consistent display across all UI components.
 * 
 * @param state - The state string from backend or API
 * @returns Formatted state string ready for display, or 'Offline' if undefined
 */
export function formatPrinterState(state: string | undefined | null): string {
  if (!state) {
    return 'Offline';
  }

  const trimmed = state.trim();
  if (!trimmed) {
    return 'Offline';
  }

  const normalized = trimmed.replace(/[\s_-]+/g, '').toLowerCase();
  const knownStateLabels: Record<string, string> = {
    cancelled: 'Cancelled',
    complete: 'Complete',
    completed: 'Completed',
    disconnected: 'Disconnected',
    error: 'Error',
    halted: 'Halted',
    idle: 'Idle',
    none: 'None',
    offline: 'Offline',
    paused: 'Paused',
    pendingready: 'Pending Ready',
    printing: 'Printing',
    ready: 'Ready',
    shutdown: 'Shutdown',
    starting: 'Starting',
    unknown: 'Unknown',
  };

  const knownLabel = knownStateLabels[normalized];
  if (knownLabel) {
    return knownLabel;
  }

  return trimmed
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .split(/\s+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1).toLowerCase())
    .join(' ');
}

/**
 * Normalize auto-dispatch state checks so UI rendering is resilient to minor casing/format changes.
 *
 * @param state - The auto-dispatch state string from the backend
 * @returns True when the state indicates a printer is waiting for bed-clear confirmation
 */
export function isPendingReadyState(state: string | undefined | null): boolean {
  if (!state) {
    return false;
  }

  return state.replace(/[\s_-]+/g, '').toLowerCase() === 'pendingready';
}

function normalizeStatusText(value: string | undefined | null): string {
  return value?.replace(/[\s_-]+/g, '').toLowerCase() ?? '';
}

function normalizeStatusMessage(value: string | undefined | null): string {
  return value?.trim().replace(/[\s_-]+/g, ' ').toLowerCase() ?? '';
}

function isWaitingForBedClearConfirmationText(value: string | undefined | null): boolean {
  const normalized = normalizeStatusMessage(value);

  if (!normalized) {
    return false;
  }

  return normalized.includes('waiting for operator')
    || normalized.includes('confirm bed is clear')
    || normalized.includes('confirm the bed is clear')
    || (normalized.includes('clear the bed') && normalized.includes('confirm ready'));
}

/**
 * Determine whether auto-dispatch is blocked on operator bed-clear confirmation.
 *
 * Prefer the explicit PendingReady state, but fall back to the failed
 * "Bed Clear Confirmed" gate from the detailed/global auto-dispatch payload so
 * UI surfaces still show the operator-facing state when the summary row is stale.
 * Ignore the backend's "No confirmation needed yet" gate message so a canonical
 * None state does not keep rendering a stale Pending Ready overlay.
 *
 * @param status - Auto-dispatch status row from the bulk or per-printer endpoint
 * @returns True when the operator must clear the bed before queued work can resume
 */
export function requiresBedClearConfirmation(status: AutoDispatchStatus | undefined | null): boolean {
  if (!status) {
    return false;
  }

  if (isPendingReadyState(status.state)) {
    return true;
  }

  const bedClearGate = status.readyGateChecks?.find((check) =>
    normalizeStatusText(check.name) === 'bedclearconfirmed',
  );

  if (bedClearGate?.passed !== false) {
    return false;
  }

  const gateMessage = normalizeStatusMessage(bedClearGate.message);
  if (!gateMessage || gateMessage === 'no confirmation needed yet') {
    return false;
  }

  return isWaitingForBedClearConfirmationText(bedClearGate.message)
    || isWaitingForBedClearConfirmationText(status.attentionMessage)
    || isWaitingForBedClearConfirmationText(status.attentionReason)
    || isWaitingForBedClearConfirmationText(status.operatorAction);
}

/**
 * Get the user-facing printer status label, including auto-dispatch overlays like PendingReady.
 *
 * @param options - Printer connectivity, printer state, and optional auto-dispatch state
 * @returns The label the UI should show for the printer
 */
export function getPrinterDisplayState(options: {
  printerState: string | undefined | null;
  autoDispatchState?: string | undefined | null;
  autoDispatchStatus?: AutoDispatchStatus | undefined | null;
  isOnline: boolean;
}): string {
  const { printerState, autoDispatchState, autoDispatchStatus, isOnline } = options;

  if (!isOnline) {
    return 'Offline';
  }

  if (requiresBedClearConfirmation(autoDispatchStatus ?? (autoDispatchState ? {
    printerId: '',
    enabled: true,
    queueDepth: 0,
    state: autoDispatchState as AutoDispatchStatus['state'],
  } : undefined))) {
    return 'Pending Ready';
  }

  return formatPrinterState(printerState);
}
