# PrintFarmer Deployment Architectures

This document explains the two deployment architectures supported by PrintFarmer: **Monolithic** and **Microservices**.

## Architecture Comparison

### Monolithic Deployment (Default)
```
┌─────────────────────────────────────┐
│           Single Container          │
│                                     │
│  ┌─────────────────────────────┐   │
│  │        React SPA            │   │
│  │    (served by ASP.NET)      │   │
│  └─────────────────────────────┘   │
│  ┌─────────────────────────────┐   │
│  │       ASP.NET API           │   │
│  │       SignalR Hubs          │   │
│  └─────────────────────────────┘   │
└─────────────────────────────────────┘
           Port 8080
```

### Microservices Deployment
```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Load Balancer  │    │    Frontend     │    │    Backend      │
│     (Nginx)     │    │   Container     │    │   Container     │
│                 │    │                 │    │                 │
│ ┌─────────────┐ │    │ ┌─────────────┐ │    │ ┌─────────────┐ │
│ │   Nginx     │ │    │ │ Nginx +     │ │    │ │ ASP.NET API │ │
│ │   Proxy     │ │────┤ │ React SPA   │ │    │ │ SignalR     │ │
│ └─────────────┘ │    │ └─────────────┘ │    │ └─────────────┘ │
└─────────────────┘    └─────────────────┘    └─────────────────┘
    Port 8080              Port 3000              Port 5000
```

## Quick Start

### Monolithic Deployment (Recommended for Development)

```bash
# Use the default docker-compose.yml
docker compose down
docker compose build --no-cache
docker compose up -d

# Access the application at http://localhost:8080
```

### Microservices Deployment (Recommended for Production)

```bash
# Use the microservices configuration
docker compose -f docker-compose.microservices.yml down
docker compose -f docker-compose.microservices.yml build --no-cache
docker compose -f docker-compose.microservices.yml up -d

# Access the application at http://localhost:8080 (load balanced)
# Direct access: Frontend at http://localhost:3000, API at http://localhost:5000
```

## Configuration Files

### Environment Variables

**Monolithic (.env.monolithic):**
```bash
# API base URL (relative for same origin)
REACT_APP_API_BASE_URL=http://localhost:8080
REACT_APP_SIGNALR_URL=http://localhost:8080/hubs/printers

# Deployment mode (omit or set to anything except "microservices")
# DEPLOYMENT_MODE=monolithic
```

**Microservices (.env.microservices):**
```bash
# API base URL (cross-origin)
REACT_APP_API_BASE_URL=http://localhost:5000
REACT_APP_SIGNALR_URL=http://localhost:5000/hubs/printers

# Deployment mode
DEPLOYMENT_MODE=microservices
```

## Benefits Comparison

| Aspect | Monolithic | Microservices |
|--------|------------|---------------|
| **Development** | Simpler setup | More complex |
| **Deployment** | Single container | Multiple containers |
| **Scaling** | Scale entire app | Scale components independently |
| **Resource Usage** | Lower overhead | Higher overhead |
| **Network** | Internal calls | HTTP/network calls |
| **Debugging** | Single service | Distributed tracing needed |
| **Production** | Good for small/medium apps | Better for large/enterprise |

## When to Use Each

### Choose Monolithic When:
- Development or staging environments
- Small to medium applications
- Team size < 5 developers
- Simpler deployment requirements
- Lower resource constraints

### Choose Microservices When:
- Production environments with high load
- Large applications with multiple teams
- Need independent scaling of components
- Have dedicated DevOps resources
- Planning for high availability

## Technical Details

### Monolithic Architecture
- **Container**: Single container with ASP.NET Core + React
- **Communication**: In-process, direct method calls
- **Static Files**: Served by ASP.NET Core SPA middleware
- **SignalR**: Internal websocket connections
- **Database**: Single SQLite instance

### Microservices Architecture
- **Containers**: 
  - Frontend: Nginx + React SPA (port 3000)
  - Backend: ASP.NET Core API only (port 5000)
  - Load Balancer: Nginx proxy (port 8080)
  - Redis: Session/cache store (port 6379)
- **Communication**: HTTP REST API calls + WebSocket (SignalR)
- **Static Files**: Served by dedicated Nginx
- **SignalR**: Cross-origin websocket connections
- **Database**: Shared SQLite (could be moved to separate container)

## Migration Path

### From Monolithic to Microservices
1. Test microservices deployment in staging
2. Update DNS/load balancer configuration
3. Deploy microservices architecture
4. Monitor and optimize

### From Microservices to Monolithic
1. Update environment variables
2. Redeploy with monolithic configuration
3. Consolidate resources

## Troubleshooting

### Common Issues

**CORS Errors in Microservices:**
```bash
# Check API CORS configuration
curl -H "Origin: http://localhost:3000" http://localhost:5000/api/printers
```

**SignalR Connection Issues:**
```bash
# Test SignalR endpoint
curl -H "Origin: http://localhost:3000" http://localhost:5000/hubs/printers
```

**Static File Issues in Monolithic:**
```bash
# Verify React build exists
ls -la src/Web/ReactApp/dist/
```

### Health Checks

**Monolithic:**
```bash
curl http://localhost:8080/healthz     # API health
curl http://localhost:8080/            # Frontend
```

**Microservices:**
```bash
curl http://localhost:5000/healthz     # API health
curl http://localhost:3000/health      # Frontend health
curl http://localhost:8080/health      # Load balancer health
```

## Performance Considerations

### Monolithic
- Lower latency (no network calls)
- Single point of failure
- Resource sharing between components

### Microservices
- Higher latency (network overhead)
- Better fault isolation
- Independent resource allocation
- Easier horizontal scaling

## Monitoring and Logging

Both architectures support:
- Application health checks
- Container health monitoring
- SignalR connection tracking
- API request/response logging

Additional for Microservices:
- Service-to-service communication tracking
- Load balancer metrics
- Cross-origin request monitoring
