/**
 * Context + hook for the global command palette (#938). Kept in a separate
 * file from {@link GlobalCommandPaletteProvider} so the provider file exports
 * only components and fast refresh works without warnings.
 */
import { createContext, useContext } from 'react';

export interface CommandPaletteContextValue {
  /** Open the palette. Safe to call while it is already open (no-op). */
  open: () => void;
  /** Close the palette. Safe to call while it is closed. */
  close: () => void;
  /** True when the palette dialog is currently rendered. */
  isOpen: boolean;
}

export const CommandPaletteContext = createContext<CommandPaletteContextValue | null>(null);

/**
 * Access the global command palette. Throws when called outside a provider —
 * matches every other context helper in the project and pushes tests to wrap
 * components under `<GlobalCommandPaletteProvider>`.
 */
export function useCommandPalette(): CommandPaletteContextValue {
  const ctx = useContext(CommandPaletteContext);
  if (!ctx) {
    throw new Error('useCommandPalette must be used inside <GlobalCommandPaletteProvider>');
  }
  return ctx;
}
