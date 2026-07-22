import { PrintFarmerLogoIcon } from '@/common/components/icons/PrintFarmerLogoIcon';

interface PrintFarmerLogoProps {
  className?: string;
  size?: number;
}

export function PrintFarmerLogo({ className, size = 48 }: PrintFarmerLogoProps) {
  return (
    <PrintFarmerLogoIcon
      decorative
      width={size}
      height={size}
      className={`${className ?? ''} inline-block align-middle text-pf-accent`}
    />
  );
}
