import type { Ref } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';

interface FileNameButtonProps {
  name: string;
  onOpen: (invoker: HTMLButtonElement) => void;
  className?: string;
  ref?: Ref<HTMLButtonElement>;
}

/**
 * The filename is the keyboard entry point to the detail panel. It truncates visually and
 * keeps the full name in `title`; the accessible name starts with the visible text.
 */
export function FileNameButton({ name, onOpen, className, ref }: FileNameButtonProps) {
  return (
    <Button
      ref={ref}
      type="button"
      variant="link"
      data-file-details-trigger=""
      title={name}
      aria-label={`${name}, show details`}
      className={clsx(
        'max-w-full min-w-0 justify-start! overflow-hidden text-left font-medium text-pf-text-primary! hover:text-pf-accent! [&>span]:truncate',
        className,
      )}
      onClick={(event) => onOpen(event.currentTarget)}
    >
      {name}
    </Button>
  );
}