import { describe, it, expect } from 'vitest';
import { formatPrinterState } from '../printerStateDisplay';

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
  });
});
