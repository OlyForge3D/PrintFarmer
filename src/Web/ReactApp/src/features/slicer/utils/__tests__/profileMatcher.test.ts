import { describe, it, expect } from 'vitest';
import {
  getPrimaryNozzleDiameter,
  findMatchingMachineProfile,
  findHierarchyManufacturer,
  findHierarchyModel,
} from '../profileMatcher';
import type { PrinterForSlicing } from '../../components/job/PrinterSlicerSelector';
import type { MachineProfileListItem, PrinterModelProfilesDto } from '@/services/slicerProfilesService';

describe('profileMatcher', () => {
  describe('getPrimaryNozzleDiameter', () => {
    it('should return primary toolhead nozzle diameter', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test Printer',
        toolheads: [
          { isPrimary: true, nozzleDiameter: 0.4 },
          { isPrimary: false, nozzleDiameter: 0.6 },
        ],
      } as PrinterForSlicing;

      expect(getPrimaryNozzleDiameter(printer)).toBe(0.4);
    });

    it('should return first toolhead nozzle if no primary', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test Printer',
        toolheads: [
          { isPrimary: false, nozzleDiameter: 0.5 },
          { isPrimary: false, nozzleDiameter: 0.6 },
        ],
      } as PrinterForSlicing;

      expect(getPrimaryNozzleDiameter(printer)).toBe(0.5);
    });

    it('should fallback to nozzleDiameter field', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test Printer',
        nozzleDiameter: 0.6,
      } as PrinterForSlicing;

      expect(getPrimaryNozzleDiameter(printer)).toBe(0.6);
    });

    it('should return undefined if no nozzle diameter available', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test Printer',
      } as PrinterForSlicing;

      expect(getPrimaryNozzleDiameter(printer)).toBeUndefined();
    });
  });

  describe('findMatchingMachineProfile', () => {
    const profiles: MachineProfileListItem[] = [
      {
        name: 'Prusa i3 MK3S+ 0.4mm',
        manufacturer: 'Prusa',
        nozzleDiameter: 0.4,
      } as MachineProfileListItem,
      {
        name: 'Prusa i3 MK3S+ 0.6mm',
        manufacturer: 'Prusa',
        nozzleDiameter: 0.6,
      } as MachineProfileListItem,
      {
        name: 'Ender 3 Pro',
        manufacturer: 'Creality',
        nozzleDiameter: 0.4,
      } as MachineProfileListItem,
    ];

    it('should find matching profile by manufacturer, model, and nozzle', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'My Prusa',
        manufacturerName: 'Prusa',
        modelName: 'i3 MK3S+',
        nozzleDiameter: 0.4,
      } as PrinterForSlicing;

      const match = findMatchingMachineProfile(printer, profiles);
      expect(match?.name).toBe('Prusa i3 MK3S+ 0.4mm');
    });

    it('should match correct nozzle size', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'My Prusa',
        manufacturerName: 'Prusa',
        modelName: 'i3 MK3S+',
        nozzleDiameter: 0.6,
      } as PrinterForSlicing;

      const match = findMatchingMachineProfile(printer, profiles);
      expect(match?.name).toBe('Prusa i3 MK3S+ 0.6mm');
    });

    it('should return undefined if no manufacturer or model', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test',
      } as PrinterForSlicing;

      const match = findMatchingMachineProfile(printer, profiles);
      expect(match).toBeUndefined();
    });

    it('should return undefined if no matches found', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test',
        manufacturerName: 'Unknown',
        modelName: 'Unknown Model',
      } as PrinterForSlicing;

      const match = findMatchingMachineProfile(printer, profiles);
      expect(match).toBeUndefined();
    });

    it('should match partial manufacturer name', () => {
      const printer: PrinterForSlicing = {
        id: '1',
        name: 'Test',
        manufacturerName: 'Creality',
        modelName: 'Ender 3',
        nozzleDiameter: 0.4,
      } as PrinterForSlicing;

      const match = findMatchingMachineProfile(printer, profiles);
      expect(match?.manufacturer).toBe('Creality');
    });
  });

  describe('findHierarchyManufacturer', () => {
    const hierarchyMfrs = ['Prusa', 'Creality', 'Bambu Lab', 'Voron'];

    it('should find exact match', () => {
      const result = findHierarchyManufacturer('Prusa', hierarchyMfrs);
      expect(result).toBe('Prusa');
    });

    it('should find case-insensitive match', () => {
      const result = findHierarchyManufacturer('prusa', hierarchyMfrs);
      expect(result).toBe('Prusa');
    });

    it('should find partial match', () => {
      const result = findHierarchyManufacturer('Bambu', hierarchyMfrs);
      expect(result).toBe('Bambu Lab');
    });

    it('should return undefined if no match', () => {
      const result = findHierarchyManufacturer('Unknown', hierarchyMfrs);
      expect(result).toBeUndefined();
    });

    it('should return undefined if printer manufacturer is undefined', () => {
      const result = findHierarchyManufacturer(undefined, hierarchyMfrs);
      expect(result).toBeUndefined();
    });
  });

  describe('findHierarchyModel', () => {
    const modelsRecord: Record<string, PrinterModelProfilesDto> = {
      'guid-1': { name: 'i3 MK3S+' } as PrinterModelProfilesDto,
      'guid-2': { name: 'MINI+' } as PrinterModelProfilesDto,
      'guid-3': { name: 'Ender 3 Pro' } as PrinterModelProfilesDto,
    };

    it('should find exact model match and return key', () => {
      const result = findHierarchyModel('i3 MK3S+', modelsRecord);
      expect(result).toBe('guid-1');
    });

    it('should find case-insensitive match', () => {
      const result = findHierarchyModel('mini+', modelsRecord);
      expect(result).toBe('guid-2');
    });

    it('should find partial word match', () => {
      const result = findHierarchyModel('Ender 3', modelsRecord);
      expect(result).toBe('guid-3');
    });

    it('should return undefined if no match', () => {
      const result = findHierarchyModel('Unknown Model', modelsRecord);
      expect(result).toBeUndefined();
    });

    it('should return undefined if printer model is undefined', () => {
      const result = findHierarchyModel(undefined, modelsRecord);
      expect(result).toBeUndefined();
    });

    it('should return undefined if modelsRecord is undefined', () => {
      const result = findHierarchyModel('i3 MK3S+', undefined);
      expect(result).toBeUndefined();
    });
  });
});
