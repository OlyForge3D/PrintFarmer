import { describe, expect, it } from 'vitest';
import { isPrinterStateShutdown } from '../statusColors';

describe('isPrinterStateShutdown (#1909)', () => {
  it('recognizes Klippy shutdown, backend error, offline, and halted states', () => {
    expect(isPrinterStateShutdown('Shutdown')).toBe(true);
    expect(isPrinterStateShutdown('Error')).toBe(true);
    expect(isPrinterStateShutdown('Offline')).toBe(true);
    expect(isPrinterStateShutdown('Halted')).toBe(true);
  });

  it('is case-insensitive and matches substrings, mirroring status-color derivation', () => {
    expect(isPrinterStateShutdown('klippy shutdown')).toBe(true);
    expect(isPrinterStateShutdown('SHUTDOWN')).toBe(true);
  });

  it('returns false for normal operating states', () => {
    expect(isPrinterStateShutdown('Idle')).toBe(false);
    expect(isPrinterStateShutdown('Printing')).toBe(false);
    expect(isPrinterStateShutdown('Paused')).toBe(false);
  });

  it('treats missing state as not shutdown', () => {
    expect(isPrinterStateShutdown(undefined)).toBe(false);
    expect(isPrinterStateShutdown(null)).toBe(false);
  });
});
