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

      console.log(
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

    // Normalize key (replace spaces with underscores)
    key = key.replace(/\s+/g, "_");

    return this.printerMap.get(key);
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
