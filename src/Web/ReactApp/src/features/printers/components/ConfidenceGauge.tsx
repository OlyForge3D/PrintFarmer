import clsx from 'clsx';

type ConfidenceGaugeSize = 'sm' | 'lg';

interface ConfidenceGaugeProps {
  /** Confidence value from 0.0 to 1.0 */
  value: number | null | undefined;
  /** Optional threshold value from 0.0 to 1.0 — shown as a tick mark on the arc */
  threshold?: number;
  /** Display size: sm (32×20) for inline summary, lg (120×72) for modal */
  size?: ConfidenceGaugeSize;
  className?: string;
}

const SIZE_CONFIG = {
  sm: { width: 48, height: 30, strokeWidth: 5, radius: 18, fontSize: 9, thickTick: 1.2, needleWidth: 1.5 },
  lg: { width: 140, height: 84, strokeWidth: 10, radius: 52, fontSize: 18, thickTick: 2, needleWidth: 2.5 },
} as const;

/** Unique counter to avoid SVG gradient id collisions when multiple gauges render on the same page. */
let idCounter = 0;

/**
 * Semicircular arc gauge that visualises a 0-100% confidence score
 * with a green → yellow → red gradient. An optional threshold tick mark
 * shows the auto-pause trigger level.
 */
export function ConfidenceGauge({ value, threshold, size = 'sm', className }: ConfidenceGaugeProps) {
  const cfg = SIZE_CONFIG[size];
  const cx = cfg.width / 2;
  const cy = cfg.height - 2;
  const r = cfg.radius;

  // Arc runs from π (left, 0%) to 0 (right, 100%).
  const startAngle = Math.PI;
  const arcLength = Math.PI;
  const circumference = arcLength * r;

  const gradientId = `cg-grad-${++idCounter}`;

  // Normalise to 0..1
  const clamped = value == null ? null : Math.max(0, Math.min(1, value));
  const percent = clamped != null ? Math.round(clamped * 100) : null;

  // Needle angle: π (left, 0%) → 0 (right, 100%)
  const needleAngle = clamped != null ? startAngle - clamped * arcLength : null;

  // Threshold tick position
  const thresholdAngle = threshold != null ? startAngle - Math.max(0, Math.min(1, threshold)) * arcLength : null;

  // Helper to get a point on the arc
  const pointOnArc = (angle: number, offset = 0): { x: number; y: number } => ({
    x: cx + (r + offset) * Math.cos(angle),
    y: cy - (r + offset) * Math.sin(angle),
  });

  // Build the semicircle arc path (left to right)
  const arcStart = pointOnArc(startAngle);
  const arcEnd = pointOnArc(0);
  const arcPath = `M ${arcStart.x} ${arcStart.y} A ${r} ${r} 0 0 1 ${arcEnd.x} ${arcEnd.y}`;

  // Filled portion when we have a value
  const filledDash = clamped != null ? clamped * circumference : 0;
  const filledGap = circumference - filledDash;

  return (
    <div
      className={clsx('inline-flex flex-col items-center', className)}
      role="img"
      aria-label={percent != null ? `Confidence ${percent}%` : 'No confidence data'}
    >
      <svg
        width={cfg.width}
        height={cfg.height}
        viewBox={`0 0 ${cfg.width} ${cfg.height}`}
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
      >
        <defs>
          <linearGradient id={gradientId} x1="0%" y1="0%" x2="100%" y2="0%">
            <stop offset="0%" stopColor="#22c55e" />
            <stop offset="45%" stopColor="#eab308" />
            <stop offset="100%" stopColor="#ef4444" />
          </linearGradient>
        </defs>

        {/* Background track */}
        <path
          d={arcPath}
          stroke="currentColor"
          className="text-pf-border"
          strokeWidth={cfg.strokeWidth}
          strokeLinecap="round"
          fill="none"
        />

        {/* Gradient-filled arc */}
        {clamped != null && (
          <path
            d={arcPath}
            stroke={`url(#${gradientId})`}
            strokeWidth={cfg.strokeWidth}
            strokeLinecap="round"
            strokeDasharray={`${filledDash} ${filledGap}`}
            fill="none"
          />
        )}

        {/* Threshold tick mark */}
        {thresholdAngle != null && (
          <line
            x1={pointOnArc(thresholdAngle, -(cfg.strokeWidth / 2 + 1)).x}
            y1={pointOnArc(thresholdAngle, -(cfg.strokeWidth / 2 + 1)).y}
            x2={pointOnArc(thresholdAngle, cfg.strokeWidth / 2 + 1).x}
            y2={pointOnArc(thresholdAngle, cfg.strokeWidth / 2 + 1).y}
            stroke="currentColor"
            className="text-pf-text-secondary"
            strokeWidth={cfg.thickTick}
            strokeLinecap="round"
          />
        )}

        {/* Needle */}
        {needleAngle != null && (() => {
          const tip = pointOnArc(needleAngle);
          return (
            <line
              x1={cx}
              y1={cy}
              x2={tip.x}
              y2={tip.y}
              stroke="currentColor"
              className="text-pf-text-primary"
              strokeWidth={cfg.needleWidth}
              strokeLinecap="round"
            />
          );
        })()}

        {/* Center dot */}
        <circle cx={cx} cy={cy} r={cfg.needleWidth} fill="currentColor" className="text-pf-text-primary" />

        {/* Percentage label (lg only) */}
        {size === 'lg' && (
          <text
            x={cx}
            y={cy - r * 0.32}
            textAnchor="middle"
            dominantBaseline="central"
            className="fill-pf-text-primary font-semibold"
            fontSize={cfg.fontSize}
          >
            {percent != null ? `${percent}%` : '—'}
          </text>
        )}
      </svg>

      {/* Text label below gauge for sm */}
      {size === 'sm' && percent != null && (
        <span className="mt-0.5 text-[10px] font-medium leading-none text-pf-text-secondary">
          {percent}%
        </span>
      )}
    </div>
  );
}
