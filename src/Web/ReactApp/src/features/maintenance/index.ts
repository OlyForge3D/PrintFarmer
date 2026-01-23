/**
 * Maintenance Feature Module
 * Phase 5.1 - Maintenance Dashboard & Fleet Overview
 * 
 * Provides comprehensive maintenance management UI including:
 * - Fleet-wide maintenance overview with statistics
 * - Priority-sorted maintenance alerts
 * - Printer maintenance status grid
 * - Real-time updates via SignalR
 */

// Pages
export { MaintenanceDashboardPage } from './pages/MaintenanceDashboardPage';

// Components
export { FleetMaintenanceOverview } from './components/FleetMaintenanceOverview';
export { MaintenanceStatusGrid } from './components/MaintenanceStatusGrid';
export { MaintenancePriorityList } from './components/MaintenancePriorityList';

// Hooks
export { useMaintenanceAlerts } from './hooks/useMaintenanceAlerts';
export { useMaintenanceStats } from './hooks/useMaintenanceStats';
