import type React from 'react';

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
 * Get the header background style for a printer card based on state.
 * Returns an inline style object to allow semi-transparent overlays using
 * the known design-system color values.
 *
 * Color values are derived from the printfarmer-dark.css design tokens.
 */
export function getStatusHeaderStyle(options: StatusColorOptions): React.CSSProperties {
  const { isOnline, isPrinting, isPaused, isShutdown } = options;
  if (!isOnline) return { background: 'rgba(100,116,139,0.15)' };     // slate-500 @ 15% — offline
  if (isPrinting) return { background: 'rgba(34,197,94,0.30)' };      // green-500 — vibrant green for printing
  if (isPaused) return { background: 'rgba(245,158,11,0.30)' };       // amber-500 — warm amber for paused
  if (isShutdown) return { background: 'rgba(239,68,68,0.30)' };      // red-500 — error/shutdown
  return { background: 'rgba(59,130,246,0.25)' };                     // blue-500 — blue for idle
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
