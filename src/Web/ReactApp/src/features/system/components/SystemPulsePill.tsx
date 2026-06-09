import { useQuery } from '@tanstack/react-query';
import clsx from 'clsx';
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import {
  ActivityIcon,
  ChartIcon,
  CloseIcon,
  PackageIcon,
  ServerIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import { Badge, Button } from '@/common/components/ui';
import { formatFileSize } from '@/common/utils/stlFileUtils';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import type { SystemInfo, SystemServiceHealth } from '@/types/api';

const EMPTY_VALUE = '—';
const SYSTEM_INFO_QUERY_KEY = ['system-info'];
const SYSTEM_INFO_REFETCH_INTERVAL_MS = 30_000;
const SYSTEM_INFO_STALE_TIME_MS = 10_000;
const SYSTEM_PULSE_PANEL_MAX_HEIGHT_CLASS = 'max-h-[calc(100vh-4rem)]';
const SYSTEM_PULSE_PANEL_MAX_WIDTH_CLASS = 'max-w-[calc(100vw-1rem)]';
const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  '[href]',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

const SYSTEM_SERVICE_HEALTH = {
  Healthy: 'Healthy',
  Degraded: 'Degraded',
  Critical: 'Critical',
} satisfies Record<SystemServiceHealth, SystemServiceHealth>;

type HealthBadgeVariant = 'success' | 'warning' | 'error';

interface HealthTone {
  label: string;
  buttonClassName: string;
  dotClassName: string;
  panelAccentClassName: string;
  badgeVariant: HealthBadgeVariant;
}

interface UsageMeterProps {
  label: string;
  value: number;
  details: string;
  icon: React.ReactNode;
}

interface SystemPulsePillProps {
  onClick?: () => void;
  className?: string;
}

function clampPercentage(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  return Math.min(100, Math.max(0, value));
}

function formatPercent(value: number): string {
  return `${clampPercentage(value).toFixed(1)}%`;
}

function getUsagePercentage(used: number, total: number): number {
  if (total <= 0) {
    return 0;
  }

  return clampPercentage((used / total) * 100);
}

function getFocusableElements(container: HTMLElement | null): HTMLElement[] {
  if (!container) {
    return [];
  }

  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter((element) => {
    if (element.hasAttribute('disabled')) {
      return false;
    }

    return element.tabIndex >= 0;
  });
}

function getWorstServiceHealth(services: SystemInfo['services']): SystemServiceHealth {
  if (services.some((service) => service.health === SYSTEM_SERVICE_HEALTH.Critical)) {
    return SYSTEM_SERVICE_HEALTH.Critical;
  }

  if (services.some((service) => service.health === SYSTEM_SERVICE_HEALTH.Degraded)) {
    return SYSTEM_SERVICE_HEALTH.Degraded;
  }

  return SYSTEM_SERVICE_HEALTH.Healthy;
}

function getHealthTone(health: SystemServiceHealth): HealthTone {
  switch (health) {
    case SYSTEM_SERVICE_HEALTH.Critical:
      return {
        label: 'Critical',
        buttonClassName: 'border-pf-error/40 bg-pf-error/10 text-pf-error-text hover:bg-pf-error/15',
        dotClassName: 'bg-pf-error text-pf-error shadow-[0_0_12px_currentColor]',
        panelAccentClassName: 'from-pf-error/18 via-pf-error/6 to-transparent',
        badgeVariant: 'error',
      };
    case SYSTEM_SERVICE_HEALTH.Degraded:
      return {
        label: 'Degraded',
        buttonClassName: 'border-pf-warning/40 bg-pf-warning/10 text-pf-warning-text hover:bg-pf-warning/15',
        dotClassName: 'bg-pf-warning text-pf-warning shadow-[0_0_12px_currentColor]',
        panelAccentClassName: 'from-pf-warning/18 via-pf-warning/6 to-transparent',
        badgeVariant: 'warning',
      };
    case SYSTEM_SERVICE_HEALTH.Healthy:
    default:
      return {
        label: 'Healthy',
        buttonClassName: 'border-pf-success/35 bg-pf-success/10 text-pf-success-text hover:bg-pf-success/15',
        dotClassName: 'bg-pf-success text-pf-success shadow-[0_0_12px_currentColor]',
        panelAccentClassName: 'from-pf-success/18 via-pf-success/6 to-transparent',
        badgeVariant: 'success',
      };
  }
}

function getServiceBadgeVariant(health: SystemServiceHealth): HealthBadgeVariant {
  switch (health) {
    case SYSTEM_SERVICE_HEALTH.Critical:
      return 'error';
    case SYSTEM_SERVICE_HEALTH.Degraded:
      return 'warning';
    case SYSTEM_SERVICE_HEALTH.Healthy:
    default:
      return 'success';
  }
}

function getMeterFillClassName(value: number): string {
  if (value >= 90) {
    return 'bg-pf-error';
  }

  if (value >= 75) {
    return 'bg-pf-warning';
  }

  return 'bg-pf-accent';
}

function UsageMeter({ label, value, details, icon }: UsageMeterProps) {
  const labelId = useId();
  const detailsId = useId();
  const normalizedValue = clampPercentage(value);

  return (
    <div className="rounded-lg border border-pf-border/70 bg-pf-bg-0/90 px-3 py-2.5">
      <div className="mb-2 flex items-center justify-between gap-3">
        <div className="flex min-w-0 items-center gap-2">
          <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-pf-border/70 bg-pf-bg-1 text-pf-text-secondary" aria-hidden="true">
            {icon}
          </span>
          <span id={labelId} className="truncate text-sm font-medium text-pf-text-primary">
            {label}
          </span>
        </div>
        <span className="text-xs font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">{formatPercent(normalizedValue)}</span>
      </div>
      <div
        role="meter"
        aria-labelledby={labelId}
        aria-describedby={detailsId}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Number(normalizedValue.toFixed(1))}
        aria-valuetext={`${formatPercent(normalizedValue)} used`}
        className="h-2 overflow-hidden rounded-full border border-pf-border/70 bg-pf-bg-2"
      >
        <div
          aria-hidden="true"
          className={clsx('h-full rounded-full transition-[width]', getMeterFillClassName(normalizedValue))}
          style={{ width: `${normalizedValue}%` }}
        />
      </div>
      <p id={detailsId} className="mt-2 text-xs text-pf-text-secondary">
        {details}
      </p>
    </div>
  );
}

export function SystemPulsePill({ onClick, className }: SystemPulsePillProps = {}) {
  const { hasRole } = useAuth();
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const dialogTitleId = useId();
  const dialogId = useId();
  const isFarmAdmin = hasRole('farm_admin');
  const usesExternalAction = typeof onClick === 'function';

  const { data, error } = useQuery({
    queryKey: SYSTEM_INFO_QUERY_KEY,
    queryFn: () => apiClient.getSystemInfo(),
    enabled: isFarmAdmin,
    refetchInterval: SYSTEM_INFO_REFETCH_INTERVAL_MS,
    staleTime: SYSTEM_INFO_STALE_TIME_MS,
  });

  const closePanel = useCallback(() => {
    setIsOpen(false);
    window.requestAnimationFrame(() => {
      triggerRef.current?.focus();
    });
  }, []);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    const panel = panelRef.current;
    if (!panel) {
      return undefined;
    }

    const focusFrame = window.requestAnimationFrame(() => {
      const [firstFocusableElement] = getFocusableElements(panel);
      (firstFocusableElement ?? panel).focus();
    });

    const handleMouseDown = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (!target) {
        return;
      }

      if (panel.contains(target) || triggerRef.current?.contains(target)) {
        return;
      }

      closePanel();
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        closePanel();
        return;
      }

      if (event.key !== 'Tab') {
        return;
      }

      const focusableElements = getFocusableElements(panel);
      if (focusableElements.length === 0) {
        event.preventDefault();
        panel.focus();
        return;
      }

      const firstFocusableElement = focusableElements[0];
      const lastFocusableElement = focusableElements[focusableElements.length - 1];
      const activeElement = document.activeElement as HTMLElement | null;

      if (event.shiftKey && (activeElement === firstFocusableElement || activeElement === panel)) {
        event.preventDefault();
        lastFocusableElement.focus();
        return;
      }

      if (!event.shiftKey && activeElement === lastFocusableElement) {
        event.preventDefault();
        firstFocusableElement.focus();
      }
    };

    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);

    return () => {
      window.cancelAnimationFrame(focusFrame);
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [closePanel, isOpen]);

  const memoryUsage = useMemo(() => {
    if (!data) {
      return 0;
    }

    return getUsagePercentage(data.memory.usedBytes, data.memory.totalBytes);
  }, [data]);

  const diskUsage = useMemo(() => {
    if (!data) {
      return 0;
    }

    return getUsagePercentage(data.disk.usedBytes, data.disk.totalBytes);
  }, [data]);

  const overallHealth = useMemo(() => {
    if (!data) {
      return SYSTEM_SERVICE_HEALTH.Healthy;
    }

    return getWorstServiceHealth(data.services);
  }, [data]);

  const tone = useMemo(() => getHealthTone(overallHealth), [overallHealth]);

  if (!isFarmAdmin) {
    return null;
  }

  if (error || !data) {
    const errorTone = getHealthTone(SYSTEM_SERVICE_HEALTH.Degraded);
    return (
      <div className="relative">
        <Button
          type="button"
          variant="subtle"
          size="sm"
          disabled={!usesExternalAction}
          onClick={onClick}
          title={usesExternalAction ? 'View system status' : 'System status degraded — unable to reach health endpoint'}
          className={clsx(
            'h-8 rounded-full border px-2.5 text-[11px] font-semibold uppercase tracking-[0.18em]',
            errorTone.buttonClassName,
            className,
          )}
          aria-label={usesExternalAction ? 'System status degraded, view system status' : 'System status degraded'}
        >
          <span className="flex items-center gap-2">
            <span className={clsx('h-2.5 w-2.5 rounded-full', errorTone.dotClassName)} aria-hidden="true" />
            <span>System</span>
          </span>
        </Button>
      </div>
    );
  }

  return (
    <div className="relative">
      <Button
        ref={triggerRef}
        type="button"
        variant="subtle"
        size="sm"
        onClick={() => {
          if (usesExternalAction) {
            onClick();
            return;
          }

          setIsOpen((currentValue) => !currentValue);
        }}
        aria-expanded={usesExternalAction ? undefined : isOpen}
        aria-haspopup={usesExternalAction ? undefined : 'dialog'}
        aria-controls={!usesExternalAction && isOpen ? dialogId : undefined}
        title={usesExternalAction ? `View system status — ${tone.label}` : `System pulse — ${tone.label}`}
        className={clsx(
          'h-8 rounded-full border px-2.5 text-[11px] font-semibold uppercase tracking-[0.18em] transition-colors',
          tone.buttonClassName,
          className,
        )}
      >
        <span className="flex items-center gap-2">
          <span className={clsx('h-2.5 w-2.5 rounded-full', tone.dotClassName)} aria-hidden="true" />
          <span aria-hidden="true" className="flex items-center text-current/80">
            <ActivityIcon className="h-3.5 w-3.5" />
          </span>
          <span>System</span>
          <span className="sr-only">, {tone.label} health</span>
        </span>
      </Button>

      {!usesExternalAction && isOpen && (
        <div
          ref={panelRef}
          id={dialogId}
          role="dialog"
          aria-modal="true"
          aria-labelledby={dialogTitleId}
          tabIndex={-1}
          className={clsx(
            'absolute right-0 top-full z-50 mt-2 w-[22rem] overflow-hidden rounded-2xl border border-pf-border/80 bg-pf-bg-1/95 shadow-[0_22px_60px_-28px_rgba(0,0,0,0.85)] backdrop-blur-md',
            SYSTEM_PULSE_PANEL_MAX_WIDTH_CLASS,
          )}
        >
          <div className={clsx('pointer-events-none absolute inset-x-0 top-0 h-16 bg-linear-to-b', tone.panelAccentClassName)} aria-hidden="true" />
          <div className={clsx('relative space-y-4 overflow-y-auto p-4', SYSTEM_PULSE_PANEL_MAX_HEIGHT_CLASS)}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className={clsx('h-2.5 w-2.5 rounded-full', tone.dotClassName)} aria-hidden="true" />
                  <h2 id={dialogTitleId} className="text-sm font-semibold uppercase tracking-[0.18em] text-pf-text-primary">
                    System Pulse
                  </h2>
                </div>
                <p className="mt-1 text-xs text-pf-text-secondary">
                  Ambient farm health snapshot for host load and service versions.
                </p>
              </div>
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={closePanel}
                aria-label="Close system pulse panel"
                className="h-8 w-8 shrink-0 rounded-full p-0"
              >
                <CloseIcon className="h-4 w-4" />
              </Button>
            </div>

            <div className="grid gap-2.5">
              <UsageMeter
                label="CPU"
                value={data.cpu.usagePercent}
                details={`${data.cpu.cores} core${data.cpu.cores === 1 ? '' : 's'} online`}
                icon={<ChartIcon className="h-4 w-4" />}
              />
              <UsageMeter
                label="Memory"
                value={memoryUsage}
                details={`${formatFileSize(data.memory.usedBytes)} of ${formatFileSize(data.memory.totalBytes)} in use`}
                icon={<ServerIcon className="h-4 w-4" />}
              />
              <UsageMeter
                label="Disk"
                value={diskUsage}
                details={`${formatFileSize(data.disk.usedBytes)} of ${formatFileSize(data.disk.totalBytes)} allocated`}
                icon={<PackageIcon className="h-4 w-4" />}
              />
            </div>

            <section aria-label="Service versions" className="rounded-xl border border-pf-border/70 bg-pf-bg-0/90 p-3">
              <div className="mb-2 flex items-center justify-between gap-3">
                <div className="flex items-center gap-2">
                  <span className="flex h-7 w-7 items-center justify-center rounded-full border border-pf-border/70 bg-pf-bg-1 text-pf-text-secondary" aria-hidden="true">
                    <WrenchIcon className="h-4 w-4" />
                  </span>
                  <div>
                    <p className="text-sm font-medium text-pf-text-primary">Service surface</p>
                    <p className="text-xs text-pf-text-secondary">Worst state: {tone.label}</p>
                  </div>
                </div>
                <Badge variant={tone.badgeVariant}>{tone.label}</Badge>
              </div>

              <ul className="space-y-2">
                {data.services.map((service) => (
                  <li
                    key={service.name}
                    className="flex items-center justify-between gap-3 rounded-lg border border-pf-border/60 bg-pf-bg-1/70 px-3 py-2"
                  >
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium text-pf-text-primary">{service.name}</p>
                      <p className="truncate text-xs text-pf-text-secondary">{service.version || EMPTY_VALUE}</p>
                    </div>
                    <span className="inline-flex shrink-0 items-center gap-1.5 text-[11px] font-semibold uppercase tracking-[0.16em] text-pf-text-secondary">
                      <Badge dot variant={getServiceBadgeVariant(service.health)} />
                      <span>{service.health}</span>
                    </span>
                  </li>
                ))}
              </ul>
            </section>

            <p className="text-[11px] uppercase tracking-[0.16em] text-pf-text-tertiary">
              App {data.app.version || EMPTY_VALUE} on {data.app.hostname || EMPTY_VALUE}
            </p>
          </div>
        </div>
      )}
    </div>
  );
}
