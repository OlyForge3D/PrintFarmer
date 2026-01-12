import { useEffect, useState } from "react";
import { assetService } from "@/services/assetService";

/**
 * Hook to use the asset service
 * Initializes on first use and provides access to printer assets
 */
export const useAssets = () => {
  const [isLoaded, setIsLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const initializeAssets = async () => {
      try {
        await assetService.initialize();
        setIsLoaded(true);
      } catch (err) {
        const message =
          err instanceof Error ? err.message : "Failed to load assets";
        setError(message);
        setIsLoaded(true); // Still mark as loaded to prevent infinite retries
      }
    };

    if (!isLoaded) {
      initializeAssets();
    }
  }, [isLoaded]);

  return {
    isLoaded,
    error,
    manifest: assetService.getManifest(),
    getManufacturer: assetService.getManufacturer.bind(assetService),
    getAsset: assetService.getAsset.bind(assetService),
    getCoverImageUrl: assetService.getCoverImageUrl.bind(assetService),
    getBedTextureUrl: assetService.getBedTextureUrl.bind(assetService),
    searchPrinters: assetService.searchPrinters.bind(assetService),
    getPrintersByManufacturer:
      assetService.getPrintersByManufacturer.bind(assetService),
  };
};
