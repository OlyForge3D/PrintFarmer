# Maintenance Components

React components for the Printer Maintenance Module.

## Components to Implement (Phase 4.4-4.7)

### Core Alert Components
- **MaintenanceAlertsPanel.tsx** - Dashboard widget showing active alerts (highest priority)
- **MaintenanceAlertsList.tsx** - Full-page list view of all alerts with filtering
- **MaintenanceAlertDetail.tsx** - Detailed alert view with Acknowledge/Resolve/Dismiss buttons

### Maintenance Logging Components
- **MaintenanceLogForm.tsx** - Form for manual maintenance logging
- **MaintenanceHistoryView.tsx** - Table view of past maintenance activities
- **MaintenanceStatsCard.tsx** - Card displaying printer cumulative statistics

### Schedule Management Components
- **MaintenanceScheduleManager.tsx** - CRUD interface for maintenance schedules
- **MaintenanceScheduleForm.tsx** - Form for creating/editing schedules
- **MaintenanceScheduleList.tsx** - List view of all schedules with filters

### Printer Integration Components
- **PrinterMaintenanceModeToggle.tsx** - Toggle button for entering/exiting maintenance mode
- **PrinterMaintenanceBadge.tsx** - Badge/indicator showing maintenance status and alert count
- **MaintenanceQuickActions.tsx** - Quick action buttons for common maintenance tasks

## Implementation Patterns

### SignalR Integration
All components should connect to `maintenanceSignalRService` for real-time updates:

```typescript
useEffect(() => {
  maintenanceSignalRService.start();
  
  const unsubscribe = maintenanceSignalRService.onAlertCreated(() => {
    // Reload data or update UI
  });
  
  return () => {
    unsubscribe();
  };
}, []);
```

### API Service Usage
Use `maintenanceService` singleton for all API calls:

```typescript
import { maintenanceService } from '@/services/maintenanceService';

const alerts = await maintenanceService.getAllAlerts();
```

### State Management
- Use React Query for caching and automatic refetching
- Local state with useState for UI-only state
- Real-time updates trigger cache invalidation

### Error Handling
- Display user-friendly error messages
- Provide retry buttons for failed API calls
- Log errors to console for debugging

## Testing Checklist

- [ ] All components render without errors
- [ ] SignalR connections are properly cleaned up on unmount
- [ ] Real-time updates work across multiple browser tabs
- [ ] API errors are handled gracefully with retry options
- [ ] Loading states are shown during async operations
- [ ] Maintenance mode toggle blocks job assignment
- [ ] Alert workflow: Create → Acknowledge → Resolve/Dismiss
- [ ] Manual maintenance logging works end-to-end

## Integration Points

### Dashboard Integration
Add `<MaintenanceAlertsPanel />` to the main dashboard page

### Printer Detail Integration
- Add `<PrinterMaintenanceBadge />` to printer cards
- Add `<PrinterMaintenanceModeToggle />` to printer detail actions
- Show active alerts count in printer list

### Navigation
Add "Maintenance" menu item linking to full maintenance page with tabs:
- Alerts (active/acknowledged/resolved/dismissed)
- History (maintenance logs)
- Schedules (task definitions)
- Statistics (fleet-wide stats)
