/**
 * Slicer Toolbar Icons — Official OrcaSlicer SVG assets
 * Source: https://github.com/SoftFever/OrcaSlicer/tree/main/resources/images
 */
import React from 'react';

import toolbarOpenSvg from '@/assets/orcaslicer/toolbar_open.svg';
import toolbarArrangeSvg from '@/assets/orcaslicer/toolbar_arrange.svg';
import toolbarOrientSvg from '@/assets/orcaslicer/toolbar_orient.svg';
import toolbarFlattenSvg from '@/assets/orcaslicer/toolbar_flatten.svg';
import toolbarMoveSvg from '@/assets/orcaslicer/toolbar_move.svg';
import toolbarRotateSvg from '@/assets/orcaslicer/toolbar_rotate.svg';
import toolbarScaleSvg from '@/assets/orcaslicer/toolbar_scale.svg';
import toolbarCutSvg from '@/assets/orcaslicer/toolbar_cut.svg';
import toolbarMeasureSvg from '@/assets/orcaslicer/toolbar_measure.svg';
import toolbarSupportSvg from '@/assets/orcaslicer/toolbar_support.svg';
import toolbarSeamSvg from '@/assets/orcaslicer/toolbar_seam.svg';
import toolbarAssemblySvg from '@/assets/orcaslicer/toolbar_assembly.svg';
import toolbarSettingsSvg from '@/assets/orcaslicer/toolbar_settings.svg';
import toolbarLayerHeightSvg from '@/assets/orcaslicer/toolbar_variable_layer_height.svg';
import splitObjectsSvg from '@/assets/orcaslicer/split_objects.svg';
import undoToolbarSvg from '@/assets/orcaslicer/undo_toolbar.svg';

interface IconProps {
  className?: string;
}

function OrcaIcon({ src, alt, className = 'w-5 h-5' }: { src: string; alt: string; className?: string }) {
  return <img src={src} alt={alt} className={className} draggable={false} />;
}

export const AddModelIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarOpenSvg} alt="Add model" className={className} />
);

export const ArrangeIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarArrangeSvg} alt="Arrange" className={className} />
);

export const OrientIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarOrientSvg} alt="Orient" className={className} />
);

export const LayFlatIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarFlattenSvg} alt="Lay flat" className={className} />
);

export const SplitIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={splitObjectsSvg} alt="Split" className={className} />
);

export const CutIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarCutSvg} alt="Cut" className={className} />
);

export const MeasureIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarMeasureSvg} alt="Measure" className={className} />
);

export const SupportPaintIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarSupportSvg} alt="Support paint" className={className} />
);

export const SeamPaintIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarSeamSvg} alt="Seam paint" className={className} />
);

export const UndoIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={undoToolbarSvg} alt="Undo" className={className} />
);

export const RedoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <img src={undoToolbarSvg} alt="Redo" className={className} draggable={false} style={{ transform: 'scaleX(-1)' }} />
);

export const AssemblyIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarAssemblySvg} alt="Assembly" className={className} />
);

export const SettingsProfilesIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarSettingsSvg} alt="Settings" className={className} />
);

export const KeyboardIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <rect x="2" y="4" width="20" height="16" rx="2" fill="#009688" fillOpacity="0.08" stroke="#009688" strokeWidth="1.5" />
    <rect x="5" y="7" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="9" y="7" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="13" y="7" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="17" y="7" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="5" y="11" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="9" y="11" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="13" y="11" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="17" y="11" width="2" height="2" rx="0.5" fill="#009688" fillOpacity="0.4" />
    <rect x="8" y="15" width="8" height="2" rx="0.5" fill="#009688" fillOpacity="0.3" stroke="#009688" strokeWidth="0.5" />
  </svg>
);

export const MoveToolIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarMoveSvg} alt="Move" className={className} />
);

export const RotateToolIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarRotateSvg} alt="Rotate" className={className} />
);

export const ScaleToolIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarScaleSvg} alt="Scale" className={className} />
);

export const LayersViewIcon: React.FC<IconProps> = ({ className }) => (
  <OrcaIcon src={toolbarLayerHeightSvg} alt="Layers" className={className} />
);

export const InfoIcon: React.FC<IconProps> = ({ className = 'w-5 h-5' }) => (
  <svg className={className} viewBox="0 0 24 24" fill="none">
    <circle cx="12" cy="12" r="9" fill="#009688" fillOpacity="0.1" stroke="#009688" strokeWidth="1.5" />
    <line x1="12" y1="16" x2="12" y2="12" stroke="#009688" strokeWidth="2" strokeLinecap="round" />
    <circle cx="12" cy="8" r="1" fill="#009688" />
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
