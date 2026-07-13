import { Badge, Button, EmptyState, Spinner } from '@/common/components/ui';
import {
  AlertIcon,
  CheckCircleIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { useReorderCandidates } from '../hooks/usePartsInventory';

interface ReorderTabProps {
  onOpenSkusTab?: () => void;
}

/**
 * ReorderTab — lists SKUs at or below reorder point.
 *
 * The endpoint returns a sorted list of {@link ReorderCandidateDto},
 * so we simply render the pre-computed deficit. Empty state is
 * treated as good news (all stock levels healthy).
 */
export function ReorderTab({ onOpenSkusTab }: ReorderTabProps) {
  const { data: candidates = [], isLoading, error, refetch, isFetching } = useReorderCandidates();

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
        <Spinner size="md" />
        <span>Loading reorder candidates…</span>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-3 border border-pf-error-border bg-pf-error-bg rounded-sm text-pf-error-text text-sm" role="alert">
        Failed to load reorder candidates.
      </div>
    );
  }

  if (candidates.length === 0) {
    return (
      <EmptyState
        icon={<CheckCircleIcon className="w-8 h-8 text-pf-success" ariaLabel="All good" />}
        title="All SKUs above reorder point"
        description="Stock levels for every active printed-part SKU are healthy."
        action={
          onOpenSkusTab ? (
            <Button variant="secondary" size="sm" onClick={onOpenSkusTab}>
              Review SKUs
            </Button>
          ) : undefined
        }
      />
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-sm font-medium">
            {candidates.length} SKU{candidates.length === 1 ? '' : 's'} need attention
          </h3>
          <p className="text-xs text-pf-text-secondary">
            SKUs at or below their reorder point.
          </p>
        </div>
        <Button
          variant="ghost"
          size="sm"
          iconLeft={<RefreshIcon className="w-4 h-4" ariaLabel="Refresh" />}
          onClick={() => refetch()}
          loading={isFetching}
        >
          Refresh
        </Button>
      </div>

      <div className="overflow-x-auto">
        <table className="min-w-full text-sm">
          <thead className="bg-pf-bg-1 text-pf-text-secondary">
            <tr>
              <th scope="col" className="px-3 py-2 text-left font-medium">SKU</th>
              <th scope="col" className="px-3 py-2 text-left font-medium">Name</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">On-hand</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">Reorder pt</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">Deficit</th>
              <th scope="col" className="px-3 py-2 text-left font-medium">Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-pf-border">
            {candidates.map((candidate) => (
              <tr key={candidate.partInventoryId}>
                <td className="px-3 py-2 font-mono">{candidate.sku}</td>
                <td className="px-3 py-2">{candidate.name}</td>
                <td className="px-3 py-2 text-right font-mono">{candidate.onHand}</td>
                <td className="px-3 py-2 text-right font-mono">{candidate.reorderPoint}</td>
                <td className="px-3 py-2 text-right font-mono font-medium">{candidate.deficit}</td>
                <td className="px-3 py-2">
                  <Badge
                    variant={candidate.onHand <= 0 ? 'error' : 'warning'}
                    size="sm"
                  >
                    <span className="inline-flex items-center gap-1">
                      <AlertIcon className="w-3 h-3" ariaLabel="" />
                      {candidate.onHand <= 0 ? 'Out of stock' : 'Below reorder pt'}
                    </span>
                  </Badge>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
