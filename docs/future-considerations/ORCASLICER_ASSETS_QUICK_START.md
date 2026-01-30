/**
 * QUICK START: Using OrcaSlicer Assets in Your Components
 * 
 * This file contains copy-paste ready examples for common use cases.
 */

// ============================================================================
// EXAMPLE 1: Display Printer Cover Image in Component
// ============================================================================

import { useAssets } from '@/hooks/useAssets';

function PrinterThumbnail({ manufacturerId, modelId, printerName }) {
  const { getCoverImageUrl, isLoaded } = useAssets();
  
  if (!isLoaded) return <div>Loading assets...</div>;
  
  const coverUrl = getCoverImageUrl(manufacturerId, modelId);
  
  return (
    <div className="printer-card">
      {coverUrl ? (
        <img 
          src={coverUrl} 
          alt={printerName}
          className="printer-cover"
        />
      ) : (
        <div className="placeholder">No image available</div>
      )}
      <h3>{printerName}</h3>
    </div>
  );
}


// ============================================================================
// EXAMPLE 2: Use Bed Texture in 3D Model Viewer
// ============================================================================

import { useAssets } from '@/hooks/useAssets';

function PrinterModelViewer({ manufacturerId, modelId, modelUrl }) {
  const { getBedTextureUrl } = useAssets();
  
  const bedTextureUrl = getBedTextureUrl(manufacturerId, modelId);
  
  return (
    <ModelViewer 
      modelUrl={modelUrl}
      bedTextureUrl={bedTextureUrl}
      bedDimensions={{
        width: 256,
        depth: 256,
        height: 10
      }}
    />
  );
}


// ============================================================================
// EXAMPLE 3: Populate Printer Model List with Images
// ============================================================================

import { useQuery } from '@tanstack/react-query';
import { useAssets } from '@/hooks/useAssets';

function PrinterModelSelector() {
  const { data: models } = useQuery({
    queryKey: ['printerModels'],
    queryFn: async () => {
      const res = await fetch('/api/catalog/models');
      return res.json();
    }
  });
  
  const { getCoverImageUrl } = useAssets();
  
  return (
    <div className="model-grid">
      {models?.map(model => {
        const coverUrl = getCoverImageUrl(
          model.manufacturer?.name,
          model.name
        );
        
        return (
          <div key={model.id} className="model-card">
            {coverUrl && <img src={coverUrl} alt={model.name} />}
            <h4>{model.name}</h4>
            <p>{model.manufacturer?.name}</p>
          </div>
        );
      })}
    </div>
  );
}


// ============================================================================
// EXAMPLE 4: Search for Printer Models and Display Results
// ============================================================================

import { useState } from 'react';
import { useAssets } from '@/hooks/useAssets';

function PrinterSearch() {
  const [query, setQuery] = useState('');
  const { searchPrinters, getCoverImageUrl } = useAssets();
  
  const results = query ? searchPrinters(query) : [];
  
  return (
    <div>
      <input
        type="text"
        placeholder="Search printers (e.g., 'ender', 'bambu')..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      
      <div className="results">
        {results.map(printer => (
          <div key={`${printer.id}`} className="result-item">
            {printer.cover && (
              <img src={printer.cover} alt={printer.name} width={100} />
            )}
            <div>
              <h4>{printer.name}</h4>
              {printer.bedTexture && (
                <small>Has bed texture</small>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}


// ============================================================================
// EXAMPLE 5: Direct Service Usage (No Hook)
// ============================================================================

import { assetService } from '@/services/assetService';

async function getAvailableManufacturers() {
  // Initialize if needed
  await assetService.initialize();
  
  // Get all manufacturers
  const manufacturers = assetService.getManufacturers();
  
  return manufacturers.map(m => ({
    id: m.id,
    name: m.name,
    printerCount: m.printers.length
  }));
}

async function getPrintersForManufacturer(manufacturerId) {
  await assetService.initialize();
  return assetService.getPrintersByManufacturer(manufacturerId);
}


// ============================================================================
// EXAMPLE 6: Use Assets via API Endpoints
// ============================================================================

async function getAssetFromAPI(manufacturerId, modelId) {
  // Get complete asset object with URLs
  const response = await fetch(
    `/api/assets/printer/${manufacturerId}/${modelId}`
  );
  
  if (!response.ok) {
    console.error('Asset not found');
    return null;
  }
  
  const asset = await response.json();
  // Result: { id: 'x1', name: 'X1', cover: '/assets/...', bedTexture: '...' }
  return asset;
}

// Get just the cover URL
async function getCoverUrl(manufacturerId, modelId) {
  const response = await fetch(
    `/api/assets/printer/${manufacturerId}/${modelId}/cover`
  );
  return response.json(); // Returns: "/assets/orcaslicer/printers/.../cover.png"
}

// Get all available assets
async function getAllAssets() {
  const response = await fetch('/api/assets/manifest');
  return response.json();
}


// ============================================================================
// EXAMPLE 7: Printer Card Component (Complete)
// ============================================================================

import { useAssets } from '@/hooks/useAssets';

interface PrinterCardProps {
  manufacturerName: string;
  modelName: string;
  specs: {
    bedSizeX: number;
    bedSizeY: number;
    bedSizeZ: number;
    nozzle: number;
  };
}

export function PrinterCard({ 
  manufacturerName, 
  modelName,
  specs 
}: PrinterCardProps) {
  const { getCoverImageUrl, getBedTextureUrl } = useAssets();
  
  const coverUrl = getCoverImageUrl(manufacturerName, modelName);
  const bedUrl = getBedTextureUrl(manufacturerName, modelName);
  
  return (
    <div className="printer-card">
      <div className="header">
        {coverUrl && (
          <img 
            src={coverUrl} 
            alt={modelName}
            className="cover-image"
          />
        )}
        <h3>{modelName}</h3>
        <p className="manufacturer">{manufacturerName}</p>
      </div>
      
      <div className="specs">
        <div className="spec">
          <span>Build Area:</span>
          <span>{specs.bedSizeX} × {specs.bedSizeY} × {specs.bedSizeZ}mm</span>
        </div>
        <div className="spec">
          <span>Nozzle:</span>
          <span>{specs.nozzle}mm</span>
        </div>
        {bedUrl && (
          <div className="spec">
            <span>Bed Texture:</span>
            <span className="badge">Available</span>
          </div>
        )}
      </div>
      
      {bedUrl && (
        <div className="bed-preview">
          <img src={bedUrl} alt="Bed Texture" />
        </div>
      )}
    </div>
  );
}


// ============================================================================
// EXAMPLE 8: Handle Missing Assets Gracefully
// ============================================================================

function SmartPrinterImage({ manufacturerId, modelId }) {
  const { getCoverImageUrl } = useAssets();
  const [imageError, setImageError] = useState(false);
  
  const imageUrl = getCoverImageUrl(manufacturerId, modelId);
  
  return (
    <div className="image-container">
      {imageUrl && !imageError ? (
        <img
          src={imageUrl}
          alt="Printer"
          onError={() => setImageError(true)}
          className="printer-image"
        />
      ) : (
        <div className="placeholder">
          <Icon name="printer" />
          <p>No image available</p>
        </div>
      )}
    </div>
  );
}


// ============================================================================
// STYLING EXAMPLE (Tailwind CSS)
// ============================================================================

export function PrinterGallery() {
  const { getManufacturers } = useAssets();
  
  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
      {getManufacturers().map(manufacturer =>
        manufacturer.printers.map(printer => (
          <div
            key={printer.id}
            className="
              flex flex-col gap-2 p-4 
              border rounded-lg shadow-sm
              hover:shadow-md transition-shadow
              bg-white
            "
          >
            {printer.cover && (
              <img
                src={printer.cover}
                alt={printer.name}
                className="w-full h-32 object-cover rounded"
              />
            )}
            <h4 className="font-medium text-sm">{printer.name}</h4>
            <p className="text-xs text-gray-500">{manufacturer.name}</p>
            {printer.bedTexture && (
              <span className="text-xs bg-blue-100 text-blue-700 px-2 py-1 rounded w-fit">
                Has bed texture
              </span>
            )}
          </div>
        ))
      )}
    </div>
  );
}
