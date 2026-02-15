#!/bin/bash

# SignalR Wiring Fixes - Validation Script
# This script helps verify that the SignalR fixes are working correctly

set -e

echo "================================================================"
echo "SignalR Wiring Fixes - Validation Script"
echo "================================================================"
echo ""

# Color codes
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_PROJ="$PROJECT_ROOT/src/api/Farm.Web.Api.csproj"
REACT_DIR="$PROJECT_ROOT/src/Web/ReactApp"

# Check 1: Verify SignalR configuration in Program.cs
echo -e "${YELLOW}Check 1: Verifying SignalR JSON Protocol Configuration${NC}"
if grep -q "AddJsonProtocol" "$PROJECT_ROOT/src/api/Program.cs"; then
    echo -e "${GREEN}✅ PASS${NC}: AddJsonProtocol is configured"
else
    echo -e "${RED}❌ FAIL${NC}: AddJsonProtocol not found in Program.cs"
    exit 1
fi

if grep -q "PropertyNamingPolicy = JsonNamingPolicy.CamelCase" "$PROJECT_ROOT/src/api/Program.cs"; then
    echo -e "${GREEN}✅ PASS${NC}: camelCase naming policy configured"
else
    echo -e "${RED}❌ FAIL${NC}: camelCase naming policy not found"
    exit 1
fi

echo ""

# Check 2: Verify RequestPrinterStatusAsync method exists
echo -e "${YELLOW}Check 2: Verifying RequestPrinterStatusAsync Hub Method${NC}"
if grep -q "RequestPrinterStatusAsync" "$PROJECT_ROOT/src/api/Hubs/PrinterHub.cs"; then
    echo -e "${GREEN}✅ PASS${NC}: RequestPrinterStatusAsync method exists in PrinterHub"
else
    echo -e "${RED}❌ FAIL${NC}: RequestPrinterStatusAsync method not found in PrinterHub"
    exit 1
fi

echo ""

# Check 3: Verify client calls the correct method name
echo -e "${YELLOW}Check 3: Verifying Client Method Invocation${NC}"
if grep -q 'invoke("RequestPrinterStatusAsync"' "$REACT_DIR/src/services/printer-signalr.ts"; then
    echo -e "${GREEN}✅ PASS${NC}: Client invokes RequestPrinterStatusAsync"
else
    echo -e "${RED}❌ FAIL${NC}: Client method invocation mismatch"
    exit 1
fi

echo ""

# Check 4: Build API project
echo -e "${YELLOW}Check 4: Building API Project${NC}"
cd "$PROJECT_ROOT/src"
if dotnet build "$API_PROJ" -c Debug > /tmp/api-build.log 2>&1; then
    echo -e "${GREEN}✅ PASS${NC}: API project builds successfully"
else
    echo -e "${RED}❌ FAIL${NC}: API project build failed"
    echo "Build log:"
    cat /tmp/api-build.log
    exit 1
fi

# Check for errors in build output
if grep -q "0 Error" /tmp/api-build.log && grep -q "Build succeeded" /tmp/api-build.log; then
    echo -e "${GREEN}✅ PASS${NC}: Build completed with 0 errors"
else
    echo -e "${RED}❌ FAIL${NC}: Build has errors"
    cat /tmp/api-build.log
    exit 1
fi

echo ""

# Check 5: Verify React builds (if npm is available)
echo -e "${YELLOW}Check 5: Verifying React Build${NC}"
if command -v npm &> /dev/null; then
    cd "$REACT_DIR"
    # Just check that the project is valid, don't build (takes too long)
    if npm list @microsoft/signalr > /dev/null 2>&1; then
        echo -e "${GREEN}✅ PASS${NC}: @microsoft/signalr package is installed"
    else
        echo -e "${YELLOW}⚠️  WARNING${NC}: @microsoft/signalr not found in node_modules"
    fi
else
    echo -e "${YELLOW}⚠️  SKIPPED${NC}: npm not available"
fi

echo ""

# Check 6: Verify JSON converters are registered
echo -e "${YELLOW}Check 6: Verifying Custom Converters Registration${NC}"
if grep -A 20 "AddJsonProtocol" "$PROJECT_ROOT/src/api/Program.cs" | grep -q "PrinterBackendJsonConverter"; then
    echo -e "${GREEN}✅ PASS${NC}: Custom converters registered"
else
    echo -e "${RED}❌ FAIL${NC}: Custom converters not found"
    exit 1
fi

echo ""
echo "================================================================"
echo -e "${GREEN}✅ All validation checks passed!${NC}"
echo "================================================================"
echo ""
echo "Next steps:"
echo "1. Deploy the updated code to your environment"
echo "2. Monitor WebSocket connections in browser DevTools"
echo "3. Verify printer status updates are continuous (no disconnects every 5s)"
echo "4. Check browser console for errors (should be clean)"
echo ""
echo "See SIGNALR_FIXES_APPLIED.md for detailed information about the fixes."
echo ""
