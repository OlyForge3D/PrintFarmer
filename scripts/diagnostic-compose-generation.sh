#!/bin/bash

# diagnostic-compose-generation.sh
# Debug script to trace the compose generation process step-by-step

set -euo pipefail

SCRIPT_DIR="/Users/jpapiez/s/PFarm1/scripts/docker"
TEMPLATES_DIR="$SCRIPT_DIR/compose-templates"
DEBUG_DIR=$(mktemp -d)

cleanup() {
    echo "Debug directory: $DEBUG_DIR"
    echo "Files:"
    ls -lh "$DEBUG_DIR"
}
trap cleanup EXIT

echo "=== DIAGNOSTIC: Compose Generation for host-network + sqlserver + orcaslicer + spoolman ==="
echo ""

# Step 1: Copy template
echo "Step 1: Copying host-network template"
cp "$TEMPLATES_DIR/docker-compose.host-network.yml" "$DEBUG_DIR/01-template.yml"
echo "  Line count: $(wc -l < "$DEBUG_DIR/01-template.yml")"
echo "  Services section lines:"
grep -n "^services:" "$DEBUG_DIR/01-template.yml"
grep -n "^  api:" "$DEBUG_DIR/01-template.yml"

# Step 2: Load database config
echo ""
echo "Step 2: Load database config for sqlserver"
provider="sqlserver"
database_template="$TEMPLATES_DIR/docker-compose.database.${provider}.yml"
echo "  Using template: $database_template"
db_config=$(cat "$database_template")
echo "  Database config (first 20 lines):"
echo "$db_config" | head -20 | sed 's/^/    /'

# Step 3: Run Python replacement
echo ""
echo "Step 3: Python database replacement"
python3 "$SCRIPT_DIR/compose-replace-db.py" "$DEBUG_DIR/01-template.yml" "$db_config" > "$DEBUG_DIR/02-after-db-replacement.yml" 2>&1
echo "  Result line count: $(wc -l < "$DEBUG_DIR/02-after-db-replacement.yml")"
echo "  Services section:"
grep -n "^services:" "$DEBUG_DIR/02-after-db-replacement.yml" | head -3
echo "  api service line:"
grep -n "^  api:" "$DEBUG_DIR/02-after-db-replacement.yml" | head -3
echo "  volumes section:"
grep -n "^volumes:" "$DEBUG_DIR/02-after-db-replacement.yml" | head -3

# Check structure
echo ""
echo "  Structure check:"
echo "    First 30 lines:"
head -30 "$DEBUG_DIR/02-after-db-replacement.yml" | cat -n | tail -10
echo ""
echo "    Lines 20-35:"
sed -n '20,35p' "$DEBUG_DIR/02-after-db-replacement.yml" | cat -n

# Step 4: Run dedupe
echo ""
echo "Step 4: Running compose-dedupe"
python3 "$SCRIPT_DIR/compose-dedupe.py" < "$DEBUG_DIR/02-after-db-replacement.yml" > "$DEBUG_DIR/03-after-dedupe.yml" 2>&1
echo "  Result line count: $(wc -l < "$DEBUG_DIR/03-after-dedupe.yml")"
diff -q "$DEBUG_DIR/02-after-db-replacement.yml" "$DEBUG_DIR/03-after-dedupe.yml" && echo "  No changes from dedupe" || echo "  Changes made by dedupe"

# Step 5: Check final structure
echo ""
echo "Step 5: Final structure validation"
echo "  Services section:"
grep -n "^services:" "$DEBUG_DIR/03-after-dedupe.yml" | head -3
echo "  api service:"
grep -n "^  api:" "$DEBUG_DIR/03-after-dedupe.yml" | head -3
echo "  volumes section:"
grep -n "^volumes:" "$DEBUG_DIR/03-after-dedupe.yml" | head -3

echo ""
echo "  First 35 lines (showing corruption area if present):"
head -35 "$DEBUG_DIR/03-after-dedupe.yml" | cat -n | tail -16

echo ""
echo "=== END DIAGNOSTIC ==="
