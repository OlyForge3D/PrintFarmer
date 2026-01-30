// ============================================================================
// MaintenanceAlertsPanel Component
// Dashboard widget showing active maintenance alerts across all printers
// ============================================================================

import React, { useState, useEffect, useCallback } from 'react';
import { maintenanceService } from '@/services/maintenanceService';
import { maintenanceSignalRService } from '@/services/maintenance-signalr';
import type { MaintenanceAlert } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';
import { Button } from '@/common/components/ui';

/**
 * Props for MaintenanceAlertsPanel component
 */
interface MaintenanceAlertsPanelProps {
  /** Optional printer ID to filter alerts for a specific printer */
  printerId?: string;
  /** Maximum number of alerts to display */
  maxAlerts?: number;
  /** Callback when an alert is clicked */
  onAlertClick?: (alert: MaintenanceAlert) => void;
}

/**
 * Dashboard widget displaying active maintenance alerts.
 * Shows alerts in order of severity (Critical → High → Medium → Low).
 * Real-time updates via SignalR.
 */
export const MaintenanceAlertsPanel: React.FC<MaintenanceAlertsPanelProps> = ({
  printerId,
  maxAlerts = 5,
  onAlertClick
}) => {
  const [alerts, setAlerts] = useState<MaintenanceAlert[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadAlerts = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      let fetchedAlerts: MaintenanceAlert[];
      if (printerId) {
        fetchedAlerts = await maintenanceService.getPrinterAlerts(printerId);
      } else {
        fetchedAlerts = await maintenanceService.getAllAlerts();
      }

      // Sort by severity (descending), then by created date (newest first)
      const sortedAlerts = fetchedAlerts
        .filter(a => a.status === MaintenanceAlertStatus.Active || a.status === MaintenanceAlertStatus.Acknowledged)
        .sort((a, b) => {
          if (a.severity !== b.severity) {
            return b.severity - a.severity; // Higher severity first
          }
          return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
        })
        .slice(0, maxAlerts);

      setAlerts(sortedAlerts);
    } catch (err) {
      console.error('[MaintenanceAlertsPanel] Error loading alerts:', err);
      setError(err instanceof Error ? err.message : 'Failed to load alerts');
    } finally {
      setLoading(false);
    }
  }, [printerId, maxAlerts]);

  // Load alerts on mount and when printerId changes
  useEffect(() => {
    loadAlerts();
  }, [loadAlerts]);

  // Subscribe to real-time alert updates
  useEffect(() => {
    // Connect to SignalR hub
    maintenanceSignalRService.start().catch(err => {
      console.error('[MaintenanceAlertsPanel] Failed to connect to SignalR:', err);
    });

    // Register event handlers
    const unsubscribeCreated = maintenanceSignalRService.onAlertCreated(() => {
      loadAlerts(); // Reload alerts when new one is created
    });

    const unsubscribeStatusChanged = maintenanceSignalRService.onAlertStatusChanged(() => {
      loadAlerts(); // Reload alerts when status changes
    });

    // Cleanup on unmount
    return () => {
      unsubscribeCreated();
      unsubscribeStatusChanged();
    };
  }, [loadAlerts]);

  const getSeverityColor = (severity: number): string => {
    switch (severity) {
      case 4: return 'text-red-600 bg-red-50 border-red-200'; // Critical
      case 3: return 'text-orange-600 bg-orange-50 border-orange-200'; // High
      case 2: return 'text-yellow-600 bg-yellow-50 border-yellow-200'; // Medium
      case 1: return 'text-blue-600 bg-blue-50 border-blue-200'; // Low
      default: return 'text-gray-600 bg-gray-50 border-gray-200';
    }
  };

  const getSeverityLabel = (severity: number): string => {
    switch (severity) {
      case 4: return 'Critical';
      case 3: return 'High';
      case 2: return 'Medium';
      case 1: return 'Low';
      default: return 'Unknown';
    }
  };

  const getStatusBadge = (status: MaintenanceAlertStatus): string => {
    switch (status) {
      case MaintenanceAlertStatus.Active:
        return 'bg-red-100 text-red-800';
      case MaintenanceAlertStatus.Acknowledged:
        return 'bg-yellow-100 text-yellow-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  if (loading && alerts.length === 0) {
    return (
      <div className="bg-white rounded-lg shadow p-4">
        <h3 className="text-lg font-semibold mb-3">Maintenance Alerts</h3>
        <div className="flex items-center justify-center py-8">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
          <span className="ml-3 text-gray-600">Loading alerts...</span>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-white rounded-lg shadow p-4">
        <h3 className="text-lg font-semibold mb-3">Maintenance Alerts</h3>
        <div className="bg-red-50 border border-red-200 rounded-md p-4">
          <p className="text-red-800">Error: {error}</p>
          <Button
            variant="link"
            onClick={loadAlerts}
            className="mt-2 text-sm text-red-600 hover:text-red-800"
          >
            Retry
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="bg-white rounded-lg shadow p-4">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-lg font-semibold">Maintenance Alerts</h3>
        {alerts.length > 0 && (
          <span className="text-sm text-gray-500">
            {alerts.length} active {alerts.length === 1 ? 'alert' : 'alerts'}
          </span>
        )}
      </div>

      {alerts.length === 0 ? (
        <div className="text-center py-8 text-gray-500">
          <svg
            className="mx-auto h-12 w-12 text-gray-400"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <p className="mt-2">No active maintenance alerts</p>
          <p className="text-sm text-gray-400">All printers are in good condition</p>
        </div>
      ) : (
        <div className="space-y-3">
          {alerts.map(alert => (
            <div
              key={alert.id}
              onClick={() => onAlertClick?.(alert)}
              className={`border rounded-lg p-3 cursor-pointer hover:shadow-md transition-shadow ${getSeverityColor(alert.severity)}`}
            >
              <div className="flex items-start justify-between">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-1">
                    <span className={`px-2 py-0.5 text-xs font-semibold rounded ${getStatusBadge(alert.status)}`}>
                      {MaintenanceAlertStatus[alert.status]}
                    </span>
                    <span className="text-xs font-medium">
                      {getSeverityLabel(alert.severity)}
                    </span>
                  </div>
                  <h4 className="font-semibold text-sm mb-1">{alert.title}</h4>
                  <p className="text-sm opacity-90">{alert.message}</p>
                  {alert.hoursSinceLastMaintenance !== null && alert.hoursSinceLastMaintenance !== undefined && (
                    <p className="text-xs mt-1 opacity-75">
                      {alert.hoursSinceLastMaintenance.toFixed(1)} hours since last maintenance
                    </p>
                  )}
                  {alert.daysSinceLastMaintenance !== null && alert.daysSinceLastMaintenance !== undefined && (
                    <p className="text-xs mt-1 opacity-75">
                      {alert.daysSinceLastMaintenance} days since last maintenance
                    </p>
                  )}
                </div>
                <svg
                  className="h-5 w-5 flex-shrink-0"
                  fill="currentColor"
                  viewBox="0 0 20 20"
                >
                  <path
                    fillRule="evenodd"
                    d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"
                    clipRule="evenodd"
                  />
                </svg>
              </div>
              <div className="text-xs opacity-75 mt-2">
                Created {new Date(alert.createdAt).toLocaleDateString()} at{' '}
                {new Date(alert.createdAt).toLocaleTimeString()}
              </div>
            </div>
          ))}
        </div>
      )}

      {alerts.length > 0 && maxAlerts < alerts.length && (
        <div className="mt-3 text-center">
          <Button variant="link" className="text-sm text-blue-600 hover:text-blue-800 font-medium">
            View all alerts →
          </Button>
        </div>
      )}
    </div>
  );
};
