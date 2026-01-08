import React from 'react';

interface ImagePlaceholderProps {
  className?: string;
}

// Branded PrintFarmer monogram used as a thumbnail placeholder
export default function ImagePlaceholder({ className = 'w-6 h-6' }: ImagePlaceholderProps) {
  return (
    <svg
      className={className}
      viewBox="0 0 48 48"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      role="img"
      aria-label="PrintFarmer logo placeholder"
    >
      <rect width="48" height="48" rx="6" fill="currentColor" opacity="0.08" />
      <circle cx="24" cy="18" r="8" stroke="currentColor" strokeWidth="2" fill="none" opacity="0.18" />
      <text x="24" y="32" textAnchor="middle" fontSize="12" fontWeight={700} fill="currentColor">
        PF
      </text>
    </svg>
  );
}
