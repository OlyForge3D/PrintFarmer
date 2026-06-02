import { useId, type ReactNode } from 'react';
import clsx from 'clsx';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Badge,
  Button,
  Card,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@/common/components/ui';
import {
  ActivityIcon,
  ChartIcon,
  ClockIcon,
  DatabaseIcon,
  PackageIcon,
  RefreshIcon,
  ServerIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import { formatFileSize } from '@/common/utils/stlFileUtils';
import { apiClient } from '@/services/api';
import { SystemServiceHealth, type SystemInfo } from '@/types/api';

const EMPTY_VALUE = '—';
const SYSTEM_INFO_QUERY_KEY = ['system-info'];
const SYSTEM_INFO_REFRESH_INTERVAL_MS = 30_000;

function clampPercentage(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  return Math.min(100, Math.max(0, value));
}

function getUsagePercentage(used: number, total: number): number {
  if (total <= 0) {
    return 0;
  }

  return clampPercentage((used / total) * 100);
}

function formatPercent(value: number): string {
  return `${clampPercentage(value).toFixed(1)}%`;
}

function formatCount(value: number): string {
  return new Intl.NumberFormat().format(value);
}

function getErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return 'Unable to load system status.';
}

function getServiceBadgeVariant(health: SystemServiceHealth): 'success' | 'warning' | 'error' {
  switch (health) {
    case SystemServiceHealth.Healthy:
      return 'success';
    case SystemServiceHealth.Degraded:
      return 'warning';
    case SystemServiceHealth.Critical:
      return 'error';
    default:
      return 'warning';
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

interface StatusCardProps {
  title: string;
  description: string;
  icon: ReactNode;
  children: ReactNode;
  className?: string;
}

function StatusCard({ title, description, icon, children, className }: StatusCardProps) {
  return (
    <Card className={clsx('border border-pf-border bg-pf-bg-0 shadow-sm', className)}>
      <Card.Header className="border-b border-pf-border/70 bg-pf-bg-1/60">
        <div className="flex items-start gap-3">
          <div className="mt-0.5 rounded-lg border border-pf-border bg-pf-bg-0 p-2 text-pf-text-primary" aria-hidden="true">
            {icon}
          </div>
          <div className="min-w-0">
            <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-pf-text-primary">{title}</h3>
            <p className="mt-1 text-sm text-pf-text-secondary">{description}</p>
          </div>
        </div>
      </Card.Header>
      <Card.Body className="space-y-4">{children}</Card.Body>
    </Card>
  );
}

interface StatItemProps {
  label: string;
  value: string;
}

function StatItem({ label, value }: StatItemProps) {
  return (
    <div className="flex items-start justify-between gap-4 rounded-lg border border-pf-border/70 bg-pf-bg-1/40 px-3 py-2">
      <dt className="text-sm text-pf-text-secondary">{label}</dt>
      <dd className="text-right text-sm font-medium text-pf-text-primary">{value}</dd>
    </div>
  );
}

interface UsageMeterProps {
  label: string;
  value: number;
  details: string;
}

function UsageMeter({ label, value, details }: UsageMeterProps) {
  const labelId = useId();
  const detailsId = useId();
  const normalizedValue = clampPercentage(value);

  return (
    <div className="space-y-2 rounded-lg border border-pf-border/70 bg-pf-bg-1/40 p-3">
      <div className="flex items-center justify-between gap-3">
        <span id={labelId} className="text-sm font-medium text-pf-text-primary">
          {label}
        </span>
        <span className="text-sm font-semibold text-pf-text-primary">{formatPercent(normalizedValue)}</span>
      </div>
      <div
        role="meter"
        aria-labelledby={labelId}
        aria-describedby={detailsId}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Number(normalizedValue.toFixed(1))}
        aria-valuetext={`${formatPercent(normalizedValue)} used`}
        className="h-3 overflow-hidden rounded-full border border-pf-border bg-pf-bg-2"
      >
        <div
          aria-hidden="true"
          className={clsx('h-full rounded-full transition-[width]', getMeterFillClassName(normalizedValue))}
          style={{ width: `${normalizedValue}%` }}
        />
      </div>
      <p id={detailsId} className="text-xs text-pf-text-secondary">
        {details}
      </p>
    </div>
  );
}

function formatUpdatedAt(value: number): string {
  if (!value) {
    return 'Waiting for first successful refresh';
  }

  return `Updated ${new Date(value).toLocaleTimeString([], {
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
  })}`;
}

function renderServicesTable(systemInfo: SystemInfo) {
  if (systemInfo.services.length === 0) {
    return <p className="text-sm text-pf-text-secondary">No services were reported by the API.</p>;
  }

  return (
    <Table>
      <TableHead>
        <TableRow>
          <TableHeaderCell scope="col">Service</TableHeaderCell>
          <TableHeaderCell scope="col">Version</TableHeaderCell>
          <TableHeaderCell scope="col">Health</TableHeaderCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {systemInfo.services.map((service) => (
          <TableRow key={service.name}>
            <TableHeaderCell scope="row">{service.name}</TableHeaderCell>
            <TableCell>{service.version || EMPTY_VALUE}</TableCell>
            <TableCell>
              <Badge variant={getServiceBadgeVariant(service.health)}>{service.health}</Badge>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

export function SystemStatusPage() {
  const { data, error, isFetching, isLoading, refetch, dataUpdatedAt } = useQuery({
    queryKey: SYSTEM_INFO_QUERY_KEY,
    queryFn: () => apiClient.getSystemInfo(),
    refetchInterval: SYSTEM_INFO_REFRESH_INTERVAL_MS,
    staleTime: SYSTEM_INFO_REFRESH_INTERVAL_MS,
  });

  if (isLoading) {
    return (
      <div className="flex min-h-[16rem] items-center justify-center rounded-lg border border-pf-border bg-pf-bg-0">
        <div className="flex flex-col items-center gap-3 text-center">
          <Spinner size="lg" />
          <p className="text-sm text-pf-text-secondary">Loading system status…</p>
        </div>
      </div>
    );
  }

  if (error || !data) {
    return (
      <Alert variant="error" title="Failed to load system status">
        {getErrorMessage(error)}
      </Alert>
    );
  }

  const memoryUsage = getUsagePercentage(data.memory.usedBytes, data.memory.totalBytes);
  const diskUsage = getUsagePercentage(data.disk.usedBytes, data.disk.totalBytes);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 rounded-lg border border-pf-border bg-pf-bg-0 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.18em] text-pf-text-secondary">Operational snapshot</p>
          <p className="mt-1 text-sm text-pf-text-secondary">
            30-second auto-refresh for host health, service versions, and worker-adjacent infrastructure.
          </p>
          <p className="mt-2 text-xs text-pf-text-secondary">{formatUpdatedAt(dataUpdatedAt)}</p>
        </div>
        <Button
          variant="secondary"
          iconLeft={<RefreshIcon className="h-4 w-4" />}
          loading={isFetching}
          onClick={() => void refetch()}
        >
          Refresh
        </Button>
      </div>

      <p className="sr-only" aria-live="polite">
        {isFetching ? 'Refreshing system status.' : 'System status is current.'}
      </p>

      <div className="grid gap-4 xl:grid-cols-2">
        <StatusCard
          title="Application"
          description="Deployment identity and host runtime details."
          icon={<ActivityIcon className="h-5 w-5" />}
        >
          <dl className="grid gap-2">
            <StatItem label="Version" value={data.app.version || EMPTY_VALUE} />
            <StatItem label="Hostname" value={data.app.hostname || EMPTY_VALUE} />
            <StatItem label="Uptime" value={data.app.uptime || EMPTY_VALUE} />
          </dl>
        </StatusCard>

        <StatusCard
          title="CPU"
          description="Live processor utilization and available core count."
          icon={<ChartIcon className="h-5 w-5" />}
        >
          <UsageMeter
            label="Processor usage"
            value={data.cpu.usagePercent}
            details={`${formatCount(data.cpu.cores)} core${data.cpu.cores === 1 ? '' : 's'} available`}
          />
          <dl className="grid gap-2">
            <StatItem label="Cores" value={formatCount(data.cpu.cores)} />
            <StatItem label="Usage" value={formatPercent(data.cpu.usagePercent)} />
          </dl>
        </StatusCard>

        <StatusCard
          title="Memory"
          description="Working set pressure across the current host."
          icon={<ServerIcon className="h-5 w-5" />}
        >
          <UsageMeter
            label="Memory usage"
            value={memoryUsage}
            details={`${formatFileSize(data.memory.usedBytes)} of ${formatFileSize(data.memory.totalBytes)} in use`}
          />
          <dl className="grid gap-2">
            <StatItem label="Used" value={formatFileSize(data.memory.usedBytes)} />
            <StatItem label="Total" value={formatFileSize(data.memory.totalBytes)} />
          </dl>
        </StatusCard>

        <StatusCard
          title="Disk"
          description="Storage usage for the host, archive footprint, and database growth."
          icon={<PackageIcon className="h-5 w-5" />}
        >
          <UsageMeter
            label="Disk usage"
            value={diskUsage}
            details={`${formatFileSize(data.disk.usedBytes)} of ${formatFileSize(data.disk.totalBytes)} allocated`}
          />
          <dl className="grid gap-2">
            <StatItem label="Archive footprint" value={formatFileSize(data.disk.archiveBytes)} />
            <StatItem label="Database footprint" value={formatFileSize(data.disk.databaseBytes)} />
            <StatItem label="Total capacity" value={formatFileSize(data.disk.totalBytes)} />
          </dl>
        </StatusCard>

        <StatusCard
          title="Services"
          description="Version inventory and health state for the running service surface."
          icon={<WrenchIcon className="h-5 w-5" />}
          className="xl:col-span-2"
        >
          {renderServicesTable(data)}
        </StatusCard>

        <StatusCard
          title="Database"
          description="Persistence engine details and object counts for the farm."
          icon={<DatabaseIcon className="h-5 w-5" />}
          className="xl:col-span-2"
        >
          <dl className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
            <StatItem label="Engine" value={data.database.engine || EMPTY_VALUE} />
            <StatItem label="Version" value={data.database.version || EMPTY_VALUE} />
            <StatItem label="Printers" value={formatCount(data.database.printerCount)} />
            <StatItem label="Archives" value={formatCount(data.database.archiveCount)} />
          </dl>
          <div className="flex flex-wrap items-center gap-2 rounded-lg border border-pf-border/70 bg-pf-bg-1/40 px-3 py-2 text-xs text-pf-text-secondary">
            <ClockIcon className="h-4 w-4" aria-hidden="true" />
            Counts come from the current database snapshot exposed by the API.
          </div>
        </StatusCard>
      </div>
    </div>
  );
}
