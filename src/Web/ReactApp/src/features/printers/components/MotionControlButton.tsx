import { ControlPadButton, Spinner } from '@/common/components/ui';
import type { ControlPadButtonProps } from '@/common/components/ui/ControlPadButton';
import { useMotionAction } from '@/features/printers/hooks/use-motion-action';

interface MotionControlButtonProps extends Omit<ControlPadButtonProps, 'onClick' | 'loading'> {
  onClick: () => void | Promise<void>;
  pending?: boolean;
}

export function MotionControlButton({ onClick, pending = false, disabled, children, ...props }: MotionControlButtonProps) {
  const action = useMotionAction();
  const busy = pending || action.pending;
  return (
    <ControlPadButton
      {...props}
      aria-label={props['aria-label'] ?? props.title}
      aria-busy={busy}
      disabled={disabled || busy}
      onClick={() => action.run(onClick)}
    >
      {/* The shared Button loading text cannot fit a compact motion pad. */}
      {busy ? <Spinner size="sm" /> : children}
    </ControlPadButton>
  );
}
