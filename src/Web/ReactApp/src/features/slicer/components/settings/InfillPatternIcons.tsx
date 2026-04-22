/**
 * Infill pattern icons using actual OrcaSlicer SVG assets from public/icons/orca/.
 *
 * Each icon is a static <img> reference to the real OrcaSlicer param_*.svg file.
 * These SVGs use the canonical two-layer design (gray #949494 + teal #009688).
 */
/* eslint-disable react-refresh/only-export-components -- Icon factory pattern */
import React from 'react';

interface IconProps { className?: string }

/** Create an icon component that renders an OrcaSlicer SVG from public/icons/orca/ */
const orcaIcon = (patternName: string, displayName: string): React.FC<IconProps> => {
  const Icon: React.FC<IconProps> = ({ className }) => (
    <img
      src={`/icons/orca/param_${patternName}.svg`}
      className={className}
      width={16}
      height={16}
      alt=""
      loading="lazy"
    />
  );
  Icon.displayName = displayName;
  return Icon;
};

// -- Individual named exports for direct imports ----------------------------

export const RectilinearIcon = orcaIcon('rectilinear', 'RectilinearIcon');
export const AlignedRectilinearIcon = orcaIcon('alignedrectilinear', 'AlignedRectilinearIcon');
export const MonotonicIcon = orcaIcon('monotonic', 'MonotonicIcon');
export const MonotonicLineIcon = orcaIcon('monotonicline', 'MonotonicLineIcon');
export const ConcentricIcon = orcaIcon('concentric', 'ConcentricIcon');
export const GridIcon = orcaIcon('grid', 'GridIcon');
export const TrianglesIcon = orcaIcon('triangles', 'TrianglesIcon');
export const TriHexagonIcon = orcaIcon('tri-hexagon', 'TriHexagonIcon');
export const CubicIcon = orcaIcon('cubic', 'CubicIcon');
export const AdaptiveCubicIcon = orcaIcon('adaptivecubic', 'AdaptiveCubicIcon');
export const QuarterCubicIcon = orcaIcon('quartercubic', 'QuarterCubicIcon');
export const SupportCubicIcon = orcaIcon('supportcubic', 'SupportCubicIcon');
export const LightningIcon = orcaIcon('lightning', 'LightningIcon');
export const LineIcon = orcaIcon('line', 'LineIcon');
export const HoneycombIcon = orcaIcon('honeycomb', 'HoneycombIcon');
export const Honeycomb3DIcon = orcaIcon('3dhoneycomb', 'Honeycomb3DIcon');
export const LateralHoneycombIcon = orcaIcon('lateral-honeycomb', 'LateralHoneycombIcon');
export const LateralLatticeIcon = orcaIcon('lateral-lattice', 'LateralLatticeIcon');
export const CrossHatchIcon = orcaIcon('crosshatch', 'CrossHatchIcon');
export const ZigZagIcon = orcaIcon('zigzag', 'ZigZagIcon');
export const CrossZagIcon = orcaIcon('crosszag', 'CrossZagIcon');
export const LockedZagIcon = orcaIcon('lockedzag', 'LockedZagIcon');
export const GyroidIcon = orcaIcon('gyroid', 'GyroidIcon');
export const HilbertCurveIcon = orcaIcon('hilbertcurve', 'HilbertCurveIcon');
export const ArchimedeanChordsIcon = orcaIcon('archimedeanchords', 'ArchimedeanChordsIcon');
export const OctagramSpiralIcon = orcaIcon('octagramspiral', 'OctagramSpiralIcon');
export const TpmsDIcon = orcaIcon('tpmsd', 'TpmsDIcon');
export const TpmsFkIcon = orcaIcon('tpmsfk', 'TpmsFkIcon');
export const RectilinearGridIcon = orcaIcon('rectilinear-grid', 'RectilinearGridIcon');
export const RectilinearInterlacedIcon = orcaIcon('rectilinear_interlaced', 'RectilinearInterlacedIcon');

// -- Lookup map -------------------------------------------------------------

export const INFILL_ICON_MAP: Record<string, React.FC<IconProps>> = {
  rectilinear: RectilinearIcon,
  alignedrectilinear: AlignedRectilinearIcon,
  monotonic: MonotonicIcon,
  monotonicline: MonotonicLineIcon,
  concentric: ConcentricIcon,
  grid: GridIcon,
  triangles: TrianglesIcon,
  'tri-hexagon': TriHexagonIcon,
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
  'rectilinear-grid': RectilinearGridIcon,
  rectilinear_interlaced: RectilinearInterlacedIcon,
};

export function getInfillIcon(value: string, className?: string): React.ReactNode | undefined {
  const Icon = INFILL_ICON_MAP[value];
  return Icon ? <Icon className={className} /> : undefined;
}
