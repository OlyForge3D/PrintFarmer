/**
 * HarvestJobAction — button + dialog pair (#722).
 *
 * Drop-in action for the History surface. Renders either a small "Harvest"
 * button or a "Harvested" badge depending on `harvestedAt`. Manages its own
 * dialog state so consumers (`HistoryJobTable`, `HistoryJobCard`) do not
 * need to plumb callbacks through the tree.
 */

import { useState } from 'react';
import { Button } from '@/common/components/ui/Button';
import { HarvestJobDialog } from './HarvestJobDialog';
import type { HarvestJobResponse } from '@/types/parts-inventory';

export interface HarvestJobActionProps {
  job: {
    id: string;
    name: string;
    /** Set when the job has already been harvested. Null/undefined otherwise. */
    harvestedAt?: string | null;
  };
  /** Rendering style. `table` is compact for action columns; `card` is full-width. */
  variant?: 'table' | 'card';
  /** Fired after a successful (or replayed) harvest, so parents can refresh. */
  onHarvested?: (response: HarvestJobResponse) => void;
  /** Optional wrapper className. */
  className?: string;
}

export function HarvestJobAction({
  job,
  variant = 'table',
  onHarvested,
  className,
}: HarvestJobActionProps) {
  const [isOpen, setIsOpen] = useState(false);
  const alreadyHarvested = Boolean(job.harvestedAt);

  if (alreadyHarvested) {
    return (
      <>
        <Button
          variant="ghost"
          size="sm"
          onClick={() => setIsOpen(true)}
          className={
            className ??
            'inline-flex items-center gap-1 rounded-sm border border-pf-success/30 bg-pf-success-bg px-1.5 py-0.5 text-xs font-medium text-pf-success-text hover:bg-pf-success-bg/80 focus:outline-none focus:ring-2 focus:ring-pf-success/60'
          }
          title="View harvest details"
          data-testid="harvest-badge"
          aria-label={`Harvested — view details for ${job.name}`}
        >
          <span aria-hidden>✔</span>
          <span>Harvested</span>
        </Button>
        <HarvestJobDialog
          isOpen={isOpen}
          onClose={() => setIsOpen(false)}
          job={job}
          onHarvested={onHarvested}
        />
      </>
    );
  }

  const button =
    variant === 'card' ? (
      <Button
        variant="secondary"
        className={className ?? 'flex-1 px-3 py-2 rounded-sm text-sm font-medium'}
        onClick={() => setIsOpen(true)}
        data-testid="harvest-button"
        aria-label={`Harvest ${job.name} into printed-part stock`}
      >
        📦 Harvest
      </Button>
    ) : (
      <Button
        variant="ghost"
        size="sm"
        className={className ?? 'px-2 py-1 text-xs'}
        onClick={() => setIsOpen(true)}
        title="Harvest into printed-part stock"
        data-testid="harvest-button"
        aria-label={`Harvest ${job.name} into printed-part stock`}
      >
        📦
      </Button>
    );

  return (
    <>
      {button}
      <HarvestJobDialog
        isOpen={isOpen}
        onClose={() => setIsOpen(false)}
        job={job}
        onHarvested={onHarvested}
      />
    </>
  );
}
