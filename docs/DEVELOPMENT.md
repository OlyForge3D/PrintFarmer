# Development Guide

Guidelines for contributing to PrintFarmer.

## Code Organization

### Backend (.NET)

```
src/
├── api/
│   ├── Controllers/        # REST API endpoints
│   ├── Hubs/              # SignalR hubs
│   ├── Program.cs         # App startup
│   └── appsettings.json   # Configuration
├── infra/
│   ├── Domain/Entities.cs # Domain models
│   ├── Models.cs          # DTOs
│   ├── Data/              # EF Core DbContext
│   ├── Services/          # Business logic
│   └── Repositories/      # Data access
├── tests/
│   └── Farm.Web.Api.Tests/ # Integration tests
└── farm-web.sln           # Solution file
```

### Frontend (React)

```
src/Web/ReactApp/
├── src/
│   ├── components/        # React components
│   ├── pages/            # Page components
│   ├── contexts/         # React Context
│   ├── services/         # API clients
│   ├── types/            # TypeScript interfaces
│   ├── utils/            # Utility functions
│   ├── App.tsx           # Root component
│   └── index.tsx         # Entry point
├── public/               # Static assets
├── vite.config.ts        # Build config
└── tsconfig.json         # TypeScript config
```

## Code Style

### C# (.NET)

- **Naming**:
  - PascalCase for types, methods, properties
  - camelCase for local variables and parameters
  - UPPER_CASE for constants

- **Formatting**:
  - Run `dotnet format` before committing
  - Max line length: 120 characters
  - Use var for obvious types

- **Async/Await**:
  - Always async I/O operations
  - Suffix method names with `Async`
  - Use `Task` not `void` for async methods

- **Exceptions**:
  - Create custom exceptions in appropriate namespaces
  - Throw meaningful, specific exceptions
  - Don't catch and ignore exceptions

Example:
```csharp
public class Printer
{
    public string Name { get; set; }
    
    public async Task<PrinterStatus> GetStatusAsync()
    {
        var response = await _client.GetStatusAsync();
        return response;
    }
}
```

### TypeScript/React

- **Naming**:
  - PascalCase for components and types
  - camelCase for functions, variables, and properties
  - UPPER_CASE for constants

- **Formatting**:
  - Run `npm run lint` before committing
  - Max line length: 100 characters
  - Use `const` for immutable, `let` for mutable

- **React Patterns**:
  - Functional components (no class components)
  - Hooks for state management
  - Props typing with interfaces

- **Imports**:
  - Organize imports: React, libraries, local, types
  - Use absolute imports with `@/` alias

Example:
```typescript
import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PrinterCard } from '@/components/PrinterCard';
import type { Printer } from '@/types/api';

interface PrinterGridProps {
  printers: Printer[];
  onSelect: (printer: Printer) => void;
}

export function PrinterGrid({ printers, onSelect }: PrinterGridProps) {
  return (
    <div className="grid grid-cols-3 gap-4">
      {printers.map(printer => (
        <PrinterCard key={printer.id} printer={printer} onClick={() => onSelect(printer)} />
      ))}
    </div>
  );
}
```

## Logging & Diagnostics

### Overview

PrintFarmer uses structured logging through `IUnifiedLoggingService` to provide visibility into application behavior. A logging extensions system automatically captures caller information (class and method names) to make debugging easier without manual context tracking.

### Using IUnifiedLoggingService

Inject `IUnifiedLoggingService` into your services and controllers:

```csharp
public class PrinterService
{
    private readonly IUnifiedLoggingService _logger;
    
    public PrinterService(IUnifiedLoggingService logger)
    {
        _logger = logger;
    }
    
    public async Task<Printer?> GetPrinterAsync(Guid printerId)
    {
        _logger.LogInformationWithSource($"Retrieving printer {printerId}");
        
        try
        {
            var printer = await _repository.GetPrinterAsync(printerId);
            return printer;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithSource(ex, $"Failed to retrieve printer {printerId}");
            throw;
        }
    }
}
```

### Extension Methods for Automatic Source Context

The following extension methods automatically capture the calling class and method name:

- `LogDebugWithSource(message)`
- `LogInformationWithSource(message)`
- `LogWarningWithSource(message)`
- `LogWarningWithSource(exception, message)`
- `LogErrorWithSource(message)`
- `LogErrorWithSource(exception, message)`
- `LogCriticalWithSource(message)`
- `LogCriticalWithSource(exception, message)`

**Example Output**:
```
[PrinterService.GetPrinterAsync] Retrieving printer 12345678-1234-1234-1234-123456789012
[PrinterService.GetPrinterAsync] Failed to retrieve printer 12345678-1234-1234-1234-123456789012
```

### How It Works

When you call `_logger.LogErrorWithSource(ex, "Failed to retrieve printer")`, the extension method uses .NET caller attributes (`CallerMemberName` and `CallerFilePath`) to automatically capture:
- **Method name**: The method calling the logger (e.g., `GetPrinterAsync`)
- **Class name**: Extracted from the file path (e.g., `PrinterService`)

The result is formatted as `[ClassName.MethodName]` and prepended to your log message, providing immediate context without manual string concatenation.

### Usage Guidelines

**Use `LogXxxWithSource` when:**
- You want automatic caller context (recommended for most logs)
- Debugging errors and warnings
- Tracking important operations (service startup, job completion, etc.)

**Use regular `LogXxx` methods when:**
- You need custom formatting or additional metadata
- Logging health checks or routine operations
- You're including correlationId or trace IDs

**Example - Error with Exception**:
```csharp
try
{
    await _printerClient.SendCommandAsync(command);
}
catch (HttpRequestException ex)
{
    _logger.LogErrorWithSource(ex, $"Failed to send command to printer {printerId}");
    throw;
}
```

**Example - Info with Context**:
```csharp
public async Task<List<Printer>> ImportPrintersAsync(List<PrinterDto> dtos)
{
    _logger.LogInformationWithSource($"Importing {dtos.Count} printers");
    
    var importedPrinters = new List<Printer>();
    foreach (var dto in dtos)
    {
        importedPrinters.Add(await CreatePrinterAsync(dto));
    }
    
    _logger.LogInformationWithSource($"Successfully imported {importedPrinters.Count} printers");
    return importedPrinters;
}
```

### Viewing Logs

**Local Development**:
```bash
# View real-time logs from running API
dotnet run --project ./api/Farm.Web.Api.csproj 2>&1 | tee api.log
```

**Docker Deployment**:
```bash
# View API logs
docker compose logs -f printfarmer-api

# Search for specific service
docker compose logs printfarmer-api | grep "PrinterService"

# Follow logs with timestamp
docker compose logs -f --timestamps printfarmer-api
```

**Log Format**:
```
[2025-12-26 14:30:45.123] [Information] [PrinterService.GetStatusAsync] Retrieving status for printer Ender 3 V2
[2025-12-26 14:30:46.456] [Error] [MoonrakerClient.GetStatusAsync] HTTP 500: Internal server error
[2025-12-26 14:30:46.789] [Warning] [PrinterService.UpdateStatusAsync] Printer offline, will retry in 30 seconds
```

## Git Workflow

### Branch Naming

```
feature/short-description       # New feature
bugfix/short-description        # Bug fix
refactor/short-description      # Code refactoring
docs/short-description          # Documentation only
```

### Commit Messages

Use conventional commits:

```
feat: Add location drag-and-drop UI
fix: Resolve printer status update delay
refactor: Consolidate URL normalization utilities
docs: Update API reference for printer endpoints
test: Add tests for LocationService
```

Format:
```
<type>: <description>

Optional detailed explanation of changes.
```

### Pull Request Process

1. **Create Branch**: `git checkout -b feature/my-feature`
2. **Make Changes**: Implement feature
3. **Test**: Run all tests locally
4. **Format**: Run linters and formatters
5. **Commit**: Use conventional commits
6. **Push**: `git push origin feature/my-feature`
7. **Create PR**: Submit with clear description
8. **Review**: Address review feedback
9. **Merge**: Squash and merge when approved

## Testing

### Test Status

**Current Coverage (Phase 22 - December 2025):**
- ✅ **API Tests**: 1,772+ tests passing (0 failures)
- ✅ **React Tests**: 150+ tests passing (0 failures)
- **Farm.Web.Api Coverage**: 37-38% method coverage
- **Farm.Infrastructure Coverage**: 8-9% method coverage
- **Coverage Goal**: Increase to 77%+ line coverage focusing on critical paths

### Backend Tests

```bash
# Run all tests
cd ./src
dotnet test ./farm-web.sln -c Debug

# Run specific test class
dotnet test ./src/tests/Farm.Web.Api.Tests --filter ClassName

# Run with coverage
dotnet test ./farm-web.sln /p:CollectCoverageEnabled=true

# Run verbose
dotnet test ./farm-web.sln -v normal
```

**Test Framework**: xUnit with FluentAssertions for readable assertions

**Integration Test Setup**: `CustomWebApplicationFactory` provides:
- In-memory SQLite database for isolation
- Complete DI container setup
- Fixture reuse across test classes
- Database cleanup between tests

### Frontend Tests

```bash
# Run tests (watch mode)
cd ./src/Web/ReactApp
npm test

# Run tests (non-interactive)
npm run test:run

# Run tests with coverage
npm test -- --coverage

# Run specific test file
npm test -- PrinterCard.test.tsx
```

**Test Framework**: Vitest with React Testing Library

**Key Points:**
- Use `npm run test:run` for CI/CD pipelines (exits after tests complete)
- Use `npm test` for interactive development (watch mode, requires 'q' to exit)
- Focus on user behavior, not implementation details
- Mock external services (API calls, WebSocket)

### Writing Tests

**Backend (xUnit):**
```csharp
public class PrinterServiceTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly PrinterService _service;
    
    public PrinterServiceTests()
    {
        _factory = new CustomWebApplicationFactory();
        var scope = _factory.Services.CreateScope();
        _service = scope.ServiceProvider.GetRequiredService<PrinterService>();
    }
    
    [Fact]
    public async Task GetPrinter_WithValidId_ReturnsPrinter()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        
        // Act
        var result = await _service.GetPrinterAsync(printerId);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(printerId, result.Id);
    }
}
```

**Frontend (Vitest):**
```typescript
import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PrinterCard } from '@/components/PrinterCard';
import type { Printer } from '@/types/api';

describe('PrinterCard', () => {
  it('displays printer name', () => {
    const printer: Printer = {
      id: '1',
      name: 'Test Printer',
      isOnline: true,
    };
    
    render(<PrinterCard printer={printer} />);
    
    expect(screen.getByText('Test Printer')).toBeInTheDocument();
  });
});
```

## Running Locally

### Full Development Setup

```bash
# Terminal 1: API server
cd ./src
dotnet run --project ./api/Farm.Web.Api.csproj

# Terminal 2: React dev server
cd ./src/Web/ReactApp
npm run dev

# Terminal 3: Tests (optional)
cd ./src/Web/ReactApp
npm test
```

### With Hot Reload

```bash
# Terminal 1: API with hot reload
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run

# Terminal 2: React with hot reload
cd ./src/Web/ReactApp
npm run dev
```

## Build & Release

### Building

```bash
# Debug build
cd ./src
dotnet build ./farm-web.sln -c Debug

# Release build
dotnet build ./farm-web.sln -c Release
```

### Docker Build

```bash
# Build image
docker build -t printfarmer:latest -f Dockerfile.multistage .

# Run container
docker run -p 5245:5245 -p 3000:3000 printfarmer:latest
```

## Documentation

### Adding Documentation

1. Add to appropriate file in `/docs/`
2. Link from main README.md or relevant doc
3. Keep documentation near code (in comments)
4. Update docs when changing behavior

### Documentation Style

- Use clear, concise language
- Include code examples
- Use Markdown formatting
- Link between related docs

## Performance Optimization

### Backend

- Use `.Include()` to avoid N+1 queries
- Denormalize counts for large result sets
- Implement pagination for large lists
- Use `AsNoTracking()` for read-only queries

### Frontend

- Lazy load routes with `React.lazy()`
- Memoize expensive components with `memo()`
- Use `useCallback()` for stable function refs
- Optimize queries with `staleTime` in React Query

## Debugging

### Backend

**Visual Studio Code:**
1. Install C# extension
2. Open workspace from `/src`
3. Set breakpoints
4. Press F5 to debug

**Command Line:**
```bash
# Run with debug symbols
dotnet run --project ./api/Farm.Web.Api.csproj -- --debug
```

### Frontend

**Browser DevTools:**
1. Open http://localhost:3000
2. Press F12
3. Use Sources tab for debugging
4. Set breakpoints in TypeScript source maps

**VS Code:**
1. Install Debugger for Chrome extension
2. Add launch configuration
3. Press F5 to debug

## Common Tasks

### Add New API Endpoint

1. Create controller method in `Controllers/`
2. Add route attribute `[HttpGet("path")]`
3. Return appropriate `ActionResult<T>`
4. Add tests to `Farm.Web.Api.Tests/`

### Add New React Component

1. Create file in `src/components/ComponentName.tsx`
2. Export named component with props interface
3. Add TypeScript types from `src/types/`
4. Write tests in `ComponentName.test.tsx`

### Add Database Migration

1. Modify entity in `Domain/Entities.cs`
2. Run: `dotnet ef migrations add MigrationName`
3. Review generated migration
4. Run: `dotnet ef database update`

### Update Dependencies

**Backend:**
```bash
cd ./src
dotnet nuget update ./farm-web.sln --interactive
```

**Frontend:**
```bash
cd ./src/Web/ReactApp
npm update
npm audit fix
```

## Troubleshooting

### Build Errors

```bash
# Clean build
cd ./src
rm -rf ./api/bin ./api/obj ./infra/bin ./infra/obj
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
```

### Test Failures

```bash
# Run with verbose output
dotnet test ./farm-web.sln -v normal

# Run specific test
dotnet test --filter "TestClass::TestMethod"
```

### Port Conflicts

```bash
# Find process on port
lsof -ti:5245

# Kill process
kill -9 <PID>
```

## Resources

### PrintFarmer Documentation

- **[UI & Styling Index](./UI_STYLING_INDEX.md)** - Quick reference to UI component guidelines and controls
  - [CONTROLS_GUIDE.md](./CONTROLS_GUIDE.md) - Complete reference for buttons, forms, alerts, tables, modals, and more
  - Located in: `src/Web/ReactApp/src/styles/controls.css` (1,401 lines of centralized control styles)

### External Resources

- [ASP.NET Core Docs](https://docs.microsoft.com/aspnet/)
- [React Docs](https://react.dev)
- [TypeScript Handbook](https://www.typescriptlang.org/docs/)
- [Entity Framework Core](https://docs.microsoft.com/ef/)
- [Tailwind CSS](https://tailwindcss.com)
