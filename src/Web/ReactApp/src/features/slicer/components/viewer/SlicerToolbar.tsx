/**
 * Slicer Toolbar Component
 * Top toolbar matching OrcaSlicer's interface style
 */
import React from 'react';
import {
  AddModelIcon,
  ArrangeIcon,
  OrientIcon,
  LayFlatIcon,
  SplitIcon,
  CutIcon,
  MeasureIcon,
  SupportPaintIcon,
  SeamPaintIcon,
  UndoIcon,
  RedoIcon,
  AssemblyIcon,
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
}) => (
  <button
    onClick={onClick}
    disabled={disabled}
    title={title || label}
    className={`
      flex items-center justify-center p-2 rounded-md transition-colors
      ${active 
        ? 'bg-pf-accent text-white' 
        : 'text-pf-text-secondary hover:bg-pf-bg-2 hover:text-pf-text'
      }
      ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}
      border border-transparent hover:border-pf-border
    `}
  >
    {icon}
    {label && <span className="ml-2 text-sm hidden xl:inline">{label}</span>}
  </button>
);

const ToolbarDivider: React.FC = () => (
  <div className="w-px h-6 bg-pf-border mx-1" />
);

export interface SlicerToolbarProps {
  onAddModel?: () => void;
  onArrange?: () => void;
  onOrient?: () => void;
  onLayFlat?: () => void;
  onSplit?: () => void;
  onCut?: () => void;
  onMeasure?: () => void;
  onSupportPaint?: () => void;
  onSeamPaint?: () => void;
  onUndo?: () => void;
  onRedo?: () => void;
  onAssemblyView?: () => void;
  onSettingsProfiles?: () => void;
  onKeyboardShortcuts?: () => void;
  canUndo?: boolean;
  canRedo?: boolean;
  hasModels?: boolean;
}

export const SlicerToolbar: React.FC<SlicerToolbarProps> = ({
  onAddModel,
  onArrange,
  onOrient,
  onLayFlat,
  onSplit,
  onCut,
  onMeasure,
  onSupportPaint,
  onSeamPaint,
  onUndo,
  onRedo,
  onAssemblyView,
  onSettingsProfiles,
  onKeyboardShortcuts,
  canUndo = false,
  canRedo = false,
  hasModels = false,
}) => {
  return (
    <div className="flex items-center gap-1 px-2 py-1.5 bg-pf-bg-1 border-b border-pf-border">
      {/* Add/Arrange group */}
      <ToolbarButton
        icon={<AddModelIcon />}
        title="Add Model (Ctrl+O)"
        onClick={onAddModel}
      />
      <ToolbarButton
        icon={<ArrangeIcon />}
        title="Auto Arrange (A)"
        onClick={onArrange}
        disabled={!hasModels}
      />

      <ToolbarDivider />

      {/* Orient group */}
      <ToolbarButton
        icon={<OrientIcon />}
        title="Orient Model"
        onClick={onOrient}
        disabled={!hasModels}
      />
      <ToolbarButton
        icon={<LayFlatIcon />}
        title="Lay Flat (F)"
        onClick={onLayFlat}
        disabled={!hasModels}
      />

      <ToolbarDivider />

      {/* Split/Cut group */}
      <ToolbarButton
        icon={<SplitIcon />}
        title="Split Model"
        onClick={onSplit}
        disabled={!hasModels}
      />
      <ToolbarButton
        icon={<CutIcon />}
        title="Cut Model (C)"
        onClick={onCut}
        disabled={!hasModels}
      />

      <ToolbarDivider />

      {/* Measure */}
      <ToolbarButton
        icon={<MeasureIcon />}
        title="Measure (M)"
        onClick={onMeasure}
        disabled={!hasModels}
      />

      <ToolbarDivider />

      {/* Paint group */}
      <ToolbarButton
        icon={<SupportPaintIcon />}
        title="Support Painting"
        onClick={onSupportPaint}
        disabled={!hasModels}
      />
      <ToolbarButton
        icon={<SeamPaintIcon />}
        title="Seam Painting"
        onClick={onSeamPaint}
        disabled={!hasModels}
      />

      <ToolbarDivider />

      {/* Undo/Redo group */}
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

      {/* Assembly view */}
      <ToolbarButton
        icon={<AssemblyIcon />}
        title="Assembly View"
        onClick={onAssemblyView}
        disabled={!hasModels}
      />

      {/* Spacer to push settings to right */}
      <div className="flex-1" />

      {/* Settings & Profiles button */}
      <button
        onClick={onSettingsProfiles}
        className="flex items-center gap-2 px-3 py-1.5 rounded-md bg-pf-accent text-white hover:bg-pf-accent-hover transition-colors border border-pf-accent"
      >
        <SettingsProfilesIcon className="w-4 h-4" />
        <span className="text-sm font-medium">SETTINGS & PROFILES</span>
      </button>

      {/* Keyboard shortcuts */}
      <ToolbarButton
        icon={<KeyboardIcon />}
        title="Keyboard Shortcuts"
        onClick={onKeyboardShortcuts}
      />

      {/* Beta badge */}
      <span className="ml-2 px-2 py-0.5 text-xs font-semibold rounded bg-blue-600 text-white">
        Beta
      </span>
    </div>
  );
};

export default SlicerToolbar;
