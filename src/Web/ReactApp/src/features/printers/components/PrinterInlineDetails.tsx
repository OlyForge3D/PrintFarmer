import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { queryKeys, usePrintJobObjects } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import {
  Button,
  CollapsibleSection,
} from '@/common/components/ui';
import { RefreshIcon } from '@/common/components/icons/MdiIcons';
import { canExcludeObject, getPrinterSupport } from '@/features/printers/utils/printerSupport';
import type {
  ApiError,
  Printer,
  PrinterBackendCapabilitiesDto,
  PrintJobObjectDto,
  PrintJobObjectListDto,
} from '@/types/api';

interface PrinterInlineDetailsProps {
  printerId: string;
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
}

const neverSyncedCutoff = new Date('1970-01-01T00:00:00.000Z').getTime();

function shouldRetryStatisticsQuery(failureCount: number, error: unknown) {
  const statusCode =
    typeof error === 'object' && error
      ? (error as ApiError).statusCode ?? (error as { response?: { status?: number } }).response?.status
      : undefined;

  if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
    return false;
  }

  return failureCount < 2;
}

function formatLastSyncTime(lastSyncTime?: string | null) {
  if (!lastSyncTime) {
    return '—';
  }

  const timestamp = Date.parse(lastSyncTime);
  if (!Number.isFinite(timestamp) || timestamp <= neverSyncedCutoff) {
    return '—';
  }

  return new Date(timestamp).toLocaleString();
}

function formatHours(hours?: number | null): string {
  if (typeof hours !== 'number' || !Number.isFinite(hours)) {
    return '—';
  }
  if (hours < 1) {
    return `${Math.round(hours * 60)} min`;
  }
  return `${hours.toFixed(1)} h`;
}

function formatFilament(grams?: number | null): string {
  if (typeof grams !== 'number' || !Number.isFinite(grams)) {
    return '—';
  }
  if (grams >= 1000) {
    return `${(grams / 1000).toFixed(2)} kg`;
  }
  return `${Math.round(grams)} g`;
}

/**
 * Informational-only detail sections rendered inline beneath a
 * {@link DetailedPrinterCard}. This is intentionally NOT the full
 * {@link PrinterDetailsSidebar}: it renders read-only Statistics and Version
 * plus the Objects-skipping affordance (the only informational sub-panel the
 * card doesn't already provide via its dedicated modals/panels), and does not
 * mount any of the sidebar's action buttons, temperature/movement pads,
 * materials rail, spool picker, files or history modals.
 *
 * Duplicating those in a detailed card previously produced two Materials
 * modules, two of each modal and per-card query fan-out from the full sidebar
 * subtree; the card owns those already, so this component contributes only the
 * missing informational surface. See #1585 blocker 4.
 */
export function PrinterInlineDetails({
  printerId,
  printer,
  backendCapabilities,
}: PrinterInlineDetailsProps) {
  const queryClient = useQueryClient();

  const [isStatisticsExpanded, setIsStatisticsExpanded] = useState(false);
  const [isVersionExpanded, setIsVersionExpanded] = useState(true);
  const [objectToSkip, setObjectToSkip] = useState<PrintJobObjectDto | null>(null);

  const printerStatisticsQuery = useQuery({
    queryKey: ['printerStatistics', printerId],
    queryFn: () => maintenanceService.getPrinterStatistics(printerId),
    enabled: !!printerId && isStatisticsExpanded,
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    refetchOnWindowFocus: false,
    retry: shouldRetryStatisticsQuery,
  });

  const printerVersionQuery = useQuery({
    queryKey: ['printerVersion', printerId],
    queryFn: () => apiClient.getPrinterVersionInfo(printerId),
    enabled: !!printerId && isVersionExpanded,
    staleTime: 10 * 60_000,
    gcTime: 60 * 60_000,
    refetchOnWindowFocus: false,
  });

  const support = getPrinterSupport(backendCapabilities);
  const rawState = printer.state ?? 'unknown';
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const isPrinting = rawState.toLowerCase().includes('printing');
  const isPaused = rawState.toLowerCase().includes('paused');

  const printJobObjectsQuery = usePrintJobObjects(printerId, {
    enabled: !!printerId && support.supportsObjectExclusion && (isPrinting || isPaused),
  });

  const excludeObjectMutation = useMutation({
    mutationFn: (name: string) => apiClient.excludePrintJobObject(printerId, name),
    onSuccess: async (result, name) => {
      if (result.success) {
        toast.success(`Skipped object "${name}"`);
        queryClient.setQueryData<PrintJobObjectListDto>(
          queryKeys.printJobObjects(printerId),
          (old) =>
            old
              ? {
                  ...old,
                  objects: old.objects.map((object) =>
                    object.name === name
                      ? { ...object, isExcluded: true, isCurrent: false }
                      : object,
                  ),
                }
              : old,
        );
        setObjectToSkip(null);
      } else {
        toast.error(`Failed to skip object: ${result.message ?? result.error ?? 'Unknown error'}`);
      }
      await queryClient.invalidateQueries({ queryKey: queryKeys.printJobObjects(printerId) });
    },
    onError: (error: Error) => {
      toast.error(`Failed to skip object: ${error.message}`);
    },
  });

  const canExcludeObjectNow = canExcludeObject({ isOnline, isEnabled, isPrinting, isPaused, support });
  const printJobObjects = printJobObjectsQuery.data?.objects ?? [];

  return (
    <section
      role="region"
      aria-label={`${printer.name ?? 'Printer'} details`}
      data-layout="inline"
      data-testid="printer-inline-details"
      className="min-w-0 space-y-4 border-t border-white/10 pt-4"
    >
      <CollapsibleSection
        title="Statistics"
        expanded={isStatisticsExpanded}
        onToggle={setIsStatisticsExpanded}
        defaultExpanded={false}
        headerActions={
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => void printerStatisticsQuery.refetch()}
            className="p-1! h-auto!"
            title="Refresh statistics"
            aria-label="Refresh statistics"
            iconCenter={<RefreshIcon className="h-4 w-4" />}
          />
        }
      >
        {printerStatisticsQuery.isLoading ? (
          <div className="text-sm text-pf-text-secondary">Loading statistics…</div>
        ) : printerStatisticsQuery.data ? (
          <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-xs">
            <div>
              <dt className="text-xs text-pf-text-secondary">Print time</dt>
              <dd className="font-medium text-pf-text-primary">
                {formatHours(printerStatisticsQuery.data.totalPrintHours)}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">Filament</dt>
              <dd className="font-medium text-pf-text-primary">
                {formatFilament(printerStatisticsQuery.data.totalFilamentUsedGrams)}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">Completed</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerStatisticsQuery.data.totalJobsCompleted}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">Failed</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerStatisticsQuery.data.totalJobsFailed}
              </dd>
            </div>
            <div className="col-span-2">
              <dt className="text-xs text-pf-text-secondary">Last sync</dt>
              <dd className="text-pf-text-primary">
                {formatLastSyncTime(printerStatisticsQuery.data.lastSyncTime)}
              </dd>
            </div>
          </dl>
        ) : (
          <div className="text-sm text-pf-text-secondary">Statistics unavailable.</div>
        )}
      </CollapsibleSection>

      <CollapsibleSection
        title="Version"
        expanded={isVersionExpanded}
        onToggle={setIsVersionExpanded}
        headerActions={
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => void printerVersionQuery.refetch()}
            className="p-1! h-auto!"
            title="Refresh version info"
            aria-label="Refresh version info"
            iconCenter={<RefreshIcon className="h-4 w-4" />}
          />
        }
      >
        {printerVersionQuery.isLoading ? (
          <div className="text-sm text-pf-text-secondary">Loading version…</div>
        ) : printerVersionQuery.data ? (
          <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-xs">
            <div>
              <dt className="text-xs text-pf-text-secondary">Firmware</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerVersionQuery.data.firmwareVersion || '—'}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">Backend</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerVersionQuery.data.backendVersion || '—'}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">API</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerVersionQuery.data.apiVersion || '—'}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-pf-text-secondary">Supported</dt>
              <dd className="font-medium text-pf-text-primary">
                {printerVersionQuery.data.supported ? 'Yes' : 'No'}
              </dd>
            </div>
            {printerVersionQuery.data.message ? (
              <div className="col-span-2">
                <dt className="text-xs text-pf-text-secondary">Message</dt>
                <dd className="text-pf-text-primary wrap-break-word">
                  {printerVersionQuery.data.message}
                </dd>
              </div>
            ) : null}
          </dl>
        ) : (
          <div className="text-sm text-pf-text-secondary">Version unavailable.</div>
        )}
      </CollapsibleSection>

      {support.supportsObjectExclusion && (
        <CollapsibleSection
          title="Objects"
          expanded={true}
          headerActions={
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => void printJobObjectsQuery.refetch()}
              disabled={!isPrinting || printJobObjectsQuery.isFetching}
              className="p-1! h-auto!"
              title="Refresh print objects"
              aria-label="Refresh print objects"
              iconCenter={<RefreshIcon className="h-4 w-4" />}
            />
          }
        >
          {printJobObjectsQuery.isLoading ? (
            <div className="text-sm text-pf-text-secondary">Loading print objects…</div>
          ) : !isPrinting && !isPaused ? (
            <div className="text-sm text-pf-text-secondary">
              Object skipping is available during an active print.
            </div>
          ) : printJobObjects.length === 0 ? (
            <div className="text-sm text-pf-text-secondary">
              No object metadata is available for this job.
            </div>
          ) : (
            <ul className="space-y-2" aria-label="Current print objects">
              {printJobObjects.map((object) => (
                <li
                  key={object.name}
                  className="flex items-center justify-between gap-3 rounded-lg border border-white/10 bg-black/15 px-3 py-2"
                >
                  <div className="min-w-0">
                    <div className="truncate text-sm font-medium text-pf-text-primary">
                      {object.name}
                    </div>
                    <div className="mt-1 flex flex-wrap gap-1 text-[10px] uppercase tracking-wide">
                      {object.isCurrent && (
                        <span className="rounded-xs border border-pf-accent/50 bg-pf-accent-bg px-2 py-0.5 text-pf-accent">
                          Printing
                        </span>
                      )}
                      {object.isExcluded && (
                        <span className="rounded-xs border border-pf-border bg-pf-bg-2 px-2 py-0.5 text-pf-text-secondary">
                          Skipped
                        </span>
                      )}
                    </div>
                  </div>
                  <Button
                    type="button"
                    variant="danger"
                    size="sm"
                    disabled={
                      !canExcludeObjectNow ||
                      object.isExcluded ||
                      excludeObjectMutation.isPending
                    }
                    onClick={() => setObjectToSkip(object)}
                    aria-label={`Skip object ${object.name}`}
                  >
                    Skip
                  </Button>
                </li>
              ))}
            </ul>
          )}
          {objectToSkip ? (
            <div
              role="dialog"
              aria-label={`Confirm skip object ${objectToSkip.name}`}
              className="mt-3 rounded-lg border border-pf-border bg-black/20 p-3 text-sm"
            >
              <div className="mb-2 text-pf-text-primary">
                Skip <span className="font-medium">{objectToSkip.name}</span> for the rest of this
                print? This cannot be undone.
              </div>
              <div className="flex justify-end gap-2">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  onClick={() => setObjectToSkip(null)}
                  disabled={excludeObjectMutation.isPending}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  variant="danger"
                  size="sm"
                  disabled={excludeObjectMutation.isPending}
                  onClick={() => excludeObjectMutation.mutate(objectToSkip.name)}
                >
                  Skip object
                </Button>
              </div>
            </div>
          ) : null}
        </CollapsibleSection>
      )}
    </section>
  );
}
