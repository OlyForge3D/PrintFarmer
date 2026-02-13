import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Badge, Card, Spinner } from '@/common/components/ui';
import { WiFiIcon, NetworkIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import type {
  ConnectionDiagnosticsResponse,
  PrinterConnectionHealthDto,
  PrinterConnectionState,
} from '@/types/api';

const STATE_BADGE_MAP: Record<PrinterConnectionState, { variant: 'success' | 'warning' | 'error' | 'default'; label: string }> = {
  Connected: { variant: 'success', label: 'Connected' },
  Reconnecting: { variant: 'warning', label: 'Reconnecting' },
  Offline: { variant: 'error', label: 'Offline' },
  Degraded: { variant: 'warning', label: 'Degraded' },
};

function formatTimestamp(ts: string | null): string {
  if (!ts) return '—';
  const date = new Date(ts);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);
  if (diffSec < 60) return `${diffSec}s ago`;
  const diffMin = Math.floor(diffSec / 60);
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ago`;
  return date.toLocaleDateString();
}

function SummaryCards({ data }: { data: ConnectionDiagnosticsResponse }) {
  const cards = [
    { label: 'Total Printers', value: data.totalPrinters, color: 'text-pf-text-primary' },
    { label: 'Connected', value: data.connectedCount, color: 'text-green-400' },
    { label: 'Reconnecting', value: data.reconnectingCount, color: 'text-yellow-400' },
    { label: 'Offline', value: data.offlineCount, color: 'text-red-400' },
  ];

  return (
    <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
      {cards.map((card) => (
        <Card key={card.label}>
          <Card.Body className="text-center py-3">
            <div className={`text-2xl font-bold ${card.color}`}>{card.value}</div>
            <div className="text-xs text-pf-text-secondary">{card.label}</div>
          </Card.Body>
        </Card>
      ))}
    </div>
  );
}

function TransitionTimeline({ printer }: { printer: PrinterConnectionHealthDto }) {
  if (printer.recentTransitions.length === 0) {
    return <p className="text-sm text-pf-text-secondary py-2">No recent transitions recorded.</p>;
  }

  return (
    <div className="space-y-1 py-2 max-h-60 overflow-y-auto">
      {[...printer.recentTransitions].reverse().map((t, i) => {
        const badge = STATE_BADGE_MAP[t.toState] ?? STATE_BADGE_MAP.Offline;
        return (
          <div key={i} className="flex items-center gap-2 text-xs text-pf-text-secondary">
            <span className="w-16 shrink-0 text-right font-mono">
              {formatTimestamp(t.timestampUtc)}
            </span>
            <span className="text-pf-text-tertiary">→</span>
            <Badge variant={badge.variant} size="sm">{badge.label}</Badge>
            {t.reason && <span className="truncate">{t.reason}</span>}
          </div>
        );
      })}
    </div>
  );
}

function PrinterRow({ printer }: { printer: PrinterConnectionHealthDto }) {
  const [expanded, setExpanded] = useState(false);
  const badge = STATE_BADGE_MAP[printer.connectionState] ?? STATE_BADGE_MAP.Offline;

  return (
    <>
      <tr
        className="hover:bg-pf-bg-1 cursor-pointer"
        onClick={() => setExpanded(!expanded)}
        role="button"
        aria-expanded={expanded}
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setExpanded(!expanded); }}
      >
        <td className="px-3 py-2 text-sm font-medium text-pf-text-primary">{printer.printerName}</td>
        <td className="px-3 py-2 text-xs text-pf-text-secondary">{printer.backend}</td>
        <td className="px-3 py-2">
          <Badge variant={badge.variant} size="sm">{badge.label}</Badge>
        </td>
        <td className="px-3 py-2 text-sm text-pf-text-secondary">{printer.uptimePercent.toFixed(1)}%</td>
        <td className="px-3 py-2 text-sm text-pf-text-secondary">{printer.totalReconnects}</td>
        <td className="px-3 py-2 text-xs text-pf-text-secondary">{formatTimestamp(printer.lastConnectedUtc)}</td>
        <td className="px-3 py-2 text-xs text-pf-text-secondary">{formatTimestamp(printer.lastDisconnectedUtc)}</td>
        <td className="px-3 py-2 text-xs text-pf-text-secondary">{printer.connectionMode ?? '—'}</td>
      </tr>
      {expanded && (
        <tr>
          <td colSpan={8} className="px-3 py-1 bg-pf-bg-1 border-t border-pf-border">
            <div className="text-xs font-medium text-pf-text-secondary mb-1">
              Recent Transitions ({printer.recentTransitions.length})
            </div>
            <TransitionTimeline printer={printer} />
          </td>
        </tr>
      )}
    </>
  );
}

export function ConnectionHealthContent() {
  const { data, isLoading, error } = useQuery({
    queryKey: ['diagnostics', 'connections'],
    queryFn: () => apiClient.getConnectionDiagnostics(),
    refetchInterval: 10_000,
    staleTime: 5_000,
  });

  if (isLoading) {
    return (
      <div className="flex justify-center py-8">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4 text-pf-error">
        Failed to load connection diagnostics: {String(error)}
      </div>
    );
  }

  if (!data || data.totalPrinters === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-12 text-pf-text-secondary">
        <WiFiIcon className="w-12 h-12 mb-3 opacity-50" />
        <p className="text-sm">No printer connections to monitor.</p>
        <p className="text-xs mt-1">Connection health data appears once printers begin communicating with the backend.</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <SummaryCards data={data} />

      <Card>
        <Card.Header>
          <div className="flex items-center gap-2">
            <NetworkIcon className="w-4 h-4 text-pf-text-secondary" />
            <span className="text-sm font-medium text-pf-text-primary">Per-Printer Connection Health</span>
          </div>
        </Card.Header>
        <Card.Body className="p-0">
          <div className="overflow-x-auto">
            <table className="w-full text-left" role="grid" aria-label="Printer connection health">
              <thead>
                <tr className="border-b border-pf-border">
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Printer</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Backend</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">State</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Uptime (1h)</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Reconnects</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Last Connected</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Last Disconnected</th>
                  <th className="px-3 py-2 text-xs font-medium text-pf-text-secondary">Mode</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-pf-border">
                {data.printers.map((printer) => (
                  <PrinterRow key={printer.printerId} printer={printer} />
                ))}
              </tbody>
            </table>
          </div>
        </Card.Body>
      </Card>
    </div>
  );
}
