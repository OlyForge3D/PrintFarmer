#!/bin/bash
#
# extract-orcaslicer-assets.sh
# Extracts printer cover images and bed textures from OrcaSlicer installation
# and organizes them into the React app's public assets folder.
#
# Usage: ./scripts/extract-orcaslicer-assets.sh
#

set -euo pipefail

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Paths
ORCA_RESOURCES="/Applications/OrcaSlicer.app/Contents/Resources/profiles"
REACT_ASSETS="src/Web/ReactApp/public/assets/orcaslicer"
MANIFEST="${REACT_ASSETS}/manifest.json"

echo -e "${BLUE}═══════════════════════════════════════════════════${NC}"
echo -e "${BLUE}OrcaSlicer Asset Extraction Tool${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════${NC}"

# Verify OrcaSlicer installation exists
if [ ! -d "$ORCA_RESOURCES" ]; then
    echo -e "${RED}✗ Error: OrcaSlicer not found at ${ORCA_RESOURCES}${NC}"
    echo -e "${YELLOW}Please install OrcaSlicer from https://github.com/SoftFever/OrcaSlicer${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Found OrcaSlicer resources${NC}"

# Create asset directory structure
mkdir -p "${REACT_ASSETS}/printers"

echo -e "${BLUE}Starting asset extraction...${NC}"

# Initialize manifest
manifest_json="{"
manifest_json+=$'\n'"  \"manufacturers\": ["

first_manufacturer=true
cover_count=0
texture_count=0

# Iterate through manufacturers
for manufacturer_dir in "${ORCA_RESOURCES}"/*; do
    if [ ! -d "$manufacturer_dir" ]; then
        continue
    fi
    
    manufacturer_name=$(basename "$manufacturer_dir")
    manufacturer_id=$(echo "$manufacturer_name" | tr ' ' '_' | tr '[:upper:]' '[:lower:]')
    
    # Create manufacturer directory
    mfg_dir="${REACT_ASSETS}/printers/${manufacturer_id}"
    mkdir -p "$mfg_dir"
    
    # Find printer models for this manufacturer
    cover_images=()
    texture_images=()
    
    for file in "$manufacturer_dir"/*_cover.png; do
        if [ -f "$file" ]; then
            cover_images+=("$file")
        fi
    done
    
    for file in "$manufacturer_dir"/*_buildplate_texture.png "$manufacturer_dir"/*_texture.png; do
        if [ -f "$file" ] 2>/dev/null; then
            texture_images+=("$file")
        fi
    done
    
    if [ ${#cover_images[@]} -eq 0 ]; then
        continue
    fi
    
    # Add manufacturer to manifest
    if [ "$first_manufacturer" = false ]; then
        manifest_json+=$'\n'"    },"
    fi
    first_manufacturer=false
    
    manifest_json+=$'\n'"    {"
    manifest_json+=$'\n'"      \"id\": \"${manufacturer_id}\","
    manifest_json+=$'\n'"      \"name\": \"${manufacturer_name}\","
    manifest_json+=$'\n'"      \"printers\": ["
    
    first_printer=true
    
    # Process each printer model
    for cover_file in "${cover_images[@]}"; do
        filename=$(basename "$cover_file")
        # Extract model name: "ModelName_cover.png" -> "ModelName"
        model_name="${filename%_cover.png}"
        model_id=$(echo "$model_name" | tr ' ' '_' | tr '[:upper:]' '[:lower:]')
        model_dir="${mfg_dir}/${model_id}"
        mkdir -p "$model_dir"
        
        # Copy cover image
        cp "$cover_file" "${model_dir}/cover.png"
        cover_count=$((cover_count + 1))
        
        echo -e "${YELLOW}  Extracted${NC}: ${manufacturer_name} - ${model_name}"
        
        # Look for matching texture
        texture_file=""
        for possible_texture in "$manufacturer_dir"/${model_name}_buildplate_texture.png \
                              "$manufacturer_dir"/${model_name}_buildplate_texture.svg \
                              "$manufacturer_dir"/${model_name}_texture.png \
                              "$manufacturer_dir"/${model_name}_texture.svg; do
            if [ -f "$possible_texture" ]; then
                texture_file="$possible_texture"
                break
            fi
        done
        
        # Copy texture if found
        texture_url=""
        if [ -n "$texture_file" ]; then
            texture_basename=$(basename "$texture_file")
            texture_ext="${texture_basename##*.}"
            cp "$texture_file" "${model_dir}/bed-texture.${texture_ext}"
            texture_url="\"bedTexture\": \"/assets/orcaslicer/printers/${manufacturer_id}/${model_id}/bed-texture.${texture_ext}\""
            texture_count=$((texture_count + 1))
        fi
        
        # Add printer to manifest
        if [ "$first_printer" = false ]; then
            manifest_json+=$'\n'"        },"
        fi
        first_printer=false
        
        manifest_json+=$'\n'"        {"
        manifest_json+=$'\n'"          \"id\": \"${model_id}\","
        manifest_json+=$'\n'"          \"name\": \"${model_name}\","
        manifest_json+=$'\n'"          \"cover\": \"/assets/orcaslicer/printers/${manufacturer_id}/${model_id}/cover.png\""
        
        if [ -n "$texture_url" ]; then
            manifest_json+=$'\n'"          ,${texture_url}"
        fi
    done
    
    manifest_json+=$'\n'"        }"
    manifest_json+=$'\n'"      ]"
done

# Close manifest JSON
manifest_json+=$'\n'"    }"
manifest_json+=$'\n'"  ]"
manifest_json+=$'\n'"}"

# Write manifest file
echo "$manifest_json" > "$MANIFEST"

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════════${NC}"
echo -e "${GREEN}✓ Asset extraction complete!${NC}"
echo -e "${GREEN}═══════════════════════════════════════════════════${NC}"
echo ""
echo -e "${BLUE}Summary:${NC}"
echo -e "  ${GREEN}Printer Covers:${NC}    $cover_count images"
echo -e "  ${GREEN}Bed Textures:${NC}     $texture_count images"
echo -e "  ${GREEN}Total Assets:${NC}     $((cover_count + texture_count)) files"
echo -e "  ${GREEN}Output Directory:${NC} ${REACT_ASSETS}"
echo -e "  ${GREEN}Manifest:${NC}         ${MANIFEST}"
echo ""
echo -e "${YELLOW}Next steps:${NC}"
echo "  1. Commit the extracted assets: git add public/assets/orcaslicer/"
echo "  2. Update PrinterModel entity to add CoverImageUrl and BedTextureUrl"
echo "  3. Create AssetResolverService to map printer models to assets"
echo "  4. Update 3D viewer to display bed textures"
echo ""
