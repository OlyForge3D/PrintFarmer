import React, { useState, useCallback } from 'react';
import { assetService } from '@/services/assetService';

/**
 * Printer image component with fallback support.
 * Shows cover image if available, falls back to motion-type-based generic SVG on error.
 */
export interface PrinterImageProps {
  manufacturerName?: string;
  modelName?: string;
  motionType?: number | string;
  alt: string;
  className?: string;
}

export const PrinterImage: React.FC<PrinterImageProps> = ({ 
  manufacturerName, 
  modelName, 
  motionType, 
  alt, 
  className 
}) => {
  const [hasError, setHasError] = useState(false);
  
  const coverUrl = manufacturerName && modelName 
    ? assetService.getCoverImageUrl(manufacturerName, modelName)
    : undefined;
  
  const fallbackUrl = assetService.getFallbackImageUrl(motionType);
  const imageUrl = hasError || !coverUrl ? fallbackUrl : coverUrl;
  
  const handleError = useCallback(() => {
    if (!hasError) {
      setHasError(true);
    }
  }, [hasError]);
  
  return (
    <img 
      src={imageUrl} 
      alt={alt}
      className={className}
      onError={handleError}
    />
  );
};

export default PrinterImage;
