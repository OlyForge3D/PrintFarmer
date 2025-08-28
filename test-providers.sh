#!/bin/bash
#!/bin/bash
# Test script for PrintFarmer database providers
# Usage: ./test-providers.sh [provider]
# Available providers: sqlite, postgres, mysql, sqlserver
# If no provider specified, tests all providers

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
PRINTERS_ENDPOINT="$API_URL/api/printers"

# Function to check if API is running
check_api_health() {
    curl -s -o /dev/null -w "%{http_code}" "$HEALTH_ENDPOINT" | grep -q "200"
}

# Function to check printers endpoint
check_printers_endpoint() {
    curl -s -o /dev/null -w "%{http_code}" "$PRINTERS_ENDPOINT" | grep -q "200"
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

# Function to wait for database to be ready using docker health checks
wait_for_database() {
    local provider=$1
    local container_name="printfarmer-$provider"
    local max_attempts=60
    local attempt=1
    
    echo "Waiting for database to be ready..."
    
    while [ $attempt -le $max_attempts ]; do
        if docker inspect "$container_name" --format='{{.State.Health.Status}}' 2>/dev/null | grep -q "healthy"; then
            echo "Database is ready!"
            return 0
        fi
        
        sleep 1
        attempt=$((attempt + 1))
    done
    
    echo "❌ Database failed to become ready within timeout"
    return 1
}

# Function to cleanup processes
cleanup_processes() {
    # Kill any running dotnet processes on our port
    if lsof -ti:$API_PORT >/dev/null 2>&1; then
        echo "Stopping existing API processes..."
        lsof -ti:$API_PORT | xargs kill -9 2>/dev/null || true
    fi
}

set -e

echo "=== PrintFarmer Database Provider Testing ==="
echo ""

# Function to test a provider
test_provider() {
    local provider=$1
    local connection_string=$2
    local db_service=${3:-""}
    
    echo "Testing $provider provider..."
    
    # Start database service if specified
    if [ ! -z "$db_service" ]; then
        echo "Starting $db_service database..."
        docker compose -f docker-compose.databases.yml up $db_service -d
        echo "Waiting for database to be ready..."
        docker compose -f docker-compose.databases.yml exec $db_service bash -c 'until pg_isready -h localhost -p 5432; do sleep 1; done' 2>/dev/null || \
        docker compose -f docker-compose.databases.yml exec $db_service bash -c 'until mysqladmin ping -h localhost --silent; do sleep 1; done' 2>/dev/null || \
        docker compose -f docker-compose.databases.yml exec $db_service bash -c 'until /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "Your_password123" -Q "SELECT 1" > /dev/null; do sleep 1; done' 2>/dev/null || \
        sleep 5
    fi
    
    # Test with the API directly
    echo "Testing $provider with local dotnet run..."
    cd src
    
    export DB_PROVIDER="$provider"
    if [ "$provider" = "Postgres" ]; then
        export ConnectionStrings__Postgres="$connection_string"
    elif [ "$provider" = "MySql" ]; then
        export ConnectionStrings__MySql="$connection_string"  
    elif [ "$provider" = "SqlServer" ]; then
        export ConnectionStrings__SqlServer="$connection_string"
    fi
    
    echo "Starting API with $provider..."
    timeout 30s dotnet run --project ./server/Farm.Web.Server.csproj &
    API_PID=$!
    
    # Wait for API to start
    echo "Waiting for API to start..."
    for i in {1..30}; do
        if curl -s http://localhost:5088/healthz > /dev/null 2>&1; then
            echo "API started successfully!"
            break
        fi
        sleep 1
    done
    
    # Test health endpoint
    response=$(curl -s http://localhost:5088/healthz || echo "failed")
    if [[ "$response" == *"ok"* ]]; then
        echo "✅ $provider: Health check passed"
    else
        echo "❌ $provider: Health check failed - $response"
    fi
    
    # Test printers endpoint
    response=$(curl -s http://localhost:5088/api/printers || echo "failed")
    if [[ "$response" == *"["* ]]; then
        echo "✅ $provider: Printers endpoint working"
    else
        echo "❌ $provider: Printers endpoint failed - $response"
    fi
    
    # Clean up
    kill $API_PID 2>/dev/null || true
    wait $API_PID 2>/dev/null || true
    
    cd ..
    echo "$provider test completed."
    echo ""
}

# Test SQLite (default - no external database needed)
echo "Testing SQLite (default)..."
cd src
export DB_PROVIDER="Sqlite"
export ConnectionStrings__Default="Data Source=test-farm.db"
timeout 15s dotnet run --project ./server/Farm.Web.Server.csproj &
API_PID=$!

echo "Waiting for API to start..."
for i in {1..15}; do
    if curl -s http://localhost:5088/healthz > /dev/null 2>&1; then
        echo "API started successfully!"
        break
    fi
    sleep 1
done

response=$(curl -s http://localhost:5088/healthz || echo "failed")
if [[ "$response" == *"ok"* ]]; then
    echo "✅ SQLite: Health check passed"
else
    echo "❌ SQLite: Health check failed - $response"
fi

kill $API_PID 2>/dev/null || true
wait $API_PID 2>/dev/null || true
rm -f test-farm.db 2>/dev/null || true
cd ..

echo "SQLite test completed."
echo ""

# Test PostgreSQL
test_provider "Postgres" "Host=localhost;Database=forgeiq;Username=postgres;Password=postgres" "postgres"

# Test MySQL  
test_provider "MySql" "Server=localhost;Database=forgeiq;User=root;Password=example" "mysql"

# Test SQL Server
test_provider "SqlServer" "Server=localhost;Database=forgeiq;User Id=sa;Password=Your_password123;TrustServerCertificate=True" "sqlserver"

echo "=== All provider tests completed ==="
