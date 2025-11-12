# React Module Resolution Fix for Workspace Packages

**Date**: November 12, 2025  
**Status**: ✅ **COMPLETE AND FULLY TESTED**  
**Issues Fixed**: 
1. Failed to resolve module specifier "react/jsx-runtime"
2. Failed to resolve module specifier "react"
3. Failed to resolve module specifier "react-dom"
4. Failed to resolve peerDependencies from OrcaSlicer workspace package

## Problems

### Problem 1: react/jsx-runtime Module Resolution
Browser error: `Uncaught TypeError: Failed to resolve module specifier 'react/jsx-runtime'. Relative references must start with either '/', './', or '../'.`

When building React app with OrcaSlicer npm workspace package, the browser couldn't resolve `react/jsx-runtime` imports. This occurred because:
1. OrcaSlicer TypeScript files use JSX syntax (e.g., `import React` + `<Component />`)
2. @vitejs/plugin-react automatically transforms JSX to use `react/jsx-runtime`
3. OrcaSlicer files are in an external directory (`/src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_x/`)
4. During build, Rollup couldn't resolve `react/jsx-runtime` from the external directory path
5. At runtime, the module specifier wasn't resolvable from the browser context

### Problem 2: React and Other Peerhe Dependencies Resolution
Docker build error: `Rollup failed to resolve import "react" from "/src/Slicers/Farm.Slicers.OrcaSlicer.v2_3_x/ui/components/OrcaImportWizard.tsx"`

OrcaSlicer specifies React, React-DOM, axios, @tanstack/react-query, and lucide-react as peerDependencies. In npm workspaces, these should be symlinked from the root node_modules. However:
1. In local development, npm symlinks work fine
2. In Docker build context, Rollup couldn't find peerDependencies because npm symlinks weren't resolved to their actual paths
3. @vitejs/plugin-react treats resolution failures as fatal errors, causing the build to exit with code 1

## Solution

Updated Vite configuration to use resolve aliases for all packages that OrcaSlicer depends on. The aliases explicitly map module names to their actual locations in the root node_modules, ensuring Rollup can resolve them at build time:

```typescript
resolve: {
  alias: [
    { find: '@', replacement: resolve(__dirname, 'src') },
    // Ensure all peerDependencies from OrcaSlicer workspace package resolve from root node_modules
    // npm symlinks these but in Docker build context, Rollup needs explicit paths
    { find: /^react\/jsx-runtime$/, replacement: resolve(__dirname, '../../../node_modules/react/jsx-runtime.js') },
    { find: /^react$/, replacement: resolve(__dirname, '../../../node_modules/react') },
    { find: /^react-dom$/, replacement: resolve(__dirname, '../../../node_modules/react-dom') },
    { find: /^axios$/, replacement: resolve(__dirname, '../../../node_modules/axios') },
    { find: /^@tanstack\/react-query$/, replacement: resolve(__dirname, '../../../node_modules/@tanstack/react-query') },
    { find: /^lucide-react$/, replacement: resolve(__dirname, '../../../node_modules/lucide-react') }
  ]
}
```

And removed ALL dependencies from Rollup externals (they should be bundled, not external):

```typescript
rollupOptions: {
  // NOTE: Do NOT mark dependencies as external for a Vite SPA
  // External modules expect to be provided by the runtime environment
  // In a browser SPA, we need all dependencies bundled
  output: {
    manualChunks: { /* ... */ }
  }
}
```

## How It Works

1. **Vite resolve aliases**: When Rollup encounters imports like `import react from "react"`, the alias resolves them to the absolute path in node_modules
2. **Rollup bundling**: Rollup successfully resolves and includes the modules in the bundle
3. **Runtime resolution**: At runtime, the bundled code is available directly in the JavaScript files (not as external references)

## Files Modified

- `src/Web/ReactApp/vite.config.ts`: Added comprehensive resolve aliases for all workspace package dependencies

## Build Results

✅ **Local Build**: `✓ 2388 modules transformed. ✓ built in 4.57s`  
✅ **Docker Build**: React frontend builds successfully in Docker  
✅ **Frontend Loading**: http://localhost:8087 responds with correct HTML  
✅ **API**: http://localhost:8087/healthz returns `{"status":"ok"}`  
✅ **No Console Errors**: Frontend loads without JavaScript errors (all modules properly resolved)  
✅ **Docker Deployment**: All services deployed and running (microservices architecture)

## Technical Details

### Why External Modules Don't Work for Vite SPAs

- **External modules** (marked with `external: [...]`): Rollup skips them, expecting them to be provided by the consumer
  - Results in `import ... from "react"` in the final bundle
  - Browser then tries to resolve `react` at runtime (fails if not provided globally)
  - **Problem**: Creates unresolvable module specifiers

- **Bundled modules** (via resolve aliases): Rollup includes actual code in the bundle
  - Results in bundled code inline in the final JavaScript files
  - All imports resolved at build time
  - **Solution**: Works for Vite SPAs where there's no external runtime to provide modules

### Why Aliases Instead of External?

1. **Aliases are Build-Time Resolution**: Rollup processes them during bundling, ensuring all paths are resolvable
2. **Direct Path Mapping**: Explicit paths to node_modules prevent Rollup from searching relative to external directories
3. **Works in Docker**: File paths remain consistent regardless of build environment
4. **Workspace-Friendly**: npm symlinks are followed correctly via explicit paths

### Why Regex Patterns?

The regex patterns (`/^react$/`, `/^react-dom$/`, etc.) ensure:
- Exact module matching (doesn't match partial names)
- Consistency across different import styles
- Future-proofing if module names change

## Validation

### Local Development
```bash
npm run build
# ✓ 2388 modules transformed
# ✓ built in 4.57s
```

### Docker Deployment
```bash
./scripts/deploy-docker.sh --non-interactive --architecture microservices
# ✅ Frontend image builds successfully
# ✅ All services deployed
# ✅ Health checks pass
```

### Runtime Verification
```bash
curl http://localhost:8087/           # Returns HTML
curl http://localhost:8087/healthz    # Returns JSON health status
```

### Browser Verification
- Frontend loads at http://localhost:8087
- No console errors
- All assets load correctly
- React components render as expected

## Key Takeaway

When integrating external TypeScript packages (like npm workspace packages) with Vite/React:
1. **Use resolve aliases** for all peerDependencies
2. **DO NOT mark them as external** - they need to be bundled
3. **Use explicit paths** from root node_modules for reliable Docker builds
4. **Use regex patterns** for exact module matching

This ensures Rollup can resolve all imports at build time, making them available at runtime in the browser SPA.

