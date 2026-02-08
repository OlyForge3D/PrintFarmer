/**
 * Slicer Toolbar Icons
 * SVG icons matching OrcaSlicer's toolbar style
 */
import React from 'react';

interface IconProps {
  className?: string;
}

// Add Model (plus icon)
export const AddModelIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <line x1="12" y1="8" x2="12" y2="16" />
    <line x1="8" y1="12" x2="16" y2="12" />
  </svg>
);

// Arrange Models (grid icon)
export const ArrangeIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="7" height="7" rx="1" />
    <rect x="14" y="3" width="7" height="7" rx="1" />
    <rect x="3" y="14" width="7" height="7" rx="1" />
    <rect x="14" y="14" width="7" height="7" rx="1" />
  </svg>
);

// Orient Model (rotate/align icon)
export const OrientIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M3 12h18M12 3v18" />
    <polygon points="12,3 8,8 16,8" fill="currentColor" />
    <polygon points="12,21 8,16 16,16" fill="currentColor" />
    <polygon points="3,12 8,8 8,16" fill="currentColor" />
    <polygon points="21,12 16,8 16,16" fill="currentColor" />
  </svg>
);

// Lay Flat icon
export const LayFlatIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="4" y="16" width="16" height="4" rx="1" />
    <polygon points="12,4 6,12 18,12" fill="none" stroke="currentColor" />
    <line x1="12" y1="12" x2="12" y2="16" />
  </svg>
);

// Split Model icon
export const SplitIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="7" height="18" rx="1" />
    <rect x="14" y="3" width="7" height="18" rx="1" />
    <line x1="12" y1="6" x2="12" y2="18" strokeDasharray="2,2" />
  </svg>
);

// Cut/Slice Model icon
export const CutIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="5" y="3" width="14" height="18" rx="1" fill="none" />
    <line x1="3" y1="12" x2="21" y2="12" strokeWidth="3" />
  </svg>
);

// Measure icon
export const MeasureIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <line x1="4" y1="4" x2="20" y2="4" />
    <line x1="4" y1="4" x2="4" y2="8" />
    <line x1="20" y1="4" x2="20" y2="8" />
    <line x1="4" y1="20" x2="20" y2="20" />
    <line x1="4" y1="16" x2="4" y2="20" />
    <line x1="20" y1="16" x2="20" y2="20" />
    <text x="12" y="13" textAnchor="middle" fill="currentColor" fontSize="8" fontWeight="bold">mm</text>
  </svg>
);

// Support paint icon
export const SupportPaintIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M12 2C7.5 2 4 5 4 9c0 3 1.5 5.5 4 7v4h8v-4c2.5-1.5 4-4 4-7 0-4-3.5-7-8-7z" />
    <line x1="8" y1="20" x2="16" y2="20" />
    <line x1="9" y1="22" x2="15" y2="22" />
    <circle cx="12" cy="9" r="2" fill="currentColor" />
  </svg>
);

// Seam paint icon
export const SeamPaintIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polygon points="12,2 4,22 12,18 20,22" />
    <line x1="12" y1="2" x2="12" y2="12" strokeDasharray="2,2" />
  </svg>
);

// Undo icon
export const UndoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M3 10h10c4.4 0 8 3.6 8 8v0" />
    <polyline points="8,15 3,10 8,5" />
  </svg>
);

// Redo icon
export const RedoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M21 10H11c-4.4 0-8 3.6-8 8v0" />
    <polyline points="16,15 21,10 16,5" />
  </svg>
);

// Assembly View icon (linked objects)
export const AssemblyIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="6" cy="6" r="3" />
    <circle cx="18" cy="6" r="3" />
    <circle cx="12" cy="18" r="3" />
    <line x1="6" y1="9" x2="12" y2="15" />
    <line x1="18" y1="9" x2="12" y2="15" />
  </svg>
);

// Settings & Profiles icon (gear with sliders)
export const SettingsProfilesIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
  </svg>
);

// Keyboard shortcuts icon
export const KeyboardIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="2" y="4" width="20" height="16" rx="2" />
    <line x1="6" y1="8" x2="6" y2="8" />
    <line x1="10" y1="8" x2="10" y2="8" />
    <line x1="14" y1="8" x2="14" y2="8" />
    <line x1="18" y1="8" x2="18" y2="8" />
    <line x1="6" y1="12" x2="6" y2="12" />
    <line x1="10" y1="12" x2="10" y2="12" />
    <line x1="14" y1="12" x2="14" y2="12" />
    <line x1="18" y1="12" x2="18" y2="12" />
    <rect x="8" y="16" width="8" height="2" rx="0.5" />
  </svg>
);

// Move tool icon
export const MoveToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <line x1="12" y1="3" x2="12" y2="21" />
    <line x1="3" y1="12" x2="21" y2="12" />
    <polyline points="8,7 12,3 16,7" />
    <polyline points="8,17 12,21 16,17" />
    <polyline points="7,8 3,12 7,16" />
    <polyline points="17,8 21,12 17,16" />
  </svg>
);

// Rotate tool icon
export const RotateToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <path d="M21 12a9 9 0 1 1-9-9c2.5 0 4.8 1 6.5 2.6" />
    <polyline points="21,3 21,9 15,9" />
  </svg>
);

// Scale tool icon
export const ScaleToolIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <rect x="3" y="3" width="6" height="6" rx="1" />
    <rect x="15" y="15" width="6" height="6" rx="1" />
    <line x1="9" y1="6" x2="15" y2="6" strokeDasharray="2,2" />
    <line x1="6" y1="9" x2="6" y2="15" strokeDasharray="2,2" />
    <line x1="18" y1="9" x2="18" y2="15" strokeDasharray="2,2" />
    <line x1="9" y1="18" x2="15" y2="18" strokeDasharray="2,2" />
  </svg>
);

// Layers view icon
export const LayersViewIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <polygon points="12,2 2,7 12,12 22,7" />
    <polyline points="2,17 12,22 22,17" />
    <polyline points="2,12 12,17 22,12" />
  </svg>
);

// Info icon
export const InfoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
    <circle cx="12" cy="12" r="10" />
    <line x1="12" y1="16" x2="12" y2="12" />
    <line x1="12" y1="8" x2="12.01" y2="8" />
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
