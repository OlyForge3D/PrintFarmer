#!/bin/bash
# Simplified test script for PrintFarmer database providers
# Works better on macOS by using Docker health checks instead of client tools

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
API_PORT=5088
API_URL="http://localhost:$API_PORT"
HEALTH_ENDPOINT="$API_URL/healthz"

echo "=== PrintFarmer Database Provider Testing (Simplified) ==="
echo ""

# Function to check if API is running
check_api_health() {
    curl -s -o /dev/null -w "%{http_code}" "$HEALTH_ENDPOINT" 2>/dev/null | grep -q "200"
}

# Function to wait for API to start
wait_for_api() {
    echo "Waiting for API to start..."
    local max_attempts=30
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        if check_api_health; then
            echo "API started successfully!"
            return 0
        fi
        
        sleep 1
        attempt=$((attempt + 1))
    done
    
    echo "❌ API failed to start within timeout"
    return 1
}

# Function to cleanup processes
cleanup_processes() {
    if lsof -ti:$API_PORT >/dev/null 2>&1; then
        echo "Stopping existing API processes..."
        lsof -ti:$API_PORT | xargs kill -9 2>/dev/null || true
        sleep 2
    fi
}

# Function to wait for database using Docker health checks
wait_for_database() {
    local service_name=$1
    local max_attempts=60
    local attempt=1
    
    echo "Waiting for $service_name database to be healthy..."
    
    while [ $attempt -le $max_attempts ]; do
        if docker compose -f docker-compose.databases.yml ps --filter "name=printfarmer-$service_name" --format "table {{.State}}" | grep -q "healthy"; then
            echo "$service_name database is ready!"
            return 0
        fi
        
        sleep 2
        attempt=$((attempt + 1))
    done
    
    echo "❌ $service_name database failed to become healthy within timeout"
    return 1
}

# Test SQLite (default)
echo -e "${YELLOW}Testing SQLite (default)...${NC}"
cleanup_processes
cd src
DB_PROVIDER="SQLite" dotnet run --project server/Farm.Web.Server.csproj --no-build &
API_PID=$!
sleep 3

if wait_for_api && check_api_health; then
    echo -e "${GREEN}✅ SQLite: Health check passed${NC}"
else
    echo -e "${RED}❌ SQLite: Health check failed${NC}"
fi

cleanup_processes
cd ..
echo "SQLite test completed."
echo ""

# Test PostgreSQL
echo -e "${YELLOW}Testing PostgreSQL...${NC}"
echo "Starting postgres database..."
docker compose -f docker-compose.databases.yml up postgres -d

if wait_for_database "postgres"; then
    cd src
    export DB_PROVIDER="Postgres"
    export ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=farm_db;Username=farm_user;Password=farm_password"
    
    echo "Starting API with PostgreSQL..."
    dotnet run --project server/Farm.Web.Server.csproj --no-build &
    API_PID=$!
    sleep 5
    
    if wait_for_api && check_api_health; then
        echo -e "${GREEN}✅ PostgreSQL: Health check passed${NC}"
    else
        echo -e "${RED}❌ PostgreSQL: Health check failed${NC}"
    fi
    
    cleanup_processes
    cd ..
else
    echo -e "${RED}❌ PostgreSQL: Database failed to start${NC}"
fi

echo "Stopping postgres database..."
docker compose -f docker-compose.databases.yml stop postgres
echo "PostgreSQL test completed."
echo ""

# Test MySQL
echo -e "${YELLOW}Testing MySQL...${NC}"
echo "Starting mysql database..."
docker compose -f docker-compose.databases.yml up mysql -d

if wait_for_database "mysql"; then
    cd src
    export DB_PROVIDER="MySql"
    export ConnectionStrings__MySql="Server=localhost;Port=3306;Database=farm_db;Uid=farm_user;Pwd=farm_password"
    
    echo "Starting API with MySQL..."
    dotnet run --project server/Farm.Web.Server.csproj --no-build &
    API_PID=$!
    sleep 5
    
    if wait_for_api && check_api_health; then
        echo -e "${GREEN}✅ MySQL: Health check passed${NC}"
    else
        echo -e "${RED}❌ MySQL: Health check failed${NC}"
    fi
    
    cleanup_processes
    cd ..
else
    echo -e "${RED}❌ MySQL: Database failed to start${NC}"
fi

echo "Stopping mysql database..."
docker compose -f docker-compose.databases.yml stop mysql
echo "MySQL test completed."
echo ""

# Test SQL Server
echo -e "${YELLOW}Testing SQL Server...${NC}"
echo "Starting sqlserver database..."
docker compose -f docker-compose.databases.yml up sqlserver -d

if wait_for_database "sqlserver"; then
    cd src
    export DB_PROVIDER="SqlServer"
    export ConnectionStrings__SqlServer="Server=localhost,1433;Database=farm_db;User Id=sa;Password=PrintFarm123!;TrustServerCertificate=true"
    
    echo "Starting API with SQL Server..."
    dotnet run --project server/Farm.Web.Server.csproj --no-build &
    API_PID=$!
    sleep 5
    
    if wait_for_api && check_api_health; then
        echo -e "${GREEN}✅ SQL Server: Health check passed${NC}"
    else
        echo -e "${RED}❌ SQL Server: Health check failed${NC}"
    fi
    
    cleanup_processes
    cd ..
else
    echo -e "${RED}❌ SQL Server: Database failed to start${NC}"
fi

echo "Stopping sqlserver database..."
docker compose -f docker-compose.databases.yml stop sqlserver
echo "SQL Server test completed."
echo ""

# Cleanup
echo "Cleaning up all database services..."
docker compose -f docker-compose.databases.yml down

echo ""
echo -e "${GREEN}=== Database provider testing completed! ===${NC}"
