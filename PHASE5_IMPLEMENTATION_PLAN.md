# Phase 5 Implementation Plan - Maintenance UI Components

## Overview

This document outlines the implementation plan for the remaining Phase 5 components for the PrintFarmer Maintenance Module. The backend infrastructure is complete with 15 REST API endpoints, SignalR hub, and services already in place.

## ✅ Phase 5.1 Status: COMPLETE

**Completed:** January 21, 2026

### Components Implemented
- ✅ `MaintenanceDashboardPage.tsx` - Main maintenance page
- ✅ `FleetMaintenanceOverview.tsx` - Statistics cards
- ✅ `MaintenanceStatusGrid.tsx` - Printer status grid
- ✅ `MaintenancePriorityList.tsx` - Priority alerts with quick actions
- ✅ `useMaintenanceAlerts.ts` - Alerts hook with SignalR
- ✅ `useMaintenanceStats.ts` - Fleet statistics hook
- ✅ `/maintenance` route added to App.tsx
- ✅ "Maintenance" nav link added to Layout.tsx
- ✅ Recharts dependency installed

### Validation
- ✅ Production build successful
- ✅ All 499 React tests pass
- ✅ ESLint passes (no new errors)

---

## Existing Foundation

### Already Implemented
- **Backend APIs**: MaintenanceController with alerts, logs, schedules, statistics endpoints
- **SignalR Hub**: `/hubs/maintenance` for real-time updates
- **React Services**: `maintenanceService.ts` (15 API methods), `maintenance-signalr.ts`
- **TypeScript Types**: `maintenance.ts` with 16 DTOs and enums
- **Sample Component**: `MaintenanceAlertsPanel.tsx` (dashboard widget)
- **Seed Data**: 22 maintenance schedules auto-seeded

### Dependencies Available
- `date-fns` - Date formatting ✅
- `@tanstack/react-query` - Data fetching ✅
- `sonner` - Toast notifications ✅
- `lucide-react` - Icons ✅
- `recharts` - Charts ✅ (installed)

---

## Phase 5.1: Core Dashboard & Fleet Overview ✅ COMPLETE

### 5.1.1 Install Dependencies ✅
```bash
cd src/Web/ReactApp
npm install recharts  # DONE
```

### 5.1.2 Folder Structure ✅ CREATED
```
src/Web/ReactApp/src/features/maintenance/
├── components/
│   ├── FleetMaintenanceOverview.tsx    # Statistics cards
│   ├── MaintenanceStatusGrid.tsx       # Printer status grid
│   ├── MaintenancePriorityList.tsx     # Urgent tasks list
│   └── index.ts                        # Barrel exports
├── hooks/
│   ├── useMaintenanceAlerts.ts         # Alerts data hook
│   ├── useMaintenanceStats.ts          # Fleet statistics hook
│   └── index.ts
├── pages/
│   └── MaintenanceDashboardPage.tsx    # Main maintenance page
└── index.ts
```

### 5.1.3 Components Built ✅

#### A. MaintenanceDashboardPage ✅
- Page layout with grid structure
- Integrates all maintenance widgets
- Real-time SignalR subscription
- Responsive design (mobile/desktop)

#### B. FleetMaintenanceOverview ✅
- **Total Printers** (with online count)
- **Printers Online** (percentage)
- **Printers Needing Attention** (active alert count)
- **Printers in Maintenance** (maintenance mode count)
- **Alert Severity Breakdown** (Critical/High/Medium/Low badges)

#### C. MaintenanceStatusGrid ✅
- Grid of printer cards showing maintenance status
- Quick visual status (green/yellow/orange/red indicators)
- Online/offline status with maintenance mode indicator
- Alert count badges by severity
- Click to navigate to printer details

#### D. MaintenancePriorityList ✅
- List sorted by urgency (Critical → High → Medium → Low)
- Shows alert title, message, severity badge
- Quick action buttons (Acknowledge, Resolve, Dismiss)
- Compact/expanded modes
- Relative time display

### 5.1.4 Routing Integration ✅
- Added `/maintenance` route to App.tsx
- Added "Maintenance" nav item to Layout sidebar with WrenchIcon
- Permission: requires `printers:read` permission

---

## Phase 5.2: Analytics & Trends (Future)

### Components
- `MaintenanceTrendsChart.tsx` - Frequency over time (Recharts)
- `ComponentLifespanChart.tsx` - Component intervals
- `PrinterUptimeChart.tsx` - Uptime vs downtime pie chart

### Data Requirements
- API: GET `/maintenance/analytics/trends?days=30`
- API: GET `/maintenance/analytics/components`
- Group maintenance logs by date, component type

---

## Phase 5.3: Calendar & Timeline ✅ COMPLETED

### Components Created
- ✅ `useUpcomingMaintenance.ts` - Hook to calculate upcoming tasks from schedules
- ✅ `UpcomingMaintenanceCalendar.tsx` - Month view calendar with task indicators
- ✅ `MaintenanceTimeline.tsx` - Timeline/list view grouped by relative date

### Features Implemented
- ✅ Month navigation (previous, next, today buttons)
- ✅ Color-coded task indicators by priority (Critical, High, Medium, Low)
- ✅ Overdue task highlighting with pulsing indicator
- ✅ Day click shows tasks for selected day
- ✅ Timeline groups: Overdue, Today, Tomorrow, This Week, Next Week, Later
- ✅ Configurable lookahead period (default 60 days)
- ✅ Integrated into MaintenanceDashboardPage via tabs (Calendar | Timeline)
- ✅ Show more/less functionality for large task lists
- ✅ ChevronLeftIcon added to MdiIcons

### Data Flow
- Uses existing `/maintenance/schedules` and `/maintenance/logs/{printerId}` APIs
- Calculates due dates from intervalDays/intervalHours + last completion time
- Tasks sorted by due date, grouped for calendar and timeline views

---

## Phase 5.4: Component Tracking (Future)

### Components
- `ComponentMaintenanceTracker.tsx` - Per-component history
- Replacement history with cost tracking
- Component lifespan predictions

---

## Phase 5.5: Reporting & Export (Future)

### Components
- `MaintenanceReport.tsx` - Report generator
- PDF export (jsPDF)
- CSV export (built-in or react-csv)
- Date range and printer filters

---

## Implementation Sequence (Phase 5.1)

1. **Install Recharts** - Chart library for visualizations
2. **Create folder structure** - features/maintenance hierarchy
3. **Create custom hooks** - useMaintenanceAlerts, useMaintenanceStats
4. **Build FleetMaintenanceOverview** - Statistics cards widget
5. **Build MaintenanceStatusGrid** - Printer cards grid
6. **Build MaintenancePriorityList** - Urgent tasks list
7. **Build MaintenanceDashboardPage** - Combine all widgets
8. **Add routing** - /maintenance route + nav link
9. **Build & verify** - ESLint, TypeScript, visual testing

---

## Success Criteria (Phase 5.1)

- [ ] MaintenanceDashboardPage renders without errors
- [ ] Fleet overview shows correct statistics
- [ ] Status grid displays all printers with maintenance status
- [ ] Priority list shows alerts sorted by severity
- [ ] Navigation link appears in sidebar with alert badge
- [ ] Real-time updates work via SignalR
- [ ] Responsive layout works on mobile
- [ ] ESLint passes with no errors
- [ ] TypeScript compiles without errors

---

## Estimated Time

| Task | Time |
|------|------|
| Dependencies & Setup | 15 min |
| Custom Hooks | 30 min |
| FleetMaintenanceOverview | 45 min |
| MaintenanceStatusGrid | 45 min |
| MaintenancePriorityList | 30 min |
| MaintenanceDashboardPage | 30 min |
| Routing & Navigation | 15 min |
| Testing & Polish | 30 min |
| **Total** | **~4 hours** |
