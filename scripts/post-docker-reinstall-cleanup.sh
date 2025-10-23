#!/bin/bash
# Post-Docker-Reinstall Cleanup Script
# Use this when containers are stuck after Docker reinstallation

set -e

# Source shared Docker utilities
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/docker-utils.sh"

echo "=== Post-Docker-Reinstall Cleanup ==="
echo "This script handles stuck containers after Docker reinstallation"
echo ""

print_warning "⚠️  This script is designed for situations where:"
print_warning "  • Docker was recently reinstalled"
print_warning "  • Normal container removal fails"
print_warning "  • Containers appear to be in inconsistent states"
echo ""

# Check Docker availability
if ! docker_check_availability; then
    print_error "Docker is not available. Please ensure Docker is properly installed and running."
    exit 1
fi

# Show current status
print_info "📊 Current Docker Status:"
docker_show_status

echo ""
print_info "🔍 Checking for problematic containers..."

# Look for any containers that might be stuck
stuck_containers=$(docker ps -a --format "{{.Names}} {{.Status}}" | grep -E "(Exited|Created|Dead)" || true)
if [[ -n "$stuck_containers" ]]; then
    print_warning "Found containers in problematic states:"
    echo "$stuck_containers"
else
    print_info "No containers found in obviously problematic states"
fi

echo ""
echo "❓ What would you like to do?"
echo "1) Diagnose specific containers"
echo "2) Try enhanced force removal"
echo "3) Nuclear cleanup (remove everything)"
echo "4) Restart Docker daemon (requires sudo)"
echo "5) Exit"
echo ""
echo "Choose [1-5]: "
read -r choice

case "$choice" in
    1)
        echo "Enter container name to diagnose: "
        read -r container_name
        if [[ -n "$container_name" ]]; then
            docker_diagnose_stuck_container "$container_name"
        fi
        ;;
    2)
        print_info "Attempting enhanced force removal..."
        docker_comprehensive_cleanup force
        ;;
    3)
        docker_nuclear_cleanup
        ;;
    4)
        print_info "Attempting Docker daemon restart..."
        if command -v systemctl >/dev/null 2>&1; then
            print_info "Using systemctl to restart Docker..."
            sudo systemctl stop docker || true
            sleep 3
            sudo systemctl start docker || true
            sleep 5
            if docker info >/dev/null 2>&1; then
                print_success "Docker daemon restarted successfully"
                docker_show_status
            else
                print_error "Docker daemon restart failed"
            fi
        else
            print_warning "systemctl not available. Manual Docker restart required:"
            print_info "  On macOS: Restart Docker Desktop application"
            print_info "  On other systems: sudo service docker restart"
        fi
        ;;
    *)
        print_info "Exiting..."
        exit 0
        ;;
esac

echo ""
print_info "📊 Final status check:"
docker_show_status

echo ""
print_success "Post-reinstall cleanup completed!"
print_info "If issues persist, consider:"
print_info "  • Completely removing Docker data: sudo rm -rf /var/lib/docker"
print_info "  • Reinstalling Docker completely"
print_info "  • Checking system logs for Docker daemon errors"