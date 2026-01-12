# OrcaSlicer Assets - Quick Start & Reference

## Quick Start

### 1. Restore Assets from OrcaSlicer
```bash
cd /Users/jpapiez/s/PFarm1
node scripts/restore-orcaslicer-assets.js
```

### 2. Verify Assets Are Populated
```bash
ls -lh src/Web/ReactApp/public/assets/orcaslicer/
# Should show: manifest.json + 56 manufacturer folders (65 MB total)
```

### 3. Build & Run
```bash
# Development
cd src/Web/ReactApp
npm run dev  # http://localhost:3000

# Production
npm run build
```

---

## Setup & Maintenance

### One-Time Setup (First Run)

If you're setting up OrcaSlicer assets for the first time, use the extraction script:

```bash
# Generate initial manifest and extract assets from your OrcaSlicer installation
./scripts/extract-orcaslicer-assets.sh
```

This script:
1. Reads from your OrcaSlicer installation (`/Applications/OrcaSlicer.app/Contents/Resources/profiles`)
2. Generates `manifest.json` from scratch
3. Extracts and organizes all printer covers and bed textures
4. Creates the asset directory structure in `src/Web/ReactApp/public/assets/orcaslicer/`

**Output**: You'll have the initial manifest + assets ready to commit to the repo.

### Ongoing Maintenance (Restore Script)

After the initial setup, use the restoration script to keep assets in sync:

```bash
# Update assets when OrcaSlicer updates or after git pull
node scripts/restore-orcaslicer-assets.js
```

This script:
1. **Requires** the manifest to already exist (it's committed to the repo)
2. Reads your local OrcaSlicer installation
3. Copies bed models, bed textures, and cover images to asset directories
4. Updates manifest with correct asset URLs
5. Updates statistics (printer counts, asset coverage percentages)

**When to run this**:
- After updating OrcaSlicer to a newer version
- After `git pull` if assets appear missing
- When you want to re-sync your local assets with the repo

### Script Usage Comparison

| Task | Script | Purpose |
|------|--------|---------|
| First-time asset extraction | `./scripts/extract-orcaslicer-assets.sh` | Generate initial manifest + assets from scratch |
| Sync after OrcaSlicer updates | `node scripts/restore-orcaslicer-assets.js` | Update assets, keep existing manifest structure |
| Requires manifest to exist | No | Yes (must be committed to repo) |
| Modifies directory structure | Yes (creates new) | No (uses existing structure) |
| Updates statistics | Yes (initial) | Yes (every run) |

### Committing to Repository

Both scripts and generated assets should be committed:

```bash
# Commit both scripts and generated assets
git add scripts/extract-orcaslicer-assets.sh
git add scripts/restore-orcaslicer-assets.js
git add src/Web/ReactApp/public/assets/orcaslicer/

git commit -m "feat: add orcaslicer asset extraction, restoration scripts, and generated assets"
```

**Why both scripts matter**:
- **extract script**: Enables initial setup (useful for documentation, recovery from scratch)
- **restore script**: Essential maintenance tool for developers (keep assets updated)
- **manifest.json**: Must be committed so restore script has a base to work from

---

## File Organization Reference

### What Gets Restored
From OrcaSlicer to `src/Web/ReactApp/public/assets/orcaslicer/{manufacturer}/`:

| OrcaSlicer File | Restored As | Purpose |
|---|---|---|
| `{PrinterName}_cover.png` | `{printerId}_cover.png` | UI gallery thumbnails |
| Bed STL (from JSON `bed_model` field) | `{printerId}_bed.stl` | 3D visualization in ModelViewer |
| Bed texture (from JSON `bed_texture` field) | `{printerId}_texture.png/.svg` | Surface texture in preview |

### Asset Naming Scheme
- **ID Format**: Uses `name` from OrcaSlicer (e.g., "Creality CR-10 Max")
- **Directory**: Uses manufacturer ID (e.g., "creality")
- **Pattern**: `{printerId}_{asset_type}{extension}`

**Examples**:
- `Creality CR-10 Max_bed.stl`
- `Creality CR-10 Max_texture.png`
- `Creality CR-10 Max_cover.png`

---

## Using Assets in React

### Hook Pattern (Recommended)
```typescript
import { useAssets } from '@/hooks/useAssets';

function MyComponent() {
  const { getBedModel, getBedTexture, getPrinterCover } = useAssets();
  
  const bedModelUrl = getBedModel("Creality CR-10 Max");
  const textureUrl = getBedTexture("Creality CR-10 Max");
  const coverUrl = getPrinterCover("Creality CR-10 Max");
  
  return (
    <div>
      <img src={coverUrl} />
      <ModelViewer3D modelUrl={bedModelUrl} textureUrl={textureUrl} />
    </div>
  );
}
```

### Direct URL Pattern
```typescript
// URLs follow pattern: /assets/orcaslicer/{manufacturer}/{printerId}_type.ext
const bedModelUrl = `/assets/orcaslicer/creality/Creality CR-10 Max_bed.stl`;
const textureUrl = `/assets/orcaslicer/creality/Creality CR-10 Max_texture.png`;
const coverUrl = `/assets/orcaslicer/creality/Creality CR-10 Max_cover.png`;
```

---

## Manifest Fields Reference

### Printer Entry Structure
```json
{
  "id": "Creality CR-10 Max",
  "name": "Creality CR-10 Max",
  "nozzleDiameters": ["0.4"],
  "bedModel": "/assets/orcaslicer/creality/Creality CR-10 Max_bed.stl",
  "bedTexture": "/assets/orcaslicer/creality/Creality CR-10 Max_texture.png",
  "bedTextureFormat": "png",
  "cover": "/assets/orcaslicer/creality/Creality CR-10 Max_cover.png"
}
```

### Field Explanations

| Field | Type | Optional | Description |
|---|---|---|---|
| `id` | string | No | Unique printer ID (same as name currently) |
| `name` | string | No | Human-readable printer name |
| `nozzleDiameters` | string[] | No | Available nozzle sizes for this model |
| `bedModel` | string | Yes | URL to 3D bed model (STL) |
| `bedTexture` | string | Yes | URL to bed surface texture (PNG/SVG) |
| `bedTextureFormat` | string | Yes | File format of texture ("png" or "svg") |
| `cover` | string | Yes | URL to printer cover image (PNG) |

### Statistics
```json
{
  "totalManufacturers": 56,
  "totalPrinters": 355,
  "printersWithBedModel": 288,
  "printersWithBedTexture": 252
}
```

---

## Coverage Analysis

### By Manufacturer
Top manufacturers by asset availability:

| Manufacturer | Printers | Models | Textures | Coverage |
|---|---|---|---|---|
| Creality | 45 | 45 | 45 | 100% |
| Anycubic | 18 | 18 | 18 | 100% |
| Bambu Lab (BBL) | 8 | 8 | 8 | 100% |
| Prusa | 10 | 10 | 10 | 100% |
| Voron | 5 | 5 | 5 | 100% |
| *Others* | 269 | 197 | 166 | 56% |

**Total Coverage**: 288/355 bed models (81%), 252/355 textures (71%)

### Missing Assets
- **67 printers** lack bed models (likely resin/SLA printers or legacy models)
- **103 printers** lack bed textures (design choice in OrcaSlicer)
- All printers should have cover images where available

---

## API Integration

### AssetService (C#)
```csharp
public interface IAssetService {
  string? GetBedModelUrl(string printerId);
  string? GetBedTextureUrl(string printerId);
  string? GetPrinterCoverUrl(string printerId);
  Manifest GetManifest();
}
```

### AssetsController (REST)
```csharp
[ApiController]
[Route("api/[controller]")]
public class AssetsController : ControllerBase {
  [HttpGet("manifest")]
  public ActionResult<Manifest> GetManifest()
  
  [HttpGet("bed-model/{printerId}")]
  public ActionResult<string> GetBedModelUrl(string printerId)
  
  [HttpGet("bed-texture/{printerId}")]
  public ActionResult<string> GetBedTextureUrl(string printerId)
  
  [HttpGet("cover/{printerId}")]
  public ActionResult<string> GetCoverUrl(string printerId)
}
```

---

## Troubleshooting Quick Reference

| Issue | Solution |
|---|---|
| Assets showing 404 in browser | Check `public/assets/orcaslicer/` directory exists and has files |
| Manifest not loading | Verify `manifest.json` is valid JSON (`jq . public/assets/orcaslicer/manifest.json`) |
| Bed model not rendering | Check `bedModel` field in manifest exists and URL is correct |
| Texture not applying | Verify `bedTextureFormat` matches file type (png/svg) |
| Build fails with asset errors | Run `npm run build` to see specific file errors |
| Assets missing after git pull | Run restoration script: `node scripts/restore-orcaslicer-assets.js` |

---

## Development Commands

### Asset Management
```bash
# Restore/update assets from OrcaSlicer
node scripts/restore-orcaslicer-assets.js

# Check manifest validity
jq . src/Web/ReactApp/public/assets/orcaslicer/manifest.json

# Count asset files
find src/Web/ReactApp/public/assets/orcaslicer -type f | wc -l

# Check asset directory size
du -sh src/Web/ReactApp/public/assets/orcaslicer/

# List manufacturers
ls src/Web/ReactApp/public/assets/orcaslicer/ | grep -v manifest
```

### Build Verification
```bash
# React production build
cd src/Web/ReactApp && npm run build

# API build
cd src && dotnet build ./api/Farm.Web.Api.csproj -c Debug

# Full solution
cd src && dotnet build ./farm-web.sln -c Debug
```

---

## Git Management

### Committing Assets
```bash
# Stage asset files and script
git add scripts/restore-orcaslicer-assets.js
git add src/Web/ReactApp/public/assets/orcaslicer/

# Large files may need LFS if exceeding limits
git lfs install  # if needed

git commit -m "feat: add orcaslicer bed assets and restoration script"
```

### .gitignore Considerations
Asset files are relatively small (~65 MB total) and should be committed to repo for consistency. No special gitignore rules needed.

---

## Performance Notes

- **Asset Load Time**: ~2-3s for all 859 files (cached after first load)
- **Network Impact**: 65 MB for production builds (only loaded once)
- **Memory Usage**: Manifest in memory is ~2 MB JSON
- **STL Models**: Average 50-200 KB per model (vertex-heavy)
- **Textures**: Average 10-50 KB per texture (highly compressible)

### Optimization Opportunities
- Enable gzip compression for static assets
- Implement lazy-loading for bed models on demand
- Use WebP format for textures where applicable
- Consider model decimation for web rendering
