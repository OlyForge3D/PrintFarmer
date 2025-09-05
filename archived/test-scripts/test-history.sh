#!/bin/bash

# Test script to verify history totals API endpoint

echo "Testing Moonraker direct endpoint..."
moonraker_response=$(curl -s "http://10.0.0.80/server/history/totals" 2>&1)
if [ $? -eq 0 ]; then
    echo "Moonraker response: $moonraker_response"
else
    echo "Failed to reach Moonraker: $moonraker_response"
fi

echo ""
echo "Testing application printers endpoint..."
app_response=$(curl -s "http://localhost:5088/api/printers" 2>&1)
if [ $? -eq 0 ]; then
    echo "App response: $app_response"
    # Try jq if available, otherwise fall back to regex for GUID
    if command -v jq >/dev/null 2>&1; then
        printer_id=$(echo "$app_response" | jq -r '.[0].id // empty')
    else
        printer_id=$(echo "$app_response" | grep -oE '"id":"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}"' | head -1 | cut -d':' -f2 | tr -d '"')
    fi
    if [ -n "$printer_id" ]; then
        echo ""
        echo "Testing history totals endpoint for printer $printer_id..."
        totals_response=$(curl -s "http://localhost:5088/api/printers/$printer_id/history/totals" 2>&1)
        echo "History totals response: $totals_response"
    else
        echo "No printers found in response"
    fi
else
    echo "Failed to reach app: $app_response"
fi
