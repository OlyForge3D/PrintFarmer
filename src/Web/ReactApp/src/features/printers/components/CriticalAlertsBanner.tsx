/**
 * CriticalAlertsBanner Component
 *
 * Compact alert strip at the top of the main dashboard that surfaces
 * only actionable items requiring user intervention:
 * - Overdue maintenance tasks (critical/high severity)
 * - Background service failures (admin only)
 * - Low stock on spare parts
 *
 * Invisible when no actionable alerts exist.
 */

import React, { useState } from 'react';
import { Link } from 'react-router-dom';
import { AlertIcon, WrenchIcon, GearIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useMaintenanceAlerts } from '@/features/maintenance/hooks/useMaintenanceAlerts';
import { useMaintenanceComponents } from '@/features/maintenance/hooks/useMaintenanceComponents';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { useQuery } from '@tanstack/react-query';

interface AlertItem {
  id: string;
  icon: React.ReactNode;
  message: string;
  link: string;
  severity: 'critical' | 'warning';
}

export function CriticalAlertsBanner() {
  const { alerts, isLoading: alertsLoading } = useMaintenanceAlerts();
  const { data: components = [], isLoading: componentsLoading } = useMaintenanceComponents();
  const { hasRole } = useAuth();
  const isAdmin = hasRole('farm_admin');
  const [dismissed, setDismissed] = useState<Set<string>>(new Set());

  const { data: servicesSummary } = useQuery({
    queryKey: ['background-services-summary'],
    queryFn: () => apiClient.getBackgroundServicesSummary(),
    staleTime: 30_000,
    enabled: isAdmin,
  });

  if (alertsLoading || componentsLoading) return null;

  const items: AlertItem[] = [];

  // Overdue maintenance (critical/high only, active status)
  const criticalAlerts = alerts.filter(
    (a) => a.status === 'Active' && (a.severity === 1 || a.severity === 2)
  );
  if (criticalAlerts.length > 0) {
    items.push({
      id: 'maintenance-overdue',
      icon: <WrenchIcon className="h-4 w-4" />,
      message: `${criticalAlerts.length} overdue maintenance task${criticalAlerts.length !== 1 ? 's' : ''} need attention`,
      link: '/maintenance',
      severity: 'critical',
    });
  }

  // Low stock parts
  const lowStock = components.filter((c) => c.inStock < c.minimumStock);
  if (lowStock.length > 0) {
    items.push({
      id: 'low-stock',
      icon: <AlertIcon className="h-4 w-4" />,
      message: `${lowStock.length} spare part${lowStock.length !== 1 ? 's' : ''} below minimum stock`,
      link: '/maintenance?tab=inventory',
      severity: 'warning',
    });
  }

  // Background service failures (admin only)
  if (isAdmin && servicesSummary && servicesSummary.servicesWithErrors > 0) {
    items.push({
      id: 'service-errors',
      icon: <GearIcon className="h-4 w-4" />,
      message: `${servicesSummary.servicesWithErrors} background service${servicesSummary.servicesWithErrors !== 1 ? 's' : ''} with errors`,
      link: '/admin/system?tab=services',
      severity: 'warning',
    });
  }

  const visible = items.filter((item) => !dismissed.has(item.id));
  if (visible.length === 0) return null;

  return (
    <div className="mb-6 space-y-2">
      {visible.map((item) => (
        <div
          key={item.id}
          className={`flex items-center gap-3 px-4 py-2.5 rounded-lg border ${
            item.severity === 'critical'
              ? 'bg-red-500/10 border-red-500/30 text-red-400'
              : 'bg-amber-500/10 border-amber-500/30 text-amber-400'
          }`}
        >
          {item.icon}
          <Link to={item.link} className="flex-1 text-sm font-medium hover:underline">
            {item.message}
          </Link>
          <Button
            variant="unstyled"
            onClick={() => setDismissed((prev) => new Set(prev).add(item.id))}
            className="text-xs opacity-60 hover:opacity-100 transition-opacity"
            aria-label={`Dismiss ${item.message}`}
          >
            Dismiss
          </Button>
        </div>
      ))}
    </div>
  );
}
