import React from 'react';
import { Card } from '@/common/components/ui/Card';
import { Badge } from '@/common/components/ui';
import { Spinner } from '@/common/components/ui';
import { useActiveAlerts } from '../hooks/usePredictiveAnalytics';
import type { BadgeVariant } from '@/common/components/ui';

function severityToVariant(severity: string): BadgeVariant {
  switch (severity.toLowerCase()) {
    case 'critical':
      return 'error';
    case 'warning':
      return 'warning';
    case 'info':
      return 'info';
    default:
      return 'default';
  }
}

export const PredictiveAlertsPanel: React.FC = () => {
  const { data: alerts = [], isLoading, error } = useActiveAlerts();

  if (isLoading) {
    return (
      <Card className="p-4">
        <div className="flex items-center gap-2">
          <Spinner size="sm" />
          <span className="text-sm text-pf-text-secondary">Loading alerts…</span>
        </div>
      </Card>
    );
  }

  if (error || alerts.length === 0) {
    return null;
  }

  return (
    <Card className="border-l-4 border-l-pf-warning p-4">
      <h3 className="mb-3 text-sm font-semibold text-pf-text-primary">
        Predictive Alerts ({alerts.length})
      </h3>
      <div className="space-y-3">
        {alerts.map((alert) => (
          <div
            key={`${alert.alertType}-${alert.message}`}
            className="flex flex-col gap-1 rounded-md border border-pf-border bg-pf-bg-1 p-3"
          >
            <div className="flex items-center gap-2">
              <Badge variant={severityToVariant(alert.severity)} size="sm">
                {alert.severity}
              </Badge>
              <span className="text-sm font-medium text-pf-text-primary">
                {alert.message}
              </span>
            </div>
            <p className="text-xs text-pf-text-secondary">
              {alert.recommendedAction}
            </p>
          </div>
        ))}
      </div>
    </Card>
  );
};
