#!/bin/bash
# Docker Utilities Library for PrintFarmer
# Shared functions for Docker container and image management
# Source this file in other scripts: source "$(dirname "$0")/docker-utils.sh"

# Exit on error unless sourced
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    echo "❌ This script should be sourced, not executed directly"
    echo "Usage: source \"$(dirname \"\$0\")/docker-utils.sh\""
    exit 1
fi

# Colors for output (if not already defined)
if [[ -z "${RED:-}" ]]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    BLUE='\033[0;34m'
    NC='\033[0m' # No Color
fi

# Print functions (if not already defined)
if ! declare -F print_info > /dev/null 2>&1; then
    print_info() { echo -e "${BLUE}ℹ️  $1${NC}"; }
    print_success() { echo -e "${GREEN}✅ $1${NC}"; }
    print_warning() { echo -e "${YELLOW}⚠️  $1${NC}"; }
    print_error() { echo -e "${RED}❌ $1${NC}"; }
fi

# Audit log helper (if not already defined)
if ! declare -F audit_log > /dev/null 2>&1; then
    DEPLOY_AUDIT_LOG=${DEPLOY_AUDIT_LOG:-"./.docker-ops-audit.log"}
    audit_log() {
        local action="$1"
        shift || true
        local details="$*"
        local user
        user=$(whoami 2>/dev/null || echo unknown)
        local ts
        ts=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
        echo "$ts [$user] $action: $details" >> "$DEPLOY_AUDIT_LOG" || true
    }
fi

# PrintFarmer container/image patterns for cleanup operations
declare -a PRINTFARMER_PATTERNS=(
    "printfarmer"
    "pfarm"
    "orcaslicer"
    "prusaslicer"
    "redis"
    "postgres" 
    "mysql"
    "mcr.microsoft.com/mssql"
    "mssql"
    "sqlserver"
)

# Known problematic container names from naming inconsistencies
declare -a KNOWN_PROBLEMATIC_CONTAINERS=(
    "pfarm-database-1"
    "pfarm-sqlserver-1"
    "pfarm-postgres-1"
    "pfarm-api-1"
    "pfarm-frontend-1"
    "pfarm-redis-1"
)

# Standardized PrintFarmer container names
declare -a PRINTFARMER_CONTAINERS=(
    "printfarmer-database"
    "printfarmer-database-postgres"
    "printfarmer-database-sqlserver"
    "printfarmer-database-mysql"
    "printfarmer-api"
    "printfarmer-frontend"
    "printfarmer-redis"
    "printfarmer-orcaslicer-worker"
    "printfarmer-prusaslicer-worker"
    "printfarmer-nginx-proxy"
)

# Function to safely stop a single container
# Usage: docker_stop_container "container_name"
docker_stop_container() {
    local container_name="$1"
    
    # Check if Docker is running
    if ! docker info >/dev/null 2>&1; then
        print_warning "Docker is not running or not accessible"
        return 1
    fi
    
    local running_containers
    running_containers=$(docker ps -q --filter "name=${container_name}" 2>/dev/null || true)
    if [[ -n "$running_containers" ]]; then
        print_info "🛑 Stopping container: $container_name"
        if docker stop "$container_name" 2>/dev/null; then
            audit_log "stop" "container: $container_name"
            return 0
        else
            print_warning "Failed to stop container: $container_name"
            return 1
        fi
    fi
    return 1
}

# Function to safely remove a single container (with force option)
# Usage: docker_remove_container "container_name" [force]
docker_remove_container() {
    local container_name="$1"
    local force_flag="${2:-}"
    local remove_cmd="docker rm"
    
    # Check if Docker is running
    if ! docker info >/dev/null 2>&1; then
        print_warning "Docker is not running or not accessible"
        return 1
    fi
    
    if [[ "$force_flag" == "force" ]]; then
        remove_cmd="docker rm -f"
    fi
    
    local existing_containers
    existing_containers=$(docker ps -aq --filter "name=${container_name}" 2>/dev/null || true)
    if [[ -n "$existing_containers" ]]; then
        print_info "🗑️  Removing container: $container_name"
        
        # First attempt: normal removal
        if $remove_cmd "$container_name" 2>/dev/null; then
            audit_log "remove" "container: $container_name (force: ${force_flag:-no})"
            return 0
        fi
        
        # Second attempt: force removal if not already tried
        if [[ "$force_flag" != "force" ]]; then
            print_warning "Normal removal failed, trying force removal..."
            if docker rm -f "$container_name" 2>/dev/null; then
                audit_log "remove" "container: $container_name (force: yes - escalated)"
                return 0
            fi
        fi
        
        # Third attempt: kill then remove for stubborn containers
        print_warning "Force removal failed, trying kill + remove..."
        docker kill "$container_name" 2>/dev/null || true
        sleep 2
        if docker rm -f "$container_name" 2>/dev/null; then
            audit_log "remove" "container: $container_name (kill+remove)"
            return 0
        fi
        
        # Fourth attempt: detailed diagnostics and system-level removal
        print_error "All removal attempts failed for: $container_name"
        print_info "Container details:"
        docker inspect "$container_name" --format "{{.State.Status}} - {{.State.Error}}" 2>/dev/null || print_warning "Cannot inspect container"
        
        # Try to get container ID and remove by ID
        local container_id
        container_id=$(docker ps -aq --filter "name=${container_name}" --format "{{.ID}}" 2>/dev/null | head -1)
        if [[ -n "$container_id" ]]; then
            print_info "Attempting removal by container ID: $container_id"
            if docker rm -f "$container_id" 2>/dev/null; then
                audit_log "remove" "container: $container_name (by ID: $container_id)"
                return 0
            fi
        fi
        
        print_error "Unable to remove container: $container_name"
        print_info "Manual intervention may be required. Try:"
        print_info "  sudo systemctl restart docker"
        print_info "  docker system prune -af"
        return 1
    fi
    return 1
}

# Function to stop and remove a single container
# Usage: docker_cleanup_container "container_name" [force]
docker_cleanup_container() {
    local container_name="$1"
    local force_flag="${2:-}"
    
    local stopped=false
    local removed=false
    
    if docker_stop_container "$container_name"; then
        stopped=true
    fi
    
    if docker_remove_container "$container_name" "$force_flag"; then
        removed=true
    fi
    
    # Return success if either stopped or removed something
    if [[ "$stopped" == "true" || "$removed" == "true" ]]; then
        return 0
    fi
    
    return 1
}

# Force-remove containers matching a set of name/image patterns (best-effort)
# Usage: docker_force_remove_matching_containers
docker_force_remove_matching_containers() {
    local removed_all=""
    
    print_info "🧹 Force-removing containers by pattern matching..."
    
    for pattern in "${PRINTFARMER_PATTERNS[@]}"; do
        # Match by name
        local byname
        byname=$(docker ps -aq --filter "name=$pattern" 2>/dev/null || true)
        if [[ -n "$byname" ]]; then
            docker rm -f $byname 2>/dev/null || true
            removed_all="$removed_all $byname"
        fi
        
        # Match by image/ancestor
        local byimage
        byimage=$(docker ps -aq --filter "ancestor=$pattern" 2>/dev/null || true)
        if [[ -n "$byimage" ]]; then
            docker rm -f $byimage 2>/dev/null || true
            removed_all="$removed_all $byimage"
        fi
    done
    
    if [[ -n "$removed_all" ]]; then
        audit_log "force-remove" "pattern-matched containers: $removed_all"
        print_success "Force-removed matching containers: $removed_all"
        return 0
    else
        print_info "No containers found matching patterns"
        return 1
    fi
}

# Clean up known problematic containers from naming inconsistencies
# Usage: docker_cleanup_problematic_containers [force]
docker_cleanup_problematic_containers() {
    local force_flag="${1:-}"
    
    print_info "🧹 Cleaning up known problematic containers..."
    
    # Check Docker availability first
    if ! docker_check_availability; then
        return 1
    fi
    
    local cleaned=0
    local total=${#KNOWN_PROBLEMATIC_CONTAINERS[@]}
    
    for container in "${KNOWN_PROBLEMATIC_CONTAINERS[@]}"; do
        print_info "  • Checking: $container"
        if docker_cleanup_container "$container" "$force_flag"; then
            ((cleaned++))
        fi
    done
    
    if [[ $cleaned -gt 0 ]]; then
        print_success "Cleaned up $cleaned problematic containers (checked $total)"
    else
        print_info "No problematic containers found (checked $total containers)"
    fi
}

# Clean up all standard PrintFarmer containers
# Usage: docker_cleanup_printfarmer_containers [force]
docker_cleanup_printfarmer_containers() {
    local force_flag="${1:-}"
    
    print_info "🧹 Cleaning up PrintFarmer containers..."
    
    # Check Docker availability first
    if ! docker_check_availability; then
        return 1
    fi
    
    local cleaned=0
    local total=${#PRINTFARMER_CONTAINERS[@]}
    
    for container in "${PRINTFARMER_CONTAINERS[@]}"; do
        print_info "  • Checking: $container"
        if docker_cleanup_container "$container" "$force_flag"; then
            ((cleaned++))
        fi
    done
    
    if [[ $cleaned -gt 0 ]]; then
        print_success "Cleaned up $cleaned PrintFarmer containers (checked $total)"
    else
        print_info "No PrintFarmer containers found (checked $total containers)"
    fi
}

# Comprehensive container cleanup with progressive force escalation
# Usage: docker_comprehensive_cleanup [force]
docker_comprehensive_cleanup() {
    local force_flag="${1:-}"
    
    print_info "🧹 Starting comprehensive Docker container cleanup..."
    
    # Step 1: Clean up known problematic containers
    docker_cleanup_problematic_containers "$force_flag"
    
    # Step 2: Clean up standard PrintFarmer containers  
    docker_cleanup_printfarmer_containers "$force_flag"
    
    # Step 3: If force is requested, do pattern-based cleanup
    if [[ "$force_flag" == "force" ]]; then
        docker_force_remove_matching_containers
        
        # Step 4: Final aggressive cleanup of any remaining containers
        print_info "🧹 Final cleanup: removing any remaining containers..."
        local all_containers
        all_containers=$(docker ps -aq)
        if [[ -n "$all_containers" ]]; then
            # Try normal remove first
            docker rm $all_containers 2>/dev/null || true
            
            # Force remove any stubborn containers
            local remaining
            remaining=$(docker ps -aq)
            if [[ -n "$remaining" ]]; then
                print_warning "Some containers remain after normal removal. Force removing..."
                docker rm -f $remaining 2>/dev/null || true
                audit_log "force-remove" "stubborn containers: $remaining"
            fi
        fi
    fi
    
    print_success "Comprehensive container cleanup completed"
}

# Remove PrintFarmer Docker images with force option
# Usage: docker_cleanup_printfarmer_images [force]
docker_cleanup_printfarmer_images() {
    local force_flag="${1:-}"
    local rmi_cmd="docker rmi"
    
    if [[ "$force_flag" == "force" ]]; then
        rmi_cmd="docker rmi -f"
    fi
    
    print_info "🗑️  Removing PrintFarmer Docker images..."
    
    if docker images --format "{{.Repository}}" | grep -q "printfarmer"; then
        docker images --format "{{.Repository}}:{{.Tag}}" | grep "printfarmer" | xargs -r $rmi_cmd 2>/dev/null || true
        audit_log "remove-images" "PrintFarmer images (force: ${force_flag:-no})"
        print_success "PrintFarmer images removed"
    else
        print_info "No PrintFarmer images to remove"
    fi
}

# Check for port conflicts on common PrintFarmer ports
# Usage: docker_check_port_conflicts
docker_check_port_conflicts() {
    print_info "🔍 Checking for port conflicts..."
    
    local ports_to_check="5001 3000 6379 5432 1433 8080 8081 8082"
    local conflicts_found=0
    
    for port in $ports_to_check; do
        if command -v lsof >/dev/null 2>&1 && lsof -i :$port >/dev/null 2>&1; then
            print_warning "Port $port is in use:"
            lsof -i :$port | head -2
            ((conflicts_found++))
        fi
    done
    
    if [[ $conflicts_found -eq 0 ]]; then
        print_success "No port conflicts detected"
    else
        print_warning "Found $conflicts_found port conflicts"
    fi
    
    return $conflicts_found
}

# Docker system cleanup (prune unused resources)
# Usage: docker_system_cleanup [aggressive]
docker_system_cleanup() {
    local aggressive="${1:-}"
    
    print_info "🐳 Docker system cleanup..."
    
    if [[ "$aggressive" == "aggressive" ]]; then
        print_info "Removing unused networks, volumes, images, and build cache..."
        docker system prune -af --volumes 2>/dev/null || true
    else
        print_info "Removing unused networks, volumes, and containers..."
        docker system prune -f --volumes 2>/dev/null || true
    fi
    
    audit_log "system-prune" "mode: ${aggressive:-standard}"
    print_success "Docker system cleanup completed"
}

# Show current Docker status summary
# Usage: docker_show_status
docker_show_status() {
    echo ""
    print_info "📊 Current Docker Status:"
    echo ""
    
    # Show running containers
    local running_containers
    running_containers=$(docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" 2>/dev/null || true)
    if [[ -n "$running_containers" ]]; then
        echo "🟢 Running Containers:"
        echo "$running_containers"
    else
        echo "🟢 No running containers"
    fi
    echo ""
    
    # Show PrintFarmer-related containers (running or stopped)
    local pf_containers
    pf_containers=$(docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | grep -E "(printfarmer|pfarm)" | head -10 || true)
    if [[ -n "$pf_containers" ]]; then
        echo "📦 PrintFarmer Containers:"
        echo "$pf_containers"
    else
        echo "📦 No PrintFarmer containers found"
    fi
    echo ""
}

# Check if Docker is available and running
docker_check_availability() {
    if ! command -v docker >/dev/null 2>&1; then
        print_error "Docker command not found. Please install Docker."
        return 1
    fi
    
    if ! docker info >/dev/null 2>&1; then
        print_error "Docker is not running or not accessible. Please start Docker."
        return 1
    fi
    
    return 0
}

# Nuclear option: remove stuck containers after Docker reinstall
# Usage: docker_nuclear_cleanup
docker_nuclear_cleanup() {
    print_warning "🚨 NUCLEAR CLEANUP: This will attempt aggressive container removal"
    print_warning "This should only be used when normal cleanup fails after Docker reinstall"
    
    echo "❓ Are you sure you want to proceed with nuclear cleanup? [y/N]"
    read -r response
    if [[ ! "$response" =~ ^[Yy]$ ]]; then
        print_info "Nuclear cleanup cancelled"
        return 0
    fi
    
    print_info "🚨 Starting nuclear cleanup..."
    
    # Step 1: Kill all running containers
    local running_containers
    running_containers=$(docker ps -q 2>/dev/null || true)
    if [[ -n "$running_containers" ]]; then
        print_info "Killing all running containers..."
        echo "$running_containers" | xargs -r docker kill 2>/dev/null || true
        sleep 3
    fi
    
    # Step 2: Force remove all containers
    local all_containers
    all_containers=$(docker ps -aq 2>/dev/null || true)
    if [[ -n "$all_containers" ]]; then
        print_info "Force removing all containers..."
        echo "$all_containers" | xargs -r docker rm -f 2>/dev/null || true
        sleep 2
    fi
    
    # Step 3: System-level cleanup
    print_info "Running aggressive system cleanup..."
    docker system prune -af --volumes 2>/dev/null || true
    
    # Step 4: Check if Docker daemon restart is needed
    if docker ps -a 2>/dev/null | grep -q .; then
        print_warning "Some containers still remain. Docker daemon restart may be needed:"
        print_info "  sudo systemctl restart docker"
        print_info "  # or on macOS: restart Docker Desktop"
    else
        print_success "Nuclear cleanup completed successfully"
    fi
    
    audit_log "nuclear-cleanup" "executed aggressive container removal"
}

# Fix Docker state after reinstallation
# Usage: docker_fix_post_reinstall
docker_fix_post_reinstall() {
    print_info "🔧 Attempting to fix Docker state after reinstallation..."
    
    # Step 1: Stop Docker daemon and socket
    print_info "Step 1: Stopping Docker daemon and socket..."
    if command -v systemctl >/dev/null 2>&1; then
        print_info "Stopping docker.socket..."
        sudo systemctl stop docker.socket 2>/dev/null || true
        print_info "Stopping docker.service..."
        sudo systemctl stop docker.service 2>/dev/null || true
        print_info "Stopping containerd.service (if present)..."
        sudo systemctl stop containerd.service 2>/dev/null || true
    elif command -v service >/dev/null 2>&1; then
        sudo service docker stop 2>/dev/null || true
    else
        print_warning "Cannot stop Docker daemon automatically. Please stop Docker manually."
        return 1
    fi
    
    sleep 3
    
    # Step 2: Clean up Docker's runtime directory
    print_info "Step 2: Cleaning Docker runtime state..."
    if [[ -d "/var/run/docker" ]]; then
        sudo rm -rf /var/run/docker/* 2>/dev/null || true
    fi
    
    # Step 3: Clean up containerd state if it exists
    print_info "Step 3: Cleaning containerd state..."
    if [[ -d "/run/containerd" ]]; then
        sudo rm -rf /run/containerd/* 2>/dev/null || true
    fi
    
    # Step 4: Clean up any remaining container metadata
    print_info "Step 4: Cleaning container metadata..."
    if [[ -d "/var/lib/docker/containers" ]]; then
        print_warning "This will remove all container metadata. Continue? [y/N]"
        read -r confirm
        if [[ "$confirm" =~ ^[Yy]$ ]]; then
            sudo rm -rf /var/lib/docker/containers/* 2>/dev/null || true
        fi
    fi
    
    # Step 5: Restart Docker daemon and socket
    print_info "Step 5: Starting Docker daemon and socket..."
    if command -v systemctl >/dev/null 2>&1; then
        print_info "Starting containerd.service (if present)..."
        sudo systemctl start containerd.service 2>/dev/null || true
        print_info "Starting docker.socket..."
        sudo systemctl start docker.socket 2>/dev/null || true
        print_info "Starting docker.service..."
        sudo systemctl start docker.service 2>/dev/null || true
    elif command -v service >/dev/null 2>&1; then
        sudo service docker start
    else
        print_warning "Cannot start Docker daemon automatically. Please start Docker manually."
        return 1
    fi
    
    # Step 6: Wait for Docker to be ready
    print_info "Step 6: Waiting for Docker to be ready..."
    local attempts=0
    while ! docker info >/dev/null 2>&1 && [[ $attempts -lt 30 ]]; do
        sleep 2
        ((attempts++))
        echo -n "."
    done
    echo ""
    
    if docker info >/dev/null 2>&1; then
        print_success "Docker daemon restarted successfully"
        return 0
    else
        print_error "Docker daemon failed to start properly"
        return 1
    fi
}

# Extreme cleanup for completely broken Docker state
# Usage: docker_extreme_cleanup
docker_extreme_cleanup() {
    print_warning "🚨 EXTREME CLEANUP: This will completely reset Docker state"
    print_warning "This will:"
    print_warning "  • Stop all Docker services including socket"
    print_warning "  • Remove all Docker runtime data"
    print_warning "  • Remove all container metadata"
    print_warning "  • Kill any orphaned Docker processes"
    echo ""
    
    echo "❓ This is a nuclear option. Are you absolutely sure? [y/N]"
    read -r response
    if [[ ! "$response" =~ ^[Yy]$ ]]; then
        print_info "Extreme cleanup cancelled"
        return 0
    fi
    
    print_info "🚨 Starting extreme cleanup..."
    
    # Step 1: Stop all Docker services completely
    print_info "Step 1: Stopping all Docker services and socket..."
    if command -v systemctl >/dev/null 2>&1; then
        sudo systemctl stop docker.socket docker.service containerd.service 2>/dev/null || true
        # Disable to prevent automatic restart
        sudo systemctl stop docker.socket 2>/dev/null || true
        sleep 3
    fi
    
    # Step 2: Kill any remaining Docker processes
    print_info "Step 2: Killing any remaining Docker processes..."
    sudo pkill -f dockerd 2>/dev/null || true
    sudo pkill -f docker-containerd 2>/dev/null || true
    sudo pkill -f containerd 2>/dev/null || true
    sudo pkill -f runc 2>/dev/null || true
    sleep 3
    
    # Step 3: Remove all Docker runtime directories
    print_info "Step 3: Removing Docker runtime directories..."
    sudo rm -rf /var/run/docker 2>/dev/null || true
    sudo rm -rf /run/containerd 2>/dev/null || true
    sudo rm -rf /run/docker 2>/dev/null || true
    
    # Step 4: Remove container metadata (this fixes stuck containers)
    print_info "Step 4: Removing all container metadata..."
    sudo rm -rf /var/lib/docker/containers 2>/dev/null || true
    sudo rm -rf /var/lib/docker/image/overlay2/repositories.json 2>/dev/null || true
    
    # Step 5: Clean up network state
    print_info "Step 5: Cleaning up network state..."
    sudo rm -rf /var/lib/docker/network 2>/dev/null || true
    
    # Step 6: Remove any Docker-related mount points
    print_info "Step 6: Cleaning up mount points..."
    mount | grep docker | awk '{print $3}' | sudo xargs -r umount 2>/dev/null || true
    
    # Step 7: Restart Docker completely
    print_info "Step 7: Restarting Docker services..."
    if command -v systemctl >/dev/null 2>&1; then
        sudo systemctl daemon-reload
        sudo systemctl start containerd.service 2>/dev/null || true
        sleep 2
        sudo systemctl start docker.socket
        sleep 2
        sudo systemctl start docker.service
    fi
    
    # Step 8: Wait for Docker to be ready and test
    print_info "Step 8: Waiting for Docker to initialize..."
    local attempts=0
    while ! docker info >/dev/null 2>&1 && [[ $attempts -lt 60 ]]; do
        sleep 2
        ((attempts++))
        echo -n "."
    done
    echo ""
    
    if docker info >/dev/null 2>&1; then
        print_success "Extreme cleanup completed successfully!"
        print_info "Docker has been completely reset. All containers and images are gone."
        docker_show_status
    else
        print_error "Docker failed to start after extreme cleanup"
        print_info "Manual intervention required:"
        print_info "  sudo systemctl status docker"
        print_info "  sudo journalctl -fu docker"
        return 1
    fi
    
    audit_log "extreme-cleanup" "completely reset Docker state"
}

# Diagnose why a container cannot be removed
# Usage: docker_diagnose_stuck_container "container_name"
docker_diagnose_stuck_container() {
    local container_name="$1"
    
    print_info "🔍 Diagnosing stuck container: $container_name"
    
    # Check if container exists
    if ! docker ps -aq --filter "name=${container_name}" | grep -q .; then
        print_info "Container does not exist"
        return 0
    fi
    
    # Get container details
    print_info "Container state and details:"
    docker inspect "$container_name" --format "State: {{.State.Status}}" 2>/dev/null || print_warning "Cannot get state"
    docker inspect "$container_name" --format "Error: {{.State.Error}}" 2>/dev/null || print_warning "Cannot get error"
    docker inspect "$container_name" --format "PID: {{.State.Pid}}" 2>/dev/null || print_warning "Cannot get PID"
    
    # Check if it's running
    if docker ps -q --filter "name=${container_name}" | grep -q .; then
        print_warning "Container is still running"
        print_info "Ports in use:"
        docker port "$container_name" 2>/dev/null || print_info "No ports mapped"
    fi
    
    # Check for volume mounts that might be causing issues
    print_info "Volume mounts:"
    docker inspect "$container_name" --format "{{range .Mounts}}{{.Source}}:{{.Destination}} {{end}}" 2>/dev/null || print_warning "Cannot get mounts"
    
    # Check system resources
    print_info "System information:"
    docker system df 2>/dev/null || print_warning "Cannot get system usage"
    
    # Check Docker daemon logs for recent errors
    print_info "Recent Docker daemon issues (if any):"
    if command -v journalctl >/dev/null 2>&1; then
        journalctl -u docker --since "10 minutes ago" --no-pager -q | tail -5 2>/dev/null || print_info "No recent Docker daemon logs"
    else
        print_info "journalctl not available (non-systemd system)"
    fi
}

print_info "📚 Docker utilities library loaded successfully"
print_info "Available functions: docker_cleanup_container, docker_force_remove_matching_containers, docker_comprehensive_cleanup, docker_cleanup_printfarmer_images, docker_check_port_conflicts, docker_system_cleanup, docker_show_status, docker_nuclear_cleanup, docker_diagnose_stuck_container"

# Check Docker availability when library is loaded
if ! docker_check_availability; then
    print_warning "Docker utilities loaded but Docker is not available"
fi