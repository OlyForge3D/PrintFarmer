# Getting Started with PrintFarmer

A quick guide to set up PrintFarmer for local development.

## Prerequisites

- **Node.js** 24.0 or later
- **.NET SDK** 10.0.102 or later
- **npm** 10.0 or later
- **Git**

## Quick Start (5 minutes)

### 1. Clone and Navigate

```bash
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer/src
```

### 2. Install Dependencies

```bash
# Restore .NET dependencies
dotnet restore ./farm-web.sln

# Install React dependencies
cd ./Web/ReactApp
npm install
cd ../../
```

### 3. Build

```bash
# Build .NET solution
dotnet build ./farm-web.sln -c Debug
```

### 4. Run Servers

**Terminal 1 - API Backend:**
```bash
dotnet run --project ./api/Farm.Web.Api.csproj
# Runs at http://localhost:5245
```

**Terminal 2 - React Frontend:**
```bash
cd ./Web/ReactApp
npm run dev
# Runs at http://localhost:3000
```

### 5. Access Application

Open **http://localhost:3000** in your browser. You'll see the PrintFarmer setup wizard.

## Testing

```bash
# .NET tests
cd ./src
dotnet test ./farm-web.sln -c Debug
# ✅ 496/496 tests passing

# React tests (non-interactive)
cd ./src/Web/ReactApp
npm run test:run
# ✅ 150/150 tests passing

# Watch mode for development
npm test
```

## Development with Hot Reload

For active development with automatic recompilation:

```bash
# Terminal 1 - API with hot reload
cd ./src
dotnet watch --project ./api/Farm.Web.Api.csproj run

# Terminal 2 - React with hot reload
cd ./src/Web/ReactApp
npm run dev
```

Changes are automatically compiled and reflected in the browser.

## Verify Setup

Check that everything is working:

```bash
# Health check
curl http://localhost:5245/healthz
# Returns: {"status":"ok"}

# API endpoint
curl http://localhost:5245/api/printers
# Returns: [] (empty array)

# Frontend
curl http://localhost:3000/ | head -5
# Shows HTML with PrintFarmer title
```

## Database

By default, PrintFarmer uses **SQLite** with a file-based database (`farm.db` in the working directory).

### Using Different Databases

```bash
# PostgreSQL
DB_PROVIDER=postgres DB_CONNECTION_STRING="Host=localhost;Database=printfarmer;Username=postgres;Password=password" dotnet run --project ./api/Farm.Web.Api.csproj

# SQL Server
DB_PROVIDER=sqlserver DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;User Id=sa;Password=YourPassword123" dotnet run --project ./api/Farm.Web.Api.csproj

# MySQL
DB_PROVIDER=mysql DB_CONNECTION_STRING="Server=localhost;Database=printfarmer;Uid=root;Pwd=password" dotnet run --project ./api/Farm.Web.Api.csproj
```

## Common Tasks

### Run Code Formatting

```bash
# .NET code formatting
cd ./src
dotnet format ./farm-web.sln

# React linting (ESLint)
cd ./src/Web/ReactApp
npm run lint
```

### Clean Build

```bash
cd ./src
rm -rf ./api/bin ./api/obj ./infra/bin ./infra/obj ./Web/ReactApp/node_modules ./Web/ReactApp/dist
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
```

### Debug API

Visual Studio Code with C# extension:
1. Open the workspace: `code .` from `/src`
2. Set breakpoints in C# code
3. Press F5 to debug
4. Make API requests to hit breakpoints

### Debug React

Browser DevTools:
1. Open **http://localhost:3000**
2. Press **F12** to open DevTools
3. Go to **Sources** tab
4. Breakpoints work in TypeScript source maps

## Environment Variables

Create a `.env` file in `src/api/`:

```bash
# Logging
ASPNETCORE_ENVIRONMENT=Development
SERILOG_LEVEL=Debug

# Database (optional, uses SQLite by default)
DB_PROVIDER=sqlite
# DB_CONNECTION_STRING=... # Use default if not set

# Printer discovery
DISCOVERY_ENABLED=true
DISCOVERY_INTERVAL=60

# SignalR
SIGNALR_HUB_TIMEOUT=30000
```

## Troubleshooting

### Port Already in Use

If ports 3000 or 5245 are in use:

```bash
# Kill process on port 5245 (API)
lsof -ti:5245 | xargs kill -9

# Kill process on port 3000 (React)
lsof -ti:3000 | xargs kill -9
```

### .NET SDK Not Found

Install .NET 10.0 SDK:

```bash
# Download installer from https://dot.net/download
# Or using package manager:
# Ubuntu/Debian
sudo apt-get install dotnet-sdk-10.0

# macOS
brew install dotnet-sdk@10

# Verify installation
dotnet --info
```

### npm Packages Not Installing

```bash
# Clear cache and reinstall
cd ./src/Web/ReactApp
rm -rf node_modules package-lock.json
npm cache clean --force
npm install
```

### Tests Failing

```bash
# Run with verbose output
dotnet test ./farm-web.sln -c Debug -v normal

# For React tests
npm run test:run -- --reporter=verbose
```

## Next Steps

- 📖 Read the **[Architecture Guide](./ARCHITECTURE.md)** to understand system design
- 🎨 Explore **[UI Documentation](./UI.md)** for frontend components
- 📡 Check **[API Reference](./API.md)** for available endpoints
- 🚀 See **[Deployment Guide](./DEPLOYMENT.md)** for production setup

## Getting Help

- 🐛 Found a bug? [Report it on GitHub](https://github.com/jpapiez/PrintFarmer/issues)
- 💬 Have questions? [Start a discussion](https://github.com/jpapiez/PrintFarmer/discussions)
- 📧 Security issue? See [SECURITY.md](../SECURITY.md)
