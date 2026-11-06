/**
 * Infill pattern icons using actual OrcaSlicer SVG assets from public/icons/orca/.
 *
 * Each icon is a static <img> reference to the real OrcaSlicer param_*.svg file.
 * These SVGs use the canonical two-layer design (gray #949494 + teal #009688).
 */
import type { ReactNode } from 'react';

const INFILL_ICON_MAP: Record<string, string> = {
  rectilinear: 'rectilinear',
  alignedrectilinear: 'alignedrectilinear',
  monotonic: 'monotonic',
  monotonicline: 'monotonicline',
  concentric: 'concentric',
  grid: 'grid',
  triangles: 'triangles',
  'tri-hexagon': 'tri-hexagon',
  cubic: 'cubic',
  adaptivecubic: 'adaptivecubic',
  quartercubic: 'quartercubic',
  supportcubic: 'supportcubic',
  lightning: 'lightning',
  line: 'line',
  honeycomb: 'honeycomb',
  '3dhoneycomb': '3dhoneycomb',
  'lateral-honeycomb': 'lateral-honeycomb',
  'lateral-lattice': 'lateral-lattice',
  crosshatch: 'crosshatch',
  zigzag: 'zigzag',
  crosszag: 'crosszag',
  lockedzag: 'lockedzag',
  gyroid: 'gyroid',
  hilbertcurve: 'hilbertcurve',
  archimedeanchords: 'archimedeanchords',
  octagramspiral: 'octagramspiral',
  tpmsd: 'tpmsd',
  tpmsfk: 'tpmsfk',
  'rectilinear-grid': 'rectilinear-grid',
  rectilinear_interlaced: 'rectilinear_interlaced',
};

export function getInfillIcon(value: string, className?: string): ReactNode | undefined {
  const patternName = INFILL_ICON_MAP[value];
  return patternName ? (
    <img
      src={`/icons/orca/param_${patternName}.svg`}
      className={className}
      width={16}
      height={16}
      alt=""
      loading="lazy"
    />
  ) : undefined;
}
