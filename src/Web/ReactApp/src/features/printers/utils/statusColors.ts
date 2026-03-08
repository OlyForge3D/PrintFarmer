/**
 * Shared utility for printer status indicator colors using pf-* design tokens.
 * 
 * Returns consistent color classes for status dots/badges across printer card components.
 */

export interface StatusColorOptions {
  state: string;
  isOnline: boolean;
  isPrinting?: boolean;
  isPaused?: boolean;
  isShutdown?: boolean;
}

/**
 * Get the status indicator color classes for a printer.
 * 
 * @param options - Status information from printer
 * @returns Tailwind classes for the status indicator (bg-pf-* tokens)
 */
export function getStatusIndicatorColor(options: StatusColorOptions): string {
  const { isOnline, isPrinting, isPaused, isShutdown } = options;

  if (!isOnline) return 'bg-pf-disabled';
  if (isPrinting) return 'bg-pf-success-bg animate-pulse';
  if (isPaused) return 'bg-pf-warning';
  if (isShutdown) return 'bg-pf-error';
  
  // Default: idle/ready state
  return 'bg-pf-accent-bg';
}

/**
 * Get status indicator color from state string (for simple cases).
 * 
 * @param state - Printer state string
 * @param isOnline - Whether printer is online
 * @returns Tailwind classes for the status indicator
 */
export function getStatusIndicatorColorFromState(state: string, isOnline: boolean): string {
  const stateLower = (state ?? 'unknown').toLowerCase();
  
  return getStatusIndicatorColor({
    state,
    isOnline,
    isPrinting: stateLower.includes('printing'),
    isPaused: stateLower.includes('paused'),
    isShutdown: stateLower.includes('shutdown') || stateLower.includes('error') || stateLower.includes('offline') || stateLower.includes('halted'),
  });
}
