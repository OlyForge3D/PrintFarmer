/**
 * Slicer Left Tools Component
 * Left sidebar tools matching OrcaSlicer's manipulation tools
 */
import React from 'react';
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
  <button
    onClick={onClick}
    title={title}
    className={`
      w-10 h-10 flex items-center justify-center rounded-lg transition-colors
      ${active 
        ? 'bg-pf-accent text-white shadow-lg' 
        : 'bg-pf-bg-2 text-pf-text-secondary hover:bg-pf-bg-3 hover:text-pf-text'
      }
      border border-pf-border hover:border-pf-accent
    `}
  >
    {icon}
  </button>
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
      <div className="flex flex-col gap-1.5 p-1.5 bg-pf-bg-1/90 backdrop-blur-sm rounded-xl border border-pf-border shadow-lg">
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
      <div className="flex flex-col gap-1.5 p-1.5 bg-pf-bg-1/90 backdrop-blur-sm rounded-xl border border-pf-border shadow-lg">
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
