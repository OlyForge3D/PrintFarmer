/**
 * Plate Tab Bar — horizontal tab strip for switching between build plates.
 * Sits between the toolbar and the 3D viewport.
 */
import React, { useState, useRef, useEffect, useCallback } from 'react';
import clsx from 'clsx';
import { MoreHorizontal, Copy, Trash2, Pencil, LayoutGrid, Compass } from 'lucide-react';
import { Button } from '@/common/components/ui';
import type { BuildPlate } from '@/features/slicer/utils/plateManager';

export interface PlateTabBarProps {
  plates: BuildPlate[];
  activePlateId: string;
  onActivePlateChange: (plateId: string) => void;
  onArrangePlate: (plateId: string) => void;
  onOrientPlate: (plateId: string) => void;
  onDeletePlate: (plateId: string) => void;
  onRenamePlate: (plateId: string, name: string) => void;
  onDuplicatePlate: (plateId: string) => void;
}

interface ContextMenuState {
  plateId: string;
  x: number;
  y: number;
}

export const PlateTabBar: React.FC<PlateTabBarProps> = ({
  plates,
  activePlateId,
  onActivePlateChange,
  onArrangePlate,
  onOrientPlate,
  onDeletePlate,
  onRenamePlate,
  onDuplicatePlate,
}) => {
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editValue, setEditValue] = useState('');
  const menuRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const canRemove = plates.length > 1;

  // Close context menu on outside click
  useEffect(() => {
    if (!contextMenu) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setContextMenu(null);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [contextMenu]);

  // Focus rename input when editing starts
  useEffect(() => {
    if (editingId && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [editingId]);

  const handleContextMenu = useCallback((e: React.MouseEvent, plateId: string) => {
    e.preventDefault();
    e.stopPropagation();
    setContextMenu({ plateId, x: e.clientX, y: e.clientY });
  }, []);

  const handleMenuAction = useCallback(
    (action: 'rename' | 'duplicate' | 'delete') => {
      if (!contextMenu) return;
      const { plateId } = contextMenu;
      setContextMenu(null);

      switch (action) {
        case 'rename': {
          const plate = plates.find(p => p.id === plateId);
          if (plate) {
            setEditingId(plateId);
            setEditValue(plate.name);
          }
          break;
        }
        case 'duplicate':
          onDuplicatePlate(plateId);
          break;
        case 'delete':
          if (canRemove) onDeletePlate(plateId);
          break;
      }
    },
    [contextMenu, plates, canRemove, onDuplicatePlate, onDeletePlate],
  );

  const commitRename = useCallback(() => {
    if (editingId && editValue.trim()) {
      onRenamePlate(editingId, editValue.trim());
    }
    setEditingId(null);
    setEditValue('');
  }, [editingId, editValue, onRenamePlate]);

  const actionIconClass =
    'flex items-center justify-center p-0.5 rounded text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2/50 transition-colors';

  return (
    <>
      <div className="flex items-center gap-0.5 px-2 py-1 bg-pf-bg-1 border-b border-pf-border overflow-x-auto">
        {plates.map(plate => {
          const isActive = plate.id === activePlateId;
          const isEditing = editingId === plate.id;
          const count = plate.modelIds.length;

          return (
            <div
              key={plate.id}
              className={clsx(
                'group relative flex items-center gap-1 px-2 py-1 text-xs font-medium rounded-md transition-colors select-none whitespace-nowrap border',
                isActive
                  ? 'bg-pf-accent-bg text-pf-accent border-pf-accent/30'
                  : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text-primary border-transparent',
              )}
            >
              {/* Tab select button (its own button — action icons are siblings, never nested).
                  While renaming, the input REPLACES the button so no interactive
                  element is ever nested inside another (a11y / HTML validity). */}
              {isEditing ? (
                <input
                  ref={inputRef}
                  className="flex items-center gap-1.5 bg-transparent border-b border-pf-accent text-pf-text-primary text-xs outline-none w-20"
                  value={editValue}
                  onChange={e => setEditValue(e.target.value)}
                  onBlur={commitRename}
                  aria-label={`Rename ${plate.name}`}
                  onKeyDown={e => {
                    if (e.key === 'Enter') commitRename();
                    if (e.key === 'Escape') {
                      setEditingId(null);
                      setEditValue('');
                    }
                  }}
                  onClick={e => e.stopPropagation()}
                />
              ) : (
                <Button
                  variant="unstyled"
                  type="button"
                  className="flex items-center gap-1.5 bg-transparent outline-none cursor-pointer"
                  onClick={() => onActivePlateChange(plate.id)}
                  onContextMenu={(e: React.MouseEvent) => handleContextMenu(e, plate.id)}
                  aria-label={`Select ${plate.name}${count > 0 ? ` (${count} model${count === 1 ? '' : 's'})` : ''}`}
                  aria-current={isActive ? 'true' : undefined}
                >
                  <span>{plate.name}</span>

                  {count > 0 && (
                    <span
                      className={clsx(
                        'inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full text-[10px] font-semibold leading-none',
                        isActive
                          ? 'bg-pf-accent/20 text-pf-accent'
                          : 'bg-pf-bg-2 text-pf-text-secondary',
                      )}
                    >
                      {count}
                    </span>
                  )}
                </Button>
              )}

              {/* Active-tab inline actions — siblings of the select button. Only the
                  active tab shows these to avoid overflow with up to 10 plates. */}
              {isActive && !isEditing && (
                <>
                  <Button
                    variant="unstyled"
                    type="button"
                    className={actionIconClass}
                    title="Auto-arrange this plate"
                    aria-label={`Auto-arrange ${plate.name}`}
                    onClick={() => onArrangePlate(plate.id)}
                  >
                    <LayoutGrid size={12} />
                  </Button>
                  <Button
                    variant="unstyled"
                    type="button"
                    className={actionIconClass}
                    title="Auto-orient all models on this plate"
                    aria-label={`Auto-orient ${plate.name}`}
                    onClick={() => onOrientPlate(plate.id)}
                  >
                    <Compass size={12} />
                  </Button>
                  <Button
                    variant="unstyled"
                    type="button"
                    className={clsx(
                      actionIconClass,
                      canRemove ? 'hover:text-pf-error' : 'opacity-30 cursor-not-allowed',
                    )}
                    title={canRemove ? 'Delete this plate' : 'Cannot delete the last plate'}
                    aria-label={`Delete ${plate.name}`}
                    disabled={!canRemove}
                    onClick={() => onDeletePlate(plate.id)}
                  >
                    <Trash2 size={12} />
                  </Button>
                </>
              )}

              {/* Kebab menu trigger — sibling button (rename / duplicate / delete) */}
              {!isEditing && (
                <Button
                  variant="unstyled"
                  type="button"
                  className={clsx(
                    actionIconClass,
                    'opacity-0 group-hover:opacity-100',
                    isActive && 'opacity-60',
                  )}
                  aria-label={`${plate.name} options`}
                  onClick={(e: React.MouseEvent) => {
                    e.stopPropagation();
                    setContextMenu({ plateId: plate.id, x: e.clientX, y: e.clientY });
                  }}
                >
                  <MoreHorizontal size={12} />
                </Button>
              )}
            </div>
          );
        })}
      </div>

      {/* Context Menu */}
      {contextMenu && (
        <div
          ref={menuRef}
          className="fixed z-50 min-w-[140px] rounded-md border border-pf-border bg-pf-bg-1 shadow-lg py-1 text-xs"
          style={{ left: contextMenu.x, top: contextMenu.y }}
        >
          <Button
            variant="unstyled"
            className="flex items-center gap-2 w-full px-3 py-1.5 text-left text-pf-text-primary hover:bg-pf-bg-2 cursor-pointer"
            onClick={() => handleMenuAction('rename')}
          >
            <Pencil size={12} /> Rename
          </Button>
          <Button
            variant="unstyled"
            className="flex items-center gap-2 w-full px-3 py-1.5 text-left text-pf-text-primary hover:bg-pf-bg-2 cursor-pointer"
            onClick={() => handleMenuAction('duplicate')}
          >
            <Copy size={12} /> Duplicate
          </Button>
          <div className="my-1 border-t border-pf-border" />
          <Button
            variant="unstyled"
            className={clsx(
              'flex items-center gap-2 w-full px-3 py-1.5 text-left cursor-pointer',
              canRemove ? 'text-pf-error hover:bg-pf-bg-2' : 'text-pf-text-secondary opacity-50 cursor-not-allowed',
            )}
            disabled={!canRemove}
            onClick={() => handleMenuAction('delete')}
          >
            <Trash2 size={12} /> Delete
          </Button>
        </div>
      )}
    </>
  );
};
