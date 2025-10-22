# OpenTelemetry Implementation for PrintFarmer

This document describes the comprehensive OpenTelemetry implementation for PrintFarmer, providing distributed tracing, metrics collection, and observability across both the .NET API backend and React frontend.

## Overview

PrintFarmer now includes full OpenTelemetry instrumentation with the following components:

- **Backend (.NET API)**: ASP.NET Core instrumentation with custom telemetry service
- **Frontend (React)**: Web instrumentation with custom component lifecycle tracking
- **Infrastructure**: OpenTelemetry Collector, Jaeger, Prometheus, and Grafana

## Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   React App     │────│  .NET API        │────│  Database       │
│   (Frontend)    │    │  (Backend)       │    │  (EF Core)      │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                        │                       │
         │ OTLP Traces           │ OTLP Traces           │
         ▼                        ▼                       ▼
┌─────────────────────────────────────────────────────────────────┐
│                OpenTelemetry Collector                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐ │
│  │  Receivers  │─▶│ Processors  │─▶│      Exporters          │ │
│  │ - OTLP gRPC │  │ - Batch     │  │ - Jaeger (traces)       │ │
│  │ - OTLP HTTP │  │ - Memory    │  │ - Prometheus (metrics)  │ │
│  └─────────────┘  │ - Attributes│  │ - Logging (debug)       │ │
│                   └─────────────┘  └─────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                                │
                ┌───────────────┼──────────────┐
                ▼               ▼              ▼
    ┌───────────────┐ ┌─────────────┐ ┌──────────────┐
    │    Jaeger     │ │ Prometheus  │ │   Grafana    │
    │ (Tracing UI)  │ │ (Metrics)   │ │ (Dashboards) │
    │ :16686        │ │ :9090       │ │ :3000        │
    └───────────────┘ └─────────────┘ └──────────────┘
```

## Features Implemented

### Backend (.NET API)

1. **Automatic Instrumentation**:
   - ASP.NET Core HTTP requests
   - Entity Framework Core database operations
   - HTTP client calls (Refit clients)
   - SignalR operations

2. **Custom Instrumentation**:
   - PrintFarmerTelemetryService for domain-specific metrics
   - TelemetryMiddleware for request tracking
   - Custom metrics for:
     - API call counts and duration
     - Printer operations
     - Slicer operations  
     - File operations
     - Database operations

3. **Configuration**:
   - Environment-based OTLP endpoint configuration
   - Console exporter for development
   - Comprehensive resource attributes

### Frontend (React)

1. **Automatic Instrumentation**:
   - Document load performance
   - User interactions (clicks, form submissions)
   - Fetch/XHR HTTP requests with CORS configuration
   - Browser performance metrics

2. **Custom Instrumentation**:
   - Component lifecycle tracking (mount/unmount)
   - API call wrapping with telemetry
   - User interaction events
   - Async operation monitoring

3. **UI Components**:
   - ObservabilityDashboard: Real-time system metrics
   - TelemetrySettingsPage: Configure telemetry options

### Infrastructure

1. **OpenTelemetry Collector**:
   - OTLP receivers (gRPC and HTTP)
   - Batch processing for efficiency
   - Resource detection and attribute enrichment
   - Export to Jaeger and Prometheus

2. **Observability Stack**:
   - **Jaeger**: Distributed tracing visualization
   - **Prometheus**: Metrics storage and querying
   - **Grafana**: Dashboard creation and visualization

## Quick Start

### 1. Start the Observability Stack

```bash
# Start the telemetry infrastructure
docker-compose -f docker-compose.telemetry.yml up -d
```

This will start:
- OpenTelemetry Collector on ports 4317 (gRPC) and 4318 (HTTP)
- Jaeger UI at http://localhost:16686
- Prometheus at http://localhost:9090
- Grafana at http://localhost:3000 (admin/printfarmer)

### 2. Configure the Backend

The backend is automatically configured with OpenTelemetry. Set environment variables for external export:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4318/v1/traces"
export OTEL_EXPORTER_OTLP_HEADERS=""
```

### 3. Configure the Frontend

The frontend automatically initializes OpenTelemetry. For external export, set environment variables:

```bash
# In .env.development
VITE_OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318/v1/traces
VITE_OTEL_EXPORTER_OTLP_HEADERS={}
```

### 4. Run the Application

```bash
# Start the API server
cd src
dotnet run --project ./api/Farm.Web.Api.csproj

# Start the React client
cd src/Web/ReactApp
npm run dev
```

## Monitoring and Observability

### Accessing the UIs

1. **PrintFarmer Application**: http://localhost:3001
2. **Jaeger Tracing**: http://localhost:16686
3. **Prometheus Metrics**: http://localhost:9090
4. **Grafana Dashboards**: http://localhost:3000

### Key Metrics to Monitor

#### Backend Metrics
- `printfarmer_api_calls_total`: Total API requests
- `printfarmer_api_call_duration_seconds`: Request latency
- `printfarmer_printer_operations_total`: Printer interactions
- `printfarmer_slicer_operations_total`: Slicer job metrics
- `printfarmer_database_operations_total`: Database query metrics

#### Frontend Metrics
- Document load performance
- User interaction events
- API call success/failure rates
- Component render performance

### Distributed Tracing

Traces automatically flow between:
- React frontend → .NET API
- .NET API → Database (EF Core)
- .NET API → External services (Moonraker, PrusaLink)
- .NET API → SignalR hubs

## Configuration Options

### Backend Configuration (appsettings.json)

```json
{
  "OpenTelemetry": {
    "ServiceName": "PrintFarmer.API",
    "ServiceVersion": "1.0.0",
    "OTLP": {
      "Endpoint": "http://localhost:4318/v1/traces",
      "Headers": ""
    }
  }
}
```

### Frontend Configuration (Environment Variables)

```bash
VITE_OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318/v1/traces
VITE_OTEL_EXPORTER_OTLP_HEADERS={}
```

### Docker Configuration

The telemetry stack can be customized by modifying:
- `docker-compose.telemetry.yml`: Service definitions
- `otel-collector-config.yaml`: Collector configuration
- `prometheus.yml`: Metrics scraping configuration
- `grafana/provisioning/`: Dashboard and datasource configs

## Development Workflow

### 1. During Development

- Traces are automatically collected and exported to console (development mode)
- Use browser dev tools to see trace information
- Check the ObservabilityDashboard component for real-time stats

### 2. For Testing

- Start the full observability stack with Docker Compose
- Use Jaeger to trace request flows
- Use Prometheus to query specific metrics
- Create custom Grafana dashboards for monitoring

### 3. For Production

- Configure external OTLP endpoints (e.g., Datadog, New Relic, Honeycomb)
- Adjust sampling rates for performance
- Set up alerting rules in Prometheus
- Use Grafana for operational dashboards

## Troubleshooting

### Common Issues

1. **No traces appearing in Jaeger**:
   - Verify OTLP collector is running: `curl http://localhost:4318/v1/traces`
   - Check collector logs: `docker logs printfarmer-otel-collector`
   - Ensure CORS is configured for frontend requests

2. **High memory usage**:
   - Adjust batch processor settings in collector config
   - Increase memory_limiter settings
   - Consider sampling rates in production

3. **Frontend traces not correlating**:
   - Verify CORS configuration in collector
   - Check propagateTraceHeaderCorsUrls in frontend config
   - Ensure both frontend and backend use same OTLP endpoint

### Debugging Commands

```bash
# Check collector health
curl http://localhost:13133/health

# Test OTLP endpoint
curl -X POST http://localhost:4318/v1/traces \
  -H "Content-Type: application/json" \
  -d '{"resourceSpans":[]}'

# View collector configuration
docker exec printfarmer-otel-collector cat /etc/otel-collector-config.yaml
```

## Advanced Features

### Custom Instrumentation

#### Backend Custom Metrics
```csharp
// Inject the telemetry service
public class PrintersController : ControllerBase
{
    private readonly IPrintFarmerTelemetryService _telemetry;
    
    public async Task<IActionResult> GetPrinters()
    {
        using var activity = _telemetry.StartActivity("GetPrinters");
        // ... controller logic
        _telemetry.RecordApiCall("/api/printers", "GET", 200, elapsed);
    }
}
```

#### Frontend Custom Tracking
```typescript
// Use the telemetry hook
const { trackUserInteraction, trackApiCall } = useTelemetry();

const handleButtonClick = () => {
    trackUserInteraction('print_start', 'printer-card', { 
        printerId: printer.id 
    });
    
    trackApiCall('/api/printers/start', 'POST', () => 
        apiClient.startPrint(printer.id)
    );
};
```

### Integration with External Services

The system is configured to work with:
- **Jaeger** (included)
- **Prometheus** (included) 
- **Grafana** (included)
- **Datadog**: Set OTLP endpoint to Datadog intake
- **New Relic**: Configure OTLP with API key
- **Honeycomb**: Use Honeycomb OTLP endpoint
- **Azure Monitor**: Configure Application Insights OTLP

## Performance Considerations

- **Sampling**: Default is 100% for development, configure for production
- **Batch Processing**: Collector batches spans for efficiency
- **Memory Limits**: Collector has memory limits to prevent OOM
- **Network**: HTTP/2 gRPC transport for better performance
- **Storage**: Jaeger and Prometheus data is ephemeral in Docker setup

## Security

- **CORS**: Configured for local development domains
- **Headers**: OTLP headers can include authentication tokens
- **Network**: Consider TLS for production OTLP transport
- **Data**: Traces may contain sensitive data, configure exporters accordingly

## Next Steps

1. **Production Deployment**: Configure external OTLP endpoints
2. **Alerting**: Set up Prometheus alerting rules
3. **Dashboards**: Create custom Grafana dashboards
4. **SLOs**: Define Service Level Objectives based on metrics
5. **Sampling**: Implement intelligent sampling strategies
6. **Custom Metrics**: Add domain-specific business metrics