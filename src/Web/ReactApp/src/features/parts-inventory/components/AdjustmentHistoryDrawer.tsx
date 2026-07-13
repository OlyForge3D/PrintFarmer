import { Badge, Spinner, EmptyState } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { AlertIcon } from '@/common/components/icons/MdiIcons';
import { usePartAdjustments } from '../hooks/usePartsInventory';
import { PART_ADJUSTMENT_REASON_LABELS } from '@/types/partsInventory';
import type { PartAdjustmentReason, PartInventoryDto } from '@/types/partsInventory';

const REASON_VARIANT: Record<PartAdjustmentReason, 'success' | 'warning' | 'default'> = {
  harvest: 'success',
  'qc-reject': 'warning',
  manual: 'default',
};

export interface AdjustmentHistoryDrawerProps {
  isOpen: boolean;
  onClose: () => void;
  part: PartInventoryDto | null;
}

function formatTimestamp(value: string): string {
  try {
    return new Date(value).toLocaleString();
  } catch {
    return value;
  }
}

/**
 * Non-editable ledger view for a single SKU. Rendered inside the shared
 * Modal so keyboard-trap and Escape behavior stay consistent with the
 * rest of the feature. Kept as a separate component so operators can
 * audit history without leaving the SKU list.
 */
export function AdjustmentHistoryDrawer({ isOpen, onClose, part }: AdjustmentHistoryDrawerProps) {
  const { data: adjustments = [], isLoading, error } = usePartAdjustments(part?.sku, 100);

  if (!part) return null;

  const title = `Adjustments — ${part.sku}`;

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={title} size="xl">
      <div className="space-y-3">
        {isLoading && (
          <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
            <Spinner size="md" />
            <span>Loading ledger…</span>
          </div>
        )}

        {error && (
          <div className="p-3 border border-pf-error-border bg-pf-error-bg rounded-sm text-pf-error-text text-sm" role="alert">
            Failed to load ledger.
          </div>
        )}

        {!isLoading && !error && adjustments.length === 0 && (
          <EmptyState
            icon={<AlertIcon className="w-8 h-8 text-pf-text-secondary" ariaLabel="No adjustments" />}
            title="No adjustments yet"
            description="Stock changes will appear here once recorded."
          />
        )}

        {adjustments.length > 0 && (
          <div className="overflow-x-auto -mx-4">
            <table className="min-w-full text-sm">
              <thead className="bg-pf-bg-1 text-pf-text-secondary">
                <tr>
                  <th scope="col" className="px-3 py-2 text-left font-medium">When</th>
                  <th scope="col" className="px-3 py-2 text-left font-medium">Reason</th>
                  <th scope="col" className="px-3 py-2 text-right font-medium">Delta</th>
                  <th scope="col" className="px-3 py-2 text-right font-medium">On-hand after</th>
                  <th scope="col" className="px-3 py-2 text-left font-medium">Bin</th>
                  <th scope="col" className="px-3 py-2 text-left font-medium">Notes</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-pf-border">
                {adjustments.map((row) => (
                  <tr key={row.id}>
                    <td className="px-3 py-2 whitespace-nowrap text-pf-text-secondary">
                      {formatTimestamp(row.createdAt)}
                    </td>
                    <td className="px-3 py-2">
                      <Badge variant={REASON_VARIANT[row.reason]} size="sm">
                        {PART_ADJUSTMENT_REASON_LABELS[row.reason]}
                      </Badge>
                    </td>
                    <td className={`px-3 py-2 text-right font-mono ${row.delta > 0 ? 'text-pf-success' : row.delta < 0 ? 'text-pf-error' : ''}`}>
                      {row.delta > 0 ? '+' : ''}
                      {row.delta}
                    </td>
                    <td className="px-3 py-2 text-right font-mono">{row.resultingBalance}</td>
                    <td className="px-3 py-2">{row.binCode ?? '—'}</td>
                    <td className="px-3 py-2 text-pf-text-secondary">{row.notes ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Modal>
  );
}
