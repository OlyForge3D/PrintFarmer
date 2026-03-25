import { describe, it, expect } from 'vitest';
import { formatPrinterState, getPrinterDisplayState, isPendingReadyState } from '../printerStateDisplay';

describe('printerStateDisplay utils', () => {
  describe('formatPrinterState', () => {
    it('should return Offline for undefined state', () => {
      expect(formatPrinterState(undefined)).toBe('Offline');
    });

    it('should return Offline for null state', () => {
      expect(formatPrinterState(null)).toBe('Offline');
    });

    it('should return Offline for empty string', () => {
      expect(formatPrinterState('')).toBe('Offline');
      expect(formatPrinterState('  ')).toBe('Offline');
    });

    it('should format PascalCase states correctly', () => {
      expect(formatPrinterState('Idle')).toBe('Idle');
      expect(formatPrinterState('Printing')).toBe('Printing');
      expect(formatPrinterState('Paused')).toBe('Paused');
      expect(formatPrinterState('Offline')).toBe('Offline');
      expect(formatPrinterState('Shutdown')).toBe('Shutdown');
    });

    it('should handle lowercase states', () => {
      expect(formatPrinterState('idle')).toBe('Idle');
      expect(formatPrinterState('printing')).toBe('Printing');
      expect(formatPrinterState('paused')).toBe('Paused');
    });

    it('should handle uppercase states', () => {
      expect(formatPrinterState('IDLE')).toBe('Idle');
      expect(formatPrinterState('PRINTING')).toBe('Printing');
      expect(formatPrinterState('PAUSED')).toBe('Paused');
    });

    it('should handle mixed case states', () => {
      expect(formatPrinterState('iDLe')).toBe('Idle');
      expect(formatPrinterState('PrInTiNg')).toBe('Printing');
    });

    it('should trim whitespace', () => {
      expect(formatPrinterState('  Idle  ')).toBe('Idle');
      expect(formatPrinterState('  printing  ')).toBe('Printing');
    });

    it('should split compound states into separate words', () => {
      expect(formatPrinterState('PendingReady')).toBe('Pending Ready');
      expect(formatPrinterState('pending_ready')).toBe('Pending Ready');
      expect(formatPrinterState('pending-ready')).toBe('Pending Ready');
    });
  });

  describe('isPendingReadyState', () => {
    it('recognizes PendingReady regardless of casing or separators', () => {
      expect(isPendingReadyState('PendingReady')).toBe(true);
      expect(isPendingReadyState('pendingready')).toBe(true);
      expect(isPendingReadyState('pending_ready')).toBe(true);
      expect(isPendingReadyState('Pending Ready')).toBe(true);
    });

    it('returns false for other states', () => {
      expect(isPendingReadyState(undefined)).toBe(false);
      expect(isPendingReadyState('Ready')).toBe(false);
      expect(isPendingReadyState('Printing')).toBe(false);
    });
  });

  describe('getPrinterDisplayState', () => {
    it('prefers Pending Ready when auto-dispatch requires bed-clear confirmation', () => {
      expect(getPrinterDisplayState({
        printerState: 'Complete',
        autoDispatchState: 'PendingReady',
        isOnline: true,
      })).toBe('Pending Ready');
    });

    it('falls back to offline when the printer is not online', () => {
      expect(getPrinterDisplayState({
        printerState: 'PendingReady',
        autoDispatchState: 'PendingReady',
        isOnline: false,
      })).toBe('Offline');
    });

    it('uses the printer state when no pending bed-clear confirmation exists', () => {
      expect(getPrinterDisplayState({
        printerState: 'Printing',
        autoDispatchState: 'Ready',
        isOnline: true,
      })).toBe('Printing');
    });
  });
});
