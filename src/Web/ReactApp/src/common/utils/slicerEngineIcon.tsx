import React from 'react';
import orcaslicerIcon from '@/assets/orcaslicer.svg';
import prusaslicerIcon from '@/assets/prusaslicer.svg';

/** Known slicer engine IDs */
export const SLICER_ENGINE = {
  ORCASLICER: 1,
  PRUSASLICER: 2,
} as const;

interface SlicerIconEntry {
  src: string;
  alt: string;
}

const slicerIcons: Record<number, SlicerIconEntry> = {
  [SLICER_ENGINE.ORCASLICER]: { src: orcaslicerIcon, alt: 'OrcaSlicer' },
  [SLICER_ENGINE.PRUSASLICER]: { src: prusaslicerIcon, alt: 'PrusaSlicer' },
};

/**
 * Get the icon `src` URL for a slicer engine by ID.
 * Returns undefined for unknown engines.
 */
export function getSlicerIconSrc(slicerId: number): string | undefined {
  return slicerIcons[slicerId]?.src;
}

/**
 * Get a React <img> element for a slicer engine icon.
 */
export function getSlicerIcon(
  slicerId: number,
  className = 'inline h-5 w-5 align-middle',
): React.ReactElement {
  const entry = slicerIcons[slicerId];
  if (entry) {
    return (
      <img
        src={entry.src}
        alt={entry.alt}
        title={entry.alt}
        className={className}
      />
    );
  }
  return (
    <span title="Slicer" aria-label="Slicer" role="img" className="inline-block h-5 w-5 text-center">
      🔪
    </span>
  );
}
