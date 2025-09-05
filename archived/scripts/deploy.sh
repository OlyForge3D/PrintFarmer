#!/bin/bash
set -e

echo "🚀 Deploying PrintFarmer React Application..."

# Load environment variables
if [ -f .env ]; then
    export $(cat .env | grep -v '#' | xargs)
else
    echo "⚠️  No .env file found. Creating from template..."
    cp .env.template .env
    echo "❌ Please configure .env file and run again"
    exit 1
fi

# Check for required environment variables
if [ -z "$JWT_SECRET" ] || [ "$JWT_SECRET" = "your-super-secret-jwt-key-minimum-32-characters-long-for-security" ]; then
    echo "❌ Please set JWT_SECRET in .env file"
    exit 1
fi

# Create necessary directories
echo "📁 Creating data directories..."
mkdir -p ./data/{uploads,gcode,postgres,redis,slicers}

# Stop existing containers
echo "🛑 Stopping existing containers..."
docker-compose down

# Pull latest images
echo "📥 Pulling latest images..."
docker-compose pull postgres redis

# Start services
echo "🚀 Starting services..."
docker-compose up -d

# Wait for database to be ready
echo "⏳ Waiting for database to be ready..."
timeout=60
while ! docker-compose exec postgres pg_isready -U printfarmer -d printfarmer >/dev/null 2>&1; do
    if [ $timeout -eq 0 ]; then
        echo "❌ Database failed to start within 60 seconds"
        docker-compose logs postgres
        exit 1
    fi
    echo "  Waiting for PostgreSQL... ($timeout seconds remaining)"
    sleep 1
    ((timeout--))
done

echo "✅ Database is ready!"

# Show status
echo "📊 Deployment Status:"
docker-compose ps

echo ""
echo "🌐 PrintFarmer is now running!"
echo "   Web App: http://localhost:5000"
echo "   API: http://localhost:5000/api"
echo "   Health: http://localhost:5000/health"
echo ""
echo "📝 To view logs: docker-compose logs -f"
echo "🛑 To stop: docker-compose down"
echo "🔄 To restart: docker-compose restart"