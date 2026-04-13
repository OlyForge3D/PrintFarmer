#!/usr/bin/env node

/**
 * Restore OrcaSlicer Assets
 * 
 * This script copies printer assets (bed models, bed textures, and cover images)
 * from an installed OrcaSlicer.app application to the PrintFarmer asset directory.
 * 
 * Assets are organized as: /assets/orcaslicer/{manufacturer_id}/{printer_id}_asset.ext
 * 
 * Usage:
 *   node scripts/restore-orcaslicer-assets.js
 * 
 * The script will:
 * 1. Read manufacturer profiles from OrcaSlicer.app
 * 2. Parse manufacturer JSON files for machine_model_list
 * 3. For each printer model, read the spec JSON to get bed_model and bed_texture paths
 * 4. Copy assets to /src/Web/ReactApp/public/assets/orcaslicer/{manufacturer}/
 * 5. Update manifest.json with asset URLs and statistics
 */

const fs = require('fs');
const path = require('path');

// Configuration
const ORCA_PATH = '/Applications/OrcaSlicer.app/Contents/Resources/profiles';
const ASSET_BASE = path.resolve(__dirname, '../src/Web/ReactApp/public/assets/orcaslicer');
const MANIFEST_PATH = path.join(ASSET_BASE, 'manifest.json');

// Validate OrcaSlicer installation
if (!fs.existsSync(ORCA_PATH)) {
  console.error(`❌ OrcaSlicer not found at ${ORCA_PATH}`);
  console.error('Please install OrcaSlicer.app to /Applications/');
  process.exit(1);
}

// Validate manifest exists
if (!fs.existsSync(MANIFEST_PATH)) {
  console.error(`❌ Manifest not found at ${MANIFEST_PATH}`);
  process.exit(1);
}

// Load manifest
const manifest = JSON.parse(fs.readFileSync(MANIFEST_PATH, 'utf8'));

// Statistics tracking
let stats = { beds: 0, textures: 0, covers: 0, failed: 0 };

/**
 * Copy a file with directory creation
 */
function copyFile(src, dest) {
  try {
    const destDir = path.dirname(dest);
    if (!fs.existsSync(destDir)) {
      fs.mkdirSync(destDir, { recursive: true });
    }
    fs.copyFileSync(src, dest);
    return true;
  } catch (e) {
    console.error(`  ❌ Failed to copy ${path.basename(src)}: ${e.message}`);
    stats.failed++;
    return false;
  }
}

/**
 * Main restoration logic
 */
function restoreAssets() {
  // Get manufacturer directories
  const manufacturerDirs = fs.readdirSync(ORCA_PATH).filter(f => {
    const fullPath = path.join(ORCA_PATH, f);
    try {
      return fs.statSync(fullPath).isDirectory();
    } catch {
      return false;
    }
  });

  console.log(`Found ${manufacturerDirs.length} manufacturer directories\n`);

  manufacturerDirs.forEach(mfrDir => {
    const mfrPath = path.join(ORCA_PATH, mfrDir);
    
    // Find manufacturer JSON file
    const possibleJsons = fs.readdirSync(ORCA_PATH).filter(f => 
      f.toLowerCase() === mfrDir.toLowerCase() + '.json'
    );
    
    if (possibleJsons.length === 0) {
      return;
    }

    const mfrJsonPath = path.join(ORCA_PATH, possibleJsons[0]);

    try {
      const mfrJson = JSON.parse(fs.readFileSync(mfrJsonPath, 'utf8'));
      
      if (!mfrJson.machine_model_list || !Array.isArray(mfrJson.machine_model_list)) {
        return;
      }

      // Find corresponding manifest entry, or create one
      let mfrEntry = manifest.manufacturers.find(m => 
        m.name.toLowerCase() === mfrDir.toLowerCase()
      );

      if (!mfrEntry) {
        // Create new manufacturer entry
        const mfrId = mfrDir.toLowerCase().replace(/\s+/g, '-');
        mfrEntry = { id: mfrId, name: mfrDir, printers: [] };
        manifest.manufacturers.push(mfrEntry);
        console.log(`  ➕ Created manifest entry for ${mfrDir}`);
      }

      // Create manufacturer asset directory
      const mfrAssetDir = path.join(ASSET_BASE, mfrEntry.id);
      if (!fs.existsSync(mfrAssetDir)) {
        fs.mkdirSync(mfrAssetDir, { recursive: true });
      }

      // Process each printer model
      mfrJson.machine_model_list.forEach(modelEntry => {
        const printerName = modelEntry.name;

        // Find corresponding printer in manifest
        let printerEntry = mfrEntry.printers.find(p => 
          p.name === printerName
        );

        if (!printerEntry) {
          // Create new printer entry
          const printerId = printerName.toLowerCase().replace(/\s+/g, '_');
          printerEntry = { id: printerId, name: printerName };
          mfrEntry.printers.push(printerEntry);
        }

        // Get printer spec JSON path
        const modelJsonPath = path.join(mfrPath, modelEntry.sub_path);

        if (!fs.existsSync(modelJsonPath)) {
          return;
        }

        try {
          const modelJson = JSON.parse(fs.readFileSync(modelJsonPath, 'utf8'));
          const printerId = printerEntry.id;

          // Copy bed model
          if (modelJson.bed_model) {
            const bedModelSrc = path.join(mfrPath, modelJson.bed_model);
            if (fs.existsSync(bedModelSrc)) {
              const destName = `${printerId}_bed.stl`;
              const dest = path.join(mfrAssetDir, destName);
              if (copyFile(bedModelSrc, dest)) {
                printerEntry.bedModel = `/assets/orcaslicer/${mfrEntry.id}/${destName}`;
                stats.beds++;
              }
            }
          }

          // Copy bed texture
          if (modelJson.bed_texture) {
            const bedTextureSrc = path.join(mfrPath, modelJson.bed_texture);
            if (fs.existsSync(bedTextureSrc)) {
              const ext = path.extname(modelJson.bed_texture);
              const destName = `${printerId}_texture${ext}`;
              const dest = path.join(mfrAssetDir, destName);
              if (copyFile(bedTextureSrc, dest)) {
                printerEntry.bedTexture = `/assets/orcaslicer/${mfrEntry.id}/${destName}`;
                printerEntry.bedTextureFormat = ext === '.svg' ? 'svg' : 'png';
                stats.textures++;
              }
            }
          }

          // Copy cover image
          const coverName = `${printerName}_cover.png`;
          const coverSrc = path.join(mfrPath, coverName);
          if (fs.existsSync(coverSrc)) {
            const destName = `${printerId}_cover.png`;
            const dest = path.join(mfrAssetDir, destName);
            if (copyFile(coverSrc, dest)) {
              printerEntry.cover = `/assets/orcaslicer/${mfrEntry.id}/${destName}`;
              stats.covers++;
            }
          }
        } catch (e) {
          console.error(`  ❌ Failed to parse model JSON for ${printerName}: ${e.message}`);
        }
      });
    } catch (e) {
      console.error(`❌ Failed to parse ${mfrJsonPath}: ${e.message}`);
    }
  });

  // Deduplicate printers by name (remove kebab-case duplicates)
  manifest.manufacturers = manifest.manufacturers.map(mfr => {
    const seen = new Set();
    const uniquePrinters = [];
    
    mfr.printers.forEach(printer => {
      if (!seen.has(printer.name)) {
        seen.add(printer.name);
        uniquePrinters.push(printer);
      }
    });
    
    return { ...mfr, printers: uniquePrinters };
  });

  // Update manifest statistics
  if (!manifest.statistics) manifest.statistics = {};
  manifest.statistics.totalPrinters = manifest.manufacturers.reduce((sum, m) => sum + m.printers.length, 0);
  manifest.statistics.printersWithBedModel = manifest.manufacturers.reduce((sum, mfr) =>
    sum + mfr.printers.filter(p => p.bedModel).length, 0);
  manifest.statistics.printersWithBedTexture = manifest.manufacturers.reduce((sum, mfr) =>
    sum + mfr.printers.filter(p => p.bedTexture).length, 0);

  // Write updated manifest
  fs.writeFileSync(MANIFEST_PATH, JSON.stringify(manifest, null, 2));

  // Report results
  console.log(`\n✅ Asset Restoration Complete:\n`);
  console.log(`  Bed models copied: ${stats.beds}`);
  console.log(`  Bed textures copied: ${stats.textures}`);
  console.log(`  Cover images copied: ${stats.covers}`);
  if (stats.failed > 0) {
    console.log(`  Failed: ${stats.failed}`);
  }
  console.log(`\n📊 Updated statistics:`);
  console.log(`  Printers with bed models: ${manifest.statistics.printersWithBedModel}`);
  console.log(`  Printers with bed textures: ${manifest.statistics.printersWithBedTexture}`);
  console.log(`\n📂 Asset directory: ${ASSET_BASE}`);
  console.log(`📝 Manifest: ${MANIFEST_PATH}`);
}

// Run restoration
restoreAssets();
