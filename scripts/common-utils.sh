#!/usr/bin/env bash
# PrintFarmer Common Utilities Script
# Shared functions for deployment and startup scripts
# Provides: logging, colors, admin setup, validation, error handling

# ============================================================================
# COLORS AND LOGGING
# ============================================================================

# Color definitions
export RED='\033[0;31m'
export GREEN='\033[0;32m'
export YELLOW='\033[1;33m'
export BLUE='\033[0;34m'
export NC='\033[0m' # No Color

# Logging functions - compatible with both bash and zsh
log_info() { 
    echo -e "${BLUE}ℹ️  $*${NC}" 
}

log_success() { 
    echo -e "${GREEN}✅ $*${NC}" 
}

log_warn() { 
    echo -e "${YELLOW}⚠️  $*${NC}" 
}

log_error() { 
    echo -e "${RED}❌ $*${NC}" 
}

log_header() {
    echo
    echo -e "${BLUE}================================================${NC}"
    echo -e "${BLUE}$*${NC}"
    echo -e "${BLUE}================================================${NC}"
    echo
}

# Alias for compatibility with deploy-docker.sh naming
print_info() { log_info "$@"; }
print_success() { log_success "$@"; }
print_warning() { log_warn "$@"; }
print_error() { log_error "$@"; }
print_header() { log_header "$@"; }

# ============================================================================
# VALIDATION AND CHECKS
# ============================================================================

# Check if required command is available
require_command() {
    local cmd="$1"
    if ! command -v "$cmd" &>/dev/null; then
        log_error "Required command '$cmd' not found. Please install it first."
        return 1
    fi
    return 0
}

# Check if port is in use
is_port_in_use() {
    local port="$1"
    if command -v lsof >/dev/null 2>&1; then
        lsof -Pi ":$port" -sTCP:LISTEN -t >/dev/null 2>&1
    elif command -v netstat >/dev/null 2>&1; then
        netstat -tuln 2>/dev/null | grep -q ":$port "
    else
        log_warn "Cannot check port $port (lsof or netstat not found)"
        return 1
    fi
}

# Free a port by killing processes on it
free_port() {
    local port="$1"
    
    if ! is_port_in_use "$port"; then
        return 0  # Port is already free
    fi
    
    log_warn "Port $port is in use. Attempting to free it..."
    
    if command -v lsof >/dev/null 2>&1; then
        lsof -ti:"$port" | xargs kill -9 2>/dev/null || true
    fi
    
    sleep 2
    
    if is_port_in_use "$port"; then
        log_error "Could not free port $port. Please stop the conflicting process manually."
        return 1
    fi
    
    return 0
}

# ============================================================================
# ADMIN USER SETUP
# ============================================================================

# Create initial admin user via API
create_initial_admin() {
    local api_url="$1"
    local username="${2:-admin}"
    local password="${3:-}"
    local email="${4:-admin@printfarmer.local}"
    
    # Validate inputs
    if [[ -z "$api_url" ]]; then
        log_error "API URL is required for admin creation"
        return 1
    fi
    
    if [[ -z "$password" ]]; then
        log_error "Password is required for admin creation"
        return 1
    fi
    
    # Check if setup is needed
    log_info "Checking if setup is needed..."
    local setup_status
    setup_status=$(curl -s -m 5 "$api_url/api/setup/status" 2>/dev/null || echo '{"needsSetup":false}')
    local needs_setup
    needs_setup=$(echo "$setup_status" | grep -o '"needsSetup":\s*true' || echo "")
    
    if [[ -z "$needs_setup" ]]; then
        log_info "Setup has already been completed (admin user exists)"
        return 0
    fi
    
    # Create the admin user
    log_info "Creating initial admin user: $username"
    local response
    response=$(curl -s -X POST "$api_url/api/setup/initial-admin" \
        -H "Content-Type: application/json" \
        -d "{
            \"username\": \"$username\",
            \"password\": \"$password\",
            \"email\": \"$email\",
            \"firstName\": \"Administrator\",
            \"lastName\": \"User\"
        }" 2>/dev/null || echo '{"success":false,"error":"Connection failed"}')
    
    local success_check
    success_check=$(echo "$response" | grep -o '"success":\s*true' || echo "")
    
    if [[ -n "$success_check" ]]; then
        log_success "✅ Initial admin user created successfully!"
        log_success "   Username: $username"
        log_success "   Email: $email"
        return 0
    else
        log_error "Failed to create initial admin user"
        log_error "Response: $response"
        return 1
    fi
}

# ============================================================================
# HEALTH CHECKS (from common-health-checks.sh)
# ============================================================================

# Check if API is responding to basic health endpoint
check_api_basic_health() {
    local api_url="$1"
    local timeout="${2:-5}"
    
    local response
    response=$(curl -s -m "$timeout" "$api_url/healthz" 2>/dev/null)
    
    if [[ -n "$response" ]] && (echo "$response" | grep -q '"status":"ok"' || echo "$response" | grep -q '^OK$'); then
        return 0  # Success
    else
        return 1  # Failed
    fi
}

# Check comprehensive health endpoint
check_api_comprehensive_health() {
    local api_url="$1"
    local timeout="${2:-5}"
    
    local response
    response=$(curl -s -m "$timeout" "$api_url/health" 2>/dev/null)
    
    if [[ -n "$response" ]]; then
        # Check if it's JSON
        if echo "$response" | grep -q '^{'; then
            # JSON response - check for Healthy status
            if echo "$response" | grep -q '"status":"Healthy"'; then
                return 0  # Healthy
            else
                return 1  # Not healthy
            fi
        else
            # Simple text response - any non-empty response is OK
            return 0
        fi
    fi
    return 1
}

# Check if setup API endpoint is accessible
check_setup_endpoint() {
    local api_url="$1"
    local timeout="${2:-5}"
    
    local response
    response=$(curl -s -m "$timeout" "$api_url/api/setup/status" 2>/dev/null)
    
    if [[ -n "$response" ]] && echo "$response" | grep -q -E '^\{|needsSetup'; then
        return 0  # Endpoint is accessible
    fi
    return 1
}

# Check if catalog endpoint is working (indicates database is initialized)
check_catalog_endpoint() {
    local api_url="$1"
    local timeout="${2:-5}"
    
    local response
    response=$(curl -s -m "$timeout" "$api_url/api/catalog/manufacturers" 2>/dev/null)
    
    # Check if response is valid JSON array or object
    if [[ -n "$response" ]] && echo "$response" | grep -q -E '^\[|^{'; then
        return 0  # Valid response
    fi
    return 1
}

# Check if React dev server is responding
check_react_dev_server() {
    local react_url="$1"
    local timeout="${2:-5}"
    
    local response
    response=$(curl -s -m "$timeout" "$react_url" 2>/dev/null)
    
    if [[ -n "$response" ]] && echo "$response" | grep -q -i "printfarmer\|vite\|<!doctype"; then
        return 0  # React server is responding
    fi
    return 1
}

# Run comprehensive health check suite
run_health_check_suite() {
    local api_url="$1"
    local react_url="$2"
    
    local all_healthy=true
    
    # Test basic health
    if check_api_basic_health "$api_url"; then
        log_success "API basic health check passed"
    else
        log_warn "API basic health check failed"
        all_healthy=false
    fi
    
    # Test comprehensive health
    if check_api_comprehensive_health "$api_url"; then
        log_success "API comprehensive health check passed"
    else
        log_warn "API comprehensive health check failed"
        all_healthy=false
    fi
    
    # Test setup endpoint
    if check_setup_endpoint "$api_url"; then
        log_success "API setup endpoint accessible"
    else
        log_warn "API setup endpoint not accessible"
        all_healthy=false
    fi
    
    # Test catalog endpoint
    if check_catalog_endpoint "$api_url"; then
        log_success "API catalog endpoint working"
    else
        log_warn "API catalog endpoint not working"
        all_healthy=false
    fi
    
    # Test React server
    if [[ -n "$react_url" ]]; then
        if check_react_dev_server "$react_url"; then
            log_success "React dev server responding"
        else
            log_warn "React dev server not responding"
            all_healthy=false
        fi
    fi
    
    return $([ "$all_healthy" = true ] && echo 0 || echo 1)
}

# ============================================================================
# WAIT FOR SERVICE HELPERS
# ============================================================================

# Wait for API to become healthy
wait_for_api() {
    local api_url="$1"
    local max_attempts="${2:-60}"
    local interval="${3:-1}"
    
    log_info "Waiting for API to be ready at $api_url..."
    
    local attempt=0
    while [[ $attempt -lt $max_attempts ]]; do
        if check_api_basic_health "$api_url" "$interval"; then
            log_success "API server ready at $api_url"
            return 0
        fi
        
        attempt=$((attempt + 1))
        if [[ $attempt -lt $max_attempts ]]; then
            sleep "$interval"
        fi
    done
    
    log_error "API server failed to start within $((max_attempts * interval)) seconds"
    return 1
}

# Wait for React dev server
wait_for_react() {
    local react_url="$1"
    local max_attempts="${2:-60}"
    local interval="${3:-1}"
    
    log_info "Waiting for React dev server to be ready at $react_url..."
    
    local attempt=0
    while [[ $attempt -lt $max_attempts ]]; do
        if check_react_dev_server "$react_url" "$interval"; then
            log_success "React dev server ready at $react_url"
            return 0
        fi
        
        attempt=$((attempt + 1))
        if [[ $attempt -lt $max_attempts ]]; then
            sleep "$interval"
        fi
    done
    
    log_error "React dev server failed to start within $((max_attempts * interval)) seconds"
    return 1
}

# ============================================================================
# EXPORTS FOR SOURCING SCRIPTS
# ============================================================================

export -f log_info log_success log_warn log_error log_header
export -f print_info print_success print_warning print_error print_header
export -f require_command is_port_in_use free_port
export -f create_initial_admin
export -f check_api_basic_health check_api_comprehensive_health
export -f check_setup_endpoint check_catalog_endpoint check_react_dev_server
export -f run_health_check_suite
export -f wait_for_api wait_for_react
