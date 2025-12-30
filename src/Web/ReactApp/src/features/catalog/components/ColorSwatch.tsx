import { useEffect, useRef } from 'react';

interface ColorSwatchProps {
  color: string;
  label?: string;
  className?: string;
}

// Small square showing filament color without inline style in JSX (uses CSS var set via ref)
export function ColorSwatch({ color, label, className = '' }: ColorSwatchProps) {
  const ref = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (ref.current) {
      ref.current.style.setProperty('--swatch-color', color);
    }
  }, [color]);
  return (
    <div
      ref={ref}
      aria-label={label}
      title={label}
      className={`color-swatch w-4 h-4 rounded-full border border-pf-border ${className}`.trim()}
      role="img"
    />
  );
}
