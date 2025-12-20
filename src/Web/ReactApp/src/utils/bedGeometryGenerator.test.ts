/**
 * Tests for Bed Geometry Generator
 */

import { describe, it, expect, beforeEach } from 'vitest';
import * as THREE from 'three';
import {
  extractBedDimensions,
  generateBedPlatformMesh,
  generateBuildVolumeWireframe,
  generateGridHelper,
  generateAxesHelper,
  generateNozzleGeometry,
  createBedVisualization,
  calculateOptimalCameraPosition,
  calculateScaleFactors,
  validateBedDimensions,
  BedDimensions,
} from '@/utils/bedGeometryGenerator';
import { PrinterModelDto } from '@/types/api';

describe('Bed Geometry Generator', () => {
  let testPrinterModel: PrinterModelDto;

  beforeEach(() => {
    testPrinterModel = {
      id: 'test-1',
      name: 'Test Printer',
      manufacturerId: 'mfg-1',
      maxX: 200,
      maxY: 200,
      maxZ: 200,
    };
  });

  describe('extractBedDimensions', () => {
    it('extracts dimensions from printer model', () => {
      const dims = extractBedDimensions(testPrinterModel);

      expect(dims.width).toBe(200);
      expect(dims.depth).toBe(200);
      expect(dims.height).toBe(200);
      expect(dims.thickness).toBe(5);
    });

    it('uses default dimensions for missing values', () => {
      const dims = extractBedDimensions({
        id: 'test-2',
        name: 'Minimal',
        manufacturerId: 'mfg-1',
      });

      expect(dims.width).toBeGreaterThan(0);
      expect(dims.depth).toBeGreaterThan(0);
      expect(dims.height).toBeGreaterThan(0);
    });

    it('handles zero dimensions gracefully', () => {
      const dims = extractBedDimensions({
        id: 'test-3',
        name: 'Zero Model',
        manufacturerId: 'mfg-1',
        maxX: 0,
        maxY: 0,
        maxZ: 0,
      });

      expect(dims.width).toBeGreaterThan(0); // Should use default
      expect(dims.depth).toBeGreaterThan(0);
      expect(dims.height).toBeGreaterThan(0);
    });
  });

  describe('generateBedPlatformMesh', () => {
    it('creates a mesh with correct geometry', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200, thickness: 5 };
      const mesh = generateBedPlatformMesh(dims);

      expect(mesh).toBeInstanceOf(THREE.Mesh);
      expect(mesh.geometry).toBeInstanceOf(THREE.BoxGeometry);
    });

    it('positions bed at correct height', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200, thickness: 10 };
      const mesh = generateBedPlatformMesh(dims);

      expect(mesh.position.y).toBe(-dims.thickness / 2);
    });

    it('applies phong material', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const mesh = generateBedPlatformMesh(dims);

      expect(mesh.material).toBeInstanceOf(THREE.MeshPhongMaterial);
    });
  });

  describe('generateBuildVolumeWireframe', () => {
    it('creates wireframe geometry', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const wireframe = generateBuildVolumeWireframe(dims);

      expect(wireframe).toBeInstanceOf(THREE.LineSegments);
    });

    it('positions correctly', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const wireframe = generateBuildVolumeWireframe(dims);

      expect(wireframe.position.y).toBe(dims.height / 2);
    });
  });

  describe('generateGridHelper', () => {
    it('creates grid helper', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const grid = generateGridHelper(dims);

      expect(grid).toBeInstanceOf(THREE.GridHelper);
    });

    it('positions grid at bed surface', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const grid = generateGridHelper(dims);

      expect(grid.position.y).toBe(0);
    });
  });

  describe('generateAxesHelper', () => {
    it('creates axes helper with default scale', () => {
      const axes = generateAxesHelper();

      expect(axes).toBeInstanceOf(THREE.AxesHelper);
    });

    it('creates axes helper with custom scale', () => {
      const axes = generateAxesHelper(100);

      expect(axes).toBeInstanceOf(THREE.AxesHelper);
    });
  });

  describe('generateNozzleGeometry', () => {
    it('creates cone geometry for nozzle', () => {
      const geometry = generateNozzleGeometry(0.4);

      expect(geometry).toBeInstanceOf(THREE.BufferGeometry);
    });

    it('uses default nozzle diameter', () => {
      const geometry = generateNozzleGeometry();

      expect(geometry).toBeInstanceOf(THREE.BufferGeometry);
    });
  });

  describe('createBedVisualization', () => {
    it('creates complete bed visualization', () => {
      const { group, dimensions } = createBedVisualization(testPrinterModel);

      expect(group).toBeInstanceOf(THREE.Group);
      expect(group.children.length).toBeGreaterThan(0);
      expect(dimensions.width).toBeGreaterThan(0);
      expect(dimensions.depth).toBeGreaterThan(0);
      expect(dimensions.height).toBeGreaterThan(0);
    });
  });

  describe('calculateOptimalCameraPosition', () => {
    it('calculates valid camera position', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const { position, target } = calculateOptimalCameraPosition(dims);

      expect(position).toBeInstanceOf(THREE.Vector3);
      expect(target).toBeInstanceOf(THREE.Vector3);
    });

    it('positions camera away from origin', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const { position } = calculateOptimalCameraPosition(dims);

      expect(position.length()).toBeGreaterThan(0);
    });

    it('targets center of build volume', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const { target } = calculateOptimalCameraPosition(dims);

      expect(target.y).toBe(dims.height / 2);
    });
  });

  describe('calculateScaleFactors', () => {
    it('calculates appropriate scale factors', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const scales = calculateScaleFactors(dims);

      expect(scales.nozzleScale).toBeGreaterThan(0);
      expect(scales.markerScale).toBeGreaterThan(0);
      expect(scales.textScale).toBeGreaterThan(0);
    });

    it('scales proportionally to bed size', () => {
      const smallDims: BedDimensions = { width: 50, depth: 50, height: 50 };
      const largeDims: BedDimensions = { width: 500, depth: 500, height: 500 };

      const smallScales = calculateScaleFactors(smallDims);
      const largeScales = calculateScaleFactors(largeDims);

      expect(largeScales.nozzleScale).toBeGreaterThan(smallScales.nozzleScale);
    });
  });

  describe('validateBedDimensions', () => {
    it('validates positive dimensions', () => {
      const dims: BedDimensions = { width: 100, depth: 100, height: 200 };
      const result = validateBedDimensions(dims);

      expect(result.valid).toBe(true);
      expect(result.error).toBeUndefined();
    });

    it('rejects zero width', () => {
      const dims: BedDimensions = { width: 0, depth: 100, height: 200 };
      const result = validateBedDimensions(dims);

      expect(result.valid).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('rejects negative depth', () => {
      const dims: BedDimensions = { width: 100, depth: -50, height: 200 };
      const result = validateBedDimensions(dims);

      expect(result.valid).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('rejects oversized dimensions', () => {
      const dims: BedDimensions = { width: 2000, depth: 2000, height: 2000 };
      const result = validateBedDimensions(dims);

      expect(result.valid).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('accepts reasonable dimensions', () => {
      const dims: BedDimensions = { width: 500, depth: 500, height: 500 };
      const result = validateBedDimensions(dims);

      expect(result.valid).toBe(true);
    });
  });

  describe('Integration', () => {
    it('works with realistic printer specs', () => {
      const realisticPrinter: PrinterModelDto = {
        id: 'prusa-mini',
        name: 'Prusa MINI',
        manufacturerId: 'prusa',
        maxX: 180,
        maxY: 180,
        maxZ: 210,
        defaultNozzleDiameter: 0.4,
      };

      const dims = extractBedDimensions(realisticPrinter);
      const validation = validateBedDimensions(dims);
      const { group } = createBedVisualization(realisticPrinter);

      expect(validation.valid).toBe(true);
      expect(group.children.length).toBeGreaterThan(0);
    });
  });
});
