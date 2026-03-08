import React from 'react';

interface ViewerSkeletonProps {
  variant: 'model' | 'gcode';
  className?: string;
}

// Lightweight accessible loading skeleton for 3D / G-code viewers
export const ViewerSkeleton: React.FC<ViewerSkeletonProps> = ({ variant, className = 'h-96 w-full' }) => {
  return (
    <div className={`${className} relative flex items-center justify-center rounded-lg overflow-hidden`} style={{ backgroundColor: '#2d3748' }} aria-busy="true" aria-label={`Loading ${variant === 'model' ? '3D model' : 'G-code preview'}`}> 
      <div className="absolute inset-0 animate-pulse">
        <div className="h-full w-full bg-linear-to-br from-pf-bg-2 via-pf-bg-1 to-pf-bg-2" />
      </div>
      <div className="relative z-10 text-center">
        <div className="text-sm font-medium text-pf-text-primary mb-2">
          {variant === 'model' ? 'Preparing To View 3D File' : 'Parsing G-code…'}
        </div>
        <div className="w-48 h-2 bg-pf-bg-2/50 rounded-sm overflow-hidden">
          <div className="h-full w-1/2 bg-pf-accent/80 animate-[progress_1.2s_ease-in-out_infinite]" />
        </div>
      </div>
      <style>{`@keyframes progress { 0% { transform: translateX(-50%);} 50% { transform: translateX(50%);} 100% { transform: translateX(150%);} }`}</style>
    </div>
  );
};
