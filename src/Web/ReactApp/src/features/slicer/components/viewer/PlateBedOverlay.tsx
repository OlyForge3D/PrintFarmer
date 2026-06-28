/**
 * PlateBedOverlay — per-plate in-scene chrome for the all-plates grid.
 *
 * Rendered INSIDE each plate's offset `<PlateGroup>` (via drei `<Html transform>`),
 * so the overlay tracks the plate as the camera orbits and inherits the grid
 * offset automatically. It shows a large plate number, an editable title, and a
 * floating vertical stack of per-plate actions (lock / auto-arrange /
 * auto-orient / delete).
 *
 * Pointer hygiene: every interactive control calls `stopPropagation()` on
 * pointer-down AND click so the R3F canvas behind the HTML does not raycast and
 * select/deselect models through the UI. The non-interactive title/number block
 * activates the plate on click.
 *
 * Clutter control: the active plate shows the full action stack; inactive plates
 * show only the number, title and delete (keeps n=10 readable).
 */
import React, { useEffect, useRef, useState } from 'react';
import { Html } from '@react-three/drei';
import { GridIcon, CompassIcon, DeleteIcon, LockIcon, LockOpenIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

export interface PlateBedOverlayProps {
  /** 1-based plate number for the big "01/02" badge. */
  plateNumber: number;
  name: string;
  active: boolean;
  locked: boolean;
  /** False for the last remaining plate (delete is then disabled). */
  canDelete: boolean;
  bedWidth: number;
  bedDepth: number;
  /**
   * Full grid span (max extent across all plates). The camera pulls back as the
   * grid grows (distance ∝ gridSpan), so we scale the drei `distanceFactor` by
   * the same span to keep the plate number/title/actions a constant on-screen
   * size regardless of how many plates are laid out. Falls back to the bed size
   * for the single-plate case.
   */
  gridSpan?: number;
  onActivate: () => void;
  onRename: (name: string) => void;
  onDelete: () => void;
  onArrange: () => void;
  onOrient: () => void;
  onToggleLock: () => void;
}

/** Swallow pointer/click so the canvas behind the HTML never raycasts. */
const swallow = (e: React.SyntheticEvent) => {
  e.stopPropagation();
};

interface PlateActionButtonProps {
  label: string;
  disabled?: boolean;
  active?: boolean;
  danger?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}

const PlateActionButton: React.FC<PlateActionButtonProps> = ({
  label,
  disabled = false,
  active = false,
  danger = false,
  onClick,
  children,
}) => (
  <Button
    variant="unstyled"
    aria-label={label}
    title={label}
    disabled={disabled}
    iconCenter={children}
    onPointerDown={swallow}
    onClick={(e) => {
      e.stopPropagation();
      if (!disabled) onClick();
    }}
    className={[
      'flex h-8 w-8 items-center justify-center rounded-md border transition-colors',
      disabled
        ? 'cursor-not-allowed border-pf-border/40 bg-pf-surface/40 text-pf-text-muted/40'
        : danger
          ? 'border-pf-border/60 bg-pf-surface/80 text-pf-text hover:border-red-500 hover:bg-red-500/20 hover:text-red-300'
          : active
            ? 'border-pf-accent bg-pf-accent/20 text-pf-accent'
            : 'border-pf-border/60 bg-pf-surface/80 text-pf-text hover:border-pf-accent hover:bg-pf-accent/15',
    ].join(' ')}
  />
);

export const PlateBedOverlay: React.FC<PlateBedOverlayProps> = ({
  plateNumber,
  name,
  active,
  locked,
  canDelete,
  bedWidth,
  bedDepth,
  gridSpan,
  onActivate,
  onRename,
  onDelete,
  onArrange,
  onOrient,
  onToggleLock,
}) => {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(name);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (editing) inputRef.current?.select();
  }, [editing]);

  const startEditing = () => {
    setDraft(name);
    setEditing(true);
  };

  const commit = () => {
    const next = draft.trim();
    if (next && next !== name) onRename(next);
    setEditing(false);
  };

  const badge = String(plateNumber).padStart(2, '0');
  // Drei zIndexRange: keep the active plate's chrome above inactive plates.
  const zIndexRange: [number, number] = active ? [40, 30] : [20, 10];
  // Keep the overlay a constant on-screen size as the grid (and camera distance)
  // grows: distanceFactor tracks the full grid span, not just this plate's bed.
  const labelDistanceFactor = Math.max(bedWidth, bedDepth, gridSpan ?? 0);

  return (
    <>
      {/* Number + editable title — anchored at the front-left bed corner. */}
      <Html
        position={[-bedWidth / 2, -bedDepth / 2 - 14, 0]}
        transform
        distanceFactor={labelDistanceFactor}
        zIndexRange={zIndexRange}
        style={{ pointerEvents: 'auto', userSelect: 'none' }}
      >
        <div
          onPointerDown={swallow}
          onClick={(e) => {
            e.stopPropagation();
            onActivate();
          }}
          className={[
            'flex items-center gap-2 whitespace-nowrap rounded-md px-2 py-1',
            active ? 'opacity-100' : 'opacity-60',
          ].join(' ')}
          style={{ cursor: 'pointer' }}
        >
          <span
            className={[
              'font-bold leading-none tracking-tight',
              active ? 'text-pf-accent' : 'text-pf-text-muted',
            ].join(' ')}
            style={{ fontSize: 34 }}
          >
            {badge}
          </span>
          {editing ? (
            <input
              ref={inputRef}
              value={draft}
              onPointerDown={swallow}
              onChange={(e) => setDraft(e.target.value)}
              onBlur={commit}
              onKeyDown={(e) => {
                e.stopPropagation();
                if (e.key === 'Enter') commit();
                if (e.key === 'Escape') setEditing(false);
              }}
              className="w-32 rounded border border-pf-accent bg-pf-surface px-1.5 py-0.5 text-sm text-pf-text outline-none"
            />
          ) : (
            <Button
              variant="unstyled"
              title="Rename plate"
              iconCenter={name}
              onPointerDown={swallow}
              onClick={(e) => {
                e.stopPropagation();
                onActivate();
                startEditing();
              }}
              className={[
                'max-w-[8rem] truncate rounded px-1 text-sm font-medium hover:bg-pf-surface/60',
                active ? 'text-pf-text' : 'text-pf-text-muted',
              ].join(' ')}
            />
          )}
        </div>
      </Html>

      {/* Floating vertical action stack — anchored at the right bed edge. */}
      <Html
        position={[bedWidth / 2 + 18, 0, 0]}
        transform
        distanceFactor={labelDistanceFactor}
        zIndexRange={zIndexRange}
        style={{ pointerEvents: 'auto' }}
      >
        <div onPointerDown={swallow} className="flex flex-col gap-1.5">
          {active && (
            <>
              <PlateActionButton
                label={locked ? 'Unlock plate' : 'Lock plate'}
                active={locked}
                onClick={onToggleLock}
              >
                {locked ? <LockIcon className="h-4 w-4" /> : <LockOpenIcon className="h-4 w-4" />}
              </PlateActionButton>
              <PlateActionButton
                label="Auto-arrange plate"
                disabled={locked}
                onClick={onArrange}
              >
                <GridIcon className="h-4 w-4" />
              </PlateActionButton>
              <PlateActionButton
                label="Auto-orient plate"
                disabled={locked}
                onClick={onOrient}
              >
                <CompassIcon className="h-4 w-4" />
              </PlateActionButton>
            </>
          )}
          <PlateActionButton
            label="Delete plate"
            danger
            disabled={locked || !canDelete}
            onClick={onDelete}
          >
            <DeleteIcon className="h-4 w-4" />
          </PlateActionButton>
        </div>
      </Html>
    </>
  );
};
