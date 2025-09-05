# PrintFarmer

A React TypeScript dashboard for managing ## Detailed Documentation

**📋 [Deployment Overview](DEPLOYMENT_OVERVIEW.md)** - Choose the right deployment approach for your needs  
**🔧 [Local Development Guide](LOCAL_DEVELOPMENT.md)** - Development setup, hot reload, debugging  
**🐳 [Docker Deployment Guide](DOCKER_DEPLOYMENT.md)** - Production containers, scaling, monitoring  
**📡 [Service Interfaces Documentation](INTERFACE_DOCUMENTATION_SUMMARY.md)** - Complete API service interfaces with XML documentation  
**🤝 [Contributing Guide](CONTRIBUTING.md)** - Development workflow, testing, code standardsle 3D printers. Supports Moonraker and PrusaLink backends, normalizes camera URLs, resolves hostnames to IPs, and streams live status via SignalR.

## Features
- **Multi-backend Support**: Moonraker and PrusaLink API integration
- **Real-time Updates**: Live printer status via SignalR
- **Network Discovery**: Automatic detection of printers on your network
- **Modern UI**: React TypeScript frontend with responsive design
- **Flexible Database**: SQLite, PostgreSQL, SQL Server, MySQL support
- **Docker Ready**: Production deployment with containers
- **WiFi Friendly**: Works with WiFi-connected printers (local development)

## Quick Start - Choose Your Path

### 🚀 **Automated Docker Deployment (Recommended for Production)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh
```
The script will guide you through configuration and deploy everything automatically.

### 💻 **Local Development (Recommended for Development)**
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
chmod +x scripts/setup-local.sh
./scripts/setup-local.sh
```
Direct development on your machine without containers.

### 📖 **Detailed Guidance**
**Not sure which approach to use?** See our comprehensive [Deployment Overview](DEPLOYMENT_OVERVIEW.md) that helps you choose based on your specific needs.

## Architecture

### Two-Tier Modern Stack
```
React TypeScript Frontend (localhost:3000)
           ↕ HTTP + SignalR
ASP.NET Core API Backend (localhost:5245)  
           ↕
      Database (SQLite/PostgreSQL/etc.)
```

### Repository Structure
```
src/
  api/              # ASP.NET Core API server (.NET 9)
  Web/ReactApp/     # React TypeScript frontend (Vite + React 19)  
  shared/           # DTOs and models shared between frontend/backend
  tests/            # Integration and unit tests
  farm-web.sln      # .NET solution file
```

## Detailed Documentation

**� [Deployment Overview](DEPLOYMENT_OVERVIEW.md)** - Choose the right deployment approach for your needs  
**🔧 [Local Development Guide](LOCAL_DEVELOPMENT.md)** - Development setup, hot reload, debugging  
**🐳 [Docker Deployment Guide](DOCKER_DEPLOYMENT.md)** - Production containers, scaling, monitoring  
**🤝 [Contributing Guide](CONTRIBUTING.md)** - Development workflow, testing, code standards

## System Requirements

### Local Development
- **.NET SDK 9.0+** (exactly 9.0.302 as specified in global.json)
- **Node.js 18+** and npm (for React frontend)
- **macOS/Windows/Linux** (macOS recommended for WiFi device access)

### Docker Deployment  
- **Docker 20.10+** and Docker Compose v2
- **Linux** (recommended for full networking), **Windows**, or **macOS**
- **4GB+ RAM** and **10GB+ storage** for containerized deployment

## Key API Endpoints

- `GET /healthz` — Basic health check
- `GET /health` — Comprehensive health status
- `GET /api/printers` — List all configured printers  
- `POST /api/printers` — Add a new printer
- `POST /api/printers/discover-streaming` — Real-time network discovery
- `GET /api/network-discovery/settings` — Network discovery configuration
- **SignalR Hub**: `/hubs/printers` — Real-time printer status updates

## Network Discovery Features

**Automatic Printer Detection:**
- Scans configurable IP ranges for Moonraker/Klipper printers
- Real-time progress updates via SignalR
- Supports WiFi and Ethernet connected devices

**Platform Considerations:**
- **Local Development**: Full WiFi device access on all platforms
- **Docker on Linux**: Full network access with proper configuration  
- **Docker on macOS**: Limited WiFi device access (use local development)
- **Docker on Windows**: Good network access with Windows containers

## Technology Stack

### Frontend (React TypeScript)
- **React 19** with TypeScript for type safety
- **Vite** for fast development and optimized builds
- **Tailwind CSS** for modern responsive design
- **React Query** for server state management
- **SignalR Client** for real-time updates

### Backend (ASP.NET Core)
- **.NET 9** with ASP.NET Core API
- **Entity Framework Core** with multi-database support  
- **SignalR** for real-time communication
- **Refit** for external API clients (Moonraker, PrusaLink)
- **FluentValidation** for input validation

### Database Support
- **SQLite** (default) - Simple file-based database
- **PostgreSQL** (recommended for production) - Advanced features
- **SQL Server** - Enterprise database support
- **MySQL** - Popular open-source option

## Development Workflow

### Local Development (Recommended)
1. **API Backend**: `dotnet run --project api/Farm.Web.Api.csproj` 
2. **React Frontend**: `cd Web/ReactApp && npm run dev`
3. **Open**: http://localhost:3000 (auto-connects to API)

### Docker Development
1. **Automated**: `./scripts/deploy-docker.sh`
2. **Manual**: `docker compose up -d --build`
3. **Open**: http://localhost:8080

## Testing

### Automated Tests
```bash
# .NET API tests (62 integration tests)
cd src && dotnet test ./farm-web.sln

# React component tests  
cd src/Web/ReactApp && npm test
```

### Manual Verification
```bash
# Test API health
curl http://localhost:5245/healthz

# Test network discovery
curl -X POST http://localhost:5245/api/printers/discover-streaming

# Test React app
curl http://localhost:3000/
```

## Configuration

### Database Configuration
Environment variables control database selection:
```bash
# SQLite (default)
DB_PROVIDER=sqlite
ConnectionStrings__Default=Data Source=farm.db

# PostgreSQL  
DB_PROVIDER=postgres
ConnectionStrings__Postgres=Host=localhost;Database=printfarmer;...
```

### Network Discovery
Configure IP ranges to scan for printers:
```bash
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
```

### Development vs Production
```bash
# Development
ASPNETCORE_ENVIRONMENT=Development  # Enables Swagger, detailed logging

# Production  
ASPNETCORE_ENVIRONMENT=Production   # Optimized for performance
```

## Deployment Options

### 🏠 **Single Machine** 
- **Local Development**: Direct .NET + React execution
- **Docker Single Container**: All-in-one container

### 🏢 **Team/Production**
- **Docker Microservices**: Separate API, Web, Database, Redis containers
- **Kubernetes**: Full orchestration (advanced)
- **Cloud**: Azure Container Instances, AWS ECS, etc.

## Troubleshooting

### Common Issues

**"External service unavailable"**
- API server not running or wrong port
- Check: `curl http://localhost:5245/healthz`

**Network discovery not finding printers**
- Configure correct IP ranges in settings
- macOS Docker: Use local development instead
- Check printer accessibility: `curl http://YOUR_PRINTER_IP:7125/printer/info`

**Build failures**
- Verify .NET 9.0.302 SDK installed: `dotnet --info`
- Clean rebuild: `dotnet clean && dotnet build`

### Getting Help

1. **Check Documentation**: [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md) or [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)  
2. **Review Issues**: GitHub Issues for known problems
3. **Check Logs**: Application logs for detailed error information

## Contributing

We welcome contributions! See [CONTRIBUTING.md](CONTRIBUTING.md) for:
- Development environment setup
- Code style guidelines  
- Testing requirements
- Pull request process

## License

This project is licensed under the MIT License - see the LICENSE file for details.
