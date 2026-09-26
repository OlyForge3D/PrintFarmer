#!/bin/bash
# Builds the standalone AG async-layout repro app for the iOS Simulator (#3067).
# Output: build/AGAsyncRepro.app next to this script (ignored by mobile/.gitignore).
set -euo pipefail

readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly app_dir="${script_dir}/build/AGAsyncRepro.app"

rm -rf "${app_dir}"
mkdir -p "${app_dir}"
cp "${script_dir}/Info.plist" "${app_dir}/Info.plist"
xcrun --sdk iphonesimulator swiftc \
  -parse-as-library -swift-version 5 -Onone -g \
  -target arm64-apple-ios17.0-simulator \
  -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  "${script_dir}/ReproApp.swift" \
  -o "${app_dir}/AGAsyncRepro"
codesign --force --sign - "${app_dir}" >/dev/null
echo "Built ${app_dir}"
