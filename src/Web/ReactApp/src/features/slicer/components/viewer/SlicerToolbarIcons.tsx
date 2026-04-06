/**
 * Slicer Toolbar Icons
 * OrcaSlicer-style toolbar icons using the signature green color palette
 */
import React from 'react';

interface IconProps {
  className?: string;
}

const ORCA_GREEN = '#00AE42';
const ORCA_GREEN_DARK = '#009639';

// Add Model — green plus in a rounded frame
export const AddModelIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="3" y="3" width="18" height="18" rx="3" stroke={ORCA_GREEN} strokeWidth="1.5" fill={ORCA_GREEN} fillOpacity="0.1" />
    <line x1="12" y1="7.5" x2="12" y2="16.5" stroke={ORCA_GREEN} strokeWidth="2.5" strokeLinecap="round" />
    <line x1="7.5" y1="12" x2="16.5" y2="12" stroke={ORCA_GREEN} strokeWidth="2.5" strokeLinecap="round" />
  </svg>
);

// Arrange Models — grid of objects on bed
export const ArrangeIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="3" y="3" width="7.5" height="7.5" rx="1.5" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <rect x="13.5" y="3" width="7.5" height="7.5" rx="1.5" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <rect x="3" y="13.5" width="7.5" height="7.5" rx="1.5" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <rect x="13.5" y="13.5" width="7.5" height="7.5" rx="1.5" fill={ORCA_GREEN} fillOpacity="0.15" stroke={ORCA_GREEN} strokeWidth="1.5" strokeDasharray="3 2" />
  </svg>
);

// Orient Model — cube with circular auto-orient arrow
export const OrientIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M12 5L6 8.5v7L12 19l6-3.5v-7L12 5z" fill={ORCA_GREEN} fillOpacity="0.15" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinejoin="round" />
    <path d="M12 12v7" stroke={ORCA_GREEN} strokeWidth="1" strokeOpacity="0.5" />
    <path d="M6 8.5L12 12l6-3.5" stroke={ORCA_GREEN} strokeWidth="1" strokeOpacity="0.5" />
    <path d="M19 4a7 7 0 0 1 0 5" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeLinecap="round" />
    <polyline points="19,3 19,6 22,6" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Lay Flat — object settling onto bed surface
export const LayFlatIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="3" y="18" width="18" height="3" rx="1" fill={ORCA_GREEN} fillOpacity="0.25" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <path d="M8 7l4-4 4 4" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    <polygon points="12,8 7,15 17,15" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinejoin="round" />
    <line x1="12" y1="15" x2="12" y2="18" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" strokeDasharray="2 2" />
  </svg>
);

// Split Model — object split into two halves
export const SplitIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M4 4h6v16H4z" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" rx="1" />
    <path d="M14 4h6v16h-6z" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" rx="1" />
    <line x1="12" y1="3" x2="12" y2="21" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeDasharray="3 2" strokeLinecap="round" />
  </svg>
);

// Cut/Slice — cutting plane through object
export const CutIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="6" y="3" width="12" height="18" rx="2" fill={ORCA_GREEN} fillOpacity="0.1" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <line x1="2" y1="12" x2="22" y2="12" stroke={ORCA_GREEN_DARK} strokeWidth="2" strokeLinecap="round" />
    <circle cx="5" cy="12" r="1.5" fill={ORCA_GREEN_DARK} />
    <circle cx="19" cy="12" r="1.5" fill={ORCA_GREEN_DARK} />
  </svg>
);

// Measure — ruler with dimension markers
export const MeasureIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <line x1="4" y1="6" x2="20" y2="6" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="4" y1="4" x2="4" y2="8" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="20" y1="4" x2="20" y2="8" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="9" y1="5" x2="9" y2="7" stroke={ORCA_GREEN} strokeWidth="1" strokeLinecap="round" />
    <line x1="12" y1="4.5" x2="12" y2="7.5" stroke={ORCA_GREEN} strokeWidth="1" strokeLinecap="round" />
    <line x1="15" y1="5" x2="15" y2="7" stroke={ORCA_GREEN} strokeWidth="1" strokeLinecap="round" />
    <text x="12" y="16" textAnchor="middle" fill={ORCA_GREEN_DARK} fontSize="7" fontWeight="bold" fontFamily="system-ui">mm</text>
    <line x1="6" y1="19" x2="18" y2="19" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="6" y1="17.5" x2="6" y2="20.5" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="18" y1="17.5" x2="18" y2="20.5" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

// Support paint — brush with support pillars
export const SupportPaintIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M4 14l3-10h2l3 10" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinejoin="round" fill={ORCA_GREEN} fillOpacity="0.15" />
    <rect x="3" y="14" width="10" height="2" rx="1" fill={ORCA_GREEN} fillOpacity="0.3" />
    <line x1="5" y1="16" x2="5" y2="20" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="8" y1="16" x2="8" y2="20" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="11" y1="16" x2="11" y2="20" stroke={ORCA_GREEN_DARK} strokeWidth="1.5" strokeLinecap="round" />
    <rect x="3" y="20" width="10" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1" />
    <path d="M16 4l2 2-6 6-2-2 6-6z" fill={ORCA_GREEN_DARK} fillOpacity="0.8" stroke={ORCA_GREEN_DARK} strokeWidth="1" />
    <path d="M18 6l2-2 1 1-2 2-1-1z" fill={ORCA_GREEN} />
  </svg>
);

// Seam paint — brush marking seam line on object
export const SeamPaintIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M8 4h8l3 6-3 6H8l-3-6 3-6z" fill={ORCA_GREEN} fillOpacity="0.15" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinejoin="round" />
    <line x1="12" y1="4" x2="12" y2="16" stroke={ORCA_GREEN_DARK} strokeWidth="2" strokeLinecap="round" strokeDasharray="2 2" />
    <path d="M14 17l2 2-4 4-2-2 4-4z" fill={ORCA_GREEN_DARK} fillOpacity="0.8" stroke={ORCA_GREEN_DARK} strokeWidth="1" />
    <circle cx="12" cy="4" r="1.5" fill={ORCA_GREEN_DARK} />
  </svg>
);

// Undo — curved arrow left
export const UndoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M4 10h10a6 6 0 0 1 6 6v2" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    <polyline points="8,14 4,10 8,6" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Redo — curved arrow right
export const RedoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M20 10H10a6 6 0 0 0-6 6v2" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    <polyline points="16,14 20,10 16,6" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Assembly View — linked object nodes
export const AssemblyIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <circle cx="6" cy="6" r="3" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <circle cx="18" cy="6" r="3" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <circle cx="12" cy="18" r="3" fill={ORCA_GREEN} fillOpacity="0.3" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <line x1="7.5" y1="8.5" x2="10.5" y2="15.5" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="16.5" y1="8.5" x2="13.5" y2="15.5" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

// Settings & Profiles — gear icon
export const SettingsProfilesIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <circle cx="12" cy="12" r="3" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" stroke={ORCA_GREEN} strokeWidth="1.5" />
  </svg>
);

// Keyboard shortcuts
export const KeyboardIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="2" y="4" width="20" height="16" rx="2" fill={ORCA_GREEN} fillOpacity="0.08" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <rect x="5" y="7" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="9" y="7" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="13" y="7" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="17" y="7" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="5" y="11" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="9" y="11" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="13" y="11" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="17" y="11" width="2" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.4" />
    <rect x="8" y="15" width="8" height="2" rx="0.5" fill={ORCA_GREEN} fillOpacity="0.3" stroke={ORCA_GREEN} strokeWidth="0.5" />
  </svg>
);

// Move tool — four-way arrows
export const MoveToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <line x1="12" y1="3" x2="12" y2="21" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <line x1="3" y1="12" x2="21" y2="12" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" />
    <polyline points="8,7 12,3 16,7" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    <polyline points="8,17 12,21 16,17" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    <polyline points="7,8 3,12 7,16" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    <polyline points="17,8 21,12 17,16" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Rotate tool — circular rotation arrow
export const RotateToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <path d="M21 12a9 9 0 1 1-3-6.7" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" />
    <polyline points="21,3 21,9 15,9" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Scale tool — resize corners
export const ScaleToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="4" y="4" width="7" height="7" rx="1" fill={ORCA_GREEN} fillOpacity="0.2" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <rect x="13" y="13" width="7" height="7" rx="1" fill={ORCA_GREEN} fillOpacity="0.3" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <line x1="11" y1="7.5" x2="16.5" y2="13" stroke={ORCA_GREEN} strokeWidth="1.5" strokeDasharray="2 2" strokeLinecap="round" />
    <polyline points="14,10 17,13 14,13" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Layers view — stacked layers
export const LayersViewIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <polygon points="12,3 3,8 12,13 21,8" fill={ORCA_GREEN} fillOpacity="0.25" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinejoin="round" />
    <polyline points="3,12 12,17 21,12" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" fill="none" />
    <polyline points="3,16 12,21 21,16" stroke={ORCA_GREEN} strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" fill="none" />
  </svg>
);

// Info icon
export const InfoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <circle cx="12" cy="12" r="9" fill={ORCA_GREEN} fillOpacity="0.1" stroke={ORCA_GREEN} strokeWidth="1.5" />
    <line x1="12" y1="16" x2="12" y2="12" stroke={ORCA_GREEN} strokeWidth="2" strokeLinecap="round" />
    <circle cx="12" cy="8" r="1" fill={ORCA_GREEN} />
  </svg>
);

export default {
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
  MoveToolIcon,
  RotateToolIcon,
  ScaleToolIcon,
  LayersViewIcon,
  InfoIcon,
};
