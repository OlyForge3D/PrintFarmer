/* eslint-disable local/pf-no-unguarded-console */
/**
 * Asset Management Service
 * Loads and provides access to OrcaSlicer assets (printer images, bed textures)
 */

export interface PrinterAsset {
  id: string;
  name: string;
  cover?: string; // PNG image URL for printer thumbnail
  bedTexture?: string; // SVG or PNG URL for bed texture
  bedTextureFormat?: "svg" | "png" | null; // Format of bed texture (SVG preferred for scaling)
  bedModel?: string; // STL URL for 3D bed model
}

export interface ManufacturerAssets {
  id: string;
  name: string;
  printers: PrinterAsset[];
}

export interface AssetManifest {
  manufacturers: ManufacturerAssets[];
}

class AssetService {
  private manifest: AssetManifest | null = null;
  private manufacturerMap: Map<string, ManufacturerAssets> = new Map();
  private printerMap: Map<string, PrinterAsset> = new Map();

  /**
   * Initialize the asset service by loading the manifest
   */
  async initialize(): Promise<void> {
    try {
      const response = await fetch("/assets/orcaslicer/manifest.json");
      if (!response.ok) {
        throw new Error(
          `Failed to load asset manifest: ${response.statusText}`
        );
      }

      this.manifest = await response.json();

      // Build lookup maps for efficient searching
      if (this.manifest?.manufacturers) {
        for (const manufacturer of this.manifest.manufacturers) {
          // Map by manufacturer ID
          this.manufacturerMap.set(manufacturer.id.toLowerCase(), manufacturer);

          // Map all printers for quick lookup
          for (const printer of manufacturer.printers) {
            const key = `${manufacturer.id}/${printer.id}`.toLowerCase();
            this.printerMap.set(key, printer);
          }
        }
      }

      console.debug(
        `[AssetService] Loaded ${this.manufacturerMap.size} manufacturers with ${this.printerMap.size} printer models`
      );
    } catch (error) {
      console.error("[AssetService] Failed to initialize:", error);
      this.manifest = { manufacturers: [] };
    }
  }

  /**
   * Get all manufacturers and their printers
   */
  getManifest(): AssetManifest {
    return this.manifest || { manufacturers: [] };
  }

  /**
   * Get all manufacturers
   */
  getManufacturers(): ManufacturerAssets[] {
    return this.manifest?.manufacturers || [];
  }

  /**
   * Get manufacturer by ID or name
   */
  getManufacturer(query: string): ManufacturerAssets | undefined {
    const lowerQuery = query.toLowerCase();
    // Try direct ID match first
    const result = this.manufacturerMap.get(lowerQuery);
    if (result) return result;

    // Try name match
    return this.manufacturerMap.get(
      Array.from(this.manufacturerMap.values()).find(
        (m) => m.name.toLowerCase() === lowerQuery
      )?.id || ""
    );
  }

  /**
   * Get printer by manufacturer and model
   * Can accept various formats:
   * - getAsset('bambu-lab', 'x1')
   * - getAsset('Bambu Lab', 'X1')
   * - getAsset('bambu_lab/x1')
   */
  getAsset(
    manufacturerOrPath: string,
    model?: string
  ): PrinterAsset | undefined {
    let key: string;

    if (model) {
      // manufacturerOrPath and model are separate
      key = `${manufacturerOrPath}/${model}`.toLowerCase();
    } else {
      // manufacturerOrPath contains full path
      key = manufacturerOrPath.toLowerCase();
    }

    // Try exact match first (with spaces preserved)
    let result = this.printerMap.get(key);
    if (result) return result;

    // Fallback: try with underscores instead of spaces for compatibility
    const keyWithUnderscores = key.replace(/\s+/g, "_");
    result = this.printerMap.get(keyWithUnderscores);
    if (result) return result;

    // Final fallback: try with dashes instead of spaces
    const keyWithDashes = key.replace(/\s+/g, "-");
    return this.printerMap.get(keyWithDashes);
  }

  /**
   * Get printer cover image URL
   */
  getCoverImageUrl(
    manufacturerId: string,
    modelId: string
  ): string | undefined {
    const asset = this.getAsset(manufacturerId, modelId);
    return asset?.cover;
  }

  /**
   * Get a fallback printer image based on motion type.
   * Returns generic CoreXY, Cartesian, or default printer SVG.
   * MotionType enum: Cartesian=0, CoreXY=1, Delta=2, Unknown=99
   */
  getFallbackImageUrl(motionType?: string | number): string {
    // motionType can be string ('CoreXY', 'Cartesian') or enum number
    const type = typeof motionType === 'string' ? motionType.toLowerCase() : motionType;
    
    if (type === 'corexy' || type === 1) {
      return '/assets/printers/generic-corexy.svg';
    }
    if (type === 'cartesian' || type === 0) {
      return '/assets/printers/generic-cartesian.svg';
    }
    // Delta, Unknown, or undefined
    return '/assets/printers/generic-printer.svg';
  }

  /**
   * Get printer cover image URL with fallback based on motion type
   */
  getCoverImageUrlWithFallback(
    manufacturerId?: string,
    modelId?: string,
    motionType?: string | number
  ): string {
    if (manufacturerId && modelId) {
      const coverUrl = this.getCoverImageUrl(manufacturerId, modelId);
      if (coverUrl) return coverUrl;
    }
    return this.getFallbackImageUrl(motionType);
  }

  /**
   * Get bed texture image URL
   */
  getBedTextureUrl(
    manufacturerId: string,
    modelId: string
  ): string | undefined {
    const asset = this.getAsset(manufacturerId, modelId);
    return asset?.bedTexture;
  }

  /**
   * Search printers by name
   */
  searchPrinters(query: string): PrinterAsset[] {
    const lowerQuery = query.toLowerCase();
    return Array.from(this.printerMap.values()).filter((p) =>
      p.name.toLowerCase().includes(lowerQuery)
    );
  }

  /**
   * Get all printers from a manufacturer
   */
  getPrintersByManufacturer(manufacturerId: string): PrinterAsset[] {
    const manufacturer = this.getManufacturer(manufacturerId);
    return manufacturer?.printers || [];
  }
}

// Export singleton instance
export const assetService = new AssetService();
