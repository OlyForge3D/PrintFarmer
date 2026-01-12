#!/usr/bin/env bash
set -euo pipefail
# Helper script to run tests with telemetry disabled (uses runsettings placed next to this script)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNSETTINGS="$SCRIPT_DIR/test.disable-telemetry.runsettings"
PROJECT_PATH="$SCRIPT_DIR/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj"

if [ ! -f "$RUNSETTINGS" ]; then
  echo "Runsettings file not found: $RUNSETTINGS" >&2
  exit 2
fi
if [ ! -f "$PROJECT_PATH" ]; then
  echo "Test project not found: $PROJECT_PATH" >&2
  exit 2
fi

echo "Running tests for project: $PROJECT_PATH"
echo "Using runsettings: $RUNSETTINGS"

dotnet test "$PROJECT_PATH" --settings "$RUNSETTINGS" "$@"
