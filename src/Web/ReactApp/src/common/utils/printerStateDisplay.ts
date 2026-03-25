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

/**
 * Get the user-facing printer status label, including auto-dispatch overlays like PendingReady.
 *
 * @param options - Printer connectivity, printer state, and optional auto-dispatch state
 * @returns The label the UI should show for the printer
 */
export function getPrinterDisplayState(options: {
  printerState: string | undefined | null;
  autoDispatchState?: string | undefined | null;
  isOnline: boolean;
}): string {
  const { printerState, autoDispatchState, isOnline } = options;

  if (!isOnline) {
    return 'Offline';
  }

  if (isPendingReadyState(autoDispatchState)) {
    return 'Pending Ready';
  }

  return formatPrinterState(printerState);
}
