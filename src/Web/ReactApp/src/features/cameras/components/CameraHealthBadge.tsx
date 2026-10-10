import React from 'react';
import { Badge } from '@/common/components/ui';
import { CheckCircleIcon, AlertCircleIcon, AlertIcon, HelpCircleIcon } from '@/common/components/icons/MdiIcons';
import { CameraHealthStatus } from '@/types/api';
import { formatDistanceToNow } from 'date-fns';

interface CameraHealthBadgeProps {
  healthStatus: CameraHealthStatus;
  previewFailed?: boolean;
  lastHealthCheck?: string;
  showLastCheck?: boolean;
  size?: 'sm' | 'md';
  details?: string;
}

const healthConfig: Record<CameraHealthStatus, { variant: 'default' | 'primary' | 'success' | 'warning' | 'error'; icon: React.ComponentType<{ className?: string }>; label: string }> = {
  [CameraHealthStatus.Unknown]: {
    variant: 'default',
    icon: HelpCircleIcon,
    label: 'Unknown',
  },
  [CameraHealthStatus.Healthy]: {
    variant: 'success',
    icon: CheckCircleIcon,
    label: 'Healthy',
  },
  [CameraHealthStatus.Degraded]: {
    variant: 'warning',
    icon: AlertIcon,
    label: 'Degraded',
  },
  [CameraHealthStatus.Unhealthy]: {
    variant: 'error',
    icon: AlertCircleIcon,
    label: 'Unhealthy',
  },
};

export function CameraHealthBadge({
  healthStatus,
  previewFailed = false,
  lastHealthCheck,
  showLastCheck = false,
  size = 'sm',
  details,
}: CameraHealthBadgeProps) {
  const config = healthConfig[healthStatus];
  const Icon = previewFailed ? AlertCircleIcon : config.icon;
  const variant = previewFailed ? 'error' : config.variant;
  const probeLabel = `Probe ${config.label.toLowerCase()}`;
  const label = previewFailed ? 'Preview failed' : probeLabel;

  const getLastCheckText = () => {
    if (!lastHealthCheck) return null;
    try {
      return formatDistanceToNow(new Date(lastHealthCheck), { addSuffix: true });
    } catch {
      return null;
    }
  };

  const lastCheckText = showLastCheck ? getLastCheckText() : null;

  return (
    <div
      className="inline-flex flex-col items-start gap-0.5"
      title={`${previewFailed ? 'Preview failed. ' : ''}${probeLabel}. ${details ? `${details} ` : ''}Camera probe health is checked periodically by the backend; preview status reflects this browser's image load.`}
      role="status"
      aria-label={previewFailed ? `${label} · ${probeLabel.toLowerCase()}` : probeLabel}
    >
      <Badge
        variant={variant}
        size={size}
        className="inline-flex items-center gap-1"
      >
        <span aria-hidden="true"><Icon className="w-3 h-3" /></span>
        <span className="sr-only">{label}</span>
      </Badge>
      {lastCheckText && (
        <span className="text-xs text-pf-text-tertiary">
          Last checked {lastCheckText}
        </span>
      )}
    </div>
  );
}
