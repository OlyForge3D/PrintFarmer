# PrintFarmer React Migration Plan

## 📋 Overview

This repository contains the comprehensive plan for migrating PrintFarmer from Blazor WebAssembly to React with TypeScript. The migration includes implementing a robust user management system with role-based access control, advanced 3D visualization capabilities, and maintaining all existing functionality.

## 🚀 Migration Phases

### Phase 1: React Foundation & Project Setup
**Duration:** 3-4 days  
**Status:** 📋 Planned

- Set up React + TypeScript development environment with Vite
- Configure build pipeline and tooling  
- Establish project structure and conventions
- Create API client layer for React components
- Set up testing framework and CI/CD integration
- Configure ASP.NET Core SPA hosting

**Key Deliverables:**
- ✅ Fully functional React development environment
- ✅ API client communicating with existing backend
- ✅ SignalR real-time connections working
- ✅ TypeScript types matching API models
- ✅ Build pipeline producing optimized bundles

### Phase 2: User Registration & Role-Based Access Control System  
**Duration:** 2-3 weeks  
**Status:** 📋 Planned

- Design flexible role-based permission system
- Implement user registration and authentication with JWT
- Create admin interface for user and role management
- Secure existing API endpoints with authorization
- Build React components for auth flows

**Key Features:**
- 🔐 JWT-based authentication with refresh tokens
- 👥 Two default roles: Farm User and Farm Admin
- 🛡️ Extensible resource + action permission system
- 🎛️ Admin interface for role and permission management
- 📊 Audit logging for security-sensitive operations

**Database Schema:**
- `users` - User accounts and profiles
- `roles` - Role definitions (Farm Admin, Farm User, custom roles)
- `resources` - System resources (printers, harvest, slicers, etc.)
- `actions` - Available actions (create, read, update, delete, execute, admin)
- `role_permissions` - Granular role-based permissions
- `user_roles` - User role assignments

### Phase 3: Core Dashboard & Printer Management Migration
**Duration:** 2-3 weeks  
**Status:** 📋 Planned

- Create responsive React dashboard with real-time updates
- Migrate printer management functionality to React
- Implement printer discovery and configuration UI  
- Build reusable UI components and layouts
- Maintain feature parity with existing Blazor implementation

**Key Components:**
- 📊 Real-time dashboard with printer status cards
- 🖨️ Printer management with CRUD operations
- 🔍 Automated printer discovery (Moonraker/PrusaLink)
- 📱 Responsive design for mobile and desktop
- 🔄 Live status updates via SignalR

### Phase 4: 3D Model Viewer & Slicer Integration
**Duration:** 3-4 weeks  
**Status:** 📋 Planned

- Implement advanced 3D visualization with React Three Fiber
- Create G-code visualization with layer-by-layer preview
- Integrate PrusaSlicer and OrcaSlicer engines
- Build slicer configuration UI with printer-specific settings
- Enable direct printing from sliced models

**3D Capabilities:**
- 📐 STL, 3MF, OBJ, PLY, and STEP file viewing
- 🎨 G-code visualization with color-coded layers
- ⚙️ PrusaSlicer and OrcaSlicer integration
- 🎛️ Advanced slicer settings based on printer capabilities
- 📊 Print time and material usage estimation

### Phase 5: G-code Harvest & File Management Migration  
**Duration:** 2-3 weeks  
**Status:** 📋 Planned

- Migrate G-code harvest functionality to React
- Build advanced file browser with search and filtering
- Implement batch operations for file management
- Create harvest operation monitoring and history
- Enable direct printing from harvested files

**Features:**
- 🚜 Multi-printer harvest operations
- 📁 Advanced file browser with metadata
- 🔍 Search and filtering by various criteria
- 📦 Batch operations (delete, download, organize)
- 📈 Harvest operation history and analytics

## 🏗️ Architecture Overview

### Frontend Stack
- **Framework:** React 18 with TypeScript
- **Build Tool:** Vite for fast development and optimized builds
- **State Management:** React Query for server state, Context API for app state
- **Styling:** Tailwind CSS with custom design system
- **3D Graphics:** React Three Fiber + Three.js
- **Real-time:** SignalR for live updates
- **Forms:** React Hook Form with Zod validation
- **Testing:** Vitest + React Testing Library

### Backend Integration  
- **API Client:** Axios with TypeScript interfaces
- **Authentication:** JWT tokens with automatic refresh
- **Authorization:** Permission-based with role inheritance
- **Real-time:** SignalR hubs for printer status and progress
- **File Handling:** Multi-part uploads with progress tracking

### User Management System
```typescript
interface User {
  id: string;
  username: string;
  email: string;
  firstName?: string;
  lastName?: string;
  roles: string[];
  permissions: string[]; // Format: "resource:action"
  isActive: boolean;
  emailConfirmed: boolean;
  lastLogin?: Date;
}

interface Role {
  id: string;
  name: string;
  displayName: string;
  description: string;
  isSystemRole: boolean;
  permissions: RolePermission[];
}

interface RolePermission {
  resource: string; // "printers", "gcode_harvest", "slicer_engines", "users", etc.
  action: string;   // "create", "read", "update", "delete", "execute", "admin"
  granted: boolean;
}
```

### Permission System
The system uses a flexible resource + action based permission model:

**Default Resources:**
- `printers` - 3D printer management
- `gcode_harvest` - File harvesting operations
- `slicer_engines` - 3D model slicing
- `users` - User account management  
- `roles` - Role and permission management
- `system_settings` - System configuration

**Actions:**
- `create` - Create new items
- `read` - View and list items
- `update` - Modify existing items
- `delete` - Remove items
- `execute` - Run operations (harvest, slice, print)
- `admin` - Full administrative access

**Example Usage:**
```typescript
// Check if user can create printers
if (hasPermission('printers', 'create')) {
  // Show "Add Printer" button
}

// Check if user can manage other users
if (hasRole('farm_admin') || hasPermission('users', 'admin')) {
  // Show user management interface
}
```

## 📂 Project Structure

```
/workspaces/PrintFarmer/
├── src/
│   ├── Web/
│   │   ├── ReactApp/                    # New React application
│   │   │   ├── src/
│   │   │   │   ├── components/          # Reusable UI components
│   │   │   │   │   ├── auth/           # Authentication components
│   │   │   │   │   ├── dashboard/      # Dashboard widgets
│   │   │   │   │   ├── printers/       # Printer management
│   │   │   │   │   ├── 3d/             # 3D viewers and slicers
│   │   │   │   │   ├── files/          # File browser components
│   │   │   │   │   └── ui/             # Base UI components
│   │   │   │   ├── contexts/           # React contexts
│   │   │   │   ├── hooks/              # Custom React hooks
│   │   │   │   ├── pages/              # Page components
│   │   │   │   ├── services/           # API and external services
│   │   │   │   ├── types/              # TypeScript type definitions
│   │   │   │   └── utils/              # Utility functions
│   │   │   ├── public/                 # Static assets
│   │   │   ├── tests/                  # Test files
│   │   │   ├── package.json
│   │   │   └── vite.config.ts
│   │   └── Farm.Web.csproj            # Existing ASP.NET Core project
│   ├── api/                           # Existing API project
│   └── shared/                        # Shared models and types
├── .github/
│   ├── issues/                        # GitHub issue templates
│   └── workflows/                     # CI/CD workflows
├── create-github-issues.sh           # Script to create GitHub issues
└── README.md
```

## 🛠️ Development Setup

### Prerequisites
- Node.js 18+ and npm
- .NET 8 SDK
- Git

### Quick Start
```bash
# 1. Clone the repository
git clone https://github.com/jpapiez/PrintFarmer.git
cd PrintFarmer

# 2. Set up the React application (Phase 1)
mkdir -p src/Web/ReactApp && cd src/Web/ReactApp
npm create vite@latest . -- --template react-ts
npm install

# 3. Install additional dependencies
npm install @tanstack/react-query axios react-router-dom
npm install @microsoft/signalr @headlessui/react lucide-react  
npm install react-hook-form @hookform/resolvers zod
npm install tailwindcss @tailwindcss/forms autoprefixer postcss
npm install three @react-three/fiber @react-three/drei

# 4. Start development servers
npm run dev &                         # React dev server (port 3000)
cd ../../../ && dotnet run            # ASP.NET Core API (port 5245)
```

## 📊 Progress Tracking

| Phase | Status | Progress | Estimated Effort | Dependencies |
|-------|--------|----------|------------------|--------------|
| Phase 1: React Foundation | 📋 Planned | 0% | 3-4 days | None |
| Phase 2: User Management | 📋 Planned | 0% | 2-3 weeks | Phase 1 |
| Phase 3: Dashboard Migration | 📋 Planned | 0% | 2-3 weeks | Phase 1, 2 |
| Phase 4: 3D Viewer & Slicer | 📋 Planned | 0% | 3-4 weeks | Phase 1, 2, 3 |
| Phase 5: Harvest Migration | 📋 Planned | 0% | 2-3 weeks | Phase 1, 2, 3 |

**Total Estimated Effort:** 10-15 weeks

## 🧪 Testing Strategy

### Unit Tests
- React component tests with React Testing Library
- Service and utility function tests
- Authentication and authorization logic tests
- API client and SignalR service tests

### Integration Tests  
- End-to-end user flows with Playwright
- API endpoint security tests
- Real-time functionality tests
- Cross-browser compatibility tests

### Performance Tests
- 3D model loading and rendering performance
- Large G-code file visualization
- Concurrent user session handling
- Build bundle size optimization

## 🚀 Deployment Strategy

### Development Environment
- Hot reload for both React and ASP.NET Core
- Proxy configuration for API requests
- Source maps for debugging

### Production Build
- Optimized React bundle served by ASP.NET Core
- Asset compression and caching
- Environment-specific configuration

### CI/CD Pipeline
- Automated testing on pull requests
- Build and deployment to staging
- Security scanning and dependency updates

## 🔒 Security Considerations

### Authentication
- JWT tokens with secure generation and validation
- Automatic token refresh handling
- Session management and logout

### Authorization  
- Role-based access control at API level
- Frontend permission enforcement
- Audit logging for sensitive operations

### Data Protection
- Input validation and sanitization
- SQL injection prevention
- XSS protection with Content Security Policy

## 📚 Documentation

### API Documentation
- OpenAPI/Swagger specifications
- Authentication and authorization guides
- WebSocket/SignalR event documentation

### Component Documentation
- Storybook for UI component library
- Usage examples and props documentation
- Accessibility guidelines

### Deployment Guides
- Environment setup instructions
- Configuration management
- Troubleshooting guides

## 🤝 Contributing

### Getting Started
1. Review the GitHub issues for current work
2. Follow the development setup instructions
3. Create feature branches following the naming convention
4. Submit pull requests with comprehensive tests

### Code Standards
- TypeScript strict mode enabled
- ESLint and Prettier for code formatting
- Conventional commit messages
- Comprehensive test coverage

## 📞 Support

For questions about the migration plan or implementation details, please:

1. Check the GitHub issues for existing discussions
2. Review the documentation in each phase
3. Create new issues for bugs or feature requests

## 📝 License

This project maintains the same license as the original PrintFarmer project.