import React, { useMemo, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { SearchIcon } from '@/common/components/icons/MdiIcons';
import {
  buildMachineProfileLabels,
  mentionsHighFlow,
} from '@/features/slicer/utils/machineProfileLabels';

/**
 * A machine profile as presented to the picker.
 *
 * `name` is the canonical profile name and doubles as the selection value —
 * it is what the slice API matches on, so it must never be a display label.
 */
export interface MachineProfileChoice {
  /** Canonical profile name; also the selection value. */
  name: string;
  /** Nozzle diameter in mm, when it could be resolved. */
  nozzleDiameter?: number;
  /** False for user-created / imported profiles. */
  isSystem: boolean;
}

export interface MachineProfileSelectorModalProps {
  isOpen: boolean;
  profiles: MachineProfileChoice[];
  /** Currently selected profile name, or '' when nothing is chosen. */
  selectedProfileName: string;
  onSelect: (profileName: string) => void;
  onClose: () => void;
  /** Shown above the list, e.g. the printer's model name. */
  printerLabel?: string;
}

/** Groups with a resolvable nozzle sort ascending; unknown-nozzle profiles sort last. */
const UNKNOWN_NOZZLE_KEY = Number.POSITIVE_INFINITY;

function formatNozzle(diameter: number): string {
  return Number(diameter.toFixed(2)).toString();
}

/**
 * Picker for a printer's machine profiles.
 *
 * Replaces the paired machine-profile and nozzle dropdowns. Nozzle appears here
 * as a filter rather than a sibling control, because in OrcaSlicer a machine
 * profile IS (printer model x nozzle) — and for Prusa CORE One two profiles can
 * share one nozzle diameter (standard vs HF), so nozzle alone can never identify
 * a profile. Resolving both in one commit also removes the old behaviour where
 * changing the nozzle silently cleared the machine profile.
 */
export function MachineProfileSelectorModal({
  isOpen,
  profiles,
  selectedProfileName,
  onSelect,
  onClose,
  printerLabel,
}: MachineProfileSelectorModalProps) {
  const [search, setSearch] = useState('');
  const [nozzleFilter, setNozzleFilter] = useState<number | null>(null);

  const handleClose = () => {
    setSearch('');
    setNozzleFilter(null);
    onClose();
  };

  /** Distinct nozzle diameters offered by this printer's profiles. */
  const nozzles = useMemo(() => {
    const set = new Set<number>();
    profiles.forEach((p) => {
      if (typeof p.nozzleDiameter === 'number' && p.nozzleDiameter > 0) {
        set.add(p.nozzleDiameter);
      }
    });
    return [...set].sort((a, b) => a - b);
  }, [profiles]);

  // Labels are computed across the whole set so the uniqueness guard sees every
  // profile, not just the ones surviving the current filter.
  const labels = useMemo(
    () => buildMachineProfileLabels(profiles.map((p) => p.name)),
    [profiles],
  );

  const visible = useMemo(() => {
    const query = search.trim().toLowerCase();
    return profiles.filter((p) => {
      if (nozzleFilter !== null && p.nozzleDiameter !== nozzleFilter) return false;
      if (!query) return true;
      return p.name.toLowerCase().includes(query);
    });
  }, [profiles, search, nozzleFilter]);

  const customProfiles = useMemo(() => visible.filter((p) => !p.isSystem), [visible]);

  /** System profiles bucketed by nozzle so same-nozzle variants sit together. */
  const systemGroups = useMemo(() => {
    const byNozzle = new Map<number, MachineProfileChoice[]>();
    visible
      .filter((p) => p.isSystem)
      .forEach((p) => {
        const key = typeof p.nozzleDiameter === 'number' && p.nozzleDiameter > 0
          ? p.nozzleDiameter
          : UNKNOWN_NOZZLE_KEY;
        const bucket = byNozzle.get(key);
        if (bucket) bucket.push(p);
        else byNozzle.set(key, [p]);
      });
    return [...byNozzle.entries()].sort(([a], [b]) => a - b);
  }, [visible]);

  const handlePick = (name: string) => {
    onSelect(name);
    handleClose();
  };

  const renderRow = (profile: MachineProfileChoice) => {
    const isSelected = profile.name === selectedProfileName;
    const highFlow = mentionsHighFlow(profile.name);
    const label = labels.get(profile.name) ?? profile.name;

    return (
      <Button
        key={profile.name}
        type="button"
        variant="unstyled"
        role="radio"
        aria-checked={isSelected}
        onClick={() => handlePick(profile.name)}
        className={`w-full flex items-center gap-2 text-left rounded-lg border px-3 py-2 mb-1.5 transition-colors ${
          isSelected
            ? 'border-pf-accent/60 bg-pf-accent/10'
            : 'border-pf-border bg-pf-bg-1 hover:border-pf-border-strong'
        }`}
      >
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm text-pf-text-primary">
            {profile.isSystem ? '' : '★ '}
            {label}
          </span>
          {highFlow && (
            <span className="block text-xs text-pf-text-muted">
              High-flow hotend — higher volumetric limit
            </span>
          )}
        </span>
        {highFlow && (
          <span
            data-pf-radius="full"
            className="shrink-0 rounded-full bg-pf-info/15 px-2 py-0.5 text-[11px] font-semibold text-pf-info"
          >
            HF
          </span>
        )}
        {typeof profile.nozzleDiameter === 'number' && profile.nozzleDiameter > 0 && (
          <span className="shrink-0 font-mono text-xs text-pf-text-muted tabular-nums">
            {formatNozzle(profile.nozzleDiameter)}mm
          </span>
        )}
      </Button>
    );
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Select machine profile"
      width="max-w-xl"
    >
      <div className="px-6 pt-4 pb-3 space-y-3">
        {printerLabel && (
          <p className="text-xs text-pf-text-muted">Profiles for {printerLabel}</p>
        )}

        <div className="relative">
          <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-secondary" />
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search profiles..."
            aria-label="Search machine profiles"
            className="w-full rounded-lg border border-pf-border bg-pf-bg-1 py-2 pl-9 pr-3 text-sm text-pf-text-primary placeholder-pf-text-secondary focus:outline-hidden focus:ring-2 focus:ring-pf-accent"
          />
        </div>

        {nozzles.length > 1 && (
          <div role="radiogroup" aria-label="Filter by nozzle diameter" className="flex flex-wrap gap-1.5">
            <Button
              type="button"
              variant="unstyled"
              role="radio"
              aria-checked={nozzleFilter === null}
              onClick={() => setNozzleFilter(null)}
              className={`rounded-md border px-2.5 py-1 text-xs font-medium transition-colors ${
                nozzleFilter === null
                  ? 'border-pf-accent/60 bg-pf-accent/12 text-pf-accent'
                  : 'border-pf-border bg-pf-bg-1 text-pf-text-tertiary hover:text-pf-text-primary'
              }`}
            >
              All
            </Button>
            {nozzles.map((n) => (
              <Button
                key={n}
                type="button"
                variant="unstyled"
                role="radio"
                aria-checked={nozzleFilter === n}
                onClick={() => setNozzleFilter(n)}
                className={`rounded-md border px-2.5 py-1 text-xs font-medium tabular-nums transition-colors ${
                  nozzleFilter === n
                    ? 'border-pf-accent/60 bg-pf-accent/12 text-pf-accent'
                    : 'border-pf-border bg-pf-bg-1 text-pf-text-tertiary hover:text-pf-text-primary'
                }`}
              >
                {formatNozzle(n)} mm
              </Button>
            ))}
          </div>
        )}
      </div>

      <div className="max-h-[52vh] overflow-y-auto px-6 pb-6">
        {visible.length === 0 ? (
          <p className="py-6 text-center text-sm text-pf-text-muted">
            No machine profiles match.
          </p>
        ) : (
          <>
            {customProfiles.length > 0 && (
              <section className="mb-3">
                <h3 className="px-0.5 pb-1.5 font-mono text-[10px] uppercase tracking-[0.16em] text-pf-text-muted">
                  ★ My Profiles
                </h3>
                <div role="radiogroup" aria-label="My machine profiles">
                  {customProfiles.map(renderRow)}
                </div>
              </section>
            )}

            {systemGroups.map(([nozzle, group]) => (
              <section key={nozzle} className="mb-3">
                <h3 className="px-0.5 pb-1.5 font-mono text-[10px] uppercase tracking-[0.16em] text-pf-text-muted">
                  {nozzle === UNKNOWN_NOZZLE_KEY ? 'Other' : `${formatNozzle(nozzle)} mm`}
                  {group.length > 1 && ` · ${group.length} profiles`}
                </h3>
                <div
                  role="radiogroup"
                  aria-label={
                    nozzle === UNKNOWN_NOZZLE_KEY
                      ? 'Machine profiles with unknown nozzle'
                      : `${formatNozzle(nozzle)} mm machine profiles`
                  }
                >
                  {group.map(renderRow)}
                </div>
              </section>
            ))}
          </>
        )}
      </div>
    </Modal>
  );
}

export default MachineProfileSelectorModal;
