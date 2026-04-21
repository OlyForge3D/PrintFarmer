/**
 * Infill pattern SVG icons showing actual OrcaSlicer toolpath cross-sections.
 *
 * Two-layer design matching OrcaSlicer visual language:
 * - Gray (#949494, 75% opacity): alternate/background layer toolpaths
 * - Teal (#009688): current/primary layer toolpaths
 * - 16x16 viewBox with rounded-rect border
 */
/* eslint-disable react-refresh/only-export-components -- Icon factory pattern */
import React from 'react';

const S = 16;
const TEAL = '#009688';
const GRAY = '#949494';
const VB = '0 0 16 16';

interface IconProps { className?: string }

/** Two-layer icon: gray alternate layer + teal current layer */
const dual = (grayD: string, tealD: string): React.FC<IconProps> => {
  const Icon: React.FC<IconProps> = ({ className }) => (
    <svg className={className} width={S} height={S} viewBox={VB}
         fill="none" xmlns="http://www.w3.org/2000/svg">
      <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
            stroke={GRAY} strokeWidth="0.6" fill="none" />
      {grayD && (
        <path d={grayD} stroke={GRAY} strokeWidth="0.8"
              strokeLinecap="round" strokeLinejoin="round" opacity="0.75" />
      )}
      <path d={tealD} stroke={TEAL} strokeWidth="0.8"
            strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
  Icon.displayName = 'InfillIcon';
  return Icon;
};

/** Single-layer icon: teal only */
const mono = (tealD: string): React.FC<IconProps> => dual('', tealD);

// -- Rectilinear family: cross-hatch toolpaths --------------------------

export const RectilinearIcon = dual(
  'M10,15L1,6 M15,10L6,1 M5,15L1,11 M15,5L11,1 M1.4,1.4L14.6,14.6',
  'M6,15L15,6 M1,10L10,1 M11,15L15,11 M1,5L5,1 M14.6,1.4L1.4,14.6'
);

export const AlignedRectilinearIcon = dual(
  'M8,15L1,8 M8,1L15,8 M1.4,1.4L14.6,14.6',
  'M11,15L1,5 M4,15L1,12 M10,1L15,6 M4,1L15,12'
);

export const MonotonicIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <polyline points="14,2 14,5 11,2 6,2 14,10 14,14 2,2 2,6 10,14 5,14 2,11 2,14"
              stroke={TEAL} strokeWidth="0.8" strokeLinecap="square"
              strokeLinejoin="round" fill="none" />
  </svg>
);
MonotonicIcon.displayName = 'MonotonicIcon';

export const MonotonicLineIcon = mono(
  'M10,15L1,6 M15,10L6,1 M5,15L1,11 M15,5L11,1 M14.6,1.4L1.4,14.6'
);

// -- Grid ---------------------------------------------------------------

export const GridIcon = dual(
  'M8,15L1,8 M8,1L15,8 M1.4,1.4L14.6,14.6',
  'M1,8L8,1 M15,8L8,15 M14.6,1.4L1.4,14.6'
);

// -- Line ---------------------------------------------------------------

export const LineIcon = dual(
  'M10,1L15,4.5 M4,1L15,12 M15,2L1,13 M10,15L1,8',
  'M1,4.5L6,1 M1,9L12,1 M1,12L15,1 M6,15L15,5 M11,15L15,11'
);

// -- Concentric ---------------------------------------------------------

export const ConcentricIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <path d="M12.5,14C14,12 14.5,9 13,6C11,3 7.5,2.5 4.5,4.5C2.5,6 2,9 3.5,12C5,14.5 8,15 11,14"
          stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round" />
    <path d="M10.5,12C11.5,10.5 11.5,8.5 10.5,7C9.5,5.5 7.5,5.5 6.5,6.5C5.5,7.5 5.5,9.5 6.5,11C7.5,12 9,12.5 10.5,12"
          stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round" />
    <ellipse cx="8.5" cy="9" rx="1" ry="0.7" transform="rotate(-15 8.5 9)"
             stroke={TEAL} strokeWidth="0.7" />
  </svg>
);
ConcentricIcon.displayName = 'ConcentricIcon';

// -- Triangles ----------------------------------------------------------

export const TrianglesIcon = mono(
  'M1,5L15,5 M1,11L15,11 M6,1L1,10 M10,15L6,1 M10,1L15,9 M4,15L13,1 M1,12L4,15 M15,7L12,1'
);

// -- Tri-Hexagon --------------------------------------------------------

export const TriHexagonIcon = mono(
  'M5,1L5,15 M11,1L11,15 M1,4L5,1.5 M5,1.5L11,4.5 M11,4.5L15,2 M1,9L5,6.5 M5,6.5L11,9 M11,9L15,6.5 M1,14L5,11.5 M5,11.5L11,14 M11,14L15,11.5'
);

// -- Stars (compatibility) ----------------------------------------------

export const StarsIcon = mono(
  'M8,3L9.5,6.5L13,8L9.5,9.5L8,13L6.5,9.5L3,8L6.5,6.5Z'
);

// -- Cubic family -------------------------------------------------------

export const CubicIcon = dual(
  'M5,1L5,5 M11,1L11,5 M5,5L8,7 M11,5L8,7 M8,7L8,12 M5,12L8,14 M11,12L8,14 M5,12L5,15 M11,12L11,15 M15,7L11,5 M1,7L5,5 M15,13L11,12 M1,13L5,12',
  'M1,5L15,5 M1,12L15,12 M5,5L1,8 M11,5L15,8 M5,12L1,15 M11,12L15,15'
);

export const AdaptiveCubicIcon = dual(
  'M5,1L5,5 M11,1L11,5 M5,5L8,7 M11,5L8,7 M8,7L8,12 M5,12L8,14 M11,12L8,14 M5,15L5,12 M11,15L11,12 M15,7L11,5 M1,7L5,5 M11,12L13.5,13.5 M13.5,13.5L13.5,15 M10,15L10,13',
  'M1,5L15,5 M1,12L15,12 M5,5L1,8 M11,5L15,8 M5,12L1,15 M11,12L15,15 M10,12L13.5,14'
);

export const QuarterCubicIcon = dual(
  'M2,2L5,5 M7,7L10,10 M11,1L15,5 M2,11L5,15 M11,11L14.5,14.5',
  'M7,1L15,9 M15,11L5,1 M1,9L9,1 M11,1L1,11 M9,15L1,7 M1,5L11,15 M7,15L15,7'
);

export const SupportCubicIcon = dual(
  'M5,1L5,5 M11,1L11,5 M5,5L8,7 M11,5L8,7 M8,7L8,12 M5,12L8,14 M11,12L8,14 M5,15L5,12 M11,15L11,12 M8,3L11,5 M8,3L5,5',
  'M1,5L15,5 M1,12L15,12 M11,5L15,2 M5,5L1,2 M11,12L15,9 M5,12L1,9'
);

// -- Lightning ----------------------------------------------------------

export const LightningIcon = mono(
  'M8,14L8,7 M8,7L4,4 M8,7L12,3 M4,4L2,2 M4,4L5,1.5 M12,3L14,1.5 M12,3L11,1 M8,10L5,12 M8,10L12,11'
);

// -- Honeycomb family ---------------------------------------------------

export const HoneycombIcon = mono(
  'M4,1L2.5,4L4,7L2.5,10L4,13L2.5,15 M7.5,1L6,4L7.5,7L6,10L7.5,13 M11,1L9.5,4L11,7L9.5,10L11,13L9.5,15 M14.5,1L13,4L14.5,7L13,10L14.5,13 M2.5,4L15,4 M2.5,10L15,10'
);

export const Honeycomb3DIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round"
       strokeLinejoin="round" opacity="0.75">
      <path d="M1,7L7,1 M15,7L9,1 M9,15L15,9 M7,15L1,9 M14,2L2,14 M2,2L14,14" />
    </g>
    <g stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3,7L1,5 M7,3L5,1 M9,3L11,1 M13,7L15,5 M13,9L15,11 M9,13L11,15 M7,13L5,15 M3,9L1,11" />
      <rect x="3" y="7" width="2" height="2" />
      <rect x="7" y="3" width="2" height="2" />
      <rect x="11" y="7" width="2" height="2" />
      <rect x="7" y="11" width="2" height="2" />
    </g>
  </svg>
);
Honeycomb3DIcon.displayName = 'Honeycomb3DIcon';

export const LateralHoneycombIcon = mono(
  'M1,4L4,2.5L7,4L10,2.5L13,4L15,2.5 M1,7.5L4,6L7,7.5L10,6L13,7.5 M1,11L4,9.5L7,11L10,9.5L13,11L15,9.5 M1,14.5L4,13L7,14.5L10,13L13,14.5 M4,2.5L4,15 M10,1L10,13'
);

// -- Lateral Lattice ----------------------------------------------------

export const LateralLatticeIcon = dual(
  'M1,5L5,1 M1,11L11,1 M5,15L15,5 M11,15L15,11 M1,3.5L3.5,1 M13.5,15L15,13.5',
  'M5,1L1,5 M11,1L1,11 M15,5L5,15 M15,11L11,15 M3.5,1L1,3.5 M15,13.5L13.5,15'
);

// -- Cross patterns -----------------------------------------------------

export const CrossHatchIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round"
       strokeLinejoin="round" opacity="0.75">
      <path d="M6,15L15,6 M1,10L10,1 M11,15L15,11 M1,5L5,1" />
      <rect x="7" y="7" width="1.8" height="1.8" transform="rotate(45 7.9 7.9)" />
      <rect x="3.5" y="10.5" width="1.4" height="1.4" transform="rotate(45 4.2 11.2)" />
      <rect x="10.5" y="3.5" width="1.4" height="1.4" transform="rotate(45 11.2 4.2)" />
    </g>
    <path d="M10,15L1,6 M15,10L6,1 M5,15L1,11 M15,5L11,1"
          stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);
CrossHatchIcon.displayName = 'CrossHatchIcon';

export const ZigZagIcon = dual(
  'M6,15L15,6 M1,10L10,1 M11,15L15,11 M1,5L5,1 M14.6,1.4L1.4,14.6',
  'M10,15L1,6 M15,10L6,1 M5,15L1,11 M15,5L11,1 M1.4,1.4L14.6,14.6 M1,6L1,10 M6,1L10,1 M15,6L15,10 M6,15L10,15'
);

export const CrossZagIcon = dual(
  'M1,8L8,1 M8,15L15,8 M14,2L2,14 M1,11L11,1 M5,15L15,5',
  'M2,14L2,6L6,6L15,6L15,2L2,2L11,11L2,14L15,14L15,11'
);

export const LockedZagIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round"
       strokeLinejoin="round" opacity="0.75">
      <rect x="2" y="2" width="12" height="12" rx="0.5" />
      <path d="M3,8.5L8.5,3 M8,15L15,8 M3,12L8,15 M12,3L15,8" />
    </g>
    <g stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3.5" y="3.5" width="9" height="9" />
      <path d="M8.5,13L13,8.5 M3.5,8L8,3.5 M3.5,5L5,3.5 M12.5,13L13,12.5" />
      <polyline points="2.5,3.5 4,2 5.5,2 4,3.5" />
      <polyline points="6,3.5 7.5,2 9,2 7.5,3.5" />
      <polyline points="9.5,3.5 11,2 12.5,2 11,3.5" />
      <polyline points="13.5,12.5 12,14 10.5,14 12,12.5" />
      <polyline points="10,12.5 8.5,14 7,14 8.5,12.5" />
      <polyline points="6.5,12.5 5,14 3.5,14 5,12.5" />
    </g>
  </svg>
);
LockedZagIcon.displayName = 'LockedZagIcon';

// -- Gyroid -------------------------------------------------------------

export const GyroidIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round" opacity="0.75">
      <path d="M3,1 C5,3 1,5 3,8 C5,11 1,13 3,15" />
      <path d="M8,1 C10,3 6,5 8,8 C10,11 6,13 8,15" />
      <path d="M13,1 C15,3 11,5 13,8 C15,11 11,13 13,15" />
    </g>
    <g stroke={TEAL} strokeWidth="0.8" strokeLinecap="round">
      <path d="M1,3 C3,5 5,1 8,3 C11,5 13,1 15,3" />
      <path d="M1,8 C3,10 5,6 8,8 C11,10 13,6 15,8" />
      <path d="M1,13 C3,15 5,11 8,13 C11,15 13,11 15,13" />
    </g>
  </svg>
);
GyroidIcon.displayName = 'GyroidIcon';

// -- Space-filling curves -----------------------------------------------

export const HilbertCurveIcon = mono(
  'M13,14.5L11.5,13L13,11 M13,11L8.5,6.5L6.5,8.5L8,10L5.5,12.5L1.5,8.5 M1.5,7L4,4.5L7,7.5L9,5.5L5.5,2L4,3.5L1.5,1.5 M10.5,1.5L14.5,5.5L15,5 M5,14.5L1.5,11 M14.5,7L11,3.5'
);

export const ArchimedeanChordsIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <path d="M1,4C2,1.5 6,0.5 9.5,1.5C13,2.5 15,5.5 14.5,9C14,12 11.5,14 8.5,13.5C6,13 4.5,11 5,8.5C5.5,6.5 7,5.5 8.5,6C10,6.5 10.5,8 10,9.5C9.5,10.5 8.5,10.5 8,10"
          stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" />
  </svg>
);
ArchimedeanChordsIcon.displayName = 'ArchimedeanChordsIcon';

export const OctagramSpiralIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <polyline points="6,7 5,7 7,9 5,11 7,11 7,13 9,11 11,13 11,11 13,11 11,9 13,7 11,7 11,5 9,7 5,3 5,5 1,5 1,6 4,9 1,12 1,13 5,13 5,15 7,15 9,12 11,15 13,15 13,13 15,13 15,11 12,9 15,7 15,5 13,5 13,1 11,1 9,4 6,1 3,1 3,3 1,3"
              stroke={TEAL} strokeWidth="0.7" strokeLinecap="round"
              strokeLinejoin="round" fill="none" />
  </svg>
);
OctagramSpiralIcon.displayName = 'OctagramSpiralIcon';

// -- TPMS surfaces ------------------------------------------------------

export const TpmsDIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round"
       strokeLinejoin="round" opacity="0.75">
      <path d="M10,15L1,6 M15,10L6,1 M5,15L1,11 M15,5L11,1 M1.4,1.4L14.6,14.6" />
    </g>
    <g stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M1,14C1,12 3,10 5,10C7,10 7,8 7,6C7,4 9,2 11,2C13,2 15,3 15,5" />
      <path d="M15,2C15,3 14,4 13,4C12,4 11,3 11,2" />
      <path d="M15,12C15,10 13,10 12,10C10,10 9,12 9,14C9,15 7,15 6,15" />
      <path d="M1,10C1,9 2,7 4,7C5,7 6,5 5,4" />
    </g>
  </svg>
);
TpmsDIcon.displayName = 'TpmsDIcon';

export const TpmsFkIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={VB}
       fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5"
          stroke={GRAY} strokeWidth="0.6" fill="none" />
    <g stroke={GRAY} strokeWidth="0.8" strokeLinecap="round"
       strokeLinejoin="round" opacity="0.75">
      <circle cx="8" cy="8" r="1.7" />
      <circle cx="3" cy="13" r="1.7" />
      <circle cx="13" cy="3" r="1.7" />
      <path d="M7,15C7.5,14 8,14 8.5,15" />
      <path d="M15,8C14,7.5 14,7 15,6.5" />
      <path d="M1,7.5C2,8 2,8.5 1,9" />
      <path d="M3,1C3,1.5 2.5,2 2,2" />
    </g>
    <g stroke={TEAL} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M10,1L10,3C10,4 9,5 8,5L6,5C5,5 4,6 4,7L4,10" />
      <path d="M12,15L12,13C12,12 11,11 10,11L8,11C7,11 6,12 6,13L6,15" />
      <path d="M15,6L13,6C12,6 11,7 11,8L11,10C11,11 12,12 13,12L15,12" />
      <path d="M5,1L5,3C5,4 4,5 3,5L1,5" />
    </g>
  </svg>
);
TpmsFkIcon.displayName = 'TpmsFkIcon';

// -- Lookup map ---------------------------------------------------------

export const INFILL_ICON_MAP: Record<string, React.FC<IconProps>> = {
  rectilinear: RectilinearIcon,
  alignedrectilinear: AlignedRectilinearIcon,
  monotonic: MonotonicIcon,
  monotonicline: MonotonicLineIcon,
  concentric: ConcentricIcon,
  grid: GridIcon,
  triangles: TrianglesIcon,
  'tri-hexagon': TriHexagonIcon,
  stars: StarsIcon,
  cubic: CubicIcon,
  adaptivecubic: AdaptiveCubicIcon,
  quartercubic: QuarterCubicIcon,
  supportcubic: SupportCubicIcon,
  lightning: LightningIcon,
  line: LineIcon,
  honeycomb: HoneycombIcon,
  '3dhoneycomb': Honeycomb3DIcon,
  'lateral-honeycomb': LateralHoneycombIcon,
  'lateral-lattice': LateralLatticeIcon,
  crosshatch: CrossHatchIcon,
  zigzag: ZigZagIcon,
  crosszag: CrossZagIcon,
  lockedzag: LockedZagIcon,
  gyroid: GyroidIcon,
  hilbertcurve: HilbertCurveIcon,
  archimedeanchords: ArchimedeanChordsIcon,
  octagramspiral: OctagramSpiralIcon,
  tpmsd: TpmsDIcon,
  tpmsfk: TpmsFkIcon,
};

export function getInfillIcon(value: string, className?: string): React.ReactNode | undefined {
  const Icon = INFILL_ICON_MAP[value];
  return Icon ? <Icon className={className} /> : undefined;
}
