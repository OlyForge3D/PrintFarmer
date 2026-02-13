interface SpoolIconProps {
  /** CSS class name */
  className?: string;
  /** Fill color for the filament area. When undefined/null, renders as empty (light gray). */
  fillColor?: string | null;
  /** Size in pixels (width & height). Defaults to 48. */
  size?: number;
}

/**
 * Filament spool icon. Shows an empty spool when no fillColor is provided,
 * or fills the visible filament areas with the given color.
 */
export function SpoolIcon({ className, fillColor, size = 48 }: SpoolIconProps) {
  const filamentFill = fillColor ?? 'transparent';
  const rimColor = '#374151'; // gray-700 (darker frame)

  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 100 100"
      width={size}
      height={size}
      className={className}
      aria-label={fillColor ? 'Loaded spool' : 'Empty spool'}
    >
      {/* Filament fill circle (behind the rim) */}
      <circle cx="50" cy="50" r="40" fill={filamentFill} />

      {/* Outer rim */}
      <circle cx="50" cy="50" r="40" fill="none" stroke={rimColor} strokeWidth="8" />

      {/* Spokes - 5 tapered spokes: narrow at hub, wide at rim with smooth radius */}
      {[0, 72, 144, 216, 288].map((angle) => {
        const rad = (angle * Math.PI) / 180;
        const perpRad = rad + Math.PI / 2;
        const hubDist = 12;   // start at hub outer edge
        const rimDist = 36;
        const hubHalfW = 3;   // narrow at hub
        const rimHalfW = 6;   // twice as wide at rim
        const filletR = 4;    // radius for smooth rim join

        // Hub edge points (narrow)
        const hx1 = 50 + hubDist * Math.cos(rad) + hubHalfW * Math.cos(perpRad);
        const hy1 = 50 + hubDist * Math.sin(rad) + hubHalfW * Math.sin(perpRad);
        const hx2 = 50 + hubDist * Math.cos(rad) - hubHalfW * Math.cos(perpRad);
        const hy2 = 50 + hubDist * Math.sin(rad) - hubHalfW * Math.sin(perpRad);

        // Rim edge points (wide)
        const rx1 = 50 + rimDist * Math.cos(rad) + rimHalfW * Math.cos(perpRad);
        const ry1 = 50 + rimDist * Math.sin(rad) + rimHalfW * Math.sin(perpRad);
        const rx2 = 50 + rimDist * Math.cos(rad) - rimHalfW * Math.cos(perpRad);
        const ry2 = 50 + rimDist * Math.sin(rad) - rimHalfW * Math.sin(perpRad);

        // Control points for quadratic curve at rim corners (smooth radius)
        const cornerDist = rimDist + filletR;
        const cx1 = 50 + cornerDist * Math.cos(rad) + rimHalfW * Math.cos(perpRad);
        const cy1 = 50 + cornerDist * Math.sin(rad) + rimHalfW * Math.sin(perpRad);
        const cx2 = 50 + cornerDist * Math.cos(rad) - rimHalfW * Math.cos(perpRad);
        const cy2 = 50 + cornerDist * Math.sin(rad) - rimHalfW * Math.sin(perpRad);

        // Rim arc endpoint (center of rim at spoke)
        const arcX = 50 + (rimDist + filletR) * Math.cos(rad);
        const arcY = 50 + (rimDist + filletR) * Math.sin(rad);

        return (
          <path
            key={angle}
            d={`M ${hx1} ${hy1} L ${rx1} ${ry1} Q ${cx1} ${cy1} ${arcX} ${arcY} Q ${cx2} ${cy2} ${rx2} ${ry2} L ${hx2} ${hy2} Z`}
            fill={rimColor}
          />
        );
      })}

      {/* Center hub ring (through-hole) */}
      <circle cx="50" cy="50" r="12" fill="none" stroke={rimColor} strokeWidth="4" />
    </svg>
  );
}
