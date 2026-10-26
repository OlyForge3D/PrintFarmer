import { useId } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Button, Spinner, Toggle } from '@/common/components/ui';
import { RefreshIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { FileDate } from '@/features/files/components/FileDate';
import { describeReasonCode } from '@/features/files/utils/fileDisplay';
import { apiClient } from '@/services/api';
import type { PrinterFileScanOutcome } from '@/types/api';

interface PrinterFileDiscoverySectionProps {
  printerId: string;
}

type DiscoveryChange = { kind: 'refresh' } | { kind: 'enabled'; enabled: boolean };

const OUTCOME_LABEL: Record<PrinterFileScanOutcome, string> = {
  NeverScanned: 'Never scanned',
  Complete: 'Complete',
  Partial: 'Partial',
  Unavailable: 'Unavailable',
  Unsupported: 'Unsupported',
  Cancelled: 'Cancelled',
  Deferred: 'Deferred',
  AuthenticationRequired: 'Authentication required',
};

/**
 * Per-printer "files on printer" discovery controls for the Edit Printer modal. Changes are
 * applied immediately through their own endpoints; nothing here is part of the printer form
 * (buttons are type="button" so they never submit it).
 */
export function PrinterFileDiscoverySection({ printerId }: PrinterFileDiscoverySectionProps) {
  const headingId = useId();
  const { hasPermission } = useAuth();
  const canRead = hasPermission('printer_files', 'read');
  const queryClient = useQueryClient();
  // A one-item catalog page carries this printer's scan status and the caller's authority.
  const catalog = useQuery({
    queryKey: ['printer-file-catalog', 'discovery', printerId],
    queryFn: ({ signal }) => apiClient.getPrinterFileCatalog({ printerId, pageSize: 1 }, signal),
    enabled: canRead,
  });
  const change = useMutation({
    mutationFn: (request: DiscoveryChange) => request.kind === 'refresh'
      ? apiClient.refreshPrinterFileIndex(printerId)
      : apiClient.setPrinterFileIndexEnabled(printerId, request.enabled),
    onSuccess: async (_result, request) => {
      toast.success(request.kind === 'refresh'
        ? 'Refresh requested. It runs when the printer is idle.'
        : `Automatic file discovery ${request.enabled ? 'enabled' : 'disabled'}.`);
      await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] });
    },
    onError: (error: Error) => toast.error(`Failed to update file discovery: ${error.message}`),
  });

  if (!canRead) return null;

  const source = catalog.data?.printers.find(printer => printer.id === printerId);
  const status = catalog.data?.scans.find(scan => scan.printerId === printerId);
  // Show the requested state while the change is in flight, then the server's answer.
  const pendingEnabled = change.isPending && change.variables?.kind === 'enabled' ? change.variables.enabled : undefined;
  const enabled = pendingEnabled ?? status?.enabled ?? true;
  const reason = describeReasonCode(status?.reasonCode);
  const refreshing = change.isPending && change.variables?.kind === 'refresh';

  return (
    <section aria-labelledby={headingId} className="border-t pt-5 mt-5">
      <div className="mb-4">
        <h4 id={headingId} className="text-lg font-medium text-pf-text-primary">Files</h4>
        <p className="text-sm text-pf-text-secondary">Discovery lists the files stored on this printer under Files &gt; On printers.</p>
      </div>
      {catalog.isLoading && (
        <div role="status" className="flex items-center gap-2 text-sm text-pf-text-secondary">
          <Spinner size="sm" /> Loading file discovery status…
        </div>
      )}
      {catalog.error && <Alert variant="error">Failed to load file discovery status: {catalog.error.message}</Alert>}
      {catalog.data && !source && (
        <p className="text-sm text-pf-text-secondary">File discovery is not available for this printer.</p>
      )}
      {catalog.data && source && (
        <div className="space-y-3">
          {catalog.data.automaticDiscoveryEnabled === false && (
            <Alert variant="info">Automatic discovery is globally disabled in Admin Settings &gt; Files.</Alert>
          )}
          <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm" aria-live="polite">
            <dt className="text-pf-text-secondary">Status</dt>
            <dd>
              {status?.isScanning ? 'Scanning (file I/O active)' : OUTCOME_LABEL[status?.outcome ?? 'NeverScanned']}
              {status?.isStale ? ' · Stale' : ''}
            </dd>
            <dt className="text-pf-text-secondary">Last complete scan</dt>
            <dd><FileDate value={status?.lastCompleteScanAtUtc} fallback="Never" /></dd>
            {reason && (
              <>
                <dt className="text-pf-text-secondary">Reason</dt>
                <dd className="break-words">{reason}</dd>
              </>
            )}
            {status?.nextScanAtUtc && (
              <>
                <dt className="text-pf-text-secondary">Next eligible scan</dt>
                <dd><FileDate value={status.nextScanAtUtc} /></dd>
              </>
            )}
          </dl>
          {source.canManage ? (
            <>
              <div className="flex flex-wrap items-center gap-4">
                <Toggle
                  label="Automatic file discovery"
                  checked={enabled}
                  disabled={change.isPending}
                  onChange={event => change.mutate({ kind: 'enabled', enabled: event.target.checked })}
                />
                <Button
                  type="button"
                  size="sm"
                  variant="secondary"
                  iconLeft={<RefreshIcon className="h-4 w-4" />}
                  loading={refreshing}
                  disabled={change.isPending}
                  onClick={() => change.mutate({ kind: 'refresh' })}
                >
                  Refresh now
                </Button>
              </div>
              <p className="text-xs text-pf-text-secondary">
                Changes apply immediately and are not saved with this form. Scans run only while the printer is idle.
                Global scan cadence and budgets are in Admin Settings &gt; Files.
              </p>
            </>
          ) : (
            <p className="text-xs text-pf-text-secondary">You do not have permission to change file discovery for this printer.</p>
          )}
        </div>
      )}
    </section>
  );
}