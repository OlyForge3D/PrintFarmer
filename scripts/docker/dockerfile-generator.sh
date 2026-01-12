#!/usr/bin/env bash
# dockerfile-generator.sh
# Utility to copy or merge canonical Dockerfiles from scripts/docker/dockerfiles
# Usage:
#   ./dockerfile-generator.sh --list
#   ./dockerfile-generator.sh --copy <basename> <outpath>
#   ./dockerfile-generator.sh --merge <basename1,basename2> <outpath>

set -euo pipefail
ROOT_DIR=$(cd "$(dirname "$0")/../.." && pwd)
CANON_DIR="$ROOT_DIR/scripts/docker/dockerfiles"

print_help() {
  cat <<'EOF'
Usage: dockerfile-generator.sh [--list] [--copy <basename> <outpath>] [--merge <base1,base2,...> <outpath>]

Options:
  --list                 List available canonical Dockerfiles (by basename)
  --copy <name> <out>    Copy canonical file <name> to <out> (name without path)
  --merge <names> <out>  Merge comma-separated canonical files in order into <out>
  --help                 Show this help

Examples:
  ./dockerfile-generator.sh --list
  ./dockerfile-generator.sh --copy Dockerfile.orcaslicer-binaries ./Dockerfile.orcaslicer-binaries
  ./dockerfile-generator.sh --merge Dockerfile.slicer-base,Dockerfile.orcaslicer-binaries ./Dockerfile.merged
EOF
}

if [ $# -eq 0 ]; then
  print_help
  exit 0
fi

case "$1" in
  --help)
    print_help
    exit 0
    ;;
  --list)
    find "$CANON_DIR" -maxdepth 1 -type f -printf "%f\n" 2>/dev/null || ls "$CANON_DIR" || true
    exit 0
    ;;
  --copy)
    if [ $# -ne 3 ]; then
      echo "--copy requires <basename> and <outpath>" >&2
      exit 2
    fi
    name="$2"
    out="$3"
    src="$CANON_DIR/$name"
    if [ ! -f "$src" ]; then
      echo "Canonical Dockerfile not found: $src" >&2
      exit 3
    fi
    echo "Copying $src -> $out"
    cp "$src" "$out"
    exit 0
    ;;
  --merge)
    if [ $# -ne 3 ]; then
      echo "--merge requires <comma-separated-names> and <outpath>" >&2
      exit 2
    fi
    IFS=',' read -r -a names <<< "$2"
    out="$3"
    echo "Merging ${names[*]} -> $out"
    rm -f "$out" || true
    for n in "${names[@]}"; do
      src="$CANON_DIR/$n"
      if [ ! -f "$src" ]; then
        echo "Canonical Dockerfile not found: $src" >&2
        exit 3
      fi
      echo "# --- Begin $n ---" >> "$out"
      cat "$src" >> "$out"
      echo "# --- End $n ---" >> "$out"
      echo >> "$out"
    done
    echo "Wrote $out"
    exit 0
    ;;
  --generate)
    # --generate <scenario> <outpath>
    if [ $# -ne 3 ]; then
      echo "--generate requires <scenario> and <outpath>" >&2
      exit 2
    fi
    scenario="$2"
    out="$3"

    # Define scenarios -> ordered list of canonical dockerfiles
    declare -A SCENARIOS
    SCENARIOS[orcaslicer-binaries]='Dockerfile.orcaslicer-binaries'
    # slicer-worker scenario: slicer-base then orcaslicer-binaries then orcaslicer (worker)
    SCENARIOS[slicer-worker]='Dockerfile.slicer-base,Dockerfile.orcaslicer-binaries,Dockerfile.orcaslicer'
    SCENARIOS[worker]='Dockerfile.orcaslicer-binaries,Dockerfile.orcaslicer'
    SCENARIOS[all]='Dockerfile.slicer-base,Dockerfile.orcaslicer-binaries,Dockerfile.orcaslicer'

    mapping="${SCENARIOS[$scenario]:-}"
    if [ -z "$mapping" ]; then
      echo "Unknown scenario: $scenario" >&2
      echo "Available scenarios: ${!SCENARIOS[*]}" >&2
      exit 3
    fi

    IFS=',' read -r -a names <<< "$mapping"
    echo "Generating merged Dockerfile for scenario '$scenario' -> $out"
    rm -f "$out" || true
    for n in "${names[@]}"; do
      src="$CANON_DIR/$n"
      if [ ! -f "$src" ]; then
        echo "Canonical Dockerfile not found: $src" >&2
        exit 4
      fi
      echo "# --- Begin $n ---" >> "$out"
      cat "$src" >> "$out"
      echo "# --- End $n ---" >> "$out"
      echo >> "$out"
    done
    echo "Wrote $out"
    exit 0
    ;;
  --generate-config)
    # --generate-config [--architecture <arch>] [--enable-orca-worker <yes|no>] [--include-monitoring <true|false>]
    #                   [--include-telemetry <true|false>] [--include-security <true|false>]
    #                   [--include-registry <true|false>] [--db-provider <name>] --out <outpath>
    shift || true
    # defaults
    ARCHITECTURE="${ARCHITECTURE:-}"
    ENABLE_ORCA_WORKER="no"
    INCLUDE_MONITORING="false"
    INCLUDE_TELEMETRY="false"
    INCLUDE_SECURITY="false"
    INCLUDE_REGISTRY="false"
    DB_PROVIDER=""
    OUT=""

    while [ $# -gt 0 ]; do
      case "$1" in
        --architecture)
          ARCHITECTURE="$2"; shift 2;;
        --enable-orca-worker)
          ENABLE_ORCA_WORKER="$2"; shift 2;;
        --include-monitoring)
          INCLUDE_MONITORING="$2"; shift 2;;
        --include-telemetry)
          INCLUDE_TELEMETRY="$2"; shift 2;;
        --include-security)
          INCLUDE_SECURITY="$2"; shift 2;;
        --include-registry)
          INCLUDE_REGISTRY="$2"; shift 2;;
        --db-provider)
          DB_PROVIDER="$2"; shift 2;;
        --out)
          OUT="$2"; shift 2;;
        *)
          echo "Unknown generate-config option: $1" >&2; exit 2;;
      esac
    done

    if [ -z "$OUT" ]; then
      echo "--out is required for --generate-config" >&2
      exit 2
    fi

    names=()

    # Architecture: prefer multistage if available
    if [ -n "$ARCHITECTURE" ]; then
      if [ -f "$CANON_DIR/Dockerfile.multistage" ]; then
        names+=("Dockerfile.multistage")
      fi
    fi

    # If orca worker enabled, include slicer base and binary layer
    if [ "$ENABLE_ORCA_WORKER" = "yes" ]; then
      [ -f "$CANON_DIR/Dockerfile.slicer-base" ] && names+=("Dockerfile.slicer-base")
      [ -f "$CANON_DIR/Dockerfile.orcaslicer-binaries" ] && names+=("Dockerfile.orcaslicer-binaries")
      [ -f "$CANON_DIR/Dockerfile.orcaslicer" ] && names+=("Dockerfile.orcaslicer")
    fi

    # Additional DB/provider or monitoring-specific Dockerfiles could be included if present
    if [ "$INCLUDE_MONITORING" = "true" ] && [ -f "$CANON_DIR/Dockerfile.monitoring" ]; then
      names+=("Dockerfile.monitoring")
    fi
    if [ "$INCLUDE_TELEMETRY" = "true" ] && [ -f "$CANON_DIR/Dockerfile.telemetry" ]; then
      names+=("Dockerfile.telemetry")
    fi

    if [ ${#names[@]} -eq 0 ]; then
      echo "No canonical Dockerfiles selected for the given configuration; nothing to generate" >&2
      exit 3
    fi

    echo "Generating merged Dockerfile -> $OUT"
    rm -f "$OUT" || true
    for n in "${names[@]}"; do
      src="$CANON_DIR/$n"
      echo "# --- Begin $n ---" >> "$OUT"
      cat "$src" >> "$OUT"
      echo "# --- End $n ---" >> "$OUT"
      echo >> "$OUT"
    done
    echo "Wrote $OUT"
    exit 0
    ;;
  *)
    echo "Unknown option: $1" >&2
    print_help
    exit 2
    ;;
esac
