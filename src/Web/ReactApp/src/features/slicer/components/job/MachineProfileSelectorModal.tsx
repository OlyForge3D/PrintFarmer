import React, { useMemo, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import { SearchIcon, CheckIcon } from '@/common/components/icons/MdiIcons';
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

interface ProfileGroup {
  key: number;
  heading: string;
  profiles: MachineProfileChoice[];
  /** Labels scoped to this group so nozzle trimming stays unique and effective. */
  labels: Map<string, string>;
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
 *
 * Rows are plain buttons with `aria-pressed`, matching PrinterSelectorModal,
 * rather than a `radio` composite: a radiogroup would owe callers roving
 * tabindex and arrow-key navigation, and the selection is split across several
 * visual groups which would misreport set size to a screen reader.
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

  const clearFilters = () => {
    setSearch('');
    setNozzleFilter(null);
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

  const visible = useMemo(() => {
    const query = search.trim().toLowerCase();
    return profiles.filter((p) => {
      if (nozzleFilter !== null && p.nozzleDiameter !== nozzleFilter) return false;
      if (!query) return true;
      return p.name.toLowerCase().includes(query);
    });
  }, [profiles, search, nozzleFilter]);

  const customProfiles = useMemo(() => visible.filter((p) => !p.isSystem), [visible]);
  const customLabels = useMemo(
    () => buildMachineProfileLabels(customProfiles.map((p) => p.name)),
    [customProfiles],
  );

  /**
   * System profiles bucketed by nozzle so same-nozzle variants sit together.
   *
   * Labels are built PER GROUP. Building them across a printer's whole profile
   * set collides for any multi-nozzle printer ("… 0.4 nozzle" and
   * "… 0.6 nozzle" both trim to the same label), which would trip the
   * uniqueness fallback and silently disable trimming everywhere.
   */
  const systemGroups = useMemo<ProfileGroup[]>(() => {
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

    return [...byNozzle.entries()]
      .sort(([a], [b]) => a - b)
      .map(([key, group]) => ({
        key,
        heading: key === UNKNOWN_NOZZLE_KEY ? 'Other' : `${formatNozzle(key)} mm`,
        profiles: group,
        labels: buildMachineProfileLabels(group.map((p) => p.name)),
      }));
  }, [visible]);

  const handlePick = (name: string) => {
    onSelect(name);
    handleClose();
  };

  const renderRow = (profile: MachineProfileChoice, labels: Map<string, string>) => {
    const isSelected = profile.name === selectedProfileName;
    const highFlow = mentionsHighFlow(profile.name);
    const label = labels.get(profile.name) ?? profile.name;

    return (
      <Button
        key={profile.name}
        type="button"
        variant="unstyled"
        aria-pressed={isSelected}
        onClick={() => handlePick(profile.name)}
        className={`w-full flex items-center gap-2 text-left rounded-lg border px-3 py-2 mb-1.5 transition-colors ${
          isSelected
            ? 'border-pf-accent bg-pf-accent/10'
            : 'border-pf-border bg-pf-bg-1 hover:border-pf-border-strong'
        }`}
      >
        {/* Selection is never conveyed by colour alone. */}
        <span className="shrink-0 w-4" aria-hidden="true">
          {isSelected && <CheckIcon className="w-4 h-4 text-pf-accent" />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm text-pf-text-primary">
            {!profile.isSystem && <span aria-hidden="true">★ </span>}
            {label}
          </span>
          {highFlow && (
            // Phrased as an observation about the NAME, not a verified hardware
            // claim: nothing structural distinguishes HF from standard profiles.
            <span className="block text-xs text-pf-text-muted">
              Name indicates a high-flow variant
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

  const hasActiveFilters = search.trim().length > 0 || nozzleFilter !== null;

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Select machine profile"
      width="max-w-xl"
    >
      <div className="space-y-3 pb-3">
        {printerLabel && (
          <p className="text-xs text-pf-text-muted">Profiles for {printerLabel}</p>
        )}

        <div className="relative">
          <span aria-hidden="true">
            <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-pf-text-secondary" />
          </span>
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search machine profiles..."
            aria-label="Search machine profiles"
            className="w-full rounded-lg border border-pf-border bg-pf-bg-1 py-2 pl-9 pr-3 text-sm text-pf-text-primary placeholder-pf-text-secondary focus:outline-hidden focus:ring-2 focus:ring-pf-accent"
          />
        </div>

        {nozzles.length > 1 && (
          <div role="group" aria-label="Filter by nozzle diameter" className="flex flex-wrap gap-1.5">
            <Button
              type="button"
              variant="unstyled"
              aria-pressed={nozzleFilter === null}
              onClick={() => setNozzleFilter(null)}
              className={`rounded-md border px-2.5 py-1 text-xs font-medium transition-colors ${
                nozzleFilter === null
                  ? 'border-pf-accent bg-pf-accent/12 text-pf-accent'
                  : 'border-pf-border bg-pf-bg-1 text-pf-text-tertiary hover:text-pf-text-primary'
              }`}
            >
              {nozzleFilter === null && <span aria-hidden="true">✓ </span>}All
            </Button>
            {nozzles.map((n) => (
              <Button
                key={n}
                type="button"
                variant="unstyled"
                aria-pressed={nozzleFilter === n}
                onClick={() => setNozzleFilter(n)}
                className={`rounded-md border px-2.5 py-1 text-xs font-medium tabular-nums transition-colors ${
                  nozzleFilter === n
                    ? 'border-pf-accent bg-pf-accent/12 text-pf-accent'
                    : 'border-pf-border bg-pf-bg-1 text-pf-text-tertiary hover:text-pf-text-primary'
                }`}
              >
                {nozzleFilter === n && <span aria-hidden="true">✓ </span>}
                {formatNozzle(n)} mm
              </Button>
            ))}
          </div>
        )}
      </div>

      <div>
        {visible.length === 0 ? (
          <div className="py-6 text-center">
            <p className="text-sm text-pf-text-muted">
              {hasActiveFilters
                ? 'No machine profiles match the current search or nozzle filter.'
                : 'No machine profiles available for this printer.'}
            </p>
            {hasActiveFilters && (
              <Button type="button" variant="subtle" size="sm" className="mt-3" onClick={clearFilters}>
                Clear filters
              </Button>
            )}
          </div>
        ) : (
          <>
            {customProfiles.length > 0 && (
              <section className="mb-3" aria-label="My machine profiles">
                <h3 className="px-0.5 pb-1.5 font-mono text-[10px] uppercase tracking-[0.16em] text-pf-text-muted">
                  <span aria-hidden="true">★ </span>My Profiles
                </h3>
                {customProfiles.map((p) => renderRow(p, customLabels))}
              </section>
            )}

            {systemGroups.map((group) => (
              <section
                key={group.key}
                className="mb-3"
                aria-label={
                  group.key === UNKNOWN_NOZZLE_KEY
                    ? 'Machine profiles with unknown nozzle'
                    : `${formatNozzle(group.key)} mm machine profiles`
                }
              >
                <h3 className="px-0.5 pb-1.5 font-mono text-[10px] uppercase tracking-[0.16em] text-pf-text-muted">
                  {group.heading}
                  {group.profiles.length > 1 && ` · ${group.profiles.length} profiles`}
                </h3>
                {group.profiles.map((p) => renderRow(p, group.labels))}
              </section>
            ))}
          </>
        )}
      </div>
    </Modal>
  );
}

export default MachineProfileSelectorModal;
