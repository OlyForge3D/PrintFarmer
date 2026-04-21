/**
 * Infill pattern SVG icons matching OrcaSlicer's visual style.
 *
 * Each icon is a 16×16 React component showing a miniature preview
 * of the fill geometry. Colour: teal (#009688) pattern on transparent bg.
 */
/* eslint-disable react-refresh/only-export-components -- Icon factory pattern; all exports are valid React components */
import React from 'react';

const S = 16;           // viewBox size
const C = '#009688';    // pattern colour (OrcaSlicer teal)
const W = 1.2;          // default stroke width

interface IconProps { className?: string }

const box = (d: string, extra?: React.ReactNode) => {
  const Icon: React.FC<IconProps> = ({ className }) => (
    <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
         xmlns="http://www.w3.org/2000/svg">
      <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
      <g stroke={C} strokeWidth={W} strokeLinecap="round" strokeLinejoin="round">
        <path d={d} />
      </g>
      {extra}
    </svg>
  );
  Icon.displayName = 'InfillIcon';
  return Icon;
};

// ── Rectilinear family ──────────────────────────────────────────────────
export const RectilinearIcon = box('M3 4h10 M3 7h10 M3 10h10 M3 13h10');
export const AlignedRectilinearIcon = box('M3 4h10 M3 7h10 M3 10h10 M3 13h10');
export const MonotonicIcon = box('M3 4h10 M3 7h10 M3 10h10 M3 13h10');
export const MonotonicLineIcon = box('M3 5h10 M3 8h10 M3 11h10');

// ── Grid ────────────────────────────────────────────────────────────────
export const GridIcon = box('M3 4h10 M3 7h10 M3 10h10 M3 13h10 M4 3v10 M7 3v10 M10 3v10 M13 3v10');

// ── Line ────────────────────────────────────────────────────────────────
export const LineIcon = box('M3 5L13 5 M3 8L13 8 M3 11L13 11');

// ── Concentric ──────────────────────────────────────────────────────────
export const ConcentricIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <rect x="3" y="3" width="10" height="10" rx="0.5" stroke={C} strokeWidth={W} fill="none" />
    <rect x="5" y="5" width="6" height="6" rx="0.3" stroke={C} strokeWidth={W} fill="none" />
    <rect x="7" y="7" width="2" height="2" rx="0.2" stroke={C} strokeWidth={W} fill="none" />
  </svg>
);
ConcentricIcon.displayName = 'ConcentricIcon';

// ── Triangles ───────────────────────────────────────────────────────────
export const TrianglesIcon = box(
  'M3 13L8 3L13 13Z M8 3L3 13 M8 3L13 13 M3 13L13 13'
);

// ── Tri-Hexagon ─────────────────────────────────────────────────────────
export const TriHexagonIcon = box(
  'M5 3L3 7L5 11 M11 3L13 7L11 11 M3 7h10 M5 3h6 M5 11h6'
);

// ── Stars ───────────────────────────────────────────────────────────────
export const StarsIcon = box(
  'M8 3L9.5 6.5L13 8L9.5 9.5L8 13L6.5 9.5L3 8L6.5 6.5Z'
);

// ── Cubic family ────────────────────────────────────────────────────────
export const CubicIcon = box(
  'M3 3L8 6L13 3 M3 8L8 11L13 8 M3 13L8 10L13 13 M8 6v5'
);

export const AdaptiveCubicIcon = box(
  'M3 3L8 5.5L13 3 M5 8L8 10L11 8 M3 13L8 11L13 13'
);

export const QuarterCubicIcon = box(
  'M3 3L8 6L13 3 M3 8L8 5L13 8 M3 13L8 10L13 13 M8 5v5.5'
);

export const SupportCubicIcon = box(
  'M3 4L13 4 M3 8L13 8 M3 12L13 12 M6 4v4 M10 8v4'
);

// ── Honeycomb family ────────────────────────────────────────────────────
export const HoneycombIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M5 3L3 5.5L5 8L3 10.5L5 13 M8 3L6 5.5L8 8L6 10.5L8 13
             M11 3L9 5.5L11 8L9 10.5L11 13 M14 3L12 5.5L14 8L12 10.5L14 13
             M3 5.5h10 M3 10.5h10"
          stroke={C} strokeWidth={W} strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);
HoneycombIcon.displayName = 'HoneycombIcon';

export const Honeycomb3DIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M5 3L3 5.5L5 8L3 10.5L5 13 M11 3L9 5.5L11 8L9 10.5L11 13
             M3 5.5h10 M3 10.5h10"
          stroke={C} strokeWidth={W} strokeLinecap="round" strokeLinejoin="round" />
    <path d="M5 5.5L8 4L11 5.5 M5 10.5L8 9L11 10.5"
          stroke={C} strokeWidth="0.8" strokeLinecap="round" strokeLinejoin="round" opacity="0.5" />
  </svg>
);
Honeycomb3DIcon.displayName = 'Honeycomb3DIcon';

export const LateralHoneycombIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M3 5L5.5 3L8 5L10.5 3L13 5 M3 8L5.5 6L8 8L10.5 6L13 8
             M3 11L5.5 9L8 11L10.5 9L13 11 M5.5 3v10 M10.5 3v10"
          stroke={C} strokeWidth={W} strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);
LateralHoneycombIcon.displayName = 'LateralHoneycombIcon';

// ── Lateral Lattice ─────────────────────────────────────────────────────
export const LateralLatticeIcon = box(
  'M3 4h10 M3 8h10 M3 12h10 M5 3v10 M8 3v10 M11 3v10'
);

// ── Cross patterns ──────────────────────────────────────────────────────
export const CrossHatchIcon = box(
  'M3 3L13 13 M13 3L3 13 M8 3v10 M3 8h10'
);

export const ZigZagIcon = box(
  'M3 4L5.5 7L3 10L5.5 13 M8 3L10.5 6L8 9L10.5 12 M13 4L10.5 7L13 10'
);

export const CrossZagIcon = box(
  'M3 3L6.5 8L3 13 M13 3L9.5 8L13 13 M3 8h10'
);

export const LockedZagIcon = box(
  'M3 4L6 7L3 10 M13 4L10 7L13 10 M6 3v10 M10 3v10'
);

// ── Gyroid ──────────────────────────────────────────────────────────────
export const GyroidIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M3 8C4.5 4 6 4 8 8S11.5 12 13 8" stroke={C} strokeWidth={W}
          strokeLinecap="round" />
    <path d="M3 5C4.5 2 6 2 8 5S11.5 9 13 5" stroke={C} strokeWidth={W}
          strokeLinecap="round" opacity="0.6" />
    <path d="M3 11C4.5 7 6 7 8 11S11.5 15 13 11" stroke={C} strokeWidth={W}
          strokeLinecap="round" opacity="0.6" />
  </svg>
);
GyroidIcon.displayName = 'GyroidIcon';

// ── Space-filling curves ────────────────────────────────────────────────
export const HilbertCurveIcon = box(
  'M3 13L3 9L6 9L6 13 M6 9L6 5L3 5L3 3L6 3L6 5 M6 3L10 3L10 5L7 5L7 9L10 9L10 5 M10 9L10 13L13 13L13 9L10 9 M13 9L13 3'
);

export const ArchimedeanChordsIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M8 8 C8 6.5 9.5 5.5 10.5 6.5 C11.5 7.5 12 9.5 10 11 C8 12.5 5 12 4 9.5 C3 7 4 3.5 7 3 C10 2.5 13 4 13.5 7.5"
          stroke={C} strokeWidth={W} strokeLinecap="round" fill="none" />
  </svg>
);
ArchimedeanChordsIcon.displayName = 'ArchimedeanChordsIcon';

export const OctagramSpiralIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M8 6L10 4L12 6L10 8L12 10L10 12L8 10L6 12L4 10L6 8L4 6L6 4Z"
          stroke={C} strokeWidth={W} strokeLinecap="round" strokeLinejoin="round" />
    <path d="M8 7.5L8.5 7L9 7.5L8.5 8Z" stroke={C} strokeWidth="0.6" />
  </svg>
);
OctagramSpiralIcon.displayName = 'OctagramSpiralIcon';

// ── Lightning ───────────────────────────────────────────────────────────
export const LightningIcon = box(
  'M8 3L6 7L9 7L5 13 M8 3L10 6 M6 7L4 10 M9 7L11 5 M5 13L7 10'
);

// ── TPMS surfaces ───────────────────────────────────────────────────────
export const TpmsDIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M3 8Q5 4 8 4T13 8 M3 8Q5 12 8 12T13 8" stroke={C} strokeWidth={W}
          strokeLinecap="round" />
    <path d="M8 3Q4 5.5 4 8T8 13 M8 3Q12 5.5 12 8T8 13" stroke={C} strokeWidth="0.8"
          strokeLinecap="round" opacity="0.5" />
  </svg>
);
TpmsDIcon.displayName = 'TpmsDIcon';

export const TpmsFkIcon: React.FC<IconProps> = ({ className }) => (
  <svg className={className} width={S} height={S} viewBox={`0 0 ${S} ${S}`} fill="none"
       xmlns="http://www.w3.org/2000/svg">
    <rect x="0.5" y="0.5" width="15" height="15" rx="1.5" stroke="#666" strokeWidth="0.6" />
    <path d="M3 5Q5.5 3 8 5T13 5 M3 11Q5.5 9 8 11T13 11" stroke={C} strokeWidth={W}
          strokeLinecap="round" />
    <path d="M5 3Q3 5.5 5 8T5 13 M11 3Q13 5.5 11 8T11 13" stroke={C} strokeWidth="0.8"
          strokeLinecap="round" opacity="0.5" />
  </svg>
);
TpmsFkIcon.displayName = 'TpmsFkIcon';

// ── Lookup map: enum value → icon component ─────────────────────────────
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

/** Convenience: returns a 16×16 icon element or undefined for unknown values */
export function getInfillIcon(value: string, className?: string): React.ReactNode | undefined {
  const Icon = INFILL_ICON_MAP[value];
  return Icon ? <Icon className={className} /> : undefined;
}
