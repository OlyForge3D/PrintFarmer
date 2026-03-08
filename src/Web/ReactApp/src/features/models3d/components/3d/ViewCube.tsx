/* eslint-disable local/pf-no-raw-html-controls */
// Complex 3D CAD-style view control - raw buttons are intentional for performance and direct SVG integration
import React from 'react';

interface ViewCubeProps {
  onViewChange: (direction: 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right' | 'iso') => void;
}

/**
 * CAD-style View Cube
 * Allows quick switching between standard orthographic views
 */
export const ViewCube: React.FC<ViewCubeProps> = ({ onViewChange }) => {

  return (
    <div className="absolute top-4 right-4 z-10 bg-pf-bg-0/90 rounded-lg shadow-lg p-2 border border-pf-border">
      {/* Top face */}
      <div className="grid grid-cols-3 gap-1 mb-1">
        {/* Empty corner */}
        <div className="w-6 h-6" />
        <button
          onClick={() => onViewChange('top')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Top View"
        >
          T
        </button>
        {/* Empty corner */}
        <div className="w-6 h-6" />
      </div>

      {/* Middle row */}
      <div className="grid grid-cols-3 gap-1 mb-1">
        <button
          onClick={() => onViewChange('left')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Left View"
        >
          L
        </button>
        <button
          onClick={() => onViewChange('iso')}
          className="w-6 h-6 bg-pf-accent/60 hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Isometric View"
        >
          I
        </button>
        <button
          onClick={() => onViewChange('right')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Right View"
        >
          R
        </button>
      </div>

      {/* Bottom row */}
      <div className="grid grid-cols-3 gap-1 mb-1">
        {/* Empty corner */}
        <div className="w-6 h-6" />
        <button
          onClick={() => onViewChange('bottom')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Bottom View"
        >
          B
        </button>
        {/* Empty corner */}
        <div className="w-6 h-6" />
      </div>

      {/* Front face */}
      <div className="grid grid-cols-3 gap-1 mt-1 pt-1 border-t border-pf-border">
        {/* Empty corner */}
        <div className="w-6 h-6" />
        <button
          onClick={() => onViewChange('front')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Front View"
        >
          F
        </button>
        <button
          onClick={() => onViewChange('back')}
          className="w-6 h-6 bg-pf-accent hover:bg-pf-accent/80 text-white text-xs font-bold rounded-sm flex items-center justify-center transition-colors cursor-pointer"
          title="Back View"
        >
          K
        </button>
      </div>

      {/* Legend */}
      <div className="mt-2 pt-2 border-t border-pf-border text-xs text-pf-text-secondary text-center">
        <div>T=Top B=Bottom</div>
        <div>F=Front K=Back</div>
        <div>L=Left R=Right</div>
        <div>I=Isometric</div>
      </div>
    </div>
  );
};

export default ViewCube;
