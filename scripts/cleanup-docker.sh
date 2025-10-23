#!/bin/bash
# Cleanup script for PrintFarmer Docker containers
# Resolves naming inconsistencies and orphaned containers

set -e

echo "=== PrintFarmer Docker Cleanup ==="
echo "This script will clean up orphaned containers and resolve naming conflicts"
echo ""

# Function to safely stop and remove containers
cleanup_container() {
    local container_name="$1"
    if docker ps -q -f name="$container_name" | grep -q .; then
        echo "🛑 Stopping container: $container_name"
        docker stop "$container_name" || true
    fi
    
    if docker ps -aq -f name="$container_name" | grep -q .; then
        echo "🗑️  Removing container: $container_name"
        docker rm "$container_name" || true
    fi
}

# Clean up known problematic containers
echo "🧹 Cleaning up known problematic containers..."

# Old naming patterns that might conflict
cleanup_container "pfarm-database-1"
cleanup_container "pfarm-sqlserver-1" 
cleanup_container "pfarm-postgres-1"

# PrintFarmer specific containers (standardized naming)
cleanup_container "printfarmer-database"
cleanup_container "printfarmer-database-postgres"
cleanup_container "printfarmer-database-sqlserver" 
cleanup_container "printfarmer-database-mysql"

echo ""
echo "🔍 Checking for any remaining PrintFarmer containers..."
existing_containers=$(docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | grep -E "(printfarmer|pfarm)" | head -10 || true)

if [ -n "$existing_containers" ]; then
    echo "Found existing containers:"
    echo "$existing_containers"
    echo ""
    echo "❓ Do you want to stop and remove ALL PrintFarmer containers? [y/N]"
    read -r response
    if [[ "$response" =~ ^[Yy]$ ]]; then
        echo "🛑 Stopping all PrintFarmer containers..."
        docker ps -q -f name=printfarmer | xargs -r docker stop
        docker ps -q -f name=pfarm | xargs -r docker stop
        
        echo "🗑️  Removing all PrintFarmer containers..."
        docker ps -aq -f name=printfarmer | xargs -r docker rm
        docker ps -aq -f name=pfarm | xargs -r docker rm
    fi
else
    echo "✅ No existing PrintFarmer containers found"
fi

echo ""
echo "🔍 Checking for port conflicts..."

# Check common ports used by PrintFarmer
ports_to_check="5001 3000 6379 5432 1433"
for port in $ports_to_check; do
    if command -v lsof >/dev/null 2>&1 && lsof -i :$port >/dev/null 2>&1; then
        echo "⚠️  Port $port is in use:"
        lsof -i :$port | head -2
    fi
done

echo ""
echo "🐳 Docker system cleanup..."
echo "Removing unused networks, volumes, and images..."
docker system prune -f --volumes 2>/dev/null || true

echo ""
echo "✅ Cleanup complete!"
echo ""
echo "📋 Next steps:"
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