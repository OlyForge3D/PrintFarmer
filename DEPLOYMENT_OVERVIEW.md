# PrintFarmer - Deployment Guide Overview

This document provides a complete overview of PrintFarmer deployment options and helps you choose the right approach for your needs.

## 🎯 Choose Your Deployment Path

### Quick Decision Matrix

| Use Case | Recommended Approach | Why |
|----------|---------------------|-----|
| **Active Development** | [Local Development](#local-development) | Fast builds, WiFi access, easier debugging |
| **macOS + WiFi Printers** | [Local Development](#local-development) | Docker on macOS can't reach WiFi devices |
| **Production Deployment** | [Docker (Automated)](#docker-automated) | Consistent, scalable, easy maintenance |
| **Team/Staging Environment** | [Docker (Microservices)](#docker-microservices) | Separate services, better monitoring |
| **Quick Testing** | [Docker (Monolithic)](#docker-monolithic) | Single container, minimal setup |

## 🚀 Local Development

**Best for:** Active development, debugging, macOS users with WiFi printers

### Quick Start
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Automated setup
chmod +x scripts/setup-local.sh
./scripts/setup-local.sh
```

### Manual Setup
```bash
cd PrintFarmer/src

# Terminal 1 - API Server
dotnet run --project api/Farm.Web.Api.csproj

# Terminal 2 - React Client  
cd Web/ReactApp && npm run dev
```

### Access Points
- **React App**: http://localhost:3000
- **API Server**: http://localhost:5245
- **API Health**: http://localhost:5245/healthz (alias: http://localhost:5245/api/healthz)

### Advantages
✅ **Full WiFi Access** - Can discover printers on WiFi networks  
✅ **Fast Development** - Hot reload, quick builds  
✅ **Easy Debugging** - Native debugging tools  
✅ **No Docker Overhead** - Direct execution on your machine  

### Requirements
- .NET 9.0.302 SDK
- Node.js 18+
- 2GB+ RAM

📖 **Detailed Guide**: [LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md)

---

## 🐳 Docker Deployment

### Docker (Automated)

**Best for:** Most users, production deployment, quick setup

```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# Automated setup with prompts
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh
```

The script will:
1. Detect your environment (macOS/Linux/Windows)
2. Guide you through architecture selection
3. Configure database and networking
4. Deploy and verify everything works

### Docker (Monolithic)

**Best for:** Simple deployments, testing, single-server setups

```bash
# Quick monolithic deployment
docker compose --env-file .env.monolithic up -d --build
```

**Architecture**: Single container with API + React + Nginx
- **Simpler**: One container to manage
- **Lighter**: Fewer resources required  
- **Faster**: Quick to start and deploy

### Docker (Microservices)

**Best for:** Production, team environments, scalability

```bash  
# Microservices deployment
docker compose --env-file .env.microservices up -d --build
```

**Architecture**: Separate API, Web, Database, Redis containers
- **Scalable**: Independent container scaling
- **Robust**: Better fault isolation
- **Monitoring**: Separate service health checks

### Platform Considerations

| Platform | Network Discovery | Recommendation |
|----------|------------------|----------------|
| **Linux** | ✅ Full support | Docker (any architecture) |
| **Windows** | ✅ Good support | Docker (monolithic recommended) |
| **macOS** | ⚠️ Limited WiFi access | Local development preferred |

📖 **Detailed Guide**: [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)

---

## 🔧 Configuration Options

### Database Providers

| Provider | Use Case | Connection Example |
|----------|----------|-------------------|
| **SQLite** | Development, small deployments | `Data Source=/data/farm.db` |
| **PostgreSQL** | Production, high concurrency | `Host=postgres;Database=printfarmer;...` |
| **SQL Server** | Enterprise environments | `Server=sqlserver;Database=printfarmer;...` |
| **MySQL** | Popular open-source option | `Server=mysql;Database=printfarmer;...` |

### Network Discovery

Configure IP ranges to scan for printers:
```bash
ALLOW_LOCAL_NETWORK=true
ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
```

**Common Network Ranges:**
- `192.168.0.0/16` - Home networks (192.168.x.x)
- `10.0.0.0/8` - Corporate networks (10.x.x.x)
- `172.16.0.0/12` - Docker networks (172.16-31.x.x)

### Environment Configuration

Use `.env.template` as a starting point:
```bash
cp .env.template .env.monolithic
# Edit .env.monolithic with your settings
```

---

## 🎛️ Management & Operations

### Health Monitoring
```bash
# Basic health check (either path works)
curl http://localhost:8080/healthz
# or
curl http://localhost:8080/api/healthz

# Comprehensive health status (either path works)
curl http://localhost:8080/health | jq '.'
# or
curl http://localhost:8080/api/health | jq '.'
```

### Log Management
```bash
# Docker logs
docker compose logs -f api

# Local development
# Logs appear in terminal output
```

### Updates & Maintenance
```bash
# Docker update
git pull origin main
docker compose up -d --build

# Local development update  
git pull origin main
cd src && dotnet build ./farm-web.sln
```

---

## 🔍 Troubleshooting Quick Reference

### Common Issues

**"External service unavailable"**
```bash
# Check if API is running
curl http://localhost:5245/healthz  # Local
curl http://localhost:8080/healthz  # Docker
```

**Network discovery not finding printers**  
```bash
# Test direct printer access
curl http://YOUR_PRINTER_IP:7125/printer/info

# Check network configuration
# macOS + Docker: Use local development instead
```

**Build failures**
```bash
# Check .NET version
dotnet --info  # Should show 9.0.x

# Clean rebuild
cd src
dotnet clean && dotnet build ./farm-web.sln
```

### Platform-Specific

**macOS Users:**
- Use local development for WiFi printer access
- Docker Desktop has WiFi networking limitations
- Consider local development for active development

**Linux Users:**
- Docker provides full networking capabilities
- Can use enhanced networking features
- Best platform for Docker deployment

**Windows Users:**
- Good Docker compatibility
- Linux containers recommended over Windows containers
- WSL2 provides better Docker performance

---

## 📊 Performance & Scaling

### Resource Requirements

| Deployment | CPU | RAM | Storage | Network |
|------------|-----|-----|---------|---------|
| **Local Dev** | 2+ cores | 2GB+ | 1GB+ | WiFi/Ethernet |
| **Docker Mono** | 2+ cores | 4GB+ | 5GB+ | Ethernet preferred |
| **Docker Micro** | 4+ cores | 8GB+ | 10GB+ | Ethernet/WiFi |

### Scaling Considerations

**Local Development:**
- Single instance only
- Good for 1-10 printers
- Limited by machine resources

**Docker Monolithic:**
- Single container scaling
- Good for 10-50 printers  
- Easier management

**Docker Microservices:**
- Independent service scaling
- Good for 50+ printers
- Can scale API and Web separately

---

## 📚 Additional Resources

### Documentation
- **[LOCAL_DEVELOPMENT.md](LOCAL_DEVELOPMENT.md)** - Complete local setup guide
- **[DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md)** - Complete Docker guide  
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Development guidelines

### Scripts
- **`scripts/setup-local.sh`** - Automated local development setup
- **`scripts/deploy-docker.sh`** - Automated Docker deployment
- **`test-providers.sh`** - Test database providers

### Configuration Files
- **`.env.template`** - Environment configuration template
- **`docker-compose.yml`** - Docker Compose configuration
- **`global.json`** - .NET SDK version requirements

---

## 🎉 Getting Started

### For Developers
```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
./scripts/setup-local.sh
```

### For Production
```bash  
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer
./scripts/deploy-docker.sh
```

### Need Help?
1. Check the appropriate detailed guide (LOCAL_DEVELOPMENT.md or DOCKER_DEPLOYMENT.md)
2. Review troubleshooting sections
3. Check GitHub Issues for known problems
4. Create a new issue with detailed error information

---

**Happy printing! 🖨️🎉**
