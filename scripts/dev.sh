#!/bin/bash
set -e

echo "🛠️  Starting PrintFarmer Development Environment..."

# Check if Node.js and npm are available
if ! command -v node &> /dev/null; then
    echo "❌ Node.js is not installed. Please install Node.js 18+ first."
    exit 1
fi

if ! command -v docker &> /dev/null; then
    echo "❌ Docker is not installed. Please install Docker first."
    exit 1
fi

# Start PostgreSQL for development
echo "🗄️  Starting PostgreSQL for development..."
docker-compose --profile dev up -d postgres-dev redis

# Wait for database
echo "⏳ Waiting for database..."
timeout=30
while ! docker-compose --profile dev exec postgres-dev pg_isready -U printfarmer -d printfarmer_dev >/dev/null 2>&1; do
    if [ $timeout -eq 0 ]; then
        echo "❌ Database failed to start"
        exit 1
    fi
    sleep 1
    ((timeout--))
done

echo "✅ Database is ready!"

# Check if React app exists
if [ ! -d "src/Web/ClientApp" ]; then
    echo "📁 React app directory not found. This will be created during Phase 1 implementation."
    echo "   For now, starting API only..."
    
    # Start API only
    cd src/api
    echo "🚀 Starting API in development mode..."
    dotnet watch run --urls="http://0.0.0.0:5000"
else
    # Start both API and React in parallel
    echo "🚀 Starting React development server..."
    cd src/Web/ClientApp
    npm install
    npm run dev &
    REACT_PID=$!

    cd ../../api
    echo "🚀 Starting API in development mode..."
    dotnet watch run --urls="http://0.0.0.0:5000" &
    API_PID=$!

    # Handle shutdown
    trap "kill $REACT_PID $API_PID 2>/dev/null; docker-compose --profile dev down; exit" INT TERM

    echo ""
    echo "🌐 Development servers started!"
    echo "   React App: http://localhost:5173"
    echo "   API: http://localhost:5000"
    echo "   Database: localhost:5433"
    echo ""
    echo "Press Ctrl+C to stop all services"
    
    wait
fi