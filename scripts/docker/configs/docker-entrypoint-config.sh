#!/bin/bash
set -e

# Default API_BASE_URL to the same host as the web app if not specified
API_BASE_URL="${API_BASE_URL:-http://localhost:8080}"

echo "Configuring Blazor client with API_BASE_URL: $API_BASE_URL"

# Update the client configuration
if [ -f "/usr/share/nginx/html/appsettings.json" ]; then
    echo "Updating appsettings.json with API base URL..."
    jq --arg apiUrl "$API_BASE_URL" '.ApiBaseUrl = $apiUrl' /usr/share/nginx/html/appsettings.json > /tmp/appsettings.json
    mv /tmp/appsettings.json /usr/share/nginx/html/appsettings.json
    echo "Updated appsettings.json:"
    cat /usr/share/nginx/html/appsettings.json
fi

# Start nginx
exec nginx -g 'daemon off;'
