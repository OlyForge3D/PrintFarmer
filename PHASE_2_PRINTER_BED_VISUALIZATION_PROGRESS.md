# Phase 2: Printer Bed Visualization - Implementation Progress

**Date Started**: December 20, 2025
**Estimated Duration**: 5-7 days
**Status**: In Progress

---

## Phase 2 Overview

Implement real-time 3D printer bed visualization showing:
- Printer bed geometry (dimensions from PrinterModel)
- Real-time nozzle position updates via SignalR
- Printer state indicators
- Temperature displays
- Multi-printer support on dashboard

---

## Architecture

### Component Hierarchy

```
Dashboard
├── PrinterBedCard (responsive container)
│   ├── PrinterBedVisualization (3D Canvas)
│   │   ├── Bed Mesh (from printer specs)
│   │   ├── Build Platform (visual reference)
│   │   ├── Nozzle Position Indicator
│   │   ├── Print Head (animated)
│   │   └── Grid/Axes
│   ├── Status Panel
│   │   ├── Printer Name
│   │   ├── Current State
│   │   ├── Temperatures (Hotend, Bed)
│   │   ├── Progress Bar
│   │   └── Current Job Info
│   └── Controls
│       ├── Zoom/Pan Controls
│       └── View Reset Button
```

### Data Flow

```
PrinterHub (SignalR)
    ↓
PrinterStatusUpdate (real-time)
    ↓
useSignalRPrinterStatus Hook
    ↓
PrinterBedVisualization Component
    ↓
3D Canvas (Three.js)
```

---

## Detailed Implementation Plan

### Task 1: Create PrinterBedVisualization Component
**Status**: Not Started
**Duration**: 2-3 days

#### Subtasks:
- [ ] Create `src/components/3D/PrinterBedVisualization.tsx`
  - Props: `printerId`, `printerModel`, `status`
  - Render bed based on printer specs
  - Display nozzle position
  - Handle real-time updates
  
- [ ] Create bed geometry generator `src/utils/bedGeometryGenerator.ts`
  - Function: `generateBedGeometry(printModel: PrinterModel)`
  - Returns Three.js BufferGeometry for bed
  - Support rectangular/circular beds
  - Include grid texture
  
- [ ] Create nozzle indicator renderer
  - Visual nozzle position indicator
  - Update with SignalR data
  - Show movement animation
  
- [ ] Add lighting and camera controls
  - Perspective camera focused on bed
  - Orbit controls for inspection
  - Auto-reset view on demand

#### Implementation Details:
```typescript
// Type: PrinterBedVisualizationProps
interface PrinterBedVisualizationProps {
  printerId: string;
  printerModel: PrinterModel;
  status: PrinterStatusUpdate;
  height?: number; // Canvas height in pixels
  autoRotate?: boolean;
}

// Bed geometry from PrinterModel
interface PrinterModel {
  name: string;
  buildVolume: {
    width: number;  // X axis (mm)
    depth: number;  // Y axis (mm)
    height: number; // Z axis (mm)
  };
  nozzleDiameter?: number;
  ...
}

// Status updates from SignalR
interface PrinterStatusUpdate {
  printerId: string;
  state: string; // "Idle", "Printing", "Paused", etc.
  nozzlePosition?: {
    x: number;
    y: number;
    z: number;
  };
  temperatures: {
    hotend: number;
    hotendTarget: number;
    bed: number;
    bedTarget: number;
  };
  progress?: number;
  ...
}
```

### Task 2: Create PrinterBedCard Dashboard Component
**Status**: Not Started
**Duration**: 1-2 days

#### Subtasks:
- [ ] Create `src/components/Dashboard/PrinterBedCard.tsx`
  - Responsive card layout (100%, 50%, 33% width options)
  - Embed PrinterBedVisualization
  - Display printer status (name, state, job info)
  - Show temperature gauges
  - Responsive design (mobile-first)

- [ ] Add status panel next to visualization
  - Printer name and model
  - Current state (Idle/Printing/Paused/Error)
  - Temperatures with gauge indicators
  - Job progress
  - Active tool display

- [ ] Add control panel
  - Zoom/pan hints
  - Reset view button
  - Toggle auto-rotate
  - Mini stats

#### Layout Options:
- **Wide (desktop)**: 600px×400px visualization + 200px status panel
- **Medium (tablet)**: 400px×300px visualization + status below
- **Small (mobile)**: Full width, stacked vertical

### Task 3: Create SignalR Hook for Printer Status
**Status**: Not Started  
**Duration**: 1 day

#### Subtasks:
- [ ] Create `src/hooks/useSignalRPrinterStatus.ts`
  - Connect to PrinterHub
  - Subscribe to printer-specific events
  - Return real-time status updates
  - Handle connection errors
  - Auto-reconnect logic

```typescript
interface UseSignalRPrinterStatusReturn {
  status: PrinterStatusUpdate | null;
  isConnected: boolean;
  error: string | null;
  reconnect: () => void;
}

function useSignalRPrinterStatus(printerId: string): UseSignalRPrinterStatusReturn {
  // Implementation
}
```

### Task 4: Integrate into Dashboard
**Status**: Not Started
**Duration**: 1 day

#### Subtasks:
- [ ] Update Dashboard page to display printer beds
  - Grid layout for multiple printers
  - Responsive columns (1/2/3 printers per row)
  - Fallback for no active printers
  
- [ ] Add to printer detail page
  - Full-size visualization
  - Extended controls
  
- [ ] Add printer selection component
  - Filter by status
  - Sort by name/state

### Task 5: Create Unit Tests
**Status**: Not Started
**Duration**: 1-2 days

#### Test Files to Create:
- [ ] `src/components/3D/PrinterBedVisualization.test.tsx`
  - Rendering with mock printer model
  - Status updates
  - Controls interaction
  - Error states
  
- [ ] `src/components/Dashboard/PrinterBedCard.test.tsx`
  - Layout rendering
  - Status display
  - Responsive behavior
  
- [ ] `src/hooks/useSignalRPrinterStatus.test.ts`
  - Connection handling
  - Event subscriptions
  - Error recovery
  
- [ ] `src/utils/bedGeometryGenerator.test.ts`
  - Geometry generation
  - Dimension validation
  - Edge cases

**Target**: 250+ tests covering all Phase 2 features

### Task 6: Documentation & Demo
**Status**: Not Started
**Duration**: 0.5 day

#### Deliverables:
- [ ] Update Phase 2 plan with completion details
- [ ] Create demo page showing bed visualization
- [ ] Document configuration options
- [ ] Add troubleshooting guide

---

## Technical Details

### Three.js Components Needed

#### 1. Bed Platform Mesh
```typescript
// Render rectangular bed with dimensions from PrinterModel
const bedGeometry = new THREE.BoxGeometry(width, depth, thickness);
const bedMaterial = new THREE.MeshPhongMaterial({ color: 0x333333 });
const bed = new THREE.Mesh(bedGeometry, bedMaterial);
```

#### 2. Build Volume Wireframe
```typescript
// Show printable area boundaries
const frameGeometry = new THREE.EdgesGeometry(bedGeometry);
const frameWireframe = new THREE.LineSegments(
  frameGeometry,
  new THREE.LineBasicMaterial({ color: 0x00ff00 })
);
```

#### 3. Nozzle Indicator
```typescript
// Position indicator showing current (x, y, z)
const nozzleGeometry = new THREE.CylinderGeometry(2, 1.5, 10);
const nozzleMaterial = new THREE.MeshPhongMaterial({ color: 0xff6600 });
const nozzle = new THREE.Mesh(nozzleGeometry, nozzleMaterial);
nozzle.position.set(x, y, z);
```

#### 4. Grid Overlay
```typescript
// Reference grid on bed surface
const gridHelper = new THREE.GridHelper(width, 10, 0x404040, 0x303030);
gridHelper.position.y = height / 2;
```

### SignalR Integration

#### Events to Monitor
- `printerupdated` - Full printer status
- `temperatureupdate` - Temperature changes
- `positionupdate` - Nozzle position in real-time
- `jobstarted` / `jobcompleted` - Job lifecycle
- `statechanged` - Printer state changes

#### Message Format (from server)
```json
{
  "printerId": "printer-1",
  "state": "Printing",
  "nozzlePosition": { "x": 50.5, "y": 75.3, "z": 10.2 },
  "temperatures": {
    "hotend": 210,
    "hotendTarget": 210,
    "bed": 60,
    "bedTarget": 60
  },
  "progress": 45.5,
  "currentJob": {
    "name": "benchy.gcode",
    "startTime": "2025-12-20T10:30:00Z",
    "estimatedEndTime": "2025-12-20T11:15:00Z"
  }
}
```

---

## Dependencies

### Already Installed
- `three` - 3D graphics
- `react-three-fiber` - React renderer for Three.js
- `@react-three/drei` - Utility components
- `@microsoft/signalr` - Real-time communication

### May Need to Add
- `@react-three/rapier` - Physics simulation (optional, for future)
- `react-gauge-chart` - Temperature gauges (optional)

---

## Success Criteria

### Functional Requirements
- [ ] Bed visualization displays correct dimensions from PrinterModel
- [ ] Nozzle position updates in real-time via SignalR
- [ ] Multiple printers display on dashboard simultaneously
- [ ] Responsive layout (mobile/tablet/desktop)
- [ ] Error handling for disconnected printers
- [ ] Auto-reconnect on SignalR disconnect

### Non-Functional Requirements
- [ ] All tests passing (250+ tests)
- [ ] 0 TypeScript compilation errors
- [ ] 60 FPS rendering performance
- [ ] < 50ms SignalR latency for position updates
- [ ] Production-ready code (no warnings)
- [ ] Backward compatible with Phase 1

### Performance Targets
- Canvas render: < 16ms per frame (60 FPS)
- Position update latency: < 100ms from server to visual
- Memory per printer: < 10MB per active visualization
- Bundle size increase: < 30KB (gzipped)

---

## Timeline

| Task | Duration | Start Date | End Date | Status |
|------|----------|-----------|----------|--------|
| Task 1: PrinterBedVisualization | 2-3 days | Dec 20 | Dec 22 | Not Started |
| Task 2: PrinterBedCard & Status | 1-2 days | Dec 22 | Dec 23 | Not Started |
| Task 3: SignalR Hook | 1 day | Dec 23 | Dec 24 | Not Started |
| Task 4: Dashboard Integration | 1 day | Dec 24 | Dec 25 | Not Started |
| Task 5: Unit Tests | 1-2 days | Dec 25 | Dec 26 | Not Started |
| Task 6: Documentation | 0.5 day | Dec 26 | Dec 26 | Not Started |
| **Total** | **5-7 days** | **Dec 20** | **Dec 26** | **In Progress** |

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation |
|------|------------|--------|-----------|
| SignalR latency issues | Medium | High | Implement local prediction, cache position |
| Bed geometry complexity | Low | Medium | Pre-generate common formats, fallback to simple box |
| Performance (many printers) | Medium | High | Implement virtualization, LOD culling |
| Mobile responsiveness | Low | Medium | Test on real devices, responsive design |
| Three.js memory leaks | Low | High | Proper cleanup in useEffect, geometry disposal |

---

## Blocked By
- None - Can start immediately after Phase 1 commit

## Blocking
- None - Phase 3 can start after Phase 2 Day 2

---

## Notes

### Design Decisions
1. **Separate Hook for SignalR**: `useSignalRPrinterStatus` allows reuse in other components
2. **Bed Geometry Generator**: Utility function allows testing without 3D renderer
3. **Card Component**: Allows reuse in multiple dashboard layouts
4. **Real-time Only**: No polling, pure SignalR subscription model

### Future Enhancements (Phase 3+)
- Print head preview with tool changer visualization
- Material color visualization
- Layer-by-layer visualization
- Collision detection visualization
- Support/raft visualization
- Filament path preview
- Multi-material print visualization

---

## Next Meeting
Prepare for daily stand-up once Task 1 is complete (estimated Dec 22).

