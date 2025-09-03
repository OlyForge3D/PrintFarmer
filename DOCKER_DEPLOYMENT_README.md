# PrintFarmer React Docker Deployment Guide

This guide explains how to deploy PrintFarmer with the new React frontend architecture using Docker and Docker Compose.

## 🏗️ Architecture Overview

The React-based PrintFarmer uses a unified deployment approach:
- **Single Container**: React app + ASP.NET Core API (.NET 9) served together
- **PostgreSQL**: Recommended database for production
- **Redis**: Caching and SignalR backplane
- **Nginx** (optional): Reverse proxy for production

## 🚀 Quick Start

### 1. Production Deployment

```bash
# Clone and navigate to the repository
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Configure environment
cp .env.template .env
# Edit .env with your settings (especially JWT_SECRET and DB_PASSWORD)

# Deploy with Docker Compose
docker-compose up -d

# Check status
docker-compose ps
```

### 2. Development Environment

```bash
# Start development environment with hot reload
./scripts/dev.sh

# Or use Docker Compose profiles
docker-compose --profile dev up -d
```

## 📁 File Structure

```
PrintFarmer/
├── docker-compose.yml              # Main production configuration
├── docker-compose.override.yml     # Development/testing overrides  
├── Dockerfile.react                # Production build
├── Dockerfile.dev                  # Development with hot reload
├── .env.template                   # Environment configuration template
├── scripts/
│   ├── build.sh                   # Build Docker images
│   ├── deploy.sh                  # Full deployment script
│   └── dev.sh                     # Development environment
└── deploy/
    └── nginx/                     # Nginx configuration (optional)
```

## 🔧 Configuration

### Environment Variables (.env file)

```bash
# Database
DB_PASSWORD=your-secure-password
POSTGRES_PASSWORD=your-secure-password

# Authentication  
JWT_SECRET=your-super-secret-jwt-key-minimum-32-characters
JWT_ISSUER=PrintFarmer
JWT_AUDIENCE=PrintFarmerClient

# Application
ASPNETCORE_ENVIRONMENT=Production
CORS_ORIGINS=https://yourdomain.com
```

### Docker Compose Services

#### Main Service: `printfarmer`
- **Image**: Built from `Dockerfile.react`
- **Ports**: 5000:8080 (HTTP), 5001:8443 (HTTPS)
- **Volumes**: Data persistence, file uploads, G-code storage
- **Dependencies**: PostgreSQL, Redis

#### Database: `postgres`
- **Image**: `postgres:15-alpine`
- **Port**: 5432:5432
- **Volume**: `postgres-data` for persistence
- **Health Checks**: Built-in readiness checks

#### Cache: `redis`
- **Image**: `redis:7-alpine`
- **Volume**: `redis-data` for persistence
- **Configuration**: Optimized for SignalR backplane

## 🛠️ Build Process

### Multi-Stage Docker Build

The `Dockerfile.react` uses a multi-stage build:

1. **Stage 1**: React Build
   ```dockerfile
   FROM node:18-alpine AS react-build
   # Install dependencies and build React app
   RUN npm ci && npm run build
   ```

2. **Stage 2**: .NET Build
   ```dockerfile
   FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
   # Restore dependencies and publish API
   RUN dotnet publish -c Release
   ```

3. **Stage 3**: Final Runtime
   ```dockerfile
   FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
   # Combine React build + .NET API
   COPY --from=react-build /app/dist ./wwwroot
   COPY --from=dotnet-build /app/publish .
   ```

### Build Commands

```bash
# Manual build
docker build -f Dockerfile.react -t printfarmer-react:latest .

# Using build script
./scripts/build.sh

# Build for registry
./scripts/build.sh registry your-registry.com
```

## 🚀 Deployment Scenarios

### 1. Single Host Production

```bash
# Full deployment
./scripts/deploy.sh

# Manual deployment
docker-compose up -d
```

**Access Points:**
- Web App: http://localhost:5000
- API: http://localhost:5000/api
- Health Check: http://localhost:5000/health

### 2. Development with Hot Reload

```bash
# Start development environment
./scripts/dev.sh

# Or manually
docker-compose --profile dev up -d
cd src/Web/ClientApp && npm run dev &
cd src/Web/Api && dotnet watch run
```

**Access Points:**
- React Dev Server: http://localhost:5173
- API: http://localhost:5000
- Database: localhost:5433

### 3. Alternative Databases

```bash
# Test with SQL Server
docker-compose --profile sqlserver up -d

# Test with MySQL  
docker-compose --profile mysql up -d
```

## 📊 Monitoring & Health Checks

### Built-in Health Checks

```bash
# Application health
curl http://localhost:5000/health

# Database health
docker-compose exec postgres pg_isready -U printfarmer

# Container status
docker-compose ps
```

### Logs

```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f printfarmer
docker-compose logs -f postgres
```

## 🔐 Security Considerations

### Production Security

1. **JWT Secret**: Use a strong, unique JWT_SECRET (min 32 characters)
2. **Database Password**: Use a strong DB_PASSWORD
3. **CORS**: Configure specific CORS_ORIGINS for your domain
4. **HTTPS**: Enable SSL certificates for production
5. **Firewall**: Restrict database ports (5432, 6379) externally

### SSL/TLS Setup

```yaml
# docker-compose.yml - Add nginx service
nginx:
  image: nginx:alpine
  ports:
    - "443:443"
  volumes:
    - ./deploy/nginx/ssl:/etc/nginx/ssl:ro
```

## 🔄 Updates & Maintenance

### Updating the Application

```bash
# Pull latest code
git pull origin main

# Rebuild and restart
docker-compose down
docker-compose build --no-cache
docker-compose up -d
```

### Database Migrations

```bash
# Run migrations
docker-compose exec printfarmer dotnet ef database update

# Backup database
docker-compose exec postgres pg_dump -U printfarmer printfarmer > backup.sql
```

### Data Persistence

The following volumes ensure data persistence:
- `postgres-data`: Database data
- `redis-data`: Cache data
- `printfarmer-uploads`: User uploads
- `printfarmer-gcode`: G-code files
- `printfarmer-data`: Application data

## 🛠️ Troubleshooting

### Common Issues

1. **Port Conflicts**
   ```bash
   # Change ports in docker-compose.yml
   ports:
     - "5050:8080"  # Instead of 5000:8080
   ```

2. **Database Connection Issues**
   ```bash
   # Check database status
   docker-compose logs postgres
   docker-compose exec postgres pg_isready
   ```

3. **React Build Failures**
   ```bash
   # Check Node.js version in Dockerfile
   FROM node:18-alpine
   
   # Clear build cache
   docker-compose build --no-cache
   ```

### Performance Tuning

1. **PostgreSQL Configuration**
   ```yaml
   command: >
     postgres 
     -c shared_buffers=256MB
     -c max_connections=100
   ```

2. **Redis Memory Limit**
   ```yaml
   command: redis-server --maxmemory 512mb --maxmemory-policy allkeys-lru
   ```

## 📚 Additional Resources

- [Phase 1 Implementation Guide](/.github/issues/phase-1-react-foundation.md)
- [React Architecture Documentation](/REACT_MIGRATION_README.md)
- [API Documentation](/src/api/README.md)
- [PostgreSQL Best Practices](/docs/database.md)

## 🆘 Support

If you encounter issues:
1. Check the logs: `docker-compose logs -f`
2. Verify configuration: `docker-compose config`
3. Review GitHub issues: https://github.com/jpapiez/PrintFarmer/issues
4. Join discussions: https://github.com/jpapiez/PrintFarmer/discussions