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

import React, { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { AlertIcon, WrenchIcon, GearIcon, CloudDownloadIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useMaintenanceAlerts } from '@/features/maintenance/hooks/useMaintenanceAlerts';
import { useMaintenanceComponents } from '@/features/maintenance/hooks/useMaintenanceComponents';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { usePrinterSummary } from '@/common/hooks/useApi';
import { usePrinterStatusUpdates } from '@/common/hooks/useSignalR';
import { toast } from 'sonner';

interface AlertItem {
  id: string;
  fingerprint?: string;
  persistDismissal?: boolean;
  icon: React.ReactNode;
  message: string;
  /** Navigation link — mutually exclusive with onAction */
  link?: string;
  /** Inline action — mutually exclusive with link */
  onAction?: () => void;
  actionLabel?: string;
  actionPending?: boolean;
  severity: 'critical' | 'warning' | 'info';
}

function AlertStrip({ item, storageKey, onDismiss }: {
  item: AlertItem;
  storageKey?: string;
  onDismiss: () => void;
}) {
  const [hidden, setHidden] = useState(() => {
    if (!storageKey) return false;
    try {
      return localStorage.getItem(storageKey) === item.fingerprint;
    } catch (error) {
      console.warn('Failed to read dashboard alert dismissal', error);
      return false;
    }
  });

  const dismiss = () => {
    setHidden(true);
    onDismiss();
    if (!storageKey || !item.fingerprint) return;
    try {
      localStorage.setItem(storageKey, item.fingerprint);
    } catch (error) {
      console.warn('Failed to save dashboard alert dismissal', error);
      toast.error('Alert hidden for now, but the dismissal could not be saved');
    }
  };

  if (hidden) return null;

  return (
    <div
      className={`flex items-center gap-3 px-4 py-2.5 rounded-lg border ${
        item.severity === 'critical'
          ? 'bg-pf-error/10 border-pf-error/30 text-pf-error'
          : item.severity === 'info'
          ? 'bg-blue-500/10 border-blue-400/20 text-blue-300'
          : 'bg-pf-warning/10 border-pf-warning/30 text-pf-warning'
      }`}
    >
      {item.icon}
      {item.link ? (
        <Link to={item.link} className="flex-1 text-sm font-medium hover:underline">
          {item.message}
        </Link>
      ) : (
        <span className="flex-1 text-sm font-medium">{item.message}</span>
      )}
      {item.onAction && (
        <Button
          variant="unstyled"
          onClick={item.onAction}
          disabled={item.actionPending}
          className="text-xs px-2.5 py-1 rounded-md bg-blue-500/20 hover:bg-blue-500/30 border border-blue-400/30 text-blue-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {item.actionLabel ?? 'Apply'}
        </Button>
      )}
      <Button
        variant="unstyled"
        onClick={dismiss}
        className="text-xs text-pf-text-primary transition-opacity"
        aria-label={`Dismiss ${item.message}`}
      >
        Dismiss
      </Button>
    </div>
  );
}

export function CriticalAlertsBanner() {
  const { alerts, isLoading: alertsLoading, error: alertsError } = useMaintenanceAlerts();
  const { data: components = [], isLoading: componentsLoading } = useMaintenanceComponents();
  const { hasPermission, user } = useAuth();
  // Backed by BackgroundServicesController, class-level `[RequirePermission("background_services", "admin")]` (#1457).
  const isAdmin = hasPermission('background_services', 'admin');
  const [dismissed, setDismissed] = useState<Set<string>>(new Set());
  const storagePrefix = user?.id ? `dashboard-alert-dismissal:${user.id}:` : undefined;
  const { data: summaries, error: summariesError, isFetching: summariesFetching } = usePrinterSummary();
  const { printerStatuses } = usePrinterStatusUpdates();
  const printers = React.useMemo(
    () => summaries?.map(summary => {
      const status = printerStatuses.get(summary.id);
      return status ? { ...summary, isOnline: status.isOnline, state: status.state } : summary;
    }),
    [summaries, printerStatuses]
  );
  const queryClient = useQueryClient();

  const applyAllTemplatesMutation = useMutation({
    mutationFn: () => apiClient.applyAllModelTemplates(),
    onSuccess: (result) => {
      toast.success(`Configuration updated for ${result.updated} printer${result.updated !== 1 ? 's' : ''}`);
      queryClient.invalidateQueries({ queryKey: ['printers'] });
    },
    onError: () => {
      toast.error('Failed to apply configuration updates');
    },
  });

  const { data: servicesSummary } = useQuery({
    queryKey: ['background-services-summary'],
    queryFn: () => apiClient.getBackgroundServicesSummary(),
    staleTime: 30_000,
    enabled: isAdmin,
  });

  const criticalAlerts = alerts.filter(
    (a) => a.status === 'Active' && (a.severity === 1 || a.severity === 2)
  );
  const printersWithUpdates = (printers ?? []).filter(p => p.hasCatalogUpdate);

  useEffect(() => {
    if (!storagePrefix) return;
    try {
      if (!alertsLoading && !alertsError && criticalAlerts.length === 0) {
        localStorage.removeItem(`${storagePrefix}maintenance-overdue`);
      }
      // The summary endpoint also returns [] during transient startup/database failures.
      if (summaries && summaries.length > 0 && !summariesError && !summariesFetching && printersWithUpdates.length === 0) {
        localStorage.removeItem(`${storagePrefix}catalog-updates`);
      }
    } catch (error) {
      console.warn('Failed to clear dashboard alert dismissals', error);
    }
  }, [alertsLoading, alertsError, criticalAlerts.length, summaries, summariesError, summariesFetching, printersWithUpdates.length, storagePrefix]);

  if (alertsLoading || componentsLoading) return null;

  const items: AlertItem[] = [];

  // Overdue maintenance (critical/high only, active status)
  if (criticalAlerts.length > 0) {
    items.push({
      id: 'maintenance-overdue',
      fingerprint: JSON.stringify(criticalAlerts.map(a => [a.id, a.createdAt, a.severity]).sort()),
      persistDismissal: true,
      icon: <WrenchIcon className="h-4 w-4" />,
      message: `${criticalAlerts.length} overdue maintenance task${criticalAlerts.length !== 1 ? 's need' : ' needs'} attention`,
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
      link: '/admin/workers',
      severity: 'warning',
    });
  }

  // Catalog template updates pending
  if (printersWithUpdates.length > 0) {
    items.push({
      id: 'catalog-updates',
      fingerprint: JSON.stringify(printersWithUpdates.map(p => [p.id, p.catalogUpdateRevision]).sort()),
      persistDismissal: printersWithUpdates.every(p => Boolean(p.catalogUpdateRevision)),
      icon: <CloudDownloadIcon className="h-4 w-4" />,
      message: `${printersWithUpdates.length} printer${printersWithUpdates.length !== 1 ? 's have' : ' has'} a configuration update available`,
      onAction: () => applyAllTemplatesMutation.mutate(),
      actionLabel: applyAllTemplatesMutation.isPending ? 'Applying…' : 'Apply All',
      actionPending: applyAllTemplatesMutation.isPending,
      severity: 'info',
    });
  }

  const visible = items.filter((item) => !dismissed.has(item.id));
  if (visible.length === 0) return null;

  return (
    <div className="mb-6 space-y-2">
      {visible.map((item) => (
        <AlertStrip
          key={`${storagePrefix ?? 'session'}${item.id}:${item.fingerprint ?? ''}`}
          item={item}
          storageKey={storagePrefix && item.persistDismissal ? `${storagePrefix}${item.id}` : undefined}
          onDismiss={() => {
            if (!item.fingerprint) setDismissed(prev => new Set(prev).add(item.id));
          }}
        />
      ))}
    </div>
  );
}
