# PrintFarmer 3D Visualization Implementation Plan

## Overview
Implement three 3D visualization features to improve user experience with STL file management and printer monitoring.

---

## Phase 1: STL File Viewer Component (Foundation)
**Estimated Duration**: 3-5 days
**Priority**: ⭐⭐⭐ High

### Goals
- Create reusable STL viewer component using react-three-fiber + Three.js
- Display STL files with basic controls (rotate, zoom, pan)
- Integrate into Print Jobs page
- Allow users to preview models before submission

### Tasks
1. **Install Dependencies**
   - [x] Add `three`, `react-three-fiber`, `drei` to package.json
   - [x] Add TypeScript types for Three.js

2. **Create STL Viewer Component** (`src/components/3D/STLViewer.tsx`)
   - [x] Create canvas-based 3D viewer using react-three-fiber
   - [x] Implement STL file loading (binary + ASCII)
   - [x] Add OrbitControls (rotate, zoom, pan)
   - [x] Add lighting (ambient + directional + point lights)
   - [x] Implement auto-fit camera to model
   - [x] Add grid overlay for reference
   - [x] Style with Tailwind CSS

3. **Create Model Preview Modal**
   - [x] Wrap STLViewer in modal component (`src/components/3D/STLPreviewModal.tsx`)
   - [x] Add file info display (size, triangles, vertices, format)
   - [x] Add close button and controls

4. **Integration**
   - [x] Add preview button to print job submission form (NewSliceJobPage)
   - [x] Create 3D Models Viewer page for standalone previewing
   - [x] Test with sample STL files

5. **Testing**
   - [x] Unit tests for STLViewer component
   - [x] Unit tests for STL utilities (parsing, validation)
   - [x] Unit tests for useSTLFile hook
   - [x] Unit tests for STLPreviewModal
   - [x] All 188 tests passing ✅

### Deliverables
- ✅ Functional STL viewer component with binary + ASCII support
- ✅ Modal integration with file information display
- ✅ Integration into NewSliceJobPage (Print Jobs)
- ✅ Standalone 3D Models Viewer page
- ✅ File validation utilities
- ✅ Custom React hook for state management
- ✅ Comprehensive unit test suite (188 tests, all passing)
- ✅ Production-ready build (0 errors)

---

## Phase 2: Printer Bed Visualization Dashboard Card (Mini 3D View)
**Estimated Duration**: 5-7 days
**Priority**: ⭐⭐⭐ High

### Goals
- Create 3D visualization of printer bed on dashboard
- Show printer dimensions and current print state
- Display as compact card on printer dashboard
- Real-time updates via SignalR

### Tasks
1. **Create Printer Bed Component** (`src/components/PrinterBedVisualization.tsx`)
   - [ ] Generate 3D bed geometry based on printer specs
   - [ ] Create bed wireframe (length × width × height)
   - [ ] Add bed grid (shows printable surface)
   - [ ] Add origin marker (0,0,0)
   - [ ] Implement basic lighting

2. **Current Print Visualization**
   - [ ] Load active print model into bed view (if available)
   - [ ] Position model based on print head location
   - [ ] Show nozzle position indicator
   - [ ] Color code (active print = highlight color)

3. **Dashboard Card Integration**
   - [ ] Create responsive card component
   - [ ] Embed bed visualization (compact size)
   - [ ] Add printer info overlay (name, status, progress)
   - [ ] Add click-to-expand functionality

4. **Real-time Updates**
   - [ ] Connect to printer status updates via SignalR
   - [ ] Update nozzle position in real-time
   - [ ] Update progress indicator

5. **Multi-Printer Support**
   - [ ] Display all printers in grid layout
   - [ ] Handle different printer bed sizes
   - [ ] Responsive design for mobile

6. **Testing**
   - [ ] Unit tests for geometry generation
   - [ ] Integration tests with mock printer data
   - [ ] SignalR update testing

### Deliverables
- ✅ 3D bed visualization component
- ✅ Dashboard card integration
- ✅ Real-time status updates
- ✅ Multi-printer support

---

## Phase 3: Multi-Model Positioning Tool (Advanced)
**Estimated Duration**: 7-10 days
**Priority**: ⭐⭐ Medium (Can be deferred if needed)

### Goals
- Allow users to arrange multiple STL models on virtual print bed
- Simulate print positions and detect collisions
- Export arranged layout
- Estimate print time per arrangement

### Tasks
1. **Multi-Model Upload & Selection**
   - [ ] Create form for uploading multiple STL files
   - [ ] Display uploaded files in list
   - [ ] Add to scene buttons for each file
   - [ ] Remove from scene buttons

2. **3D Positioning Interface** (`src/components/MultiModelPositioner.tsx`)
   - [ ] Create enhanced 3D scene with bed + models
   - [ ] Implement drag-to-position models
   - [ ] Add rotate controls per model
   - [ ] Add scale controls per model
   - [ ] Add snap-to-grid option
   - [ ] Show model bounding boxes

3. **Collision Detection**
   - [ ] Implement basic AABB collision detection
   - [ ] Highlight colliding models in red
   - [ ] Show warning message
   - [ ] Prevent overlapping placements

4. **Bed Fit Visualization**
   - [ ] Show bed boundary
   - [ ] Warn if models exceed bed area
   - [ ] Show print area coverage percentage
   - [ ] Display total model count and weight estimate

5. **Layout Management**
   - [ ] Save/load arrangement layouts
   - [ ] Export layout as JSON
   - [ ] Export as images (screenshots)
   - [ ] Create print queue from layout

6. **Print Time Estimation**
   - [ ] Integrate with slicer API
   - [ ] Estimate time per model
   - [ ] Show total estimated time
   - [ ] Add to print job metadata

7. **Testing**
   - [ ] Collision detection tests
   - [ ] Layout save/load tests
   - [ ] Positioning accuracy tests

### Deliverables
- ✅ Multi-model editor interface
- ✅ Collision detection system
- ✅ Layout save/export functionality
- ✅ Print time estimation

---

## Technical Stack

### Libraries to Add
```json
{
  "three": "^r128",
  "react-three-fiber": "^8.15.0",
  "drei": "^9.88.0",
  "three-stdlib": "^1.29.0"
}
```

### New Files to Create
```
src/
├── components/
│   ├── 3D/
│   │   ├── STLViewer.tsx              [Phase 1]
│   │   ├── PrinterBedVisualization.tsx [Phase 2]
│   │   ├── MultiModelPositioner.tsx    [Phase 3]
│   │   ├── BedGeometry.tsx             [Phase 2]
│   │   └── StlLoader.ts                [Phase 1]
│   ├── PrinterBedCard.tsx              [Phase 2]
│   ├── STLPreviewModal.tsx             [Phase 1]
│   └── MultiModelEditor.tsx            [Phase 3]
├── types/
│   └── three-models.ts
├── utils/
│   ├── stlLoader.ts                    [Phase 1]
│   ├── geometry.ts                     [Phase 2]
│   └── collision.ts                    [Phase 3]
└── services/
    └── bedVisualizationService.ts      [Phase 2]
```

### Existing Files to Modify
- `src/pages/PrintJobsPage.tsx` - Add preview button [Phase 1]
- `src/pages/DashboardPage.tsx` - Add bed visualization cards [Phase 2]
- `src/components/JobQueueTable.tsx` - Add preview button [Phase 1]
- `package.json` - Add new dependencies

---

## Implementation Strategy

### Phase 1 Focus Areas
1. Simple, functional STL viewer
2. Integration with existing print jobs
3. No real-time features yet
4. Foundation for later phases

### Phase 2 Focus Areas
1. Real-time SignalR integration
2. Compact, responsive design
3. Performance optimization for multiple printers
4. Mobile-friendly layout

### Phase 3 Focus Areas
1. Complex 3D interactions
2. Advanced collision detection
3. Slicer integration
4. State persistence

---

## Risks & Mitigation

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Large STL files | Performance issues | Implement file size limits, LOD |
| 3D rendering on low-end devices | Poor UX | Add fallback 2D view, reduce geometry |
| SignalR update frequency | Network strain | Throttle updates, batch changes |
| Collision detection accuracy | User frustration | Extensive testing, clear feedback |
| Browser memory | Crashes with many models | Unload models when hidden |

---

## Success Criteria

### Phase 1
- [ ] STL files preview correctly in modal
- [ ] Rotate/zoom/pan controls work smoothly
- [ ] Performance acceptable (60 FPS)
- [ ] Works on Windows, macOS, Linux

### Phase 2
- [ ] Bed visualization displays on dashboard
- [ ] Real-time position updates work
- [ ] Responsive on mobile (500px width)
- [ ] Multiple printer cards render smoothly

### Phase 3
- [ ] Models can be positioned without collision
- [ ] Layout can be saved and restored
- [ ] Print time estimates are reasonable
- [ ] UI remains responsive with 10+ models

---

## Timeline Summary

| Phase | Tasks | Est. Days | Start | End |
|-------|-------|-----------|-------|-----|
| 1 | STL Viewer | 3-5 | Day 1 | Day 5 |
| 2 | Bed Viz | 5-7 | Day 6 | Day 13 |
| 3 | Multi-Model | 7-10 | Day 14 | Day 24 |
| **Total** | | **15-22 days** | | |

---

## Notes

- Phase 1 is standalone and can be used immediately
- Phase 2 enhances dashboard, integrates with existing monitoring
- Phase 3 is optional/advanced feature for power users
- Can defer Phase 3 if timeline is tight
- Each phase maintains backward compatibility
