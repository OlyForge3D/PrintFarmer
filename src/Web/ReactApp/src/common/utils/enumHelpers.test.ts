import { describe, it, expect } from 'vitest';
import { getPrinterBackendOptions, getPrinterBackendName, getMotionTypeOptions, getMotionTypeName } from './enumHelpers';
import { PrinterBackend, MotionType } from '@/types/api';

describe('enumHelpers', () => {
  describe('getPrinterBackendOptions', () => {
    it('should return all PrinterBackend enum values as options', () => {
      const options = getPrinterBackendOptions();

      expect(options).toEqual([
        { value: 1, label: 'Moonraker' },
        { value: 2, label: 'PrusaLink' },
        { value: 3, label: 'SDCP' },
        { value: 4, label: 'OctoPrint' },
      ]);
    });

    it('should only include numeric enum values', () => {
      const options = getPrinterBackendOptions();
      
      options.forEach(option => {
        expect(typeof option.value).toBe('number');
        expect(typeof option.label).toBe('string');
      });
    });
  });

  describe('getPrinterBackendName', () => {
    it('should return correct name for valid backend', () => {
      expect(getPrinterBackendName(PrinterBackend.Moonraker)).toBe('Moonraker');
      expect(getPrinterBackendName(PrinterBackend.PrusaLink)).toBe('PrusaLink');
      expect(getPrinterBackendName(PrinterBackend.SDCP)).toBe('SDCP');
      expect(getPrinterBackendName(PrinterBackend.OctoPrint)).toBe('OctoPrint');
    });

    it('should return empty string for undefined', () => {
      expect(getPrinterBackendName(undefined)).toBe('');
    });
  });

  describe('getMotionTypeOptions', () => {
    it('should return all MotionType enum values as options', () => {
      const options = getMotionTypeOptions();
      
      expect(options).toEqual([
        { value: 0, label: 'Cartesian' },
        { value: 1, label: 'CoreXY' },
        { value: 2, label: 'Delta' },
        { value: 99, label: 'Unknown' },
      ]);
    });
  });

  describe('getMotionTypeName', () => {
    it('should return correct name for valid motion type', () => {
      expect(getMotionTypeName(MotionType.Cartesian)).toBe('Cartesian');
      expect(getMotionTypeName(MotionType.CoreXY)).toBe('CoreXY');
      expect(getMotionTypeName(MotionType.Delta)).toBe('Delta');
      expect(getMotionTypeName(MotionType.Unknown)).toBe('Unknown');
    });

    it('should return empty string for undefined', () => {
      expect(getMotionTypeName(undefined)).toBe('');
    });
  });
});
