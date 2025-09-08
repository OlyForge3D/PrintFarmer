import React from 'react';

declare const __BUILD_TIME__: string;
declare const __GIT_HASH__: string;

export const BuildInfo: React.FC<{ className?: string }> = ({ className = '' }) => {
  return (
    <span className={`text-xs opacity-60 font-mono ${className}`} title={`Built ${__BUILD_TIME__}`}>
      {__GIT_HASH__}
    </span>
  );
};