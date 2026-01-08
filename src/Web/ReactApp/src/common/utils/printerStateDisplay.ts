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
  
  // State should already be PascalCase from backend normalization
  // Just return it as-is. If it's not PascalCase for some reason, fix it here
  const trimmed = state.trim();
  if (!trimmed) {
    return 'Offline';
  }
  
  // Ensure first letter is uppercase and rest is lowercase (handles edge cases)
  return trimmed.charAt(0).toUpperCase() + trimmed.slice(1).toLowerCase();
}
