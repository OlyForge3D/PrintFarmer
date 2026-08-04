import { describe, it, expect } from 'vitest';
import { getPrinterBackendOptions, getPrinterBackendName, getMotionTypeOptions, getMotionTypeName } from './enumHelpers';
import { PrinterBackend, MotionType } from '@/types/api';

describe('enumHelpers', () => {
  describe('getPrinterBackendOptions', () => {
    it('should return all PrinterBackend enum values as options', () => {
      const options = getPrinterBackendOptions();

      expect(options).toEqual([
        { value: 'Moonraker', label: 'Moonraker' },
        { value: 'PrusaLink', label: 'PrusaLink' },
        { value: 'SDCP', label: 'SDCP' },
        { value: 'OctoPrint', label: 'OctoPrint' },
        { value: 'FlashForge', label: 'FlashForge' },
      ]);
    });

    // The API serializes PrinterBackend as its PascalCase member name
    // (EnumJsonConverters.cs:57), so option values are strings, not ordinals.
    it('should only include string enum values', () => {
      const options = getPrinterBackendOptions();

      options.forEach(option => {
        expect(typeof option.value).toBe('string');
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
      expect(getPrinterBackendName(PrinterBackend.FlashForge)).toBe('FlashForge');
    });

    it('should return empty string for undefined', () => {
      expect(getPrinterBackendName(undefined)).toBe('');
    });
  });

  describe('getMotionTypeOptions', () => {
    it('should return all MotionType enum values as options', () => {
      const options = getMotionTypeOptions();
      
      expect(options).toEqual([
        { value: 'Cartesian', label: 'Cartesian' },
        { value: 'CoreXY', label: 'CoreXY' },
        { value: 'Delta', label: 'Delta' },
        { value: 'Unknown', label: 'Unknown' },
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
