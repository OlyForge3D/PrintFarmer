/**
 * Slicer Left Tools Component
 * Left sidebar tools matching OrcaSlicer's manipulation tools
 */
import React from 'react';
import { Button } from '@/common/components/ui';
import clsx from 'clsx';
import {
  MoveToolIcon,
  RotateToolIcon,
  ScaleToolIcon,
  LayersViewIcon,
} from './SlicerToolbarIcons';

export type ToolType = 'move' | 'rotate' | 'scale' | 'layers';

interface ToolButtonProps {
  icon: React.ReactNode;
  title: string;
  active?: boolean;
  onClick?: () => void;
}

const ToolButton: React.FC<ToolButtonProps> = ({ icon, title, active = false, onClick }) => (
  <Button
    onClick={onClick}
    title={title}
    variant="unstyled"
    className={clsx(
      'w-12 h-12 flex items-center justify-center rounded-lg transition-all p-0',
      'border shadow-sm',
      active
        ? 'bg-pf-accent text-white shadow-lg border-pf-accent'
        : 'bg-pf-bg-2 text-pf-text-secondary border-pf-border/60 hover:bg-pf-bg-2/80 hover:text-pf-text-primary hover:border-pf-accent/50 hover:shadow-md',
    )}
  >
    {React.isValidElement<{ className?: string }>(icon)
      ? React.cloneElement(icon, { className: clsx('w-9 h-9', icon.props.className) })
      : icon}
  </Button>
);

export interface SlicerLeftToolsProps {
  activeTool: ToolType;
  onToolChange: (tool: ToolType) => void;
  onLayersToggle?: () => void;
  showLayers?: boolean;
}

export const SlicerLeftTools: React.FC<SlicerLeftToolsProps> = ({
  activeTool,
  onToolChange,
  onLayersToggle,
  showLayers = false,
}) => {
  return (
    <div className="absolute left-4 top-1/2 -translate-y-1/2 flex flex-col gap-2 z-10">
      {/* Manipulation tools group */}
      <div className="flex flex-col gap-1.5 p-1.5 bg-pf-bg-1/90 backdrop-blur-xs rounded-xl border border-pf-border shadow-lg">
        <ToolButton
          icon={<MoveToolIcon />}
          title="Move (T)"
          active={activeTool === 'move'}
          onClick={() => onToolChange('move')}
        />
        <ToolButton
          icon={<RotateToolIcon />}
          title="Rotate (R)"
          active={activeTool === 'rotate'}
          onClick={() => onToolChange('rotate')}
        />
        <ToolButton
          icon={<ScaleToolIcon />}
          title="Scale (S)"
          active={activeTool === 'scale'}
          onClick={() => onToolChange('scale')}
        />
      </div>

      {/* Layers toggle - separate group */}
      <div className="flex flex-col gap-1.5 p-1.5 bg-pf-bg-1/90 backdrop-blur-xs rounded-xl border border-pf-border shadow-lg">
        <ToolButton
          icon={<LayersViewIcon />}
          title="Layer View (L)"
          active={showLayers}
          onClick={onLayersToggle}
        />
      </div>
    </div>
  );
};

export default SlicerLeftTools;
