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
  if (!isOnline) return { background: 'rgba(156,163,175,0.06)' };   // --pf-disabled #9ca3af @ 6%
  if (isPrinting) return { background: 'rgba(4,120,87,0.35)' };     // --pf-success-bg #047857 @ 35%
  if (isPaused) return { background: 'rgba(217,119,6,0.30)' };      // --pf-warning #d97706 @ 30%
  if (isShutdown) return { background: 'rgba(220,38,38,0.25)' };    // --pf-error #dc2626 @ 25%
  return { background: 'rgba(4,120,87,0.10)' };                     // idle — subtle accent
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
