#!/bin/bash
# Cleanup script for PrintFarmer Docker containers
# Resolves naming inconsistencies and orphaned containers

set -e

# Source shared Docker utilities
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/docker-utils.sh"

echo "=== PrintFarmer Docker Cleanup ==="
echo "This script will clean up orphaned containers and resolve naming conflicts"
echo ""

# Clean up known problematic containers and PrintFarmer containers
docker_cleanup_problematic_containers
docker_cleanup_printfarmer_containers

echo ""
echo "🔍 Checking for any remaining PrintFarmer containers..."
docker_show_status

existing_containers=$(docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | grep -E "(printfarmer|pfarm)" | head -10 || true)

if [ -n "$existing_containers" ]; then
    echo ""
    echo "❓ Do you want to force remove ALL remaining PrintFarmer containers? [y/N]"
    read -r response
    if [[ "$response" =~ ^[Yy]$ ]]; then
        print_info "🛑 Force removing all remaining PrintFarmer containers..."
        docker_force_remove_matching_containers
    fi
else
    print_success "No remaining PrintFarmer containers found"
fi

echo ""
docker_check_port_conflicts

echo ""
docker_system_cleanup

echo ""
print_success "Cleanup complete!"
echo ""
print_info "📋 Next steps:"
echo "1. Use consistent compose files:"
echo "   • Main deployment: docker compose up"
echo "   • Microservices: docker compose -f docker-compose.microservices.yml up"
echo "   • With databases: docker compose -f docker-compose.yml -f docker-compose.databases.yml up"
echo ""
echo "2. All database services now use consistent naming:"
echo "   • Main deployments: database (container: printfarmer-database)"
echo "   • Database-specific: postgres/sqlserver/mysql (containers: printfarmer-database-*)"
echo ""
echo "3. If you still see orphan warnings, add --remove-orphans:"
echo "   docker compose up --remove-orphans"
echo ""
print_info "📊 Final status check:"
docker_show_status