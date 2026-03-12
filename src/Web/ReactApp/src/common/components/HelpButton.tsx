import { Button } from '@/common/components/ui';
import { HelpCircleIcon } from '@/common/components/icons/MdiIcons';

interface HelpButtonProps {
  onClick: () => void;
}

export function HelpButton({ onClick }: HelpButtonProps) {
  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={onClick}
      aria-label="Take a tour of this page"
    >
      <HelpCircleIcon className="w-5 h-5" />
    </Button>
  );
}
