#!/bin/bash
# Batch render thumbnails for all supported model files in a directory tree using ThumbnailCli.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CLI_PROJECT="${REPO_ROOT}/src/tools/ThumbnailCli/ThumbnailCli.csproj"

CONFIGURATION="Release"
BUILD=true
INPUT_DIR=""
OUTPUT_DIR=""
PRESET="orca"
WIDTH=1024
HEIGHT=1024
ENABLE_SHADOW=1
TWO_SIDED=1
ENABLE_PLATE=0
ENABLE_AO=1
SUPPORTED_EXTS=("stl" "3mf" "obj" "ply" "amf")

usage() {
    local script_name
    script_name="$(basename "$0")"
    cat <<EOF
Usage: ${script_name} -i <input-dir> -o <output-dir> [options]

Options:
  -i, --input-dir PATH       Directory containing model files (required)
  -o, --output-dir PATH      Directory where PNGs will be written (required)
  -p, --preset NAME          Renderer preset: orca | prusa (default: orca)
  -w, --width INT            Output width in pixels (default: 1024)
  -H, --height INT           Output height in pixels (default: 1024)
      --config CONFIG        Build configuration for ThumbnailCli (default: Release)
      --skip-build           Skip building ThumbnailCli before rendering
      --no-shadow            Disable ground shadow
      --single-sided         Disable two-sided rendering (cull backfaces)
      --no-plate             Disable build plate drawing
      --no-ao                Disable ambient occlusion
  -h, --help                 Show this help

Notes:
- The script builds ThumbnailCli once, then calls it with --no-build for each file.
- The input directory tree is mirrored under the output directory with .png outputs.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -i|--input-dir)
            INPUT_DIR="$2"; shift 2 ;;
        -o|--output-dir)
            OUTPUT_DIR="$2"; shift 2 ;;
        -p|--preset)
            PRESET="$2"; shift 2 ;;
        -w|--width)
            WIDTH="$2"; shift 2 ;;
        -H|--height)
            HEIGHT="$2"; shift 2 ;;
        --config)
            CONFIGURATION="$2"; shift 2 ;;
        --skip-build)
            BUILD=false; shift 1 ;;
        --no-shadow)
            ENABLE_SHADOW=0; shift 1 ;;
        --single-sided)
            TWO_SIDED=0; shift 1 ;;
        --no-plate)
            ENABLE_PLATE=0; shift 1 ;;
        --no-ao)
            ENABLE_AO=0; shift 1 ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            exit 1 ;;
    esac
done

if [[ -z "$INPUT_DIR" || -z "$OUTPUT_DIR" ]]; then
    echo "Error: --input-dir and --output-dir are required." >&2
    usage
    exit 1
fi

if [[ ! -d "$INPUT_DIR" ]]; then
    echo "Error: input directory not found: $INPUT_DIR" >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Error: dotnet SDK is required." >&2
    exit 1
fi

mkdir -p "$OUTPUT_DIR"

if [[ "$BUILD" == true ]]; then
    dotnet build "$CLI_PROJECT" -c "$CONFIGURATION"
fi

find_expr=(-type f \()
for ext in "${SUPPORTED_EXTS[@]}"; do
    find_expr+=(-iname "*.${ext}" -o)
done
# Remove trailing -o
unset 'find_expr[${#find_expr[@]}-1]'
find_expr+=(\))

mapfile -t files < <(find "$INPUT_DIR" "${find_expr[@]}" -print | sort)

if [[ ${#files[@]} -eq 0 ]]; then
    echo "No supported model files found under $INPUT_DIR" >&2
    exit 1
fi

echo "Rendering ${#files[@]} file(s) from $INPUT_DIR to $OUTPUT_DIR using preset '$PRESET'..."

rendered=0
for src in "${files[@]}"; do
    rel="${src#$INPUT_DIR}"
    rel="${rel#/}"
    base_no_ext="${rel%.*}"
    out_dir="${OUTPUT_DIR}/$(dirname "$rel")"
    out_file="${out_dir}/$(basename "$base_no_ext").png"
    mkdir -p "$out_dir"

    cmd=(dotnet run --project "$CLI_PROJECT" --configuration "$CONFIGURATION" --no-build -- --input "$src" --output "$out_file" --preset "$PRESET" --width "$WIDTH" --height "$HEIGHT")
    [[ $ENABLE_SHADOW -eq 0 ]] && cmd+=(--no-shadow)
    [[ $TWO_SIDED -eq 0 ]] && cmd+=(--single-sided)
    [[ $ENABLE_PLATE -eq 0 ]] && cmd+=(--no-plate)
    [[ $ENABLE_AO -eq 0 ]] && cmd+=(--no-ao)

    echo "- ${rel} -> ${out_file#$OUTPUT_DIR/}"
    "${cmd[@]}"
    rendered=$((rendered + 1))
done

echo "Done. Rendered ${rendered} file(s) to $OUTPUT_DIR."
