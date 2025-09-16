# PrintFarmer Unified Logging Implementation

## Overview

This document describes the unified logging solution implemented for PrintFarmer, which creates a single source of truth for health monitoring and debugging by integrating console logging with OpenTelemetry observability.

## Architecture

### Backend (.NET API)

The backend unified logging system consists of three main components:

#### 1. UnifiedLoggingService (`/src/api/Services/Telemetry/UnifiedLoggingService.cs`)

**Purpose**: Central logging service that integrates structured logging with OpenTelemetry tracing.

**Key Features**:
- Implements `IUnifiedLoggingService` interface with standard log levels (Debug, Info, Warning, Error, Critical)
- Creates OpenTelemetry activities/spans for each log entry
- Context-aware logging with custom categories
- Extension methods for domain-specific logging (Printer operations, Slicer operations, File operations, API requests)

**Usage Example**:
```csharp
public class MyController : ControllerBase
{
    private readonly IUnifiedLoggingService _logger;
    
    public MyController(IUnifiedLoggingService logger)
    {
        _logger = logger;
    }
    
    public async Task<IActionResult> GetPrinters()
    {
        _logger.LogInformation("Fetching printers list");
        
        // Use extension methods for specific operations
        _logger.LogPrinterOperation("fetch_list", "all", true, "Retrieved 5 printers");
        
        return Ok(printers);
    }
}
```

#### 2. ConsoleRedirectionService (`/src/api/Services/Telemetry/ConsoleRedirectionService.cs`)

**Purpose**: Captures existing Console.WriteLine statements and redirects them to the unified logging system.

**Key Features**:
- Intercepts all Console.Out and Console.Error streams
- Preserves original console output for development debugging
- Automatic integration with OpenTelemetry spans
- Backward compatibility with existing console logging patterns

**Behavior**:
- All `Console.WriteLine()` calls are automatically captured in OpenTelemetry
- Console output continues to appear in terminal for development visibility
- Error streams are properly categorized with appropriate log levels

#### 3. Integration with Program.cs

The unified logging system is automatically initialized in `Program.cs`:

```csharp
// Services registered in DI container
builder.Services.AddScoped<IUnifiedLoggingService, UnifiedLoggingService>();
builder.Services.AddSingleton<IConsoleRedirectionService, ConsoleRedirectionService>();

// Automatic console redirection enabled at startup
using (var scope = app.Services.CreateScope())
{
    var consoleRedirection = scope.ServiceProvider.GetRequiredService<IConsoleRedirectionService>();
    UnifiedConsole.Initialize(consoleRedirection);
    consoleRedirection.RedirectConsoleOutput();
}
```

### Frontend (React TypeScript)

The frontend unified logging system provides comprehensive console log capture and OpenTelemetry integration:

#### 1. UnifiedLoggingService (`/src/Web/ReactApp/src/services/unifiedLogging.ts`)

**Purpose**: Captures all console statements and creates OpenTelemetry spans for frontend logging.

**Key Features**:
- Monkey-patches `console.log`, `console.info`, `console.warn`, `console.error`, `console.debug`
- Maintains original console behavior for development debugging
- Creates OpenTelemetry spans with proper attributes and context
- Session-based log storage for debugging and export
- Context-aware logging for API requests, SignalR events, component lifecycle

**Automatic Console Redirection**:
```typescript
// All existing console statements are automatically captured
console.log('This appears in both console AND OpenTelemetry');
console.error('Error messages create OpenTelemetry error spans');
```

#### 2. useUnifiedLogging Hook (`/src/Web/ReactApp/src/hooks/useUnifiedLogging.tsx`)

**Purpose**: React hook providing easy access to unified logging with component-specific context.

**Usage Example**:
```tsx
function MyComponent() {
  const { logger } = useUnifiedLogging({ 
    component: 'MyComponent',
    logLifecycle: true 
  });
  
  const handleClick = () => {
    logger.logUserAction('button_click', { buttonId: 'submit' });
    logger.info('User clicked submit button');
  };
  
  return <button onClick={handleClick}>Submit</button>;
}
```

**Specialized Hooks**:
- `useApiLogging()`: Automatic API call logging with timing and error handling
- `useSignalRLogging()`: SignalR connection and message logging
- `useFormLogging()`: Form interaction and validation logging

#### 3. UnifiedLoggingDashboard (`/src/Web/ReactApp/src/components/UnifiedLoggingDashboard.tsx`)

**Purpose**: Real-time log viewing and debugging interface.

**Features**:
- Live log stream with configurable refresh interval
- Filtering by log level (debug, info, warn, error)
- Search functionality across messages and components
- Export/download logs as JSON
- Clear stored logs
- Test log generation for verification

**Access**: Available in the Telemetry Settings page (`/settings/telemetry`)

## Integration Points

### 1. Automatic Console Capture

**Backend**: All existing `Console.WriteLine` statements are automatically captured without code changes.

**Frontend**: All existing `console.log/info/warn/error/debug` statements are automatically captured without code changes.

### 2. OpenTelemetry Spans

Every log entry creates an OpenTelemetry span with:
- **Span Name**: `log.{level}` (e.g., `log.error`, `log.info`)
- **Attributes**: 
  - `log.level`: The log level
  - `log.message`: The log message
  - `log.component`: Component/service that generated the log
  - `log.context`: Additional context data (JSON serialized)
  - `log.timestamp`: When the log was created
  - `log.session_id`: Frontend session identifier
  - `log.user_id`: Current user identifier (if available)

### 3. Storage and Export

**Backend**: Logs are sent to configured OpenTelemetry backends (Jaeger, Grafana, etc.)

**Frontend**: 
- Logs stored in browser sessionStorage (last 1000 entries)
- Available for export as JSON
- Accessible via debugging dashboard

## Configuration

### Backend Configuration

Unified logging is configured through the existing OpenTelemetry setup in `Program.cs`. The console redirection is automatically enabled and requires no additional configuration.

**Environment Variables**:
- Standard OpenTelemetry environment variables apply
- `OTEL_EXPORTER_OTLP_ENDPOINT`: OTLP endpoint for log export
- `OTEL_EXPORTER_OTLP_HEADERS`: Additional headers for authentication

### Frontend Configuration

Unified logging is initialized automatically when the telemetry system starts:

```typescript
// In telemetry/config.ts
import '../services/unifiedLogging'; // Initializes console redirection
```

**Environment Variables**:
- `VITE_OTEL_EXPORTER_OTLP_ENDPOINT`: Frontend OTLP endpoint
- `VITE_OTEL_EXPORTER_OTLP_HEADERS`: Authentication headers

## Usage Examples

### Backend Scenarios

#### 1. Controller Error Handling
```csharp
public async Task<IActionResult> CreatePrinter([FromBody] PrinterDto dto)
{
    try
    {
        _logger.LogInformation("Creating new printer", new { Name = dto.Name, Type = dto.Type });
        var result = await _printerService.CreateAsync(dto);
        _logger.LogPrinterOperation("create", result.Id, true);
        return Ok(result);
    }
    catch (Exception ex)
    {
        _logger.LogError("Failed to create printer", new { Name = dto.Name, Error = ex.Message });
        return StatusCode(500);
    }
}
```

#### 2. Background Service Logging
```csharp
public class MoonrakerSubscriptionService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Moonraker subscription service");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessSubscriptions();
                _logger.LogWithContext(LogLevel.Debug, "MoonrakerService", "Subscription cycle completed");
            }
            catch (Exception ex)
            {
                _logger.LogError("Subscription processing failed", new { Error = ex.Message });
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
```

### Frontend Scenarios

#### 1. Component with User Interactions
```tsx
function PrinterCard({ printer }: PrinterCardProps) {
  const { logger } = useUnifiedLogging({ component: 'PrinterCard' });
  
  const handleStart = async () => {
    logger.logUserAction('start_print', { printerId: printer.id });
    
    try {
      await printerApi.startPrint(printer.id);
      logger.info('Print job started successfully', { printerId: printer.id });
    } catch (error) {
      logger.error('Failed to start print job', { 
        printerId: printer.id, 
        error: error.message 
      });
    }
  };
  
  return (
    <div>
      <h3>{printer.name}</h3>
      <button onClick={handleStart}>Start Print</button>
    </div>
  );
}
```

#### 2. API Client with Automatic Logging
```tsx
function useApiWithLogging() {
  const { logApiCall } = useApiLogging();
  
  const fetchPrinters = useCallback(async () => {
    return logApiCall(
      fetch('/api/printers').then(r => r.json()),
      'GET',
      '/api/printers',
      { component: 'PrintersPage' }
    );
  }, [logApiCall]);
  
  return { fetchPrinters };
}
```

#### 3. SignalR Integration
```tsx
function usePrinterSignalR() {
  const { logConnectionEvent, logMessageReceived } = useSignalRLogging();
  
  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/printers')
      .build();
    
    connection.start()
      .then(() => logConnectionEvent('start', 'Connected'))
      .catch(err => logConnectionEvent('start_failed', 'Disconnected', { error: err.message }));
    
    connection.on('PrinterStatusUpdate', (data) => {
      logMessageReceived('PrinterStatusUpdate', data);
      // Handle the update...
    });
    
    return () => connection.stop();
  }, [logConnectionEvent, logMessageReceived]);
}
```

## Benefits

### 1. Single Source of Truth
- All console logging (both existing and new) flows through OpenTelemetry
- Centralized log aggregation and analysis
- Consistent log format and metadata across frontend and backend

### 2. Zero Migration Effort
- Existing `Console.WriteLine` and `console.log` statements work unchanged
- Automatic capture without code modifications
- Gradual migration path to structured logging

### 3. Enhanced Debugging
- Real-time log viewing in development
- Export logs for offline analysis
- Rich context and correlation data
- Integration with distributed tracing

### 4. Production Observability
- Full integration with observability backends (Jaeger, Grafana, etc.)
- Correlation between logs and traces
- User session and request correlation
- Performance impact monitoring

## Troubleshooting

### Backend Issues

**Logs not appearing in OpenTelemetry**:
1. Verify OpenTelemetry configuration in `Program.cs`
2. Check OTLP endpoint configuration
3. Ensure `IUnifiedLoggingService` is properly injected

**Console redirection not working**:
1. Verify console redirection is initialized in `Program.cs`
2. Check that `IConsoleRedirectionService` is registered as singleton

### Frontend Issues

**Console logs not captured**:
1. Verify `unifiedLogging.ts` is imported in `telemetry/config.ts`
2. Check browser console for initialization errors
3. Ensure OpenTelemetry is properly initialized

**Dashboard not showing logs**:
1. Check sessionStorage for stored logs
2. Verify the dashboard component is properly mounted
3. Check for JavaScript errors in browser console

## Performance Considerations

### Backend
- Unified logging service uses scoped lifetime to avoid memory leaks
- Console redirection uses efficient stream wrapping
- OpenTelemetry spans are batched for optimal performance

### Frontend
- Console redirection preserves original console behavior
- Log storage is limited to 1000 entries to prevent memory issues
- SessionStorage is used (not localStorage) to prevent long-term storage bloat
- Span creation is optimized with proper span lifecycle management

## Future Enhancements

1. **Log Levels Configuration**: Runtime configuration of log levels per component
2. **Structured Query Interface**: Advanced filtering and search capabilities
3. **Log Retention Policies**: Configurable retention and cleanup policies
4. **Performance Metrics**: Built-in performance impact monitoring
5. **Alerting Integration**: Integration with monitoring and alerting systems

---

This unified logging implementation provides a comprehensive observability solution that maintains backward compatibility while enabling modern observability practices. The system captures all existing logging patterns while providing rich structured data for production monitoring and debugging.