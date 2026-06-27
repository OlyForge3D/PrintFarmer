/**
 * Slicer Toolbar Component
 * Top toolbar matching OrcaSlicer's interface style
 */
import React from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
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
  SettingsProfilesIcon,
  KeyboardIcon,
} from './SlicerToolbarIcons';

interface ToolbarButtonProps {
  icon: React.ReactNode;
  label?: string;
  onClick?: () => void;
  active?: boolean;
  disabled?: boolean;
  title?: string;
}

const ToolbarButton: React.FC<ToolbarButtonProps> = ({
  icon,
  label,
  onClick,
  active = false,
  disabled = false,
  title
}) => {
  const sizedIcon = React.isValidElement<{ className?: string }>(icon)
    ? React.cloneElement(icon, {
        className: clsx('w-7 h-7 shrink-0', icon.props.className),
      })
    : icon;

  return (
    <Button
      type="button"
      variant="unstyled"
      onClick={onClick}
      disabled={disabled}
      title={title || label}
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
};

const ToolbarDivider: React.FC = () => (
  <div className="w-px h-5 bg-pf-border/40 mx-0.5" />
);

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
  onSettingsProfiles?: () => void;
  onKeyboardShortcuts?: () => void;
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
  onSettingsProfiles,
  onKeyboardShortcuts,
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
  return (
    <div className="flex items-center gap-0.5 px-2 py-1 bg-pf-bg-1 border-b border-pf-border shrink-0">
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

      {/* Scrollable tool region — flexes to fill, scrolls horizontally so tools
          never run off the visible area at any width. Scrollbar hidden visually. */}
      <div className="flex items-center gap-0.5 min-w-0 flex-1 overflow-x-auto [scrollbar-width:none] [-ms-overflow-style:none] [&::-webkit-scrollbar]:hidden">
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

        {/* Settings & Profiles button */}
        <Button
          variant="primary"
          onClick={onSettingsProfiles}
          className="px-3 py-1.5"
          iconLeft={<SettingsProfilesIcon className="w-4 h-4" />}
        >
          <span className="text-sm font-medium hidden lg:inline">SETTINGS &amp; PROFILES</span>
        </Button>

        {/* Keyboard shortcuts */}
        <ToolbarButton
          icon={<KeyboardIcon />}
          title="Keyboard Shortcuts"
          onClick={onKeyboardShortcuts}
        />

        {/* Beta badge */}
        <span className="ml-1 px-2 py-0.5 text-xs font-semibold rounded-sm bg-pf-accent-bg text-white">
          Beta
        </span>
      </div>
    </div>
  );
};

export default SlicerToolbar;
