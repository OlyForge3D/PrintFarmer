import React, { useId, useMemo } from 'react';
import clsx from 'clsx';
import { Button, InfoTooltip, Select } from '@/common/components/ui';
import { GearIcon } from '@/common/components/icons/MdiIcons';
import { getSlicerIconSrc } from '@/common/utils/slicerEngineIcon';
import type { SlicerEngineOption } from './types';

/**
 * Per-version availability for the selected engine, mirroring
 * `SlicerEngineInfo.versionEntries` from GET /api/slicers/engines.
 */
export interface SlicerVersionChoice {
  version: string;
  available: boolean;
}

interface SlicerSelectorProps {
  /** Currently selected slicer ID (1=OrcaSlicer, 2=PrusaSlicer) */
  selectedSlicerId: number;
  /** Callback when slicer selection changes */
  onSlicerChange: (slicerId: number) => void;
  /** Available slicer engine options */
  engineOptions: SlicerEngineOption[];
  /** Per-version availability for the SELECTED engine (raw, unfiltered). */
  versionEntries?: SlicerVersionChoice[];
  /** Backend-resolved newest available version; undefined when unpinnable. */
  latestVersion?: string;
  /** Pinned version, or undefined for "Latest". */
  selectedVersion?: string;
  /** Callback when the version pin changes. undefined = back to Latest. */
  onVersionChange?: (version: string | undefined) => void;
  /** Display name of the selected engine, used in copy (e.g. "OrcaSlicer"). */
  engineName?: string;
  /** Optional CSS class name */
  className?: string;
}

/**
 * Above this many selectable versions the pill row is swapped for a dropdown —
 * the sidebar is only 24rem wide and a long wrapped pill grid reads as noise.
 */
const MAX_PILL_VERSIONS = 3;

/**
 * Parse engine label into name + version.
 * e.g. "OrcaSlicer v2.3.1" → { name: "OrcaSlicer", version: "v2.3.1" }
 * The `v` is optional because the registry emits bare versions ("OrcaSlicer 2.4.2").
 */
function parseLabel(label: string): { name: string; version?: string } {
  const match = label.match(/^(.+?)\s+(v?\d[\d.]*)$/i);
  if (match) return { name: match[1].trim(), version: match[2].trim() };
  return { name: label };
}

/**
 * Slicer engine + engine version selector.
 *
 * Engine and version are one decision, not two: a version only means anything
 * relative to its engine, so both live in a single panel (mirroring the
 * printer + machine-profile grouping) instead of sibling group boxes.
 *
 * Only versions that can actually claim a job are offered. A version with no
 * online worker — whether it was never configured, or its worker container is
 * gone and only a stale registration row remains — can never be chosen, so
 * rendering it as a disabled "(offline)" option is pure noise. The single
 * exception is a version the user has already pinned: that stays visible and
 * flagged, so a pin that went stale is diagnosable rather than silently
 * reverting to Latest.
 *
 * NOTE: filtering happens at RENDER time only. Callers must keep passing the
 * raw, unfiltered `versionEntries` so the submit guard's "engine known but has
 * zero available workers" check still fires — an emptied list reads as
 * "nothing to check" (issue #1772 regression found in review).
 */
export const SlicerSelector: React.FC<SlicerSelectorProps> = ({
  selectedSlicerId,
  onSlicerChange,
  engineOptions,
  versionEntries,
  latestVersion,
  selectedVersion,
  onVersionChange,
  engineName,
  className,
}) => {
  const labelId = useId();
  const entries = useMemo(() => versionEntries ?? [], [versionEntries]);

  // Versions a job could actually be dispatched to right now, plus the user's
  // current pin (even if it just went offline) so it never vanishes silently.
  const offeredVersions = useMemo(() => {
    const offered = entries.filter(e => e.available);
    if (selectedVersion && !offered.some(e => e.version === selectedVersion)) {
      const pinned = entries.find(e => e.version === selectedVersion);
      return [...offered, pinned ?? { version: selectedVersion, available: false }];
    }
    return offered;
  }, [entries, selectedVersion]);

  // A single choice is not a choice — the engine card already shows it.
  const showVersionPicker = offeredVersions.length > 1;
  const noVersionsAvailable = entries.length > 0 && !entries.some(e => e.available);
  const useDropdown = offeredVersions.length > MAX_PILL_VERSIONS;
  const pinnedVersionIsStale = selectedVersion !== undefined
    && !entries.some(e => e.version === selectedVersion && e.available);

  // The selected engine card shows the version the job will actually use, so
  // card and picker can never disagree.
  const effectiveVersion = selectedVersion ?? latestVersion;

  return (
    <div className={clsx('bg-pf-panel border border-pf-border rounded-lg p-2.5', className)}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-1.5">Slicer Engine</label>
      <div className="flex flex-col gap-2">
        {engineOptions.map(opt => {
          const isSelected = opt.value === selectedSlicerId;
          const iconSrc = getSlicerIconSrc(opt.value);
          const { name, version } = parseLabel(opt.label);
          const shownVersion = isSelected ? (effectiveVersion ?? version) : version;

          return (
            <Button
              key={opt.value}
              variant="unstyled"
              type="button"
              onClick={() => onSlicerChange(opt.value)}
              aria-pressed={isSelected}
              className={clsx(
                'w-full px-3 py-1.5 rounded-lg border-2 cursor-pointer',
                'transition-[color,background-color,border-color,box-shadow] duration-200 ease-out',
                'hover:border-pf-accent hover:bg-pf-accent-bg/10',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
                isSelected
                  ? 'border-pf-accent bg-pf-accent-bg/15 shadow-sm'
                  : 'border-pf-border bg-pf-bg-1',
              )}
            >
              <span className="flex w-full items-center gap-3 text-left">
                {iconSrc ? (
                  <img
                    src={iconSrc}
                    alt=""
                    className="h-8 w-8 shrink-0 rounded-lg object-contain"
                  />
                ) : (
                  <span className="h-8 w-8 shrink-0 flex items-center justify-center rounded-lg bg-pf-bg-2" aria-hidden="true">
                    <GearIcon className="w-5 h-5 text-pf-text-muted" />
                  </span>
                )}
                <span className="min-w-0">
                  <span className={clsx(
                    'block text-sm leading-tight font-semibold truncate',
                    isSelected ? 'text-pf-accent' : 'text-pf-text-primary',
                  )}>
                    {name}
                  </span>
                  {shownVersion && (
                    <span className="block text-xs leading-tight text-pf-text-muted truncate mt-0.5">
                      {shownVersion}
                      {isSelected && !selectedVersion && effectiveVersion && (
                        <span className="text-pf-text-muted/70"> · latest</span>
                      )}
                    </span>
                  )}
                </span>
              </span>
            </Button>
          );
        })}
      </div>

      {/* ENGINE VERSION (issues #578, #1772) — lives in the same panel as the
          engine it qualifies. Rendered only when there is a real choice. */}
      {showVersionPicker && (
        <div className="mt-2 border-t border-pf-border/70 pt-2">
          <div className="flex items-center gap-1 mb-1.5">
            <span id={labelId} className="block text-xs text-pf-text-muted">
              Engine version
            </span>
            <InfoTooltip
              content={
                <>
                  Pins the slice job to a specific {engineName ?? 'slicer'} engine. Leave on
                  Latest unless you need a particular version for compatibility. Only versions
                  with an online worker are listed — anything else could never claim the job.
                </>
              }
              label="More information about engine version"
            />
          </div>

          {useDropdown ? (
            <Select
              aria-labelledby={labelId}
              value={selectedVersion ?? ''}
              onChange={(e) => onVersionChange?.(e.target.value === '' ? undefined : e.target.value)}
            >
              <option value="">Latest{latestVersion ? ` (${latestVersion})` : ''}</option>
              {offeredVersions.map(entry => (
                <option key={entry.version} value={entry.version}>
                  {entry.version}{entry.available ? '' : ' — no online worker'}
                </option>
              ))}
            </Select>
          ) : (
            /* Toggle buttons rather than a radiogroup: every pill stays in the
               natural tab order, so no roving-tabindex handling is needed, and
               it reuses the aria-pressed pattern the engine cards already use. */
            <div role="group" aria-labelledby={labelId} className="flex flex-wrap gap-1.5">
              <VersionPill
                isSelected={selectedVersion === undefined}
                onClick={() => onVersionChange?.(undefined)}
              >
                Latest{latestVersion ? ` · ${latestVersion}` : ''}
              </VersionPill>
              {offeredVersions.map(entry => (
                <VersionPill
                  key={entry.version}
                  isSelected={selectedVersion === entry.version}
                  isStale={!entry.available}
                  onClick={() => onVersionChange?.(entry.version)}
                >
                  {entry.version}
                </VersionPill>
              ))}
            </div>
          )}

          {pinnedVersionIsStale && (
            <p className="mt-1.5 text-xs text-pf-warning">
              {selectedVersion} has no online worker. Switch back to Latest, or start that
              worker before slicing.
            </p>
          )}
        </div>
      )}

      {noVersionsAvailable && (
        <div className="mt-2 border-t border-pf-border/70 pt-2">
          <p className="text-xs text-pf-warning">
            No online {engineName ?? 'slicer'} worker is registered. Slice jobs cannot be
            dispatched until one comes online.
          </p>
        </div>
      )}
    </div>
  );
};

interface VersionPillProps {
  isSelected: boolean;
  /** Pinned but with no online worker — kept visible so the pin is diagnosable. */
  isStale?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}

const VersionPill: React.FC<VersionPillProps> = ({ isSelected, isStale, onClick, children }) => (
  <Button
    variant="unstyled"
    type="button"
    onClick={onClick}
    aria-pressed={isSelected}
    data-pf-radius="full"
    className={clsx(
      'rounded-full border px-2.5 py-1 text-xs font-medium tabular-nums cursor-pointer',
      'transition-[color,background-color,border-color] duration-150 ease-out',
      'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
      isSelected
        ? 'border-pf-accent bg-pf-accent-bg/20 text-pf-accent'
        : 'border-pf-border bg-pf-bg-1 text-pf-text-secondary hover:border-pf-border-strong hover:text-pf-text-primary',
      isStale && !isSelected && 'text-pf-warning',
    )}
  >
    {children}
    {isStale && <span className="sr-only"> (no online worker)</span>}
  </Button>
);

export default SlicerSelector;
