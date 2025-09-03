# PrintFarmer DevContainer Configuration

## Overview

The devcontainer has been updated to support the React-based PrintFarmer architecture while maintaining .NET 9 (not downgrading).

## Key Configuration Details

### ✅ **Why .NET 9?**
- PrintFarmer was already using .NET 9 in the original Blazor implementation
- .NET 9 provides the latest performance improvements and features
- No reason to downgrade when migrating to React - the backend API remains .NET
- Maintains compatibility with existing codebase
- Access to latest C# features and ASP.NET Core improvements

### 🛠️ **DevContainer Features**

**Base Image:**
- `mcr.microsoft.com/devcontainers/typescript-node:18-bookworm`
- Provides Node.js 18 for React development
- Includes TypeScript support out of the box

**Key Features Added:**
- **.NET 9.0 SDK** - Full .NET 9 development environment
- **Docker-in-Docker** - For building and running containers
- **GitHub CLI** - For managing issues and deployments
- **Git** - Version control integration

### 📦 **VS Code Extensions**

**React/TypeScript Development:**
- TailwindCSS support
- ESLint integration
- TypeScript language features
- Auto-rename tags
- Path intellisense

**.NET Development:**
- C# DevKit
- .NET runtime support
- IntelliSense and debugging

**Database Integration:**
- PostgreSQL client
- Redis client

**DevOps Tools:**
- Docker extension
- GitLens
- YAML support

### 🚀 **Port Forwarding**

- **5000**: PrintFarmer API (.NET 9)
- **5173**: Vite React dev server
- **5432**: PostgreSQL database
- **6379**: Redis cache
- **8080**: Docker containerized app

### ⚙️ **Post-Create Setup**

The `post-create.sh` script automatically:
1. Updates system packages
2. Installs global Node.js tools (Vite, TypeScript, ESLint, Prettier)
3. Installs .NET global tools (EF Core, dotnet-watch)
4. Restores .NET solution
5. Sets up React dependencies (when available)
6. Creates environment configuration
7. Sets up useful development aliases
8. Creates VS Code workspace file

### 🎯 **Development Aliases**

After setup, these commands are available:
- `pf-api` - Start .NET API with hot reload
- `pf-react` - Start React dev server
- `pf-build` - Build Docker images
- `pf-deploy` - Deploy with Docker Compose
- `pf-dev` - Start full development environment

### 🔧 **Environment Support**

**Development Mode:**
- Hot reload for both React and .NET
- Separate database instance
- Development-optimized settings

**Production Mode:**
- Optimized Docker builds
- PostgreSQL with performance tuning
- Redis caching enabled

## Migration Phases Support

The devcontainer is designed to support all React migration phases:

1. **Phase 1** - React foundation setup
2. **Phase 2** - User management system
3. **Phase 3** - Dashboard migration
4. **Phase 4** - 3D viewer integration
5. **Phase 5** - G-code harvest migration

## Getting Started

1. Open in VS Code with Dev Containers extension
2. Container will build automatically with .NET 9 + Node.js 18
3. Post-create script runs setup automatically
4. Configure `.env` file for your environment
5. Start development with `pf-dev` or individual services

## Benefits

- **No Downgrade**: Keeps .NET 9 for latest features
- **Full Stack**: React + .NET 9 in one container
- **Docker Ready**: Build and run containers locally
- **Database Integrated**: PostgreSQL and Redis support
- **Hot Reload**: Fast development cycle
- **Extensible**: Easy to add new tools and extensions

The configuration ensures you can work on both the existing .NET 9 API and the new React frontend seamlessly!