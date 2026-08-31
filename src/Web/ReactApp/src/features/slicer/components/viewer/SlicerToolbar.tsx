/**
 * Slicer Toolbar Component
 * Top toolbar matching OrcaSlicer's interface style
 */
import React, { useCallback, useEffect, useId, useRef, useState } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import {
  AddModelIcon,
  AddPlateIcon,
  ArrangeIcon,
  OrientIcon,
  LayFlatIcon,
  MoveToolIcon,
  RotateToolIcon,
  ScaleToolIcon,
  SplitIcon,
  CutIcon,
  MeshBooleanIcon,
  LayersViewIcon,
  ColorPaintIcon,
  SupportPaintIcon,
  SeamPaintIcon,
  FuzzySkinPaintIcon,
  TextToolSvgIcon,
  MeasureIcon,
  AssemblyIcon,
  SequentialIcon,
  UndoIcon,
  RedoIcon,
  KeyboardIcon,
} from './SlicerToolbarIcons';

interface ToolbarButtonProps {
  icon: React.ReactNode;
  label?: string;
  onClick?: () => void;
  active?: boolean;
  disabled?: boolean;
  title?: string;
  ariaLabel?: string;
  ariaExpanded?: boolean;
  ariaControls?: string;
  ariaHaspopup?: React.AriaAttributes['aria-haspopup'];
}

const ToolbarButton = React.forwardRef<HTMLButtonElement, ToolbarButtonProps>(function ToolbarButton({
  icon,
  label,
  onClick,
  active = false,
  disabled = false,
  title,
  ariaLabel,
  ariaExpanded,
  ariaControls,
  ariaHaspopup,
}, ref) {
  const sizedIcon = React.isValidElement<{ className?: string }>(icon)
    ? React.cloneElement(icon, {
        className: clsx('w-7 h-7 shrink-0', icon.props.className),
      })
    : icon;

  return (
    <Button
      ref={ref}
      type="button"
      variant="unstyled"
      onClick={onClick}
      disabled={disabled}
      title={title || label}
      aria-label={ariaLabel ?? (!label ? title : undefined)}
      aria-expanded={ariaExpanded}
      aria-controls={ariaControls}
      aria-haspopup={ariaHaspopup}
      className={clsx(
        'flex items-center justify-center p-1 rounded transition-all shrink-0',
        active
          ? 'bg-pf-accent/20 text-pf-accent'
          : 'text-pf-text-muted hover:text-pf-text-primary hover:bg-pf-bg-2/50',
        disabled && 'opacity-30 cursor-not-allowed',
      )}
      iconLeft={label ? sizedIcon : undefined}
    >
      {label ? <span className="text-sm hidden xl:inline">{label}</span> : sizedIcon}
    </Button>
  );
});

const ToolbarDivider: React.FC = () => (
  <div className="w-px h-5 bg-pf-border/40 mx-0.5" />
);

const SHORTCUTS = [
  { keys: ['A'], action: 'Auto arrange models' },
  { keys: ['T'], action: 'Move selected model' },
  { keys: ['R'], action: 'Rotate selected model' },
  { keys: ['S'], action: 'Scale selected model' },
  { keys: ['P'], action: 'Cycle paint tools' },
  { keys: ['[', ']'], action: 'Decrease or increase brush size' },
  { keys: ['X'], action: 'Toggle paint and erase while painting' },
  { keys: ['Esc'], action: 'Exit an active tool or clear the selection' },
] as const;

export interface SlicerToolbarProps {
  onAddModel?: () => void;
  onAddPlate?: () => void;
  onArrange?: () => void;
  onOrient?: () => void;
  onLayFlat?: () => void;
  onMove?: () => void;
  onRotate?: () => void;
  onScale?: () => void;
  onSplit?: () => void;
  onCut?: () => void;
  onMeshBoolean?: () => void;
  onVariableLayerHeight?: () => void;
  onColorPaint?: () => void;
  onSupportPaint?: () => void;
  onSeamPaint?: () => void;
  onFuzzySkinPaint?: () => void;
  onTextTool?: () => void;
  onMeasure?: () => void;
  onAssemblyView?: () => void;
  onSequentialToggle?: () => void;
  onUndo?: () => void;
  onRedo?: () => void;
  onToggleSidebar?: () => void;
  sidebarOpen?: boolean;
  canUndo?: boolean;
  canRedo?: boolean;
  hasModels?: boolean;
  hasSelection?: boolean;
  /** Whether another plate can be added (false at the 10-plate cap). */
  canAddPlate?: boolean;
  moveActive?: boolean;
  rotateActive?: boolean;
  scaleActive?: boolean;
  cutActive?: boolean;
  measureActive?: boolean;
  assemblyActive?: boolean;
  colorPaintActive?: boolean;
  supportPaintActive?: boolean;
  seamPaintActive?: boolean;
  fuzzySkinPaintActive?: boolean;
  textToolActive?: boolean;
  sequentialActive?: boolean;
  /** When true, hides cut plane and text tool buttons (Simple slicer mode). */
  simpleMode?: boolean;
}

/** Hamburger icon for sidebar toggle */
const HamburgerIcon: React.FC<{ className?: string }> = ({ className }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
    <line x1="3" y1="6" x2="21" y2="6" />
    <line x1="3" y1="12" x2="21" y2="12" />
    <line x1="3" y1="18" x2="21" y2="18" />
  </svg>
);

export const SlicerToolbar: React.FC<SlicerToolbarProps> = ({
  onAddModel,
  onAddPlate,
  onArrange,
  onOrient,
  onLayFlat,
  onMove,
  onRotate,
  onScale,
  onSplit,
  onCut,
  onMeshBoolean,
  onVariableLayerHeight,
  onColorPaint,
  onSupportPaint,
  onSeamPaint,
  onFuzzySkinPaint,
  onTextTool,
  onMeasure,
  onAssemblyView,
  onSequentialToggle,
  onUndo,
  onRedo,
  onToggleSidebar,
  sidebarOpen = true,
  canUndo = false,
  canRedo = false,
  hasModels = false,
  hasSelection = false,
  canAddPlate = true,
  moveActive = false,
  rotateActive = false,
  scaleActive = false,
  cutActive = false,
  measureActive = false,
  assemblyActive = false,
  colorPaintActive = false,
  supportPaintActive = false,
  seamPaintActive = false,
  fuzzySkinPaintActive = false,
  textToolActive = false,
  sequentialActive = false,
  simpleMode = false,
}) => {
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const shortcutsTriggerRef = useRef<HTMLButtonElement>(null);
  const shortcutsPanelRef = useRef<HTMLDivElement>(null);
  const shortcutsCloseRef = useRef<HTMLButtonElement>(null);
  const shortcutsPanelId = useId();
  const shortcutsTitleId = useId();

  const dismissShortcuts = useCallback(() => {
    setShortcutsOpen(false);
  }, []);

  const closeShortcuts = useCallback(() => {
    dismissShortcuts();
    window.requestAnimationFrame(() => shortcutsTriggerRef.current?.focus());
  }, [dismissShortcuts]);

  useEffect(() => {
    if (!shortcutsOpen) {
      return undefined;
    }

    const focusFrame = window.requestAnimationFrame(() => shortcutsCloseRef.current?.focus());
    const handleMouseDown = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (
        target
        && !shortcutsPanelRef.current?.contains(target)
        && !shortcutsTriggerRef.current?.contains(target)
      ) {
        dismissShortcuts();
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        closeShortcuts();
      }
    };

    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      window.cancelAnimationFrame(focusFrame);
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [closeShortcuts, dismissShortcuts, shortcutsOpen]);

  return (
    // flex-wrap (issue #1902): the pinned left/right groups and the tool
    // region are each `shrink-0`/no-grow blocks, so when the toolbar's own
    // available width is too small — a narrow viewport, or a normal desktop
    // width with the settings drawer eating space — the browser drops whole
    // groups to their own line instead of squeezing/overlapping them. This is
    // a no-op whenever everything already fits on one line, so desktop
    // behavior is unchanged.
    <div className="flex flex-wrap items-center gap-0.5 px-2 py-1 bg-pf-bg-1 border-b border-pf-border shrink-0">
      {/* Hamburger toggle — pinned left, never scrolls off */}
      {onToggleSidebar && (
        <ToolbarButton
          icon={<HamburgerIcon />}
          title={sidebarOpen ? 'Hide Settings' : 'Show Settings'}
          onClick={onToggleSidebar}
          active={sidebarOpen}
        />
      )}

      {/* Add Model / Add Plate — pinned left so they never scroll out of view */}
      <div className="flex items-center gap-0.5 shrink-0">
        <ToolbarButton
          icon={<AddModelIcon />}
          title="Add Model (Ctrl+O)"
          onClick={onAddModel}
        />
        <ToolbarButton
          icon={<AddPlateIcon />}
          title={canAddPlate ? 'Add Plate' : 'Maximum of 10 plates reached'}
          onClick={onAddPlate}
          disabled={!canAddPlate}
        />
        <ToolbarDivider />
      </div>

      {/* Tool region — flexes to fill remaining width and wraps its own
          buttons onto additional lines rather than clipping or relying on an
          invisible horizontal scrollbar (issue #1902). */}
      <div className="flex flex-wrap items-center gap-0.5 min-w-0 flex-1">
        {/* ── Group 1: Object Operations ── */}
        <ToolbarButton
          icon={<ArrangeIcon />}
          title="Auto Arrange (A)"
          onClick={onArrange}
          disabled={!hasModels}
        />
        <ToolbarButton
          icon={<OrientIcon />}
          title="Auto-Orient"
          onClick={onOrient}
          disabled={!hasSelection}
        />
        <ToolbarButton
          icon={<LayFlatIcon />}
          title="Lay Flat (F)"
          onClick={onLayFlat}
          disabled={!hasSelection}
        />

        <ToolbarDivider />

        {/* ── Group 2: Transform & Tools ── */}
        <ToolbarButton
          icon={<MoveToolIcon />}
          title="Move"
          onClick={onMove}
          active={moveActive}
        />
        <ToolbarButton
          icon={<RotateToolIcon />}
          title="Rotate"
          onClick={onRotate}
          active={rotateActive}
        />
        <ToolbarButton
          icon={<ScaleToolIcon />}
          title="Scale"
          onClick={onScale}
          active={scaleActive}
        />
        {/* Advanced-only mesh-editing tools (hidden in Simple per EasyPrint) */}
        {!simpleMode && (
          <>
            <ToolbarButton
              icon={<SplitIcon />}
              title="Split Model"
              onClick={onSplit}
              disabled={!hasSelection}
            />
            <ToolbarButton
              icon={<CutIcon />}
              title="Cut Model (C)"
              onClick={onCut}
              disabled={!hasSelection}
              active={cutActive}
            />
            <ToolbarButton
              icon={<MeshBooleanIcon />}
              title="Mesh Boolean (Coming Soon)"
              onClick={onMeshBoolean}
              disabled
            />
            <ToolbarButton
              icon={<LayersViewIcon />}
              title="Variable Layer Height (Coming Soon)"
              onClick={onVariableLayerHeight}
              disabled
            />
          </>
        )}

        <ToolbarDivider />

        {/* ── Group 3: Paint & Inspection Tools ── */}
        {/* Simple keeps color paint + fuzzy skin (EasyPrint); rest are Advanced-only. */}
        <ToolbarButton
          icon={<ColorPaintIcon />}
          title="Color Painting (P)"
          onClick={onColorPaint}
          disabled={!hasSelection}
          active={colorPaintActive}
        />
        <ToolbarButton
          icon={<FuzzySkinPaintIcon />}
          title="Fuzzy Skin Painting"
          onClick={onFuzzySkinPaint}
          disabled={!hasSelection}
          active={fuzzySkinPaintActive}
        />
        {!simpleMode && (
          <>
            <ToolbarButton
              icon={<SupportPaintIcon />}
              title="Support Painting"
              onClick={onSupportPaint}
              disabled={!hasSelection}
              active={supportPaintActive}
            />
            <ToolbarButton
              icon={<SeamPaintIcon />}
              title="Seam Painting"
              onClick={onSeamPaint}
              disabled={!hasSelection}
              active={seamPaintActive}
            />
            <ToolbarButton
              icon={<TextToolSvgIcon />}
              title="Text Tool"
              onClick={onTextTool}
              active={textToolActive}
            />
            <ToolbarButton
              icon={<MeasureIcon />}
              title="Measure (M)"
              onClick={onMeasure}
              disabled={!hasSelection}
              active={measureActive}
            />
            <ToolbarButton
              icon={<AssemblyIcon />}
              title="Assembly View"
              onClick={onAssemblyView}
              disabled={!hasModels}
              active={assemblyActive}
            />
            <ToolbarButton
              icon={<SequentialIcon />}
              title="Sequential Printing (by object)"
              onClick={onSequentialToggle}
              disabled={!hasModels}
              active={sequentialActive}
            />
          </>
        )}
      </div>

      {/* ── Right side — pinned, never scrolls off ── */}
      <div className="flex items-center gap-0.5 shrink-0">
        <ToolbarButton
          icon={<UndoIcon />}
          title="Undo (Ctrl+Z)"
          onClick={onUndo}
          disabled={!canUndo}
        />
        <ToolbarButton
          icon={<RedoIcon />}
          title="Redo (Ctrl+Y)"
          onClick={onRedo}
          disabled={!canRedo}
        />

        <ToolbarDivider />

        {/* Keyboard shortcuts */}
        <div className="relative">
          <ToolbarButton
            ref={shortcutsTriggerRef}
            icon={<KeyboardIcon />}
            title="Keyboard Shortcuts"
            ariaLabel="Show keyboard shortcuts"
            ariaExpanded={shortcutsOpen}
            ariaControls={shortcutsOpen ? shortcutsPanelId : undefined}
            ariaHaspopup="dialog"
            onClick={() => setShortcutsOpen((open) => !open)}
          />
          {shortcutsOpen && (
            <div
              ref={shortcutsPanelRef}
              id={shortcutsPanelId}
              role="dialog"
              aria-labelledby={shortcutsTitleId}
              className="absolute right-0 top-full z-50 mt-2 w-72 rounded-lg border border-pf-border bg-pf-card p-3 shadow-xl"
            >
              <div className="mb-2 flex items-center justify-between gap-3">
                <h2 id={shortcutsTitleId} className="text-sm font-semibold text-pf-text-primary">
                  Keyboard shortcuts
                </h2>
                <Button
                  ref={shortcutsCloseRef}
                  type="button"
                  variant="ghost"
                  size="sm"
                  aria-label="Close keyboard shortcuts"
                  title="Close"
                  className="h-7 w-7 p-1"
                  iconCenter={<CloseIcon className="h-4 w-4" />}
                  onClick={closeShortcuts}
                />
              </div>
              <dl className="space-y-1.5">
                {SHORTCUTS.map(({ keys, action }) => (
                  <div key={action} className="flex items-center justify-between gap-4 text-xs">
                    <dt className="text-pf-text-secondary">{action}</dt>
                    <dd className="flex shrink-0 items-center gap-1">
                      {keys.map((key) => (
                        <kbd
                          key={key}
                          className="min-w-6 rounded border border-pf-border bg-pf-bg-1 px-1.5 py-0.5 text-center font-mono font-semibold text-pf-text-primary"
                        >
                          {key}
                        </kbd>
                      ))}
                    </dd>
                  </div>
                ))}
              </dl>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default SlicerToolbar;
