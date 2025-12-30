import React from 'react';

interface ViewerSkeletonProps {
  variant: 'model' | 'gcode';
  className?: string;
}

// Lightweight accessible loading skeleton for 3D / G-code viewers
export const ViewerSkeleton: React.FC<ViewerSkeletonProps> = ({ variant, className = 'h-96 w-full' }) => {
  return (
    <div className={`${className} relative flex items-center justify-center bg-gray-100 rounded-lg overflow-hidden`} aria-busy="true" aria-label={`Loading ${variant === 'model' ? '3D model' : 'G-code preview'}`}> 
      <div className="absolute inset-0 animate-pulse">
        <div className="h-full w-full bg-gradient-to-br from-gray-200 via-gray-100 to-gray-200" />
      </div>
      <div className="relative z-10 text-center">
        <div className="text-sm font-medium text-gray-600 mb-2">
          {variant === 'model' ? 'Preparing 3D Viewer…' : 'Parsing G-code…'}
        </div>
        <div className="w-48 h-2 bg-gray-300 rounded overflow-hidden">
          <div className="h-full w-1/2 bg-blue-500/60 animate-[progress_1.2s_ease-in-out_infinite]" />
        </div>
      </div>
      <style>{`@keyframes progress { 0% { transform: translateX(-50%);} 50% { transform: translateX(50%);} 100% { transform: translateX(150%);} }`}</style>
    </div>
  );
};
