import type { SVGProps } from 'react';

interface PrintFarmerLogoIconProps extends SVGProps<SVGSVGElement> {
  decorative?: boolean;
  title?: string;
}

export function PrintFarmerLogoIcon({
  decorative = false,
  title = 'PrintFarmer logo',
  className,
  ...props
}: PrintFarmerLogoIconProps) {
  return (
    <svg
      viewBox="0 0 128 128"
      className={className}
      role="img"
      aria-hidden={decorative ? true : undefined}
      aria-label={decorative ? undefined : title}
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      {...props}
    >
      <path
        d="M16 62V52.8c0-4.7 2.4-9.1 6.4-11.7l38.7-24a8 8 0 0 1 8.5 0l38.7 24A13.8 13.8 0 0 1 114 52.8V62"
        stroke="currentColor"
        strokeWidth="6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <path d="M22 120V63" stroke="currentColor" strokeWidth="6" strokeLinecap="round" opacity="0.75" />
      <path d="M106 120V63" stroke="currentColor" strokeWidth="6" strokeLinecap="round" opacity="0.75" />
      <path d="M36 54h56" stroke="currentColor" strokeWidth="8" strokeLinecap="round" />
      <rect x="55" y="42" width="18" height="22" rx="2" fill="currentColor" opacity="0.78" />
      <circle cx="64" cy="68" r="5" fill="currentColor" />
      <g fill="currentColor" stroke="currentColor" strokeWidth="2" strokeLinejoin="round">
        <path d="M64 80 48 72l16-8 16 8-16 8Z" opacity="0.45" />
        <path d="M64 80v20l16-8V72" opacity="0.28" />
        <path d="M64 80v20L48 92V72" opacity="0.18" />
      </g>
      <path
        d="M34 116h60l10 0c2.8 0 5-2.2 5-5v-8c0-2.8-2.2-5-5-5h-10l-8 0H42l-8 0H24c-2.8 0-5 2.2-5 5v8c0 2.8 2.2 5 5 5h10Z"
        fill="currentColor"
        fillOpacity="0.12"
        stroke="currentColor"
        strokeWidth="4"
        strokeLinejoin="round"
      />
      <circle cx="40" cy="106" r="5" fill="currentColor" opacity="0.75" />
      <circle cx="88" cy="106" r="5" fill="currentColor" opacity="0.75" />
    </svg>
  );
}
