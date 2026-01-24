/**
 * OrcaSlicer-style setting icons for the slicer settings panel
 * These SVG icons match the visual style of OrcaSlicer's UI
 */
import React from 'react';

interface IconProps {
  className?: string;
}

/** Diagonal hatch pattern icon for Infill Density */
export const InfillDensityIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <line x1="6" y1="21" x2="21" y2="6" />
    <line x1="3" y1="18" x2="18" y2="3" />
    <line x1="3" y1="12" x2="12" y2="3" />
    <line x1="12" y1="21" x2="21" y2="12" />
    <line x1="6" y1="21" x2="21" y2="6" opacity="0.5" />
  </svg>
);

/** Grid/crosshatch pattern icon for Infill Pattern */
export const InfillPatternIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <circle cx="8" cy="8" r="2" fill="currentColor" />
    <circle cx="16" cy="8" r="2" fill="currentColor" />
    <circle cx="8" cy="16" r="2" fill="currentColor" />
    <circle cx="16" cy="16" r="2" fill="currentColor" />
    <circle cx="12" cy="12" r="2" fill="currentColor" />
    <line x1="8" y1="10" x2="8" y2="14" />
    <line x1="16" y1="10" x2="16" y2="14" />
    <line x1="10" y1="8" x2="14" y2="8" />
    <line x1="10" y1="16" x2="14" y2="16" />
  </svg>
);

/** Stacked layers icon for Wall Count */
export const WallCountIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="4" y="4" width="16" height="16" rx="1" />
    <rect x="6" y="6" width="12" height="12" rx="1" />
    <rect x="8" y="8" width="8" height="8" rx="1" />
  </svg>
);

/** First layer / bed adhesion icon */
export const BedAdhesionIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="2" y="18" width="20" height="3" rx="1" fill="currentColor" opacity="0.3" />
    <path d="M6 18V10a2 2 0 012-2h8a2 2 0 012 2v8" />
    <path d="M4 18h16" strokeDasharray="2 2" />
  </svg>
);

/** Support structure icon */
export const SupportsIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 4L6 20" />
    <path d="M12 4L18 20" />
    <path d="M8 12h8" />
    <path d="M7 16h10" />
    <circle cx="12" cy="4" r="2" fill="currentColor" />
  </svg>
);

/** Layer height icon */
export const LayerHeightIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="4" y="4" width="16" height="3" rx="0.5" fill="currentColor" opacity="0.8" />
    <rect x="4" y="9" width="16" height="3" rx="0.5" fill="currentColor" opacity="0.6" />
    <rect x="4" y="14" width="16" height="3" rx="0.5" fill="currentColor" opacity="0.4" />
    <rect x="4" y="19" width="16" height="2" rx="0.5" fill="currentColor" opacity="0.2" />
  </svg>
);

/** Line width icon */
export const LineWidthIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <line x1="4" y1="6" x2="20" y2="6" strokeWidth="1" />
    <line x1="4" y1="12" x2="20" y2="12" strokeWidth="3" />
    <line x1="4" y1="18" x2="20" y2="18" strokeWidth="5" />
  </svg>
);

/** Seam position icon */
export const SeamIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="4" y="4" width="16" height="16" rx="2" />
    <circle cx="20" cy="12" r="3" fill="currentColor" className="text-pf-accent-2" />
    <path d="M17 12h-5" strokeDasharray="2 1" />
  </svg>
);

/** Speed icon */
export const SpeedIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="14" r="8" />
    <path d="M12 14l4-6" strokeLinecap="round" />
    <circle cx="12" cy="14" r="2" fill="currentColor" />
  </svg>
);

/** Temperature icon */
export const TemperatureIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z" />
    <circle cx="11.5" cy="17.5" r="2" fill="currentColor" />
  </svg>
);

/** Precision/compensation icon */
export const PrecisionIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="9" />
    <circle cx="12" cy="12" r="5" />
    <circle cx="12" cy="12" r="1" fill="currentColor" />
    <line x1="12" y1="2" x2="12" y2="5" />
    <line x1="12" y1="19" x2="12" y2="22" />
    <line x1="2" y1="12" x2="5" y2="12" />
    <line x1="19" y1="12" x2="22" y2="12" />
  </svg>
);

/** Help/question mark icon */
export const HelpIcon: React.FC<IconProps> = ({ className = 'w-4 h-4' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="10" />
    <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
    <circle cx="12" cy="17" r="1" fill="currentColor" />
  </svg>
);
