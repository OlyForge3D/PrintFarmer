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
print_info "Starting problematic containers cleanup..."
docker_cleanup_problematic_containers
print_info "Starting PrintFarmer containers cleanup..."
docker_cleanup_printfarmer_containers
print_info "Container cleanup completed."

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
        
        # Check if any containers are still stuck after force removal
        remaining_after_force=$(docker ps -a --format "{{.Names}}" | grep -E "(printfarmer|pfarm)" || true)
        if [[ -n "$remaining_after_force" ]]; then
            echo ""
            print_warning "Some containers could not be removed with standard force methods"
            echo "Stuck containers:"
            echo "$remaining_after_force"
            echo ""
            echo "❓ Would you like to:"
            echo "1) Diagnose stuck containers"
            echo "2) Try nuclear cleanup (aggressive removal)"
            echo "3) Skip and continue"
            echo "Choose [1/2/3]: "
            read -r cleanup_choice
            
            case "$cleanup_choice" in
                1)
                    for container in $remaining_after_force; do
                        docker_diagnose_stuck_container "$container"
                        echo ""
                    done
                    ;;
                2)
                    docker_nuclear_cleanup
                    ;;
                *)
                    print_info "Skipping advanced cleanup"
                    ;;
            esac
        fi
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
echo "   • Standard: docker compose -f docker-compose.yml up"
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