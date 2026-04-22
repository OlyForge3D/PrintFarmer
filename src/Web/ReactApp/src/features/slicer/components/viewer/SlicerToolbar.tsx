/**
 * Slicer Toolbar Component
 * Top toolbar matching OrcaSlicer's interface style
 */
import React from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';
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
}) => {
  const sizedIcon = React.isValidElement<{ className?: string }>(icon)
    ? React.cloneElement(icon, {
        className: clsx('w-9 h-9 shrink-0', icon.props.className),
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
        'flex items-center justify-center p-1.5 rounded-lg transition-all shrink-0',
        'border shadow-sm',
        active
          ? 'bg-pf-accent/20 border-pf-accent shadow-pf-accent/20'
          : 'bg-pf-bg-2 border-pf-border/60 hover:bg-pf-bg-2/80 hover:border-pf-accent/50 hover:shadow-md',
        disabled && 'opacity-40 cursor-not-allowed',
      )}
      iconLeft={label ? sizedIcon : undefined}
    >
      {label ? <span className="text-sm hidden xl:inline">{label}</span> : sizedIcon}
    </Button>
  );
};

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
  onToggleSidebar?: () => void;
  sidebarOpen?: boolean;
  canUndo?: boolean;
  canRedo?: boolean;
  hasModels?: boolean;
  hasSelection?: boolean;
  measureActive?: boolean;
  assemblyActive?: boolean;
  cutActive?: boolean;
  supportPaintActive?: boolean;
  seamPaintActive?: boolean;
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
  onToggleSidebar,
  sidebarOpen = true,
  canUndo = false,
  canRedo = false,
  hasModels = false,
  hasSelection = false,
  measureActive = false,
  assemblyActive = false,
  cutActive = false,
  supportPaintActive = false,
  seamPaintActive = false,
}) => {
  return (
    <div className="flex items-center gap-1 px-2 py-1.5 bg-pf-bg-1 border-b border-pf-border shrink-0">
      {/* Hamburger toggle */}
      {onToggleSidebar && (
        <ToolbarButton
          icon={<HamburgerIcon />}
          title={sidebarOpen ? 'Hide Settings' : 'Show Settings'}
          onClick={onToggleSidebar}
          active={sidebarOpen}
        />
      )}

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

      {/* Less-essential buttons — hidden on very narrow viewports */}
      <div className="hidden md:contents">
        <ToolbarDivider />

        {/* Split/Cut group */}
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

        <ToolbarDivider />

        {/* Measure */}
        <ToolbarButton
          icon={<MeasureIcon />}
          title="Measure (M)"
          onClick={onMeasure}
          disabled={!hasSelection}
          active={measureActive}
        />

        <ToolbarDivider />

        {/* Paint group */}
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
      </div>

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

      <div className="hidden md:contents">
        <ToolbarDivider />

        {/* Assembly view */}
        <ToolbarButton
          icon={<AssemblyIcon />}
          title="Assembly View"
          onClick={onAssemblyView}
          disabled={!hasModels}
          active={assemblyActive}
        />
      </div>

      {/* Spacer to push settings to right */}
      <div className="flex-1 min-w-1" />

      {/* Settings & Profiles button */}
      <Button
        variant="primary"
        onClick={onSettingsProfiles}
        className="px-3 py-1.5"
        iconLeft={<SettingsProfilesIcon className="w-4 h-4" />}
      >
        <span className="text-sm font-medium">SETTINGS & PROFILES</span>
      </Button>

      {/* Keyboard shortcuts */}
      <ToolbarButton
        icon={<KeyboardIcon />}
        title="Keyboard Shortcuts"
        onClick={onKeyboardShortcuts}
      />

      {/* Beta badge */}
      <span className="ml-2 px-2 py-0.5 text-xs font-semibold rounded-sm bg-pf-accent-bg text-white">
        Beta
      </span>
    </div>
  );
};

export default SlicerToolbar;
