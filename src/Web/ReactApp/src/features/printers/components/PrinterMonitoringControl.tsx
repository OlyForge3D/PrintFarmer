import { Button } from '@/common/components/ui';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinterMonitoring } from '@/features/printers/hooks/usePrinterMonitoring';
import type { Printer } from '@/types/api';

export function PrinterMonitoringControl({ printer }: { printer: Printer }) {
  const { hasPermission } = useAuth();
  const monitoring = usePrinterMonitoring();
  const expectedOffline = !!printer.expectedOfflineSinceUtc;
  const knownOffline = !printer.isOnline && printer.isEnabled !== false
    && (!printer.state || ['offline', 'disconnected', 'unreachable'].includes(printer.state.toLowerCase()));
  if (!expectedOffline && !knownOffline) return null;

  return (
    <div className="flex flex-wrap items-center gap-2 text-xs">
      {expectedOffline && <span className="text-pf-text-secondary">Expected offline</span>}
      {hasPermission('printers', 'admin') && (
        <Button
          variant="subtle"
          size="sm"
          loading={monitoring.isPending}
          disabled={monitoring.isPending || !printer.rowVersion}
          title={expectedOffline
            ? 'Restore connectivity alerts. Monitoring also resumes automatically after a confirmed reconnect.'
            : 'Acknowledge this shutdown until a confirmed reconnect. Does not change power or job dispatch.'}
          onClick={(event) => {
            event.stopPropagation();
            monitoring.mutate({ printers: [printer], expectedOffline: !expectedOffline });
          }}
        >
          {expectedOffline ? 'Expect online' : 'Mark expected offline'}
        </Button>
      )}
    </div>
  );
}
