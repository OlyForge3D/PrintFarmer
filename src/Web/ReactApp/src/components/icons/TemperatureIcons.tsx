import { mdiPrinter3dNozzleAlert, mdiPrinter3dNozzle, mdiRadiator, mdiRadiatorDisabled } from '@mdi/js';

interface IconProps {
  className?: string;
  ariaLabel?: string;
  isOn?: boolean;
}

export function NozzleIcon({ className = 'w-4 h-4', ariaLabel = 'Hotend temperature', isOn = true }: IconProps) {
  const iconPath = isOn ? mdiPrinter3dNozzleAlert : mdiPrinter3dNozzle;
  return (
    <svg
      className={`${className} transition-opacity ${isOn ? 'opacity-100' : 'opacity-40'}`}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={iconPath} />
    </svg>
  );
}

export function BedIcon({ className = 'w-4 h-4', ariaLabel = 'Bed temperature', isOn = true }: IconProps) {
  const iconPath = isOn ? mdiRadiator : mdiRadiatorDisabled;
  return (
    <svg
      className={`${className} transition-opacity ${isOn ? 'opacity-100' : 'opacity-40'}`}
      viewBox="0 0 24 24"
      aria-label={ariaLabel}
      role="img"
    >
      <path fill="currentColor" d={iconPath} />
    </svg>
  );
}
