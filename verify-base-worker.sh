#!/bin/bash
# Deprecated: The generic base slicer worker project (src/slicer-worker) has been removed.
# This script is retained to avoid CI or documentation references failing hard.
# It now prints an informational notice and exits successfully.

echo "[verify-base-worker] Generic slicer-worker project has been removed."
echo "Use engine-specific workers (orcaslicer-worker, prusaslicer-worker) and Dockerfile.slicer-base as the neutral layer."
echo "Nothing to verify. Exiting 0."
exit 0