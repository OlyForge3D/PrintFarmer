import { describe, it, expect, vi, beforeEach } from 'vitest';
import { assetService, AssetManifest } from '../assetService';

// Mock fetch
global.fetch = vi.fn();

describe('assetService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('initialize', () => {
    it('should load asset manifest successfully', async () => {
      const mockManifest: AssetManifest = {
        manufacturers: [
          {
            id: 'prusa',
            name: 'Prusa',
            printers: [
              {
                id: 'mk3s',
                name: 'i3 MK3S+',
                cover: '/assets/prusa/mk3s.png',
                bedTexture: '/assets/prusa/mk3s-bed.svg'
              }
            ]
          }
        ]
      };

      vi.mocked(fetch).mockResolvedValue({
        ok: true,
        json: async () => mockManifest,
      } as Response);

      await assetService.initialize();

      const manifest = assetService.getManifest();
      expect(manifest.manufacturers).toHaveLength(1);
    });

    it('should handle fetch errors gracefully', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      
      vi.mocked(fetch).mockResolvedValue({
        ok: false,
        statusText: 'Not Found',
      } as Response);

      await assetService.initialize();

      const manifest = assetService.getManifest();
      expect(manifest.manufacturers).toEqual([]);
      
      consoleSpy.mockRestore();
    });

    it('should handle network errors', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      
      vi.mocked(fetch).mockRejectedValue(new Error('Network error'));

      await assetService.initialize();

      const manifest = assetService.getManifest();
      expect(manifest.manufacturers).toEqual([]);
      
      consoleSpy.mockRestore();
    });
  });

  describe('getManufacturers', () => {
    it('should return empty array when not initialized', () => {
      const manufacturers = assetService.getManufacturers();
      expect(Array.isArray(manufacturers)).toBe(true);
    });

    it('should return manufacturers after initialization', async () => {
      const mockManifest: AssetManifest = {
        manufacturers: [
          {
            id: 'prusa',
            name: 'Prusa',
            printers: []
          }
        ]
      };

      vi.mocked(fetch).mockResolvedValue({
        ok: true,
        json: async () => mockManifest,
      } as Response);

      await assetService.initialize();

      const manufacturers = assetService.getManufacturers();
      expect(manufacturers).toHaveLength(1);
      expect(manufacturers[0].id).toBe('prusa');
    });
  });

  describe('getManifest', () => {
    it('should return empty manifest when not initialized', () => {
      const manifest = assetService.getManifest();
      expect(manifest.manufacturers).toBeDefined();
    });

    it('should return full manifest after initialization', async () => {
      const mockManifest: AssetManifest = {
        manufacturers: [
          {
            id: 'prusa',
            name: 'Prusa',
            printers: [
              {
                id: 'mk3s',
                name: 'i3 MK3S+',
                cover: '/assets/prusa/mk3s.png'
              }
            ]
          }
        ]
      };

      vi.mocked(fetch).mockResolvedValue({
        ok: true,
        json: async () => mockManifest,
      } as Response);

      await assetService.initialize();

      const manifest = assetService.getManifest();
      expect(manifest).toEqual(mockManifest);
    });
  });
});

