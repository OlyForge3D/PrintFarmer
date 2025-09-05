#!/bin/bash

# Comprehensive SignalR Health Check Script
# Tests all SignalR functionality before considering the application ready

set -e

API_BASE_URL="${API_BASE_URL:-http://localhost:5001}"
FRONTEND_URL="${FRONTEND_URL:-http://localhost:3000}"
PROXY_URL="${PROXY_URL:-http://localhost:8080}"

echo "🔍 Starting comprehensive SignalR health checks..."
echo "API Base URL: $API_BASE_URL"
echo "Frontend URL: $FRONTEND_URL"
echo "Proxy URL: $PROXY_URL"
echo ""

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to check if a service is responding
check_service() {
    local url=$1
    local service_name=$2
    local timeout=${3:-10}
    
    echo -n "Checking $service_name... "
    if curl -s -f --connect-timeout $timeout "$url" > /dev/null 2>&1; then
        echo -e "${GREEN}✓ OK${NC}"
        return 0
    else
        echo -e "${RED}✗ FAIL${NC}"
        return 1
    fi
}

# Function to check JSON response contains expected fields
check_json_response() {
    local url=$1
    local service_name=$2
    local expected_field=$3
    local timeout=${4:-10}
    
    echo -n "Checking $service_name JSON response... "
    local response=$(curl -s --connect-timeout $timeout "$url")
    
    if echo "$response" | grep -q "$expected_field"; then
        echo -e "${GREEN}✓ OK${NC}"
        return 0
    else
        echo -e "${RED}✗ FAIL${NC}"
        echo "  Response: $response"
        return 1
    fi
}

# Function to test SignalR test endpoint
test_signalr_functionality() {
    local api_url=$1
    
    echo -e "\n${BLUE}Testing SignalR functionality...${NC}"
    
    # Test connection stats
    echo -n "Getting SignalR connection stats... "
    SIGNALR_STATS=$(curl -s "${API_BASE}/api/signalr/connection-stats" -H "Accept: application/json")
    if [ $? -eq 0 ] && [ -n "$SIGNALR_STATS" ]; then
        # Check if response contains expected fields
        if echo "$SIGNALR_STATS" | jq -e '.hubName' >/dev/null 2>&1; then
            echo "✓ OK"
            SIGNALR_SUCCESS=$((SIGNALR_SUCCESS + 1))
            echo "  Hub: $(echo "$SIGNALR_STATS" | jq -r '.hubName // "Unknown"')"
            echo "  Health: $(echo "$SIGNALR_STATS" | jq -r '.healthStatus // "Unknown"')"
            AVAILABLE_METHODS=$(echo "$SIGNALR_STATS" | jq -r '.availableMethods // [] | join(", ")')
            echo "  Available Methods: $AVAILABLE_METHODS"
        else
            echo "✗ FAIL"
            echo "  Response: $SIGNALR_STATS"
            SIGNALR_FAILURES=$((SIGNALR_FAILURES + 1))
        fi
    else
        echo "✗ FAIL"
        echo "  Could not connect to SignalR stats endpoint"
        SIGNALR_FAILURES=$((SIGNALR_FAILURES + 1))
    fi
    
    # Test sending test message
    echo -n "Testing SignalR test message broadcast... "
    local test_response=$(curl -s -X POST -H "Content-Type: application/json" \
        -d '{"message":"Health check test from script"}' \
        "$api_url/api/signalrtest/send-test-message")
    
    if echo "$test_response" | grep -q '"Success":true'; then
        echo -e "${GREEN}✓ OK${NC}"
    else
        echo -e "${RED}✗ FAIL${NC}"
        echo "  Response: $test_response"
        return 1
    fi
    
    # Test discovery group functionality
    echo -n "Testing discovery group functionality... "
    local discovery_response=$(curl -s -X POST -H "Content-Type: application/json" \
        -d '{"delayBetweenMessages":false}' \
        "$api_url/api/signalrtest/test-discovery-group")
    
    if echo "$discovery_response" | grep -q '"Success":true'; then
        echo -e "${GREEN}✓ OK${NC}"
        local session_id=$(echo "$discovery_response" | grep -o '"SessionId":"[^"]*"' | cut -d'"' -f4)
        echo "  Session ID: $session_id"
    else
        echo -e "${RED}✗ FAIL${NC}"
        echo "  Response: $discovery_response"
        return 1
    fi
    
    return 0
}

# Function to check comprehensive health endpoint
check_comprehensive_health() {
    local api_url=$1
    
    echo -e "\n${BLUE}Checking comprehensive health endpoint...${NC}"
    
    local health_response=$(curl -s "$api_url/health")
    
    # Check overall status
    echo -n "Overall health status... "
    if echo "$health_response" | grep -q '"status":"Healthy"'; then
        echo -e "${GREEN}✓ Healthy${NC}"
    elif echo "$health_response" | grep -q '"status":"Degraded"'; then
        echo -e "${YELLOW}⚠ Degraded${NC}"
    else
        echo -e "${RED}✗ Unhealthy${NC}"
        echo "Health response: $health_response"
        return 1
    fi
    
    # Check SignalR specific health
    echo -n "SignalR health check... "
    if echo "$health_response" | grep -q '"signalr"'; then
        if echo "$health_response" | grep -A 20 '"signalr"' | grep -q '"Status":"Healthy"'; then
            echo -e "${GREEN}✓ OK${NC}"
        else
            echo -e "${RED}✗ FAIL${NC}"
            echo "SignalR health details:"
            echo "$health_response" | grep -A 30 '"signalr"' | head -20
            return 1
        fi
    else
        echo -e "${YELLOW}⚠ SignalR health check not found${NC}"
    fi
    
    # Check Redis connectivity
    echo -n "Redis connectivity... "
    if echo "$health_response" | grep -q '"Redis"'; then
        if echo "$health_response" | grep -A 10 '"Redis"' | grep -q '"Status":"Healthy"'; then
            echo -e "${GREEN}✓ OK${NC}"
        else
            echo -e "${YELLOW}⚠ Redis issues detected${NC}"
            echo "Redis details:"
            echo "$health_response" | grep -A 15 '"Redis"' | head -10
        fi
    else
        echo -e "${YELLOW}⚠ Redis not configured${NC}"
    fi
    
    return 0
}

# Main health check execution
main() {
    local failed_checks=0
    
    echo -e "${BLUE}=== Basic Service Connectivity ===${NC}"
    
    # Basic connectivity checks
    check_service "$API_BASE_URL/healthz" "API Basic Health" || ((failed_checks++))
    check_service "$FRONTEND_URL/" "Frontend" || ((failed_checks++))
    check_service "$PROXY_URL/health" "Nginx Proxy" || ((failed_checks++))
    
    # JSON response checks
    check_json_response "$API_BASE_URL/healthz" "API Basic Response" '"status":"ok"' || ((failed_checks++))
    check_json_response "$API_BASE_URL/api/printers" "API Printers Endpoint" '\[' || ((failed_checks++))
    
    echo -e "\n${BLUE}=== SignalR Functionality Tests ===${NC}"
    
    # SignalR functionality tests
    test_signalr_functionality "$API_BASE_URL" || ((failed_checks++))
    
    echo -e "\n${BLUE}=== Comprehensive Health Analysis ===${NC}"
    
    # Comprehensive health check
    check_comprehensive_health "$API_BASE_URL" || ((failed_checks++))
    
    # Summary
    echo ""
    echo "=============================================="
    if [ $failed_checks -eq 0 ]; then
        echo -e "${GREEN}🎉 All SignalR health checks passed!${NC}"
        echo -e "${GREEN}✅ Application is ready for use${NC}"
        exit 0
    else
        echo -e "${RED}❌ $failed_checks health check(s) failed${NC}"
        echo -e "${RED}🚫 Application is not ready${NC}"
        exit 1
    fi
}

# Run main function
main "$@"
