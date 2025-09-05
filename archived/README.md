# Archived PrintFarmer Components

This directory contains legacy components from PrintFarmer that have been archived for reference purposes.

## Directory Structure

### `blazor-client/`
Contains the original Blazor WebAssembly frontend that was replaced with the React TypeScript frontend.

### `dockerfiles/`
Contains obsolete Docker build configurations that have been superseded by current active Dockerfiles.

### `scripts/`  
Contains legacy build and deployment scripts replaced by automated setup scripts.

### `test-scripts/`
Contains development and testing utilities moved for organization.

### `documentation/`
Contains legacy documentation and summary files superseded by comprehensive documentation system.

**Moved from:**
- `src/client/` → `archived/blazor-client/client/`
- `src/tests/Farm.Web.Client.Tests/` → `archived/blazor-client/Farm.Web.Client.Tests/`

**Reason for archival:** PrintFarmer migrated from Blazor WebAssembly to React TypeScript for better performance, modern tooling, and enhanced user experience. The React frontend is located at `src/Web/ReactApp/`.

**Contents:**
- Complete Blazor WebAssembly client application
- Razor components (.razor files)
- Client-side services and models
- CSS and static assets
- Unit tests for client components

### `dockerfiles/`
Contains Docker configuration files that were specific to the Blazor client deployment.

**Moved files:**
- `Dockerfile.web` → `archived/dockerfiles/Dockerfile.web`
- `Dockerfile.web.config` → `archived/dockerfiles/Dockerfile.web.config`

**Reason for archival:** These Dockerfiles were designed to build and serve the Blazor WebAssembly client. They have been replaced with:
- `Dockerfile.react` - For React-based frontend container
- `Dockerfile.frontend` - For frontend-only deployments
- `Dockerfile.api` - For API-only deployments

## Migration Context

### From Blazor to React (Completed)
- **Original Stack:** ASP.NET Core API + Blazor WebAssembly Client
- **New Stack:** ASP.NET Core API + React TypeScript Client
- **Migration Date:** 2025
- **Key Benefits:**
  - Better development experience with modern tooling (Vite, npm ecosystem)
  - Improved performance and bundle sizes
  - Enhanced real-time capabilities with SignalR
  - Better mobile responsiveness
  - TypeScript for type safety

### What Remains Active
- **API Backend:** `src/api/` - ASP.NET Core API (unchanged)
- **React Frontend:** `src/Web/ReactApp/` - New React TypeScript frontend
- **Shared Models:** `src/shared/` - DTOs shared between API and frontend
- **Tests:** `src/tests/Farm.Web.Api.Tests/` - API integration tests

## Using Archived Components

### If You Need to Reference Blazor Code
The archived Blazor client can be used as reference for:
1. Understanding original business logic implementations
2. Comparing UI patterns and component structure
3. Extracting any missed functionality during migration
4. Historical context for code decisions

### To Restore Blazor Client (Not Recommended)
If for some reason you need to restore the Blazor client:

1. Copy `archived/blazor-client/client/` back to `src/client/`
2. Copy `archived/blazor-client/Farm.Web.Client.Tests/` back to `src/tests/`
3. Add the client project back to `src/farm-web.sln`
4. Update Docker configurations to use archived Dockerfiles
5. Modify `src/api/Program.cs` to serve Blazor instead of React

**⚠️ Note:** This would require significant work to restore compatibility with the current API, as the migration may have included API changes optimized for React.

## Cleanup History

**Archived on:** September 5, 2025
**Archived by:** Automated migration cleanup
**Original size:** ~45+ files across client, tests, and Docker configurations

This archival was done to:
- Clean up the active codebase
- Reduce confusion between old and new frontend approaches
- Preserve historical reference without cluttering development
- Allow for easier maintenance and onboarding

## Related Documentation

- **React Migration Plan:** `/REACT_MIGRATION_README.md`
- **Local Development:** `/LOCAL_DEVELOPMENT.md` (covers React setup)
- **Docker Deployment:** `/DOCKER_DEPLOYMENT.md` (covers current architecture)
- **Contributing:** `/CONTRIBUTING.md` (updated for React workflow)
