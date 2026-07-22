import { useEffect, useRef } from 'react';
import { SearchIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { Button, Input } from '@/common/components/ui';

interface SettingsSearchProps {
  value: string;
  onChange: (value: string) => void;
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  if (target.isContentEditable) {
    return true;
  }

  return ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

export const SettingsSearch: React.FC<SettingsSearchProps> = ({ value, onChange }) => {
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (
        event.key !== '/'
        || event.metaKey
        || event.ctrlKey
        || event.altKey
        || isEditableTarget(event.target)
      ) {
        return;
      }

      event.preventDefault();
      inputRef.current?.focus();
      inputRef.current?.select();
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  const handleClear = () => {
    onChange('');
    inputRef.current?.focus();
  };

  return (
    <div className="relative w-full max-w-sm">
      <SearchIcon
        className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-pf-text-secondary"
        ariaLabel="Search"
      />
      <Input
        ref={inputRef}
        type="search"
        placeholder="Search settings..."
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label="Search settings"
        className="h-11 pl-9 pr-20"
      />

      {value ? (
        <Button
          type="button"
          variant="unstyled"
          size="sm"
          onClick={handleClear}
          aria-label="Clear search"
          className="absolute right-2 top-1/2 -translate-y-1/2 rounded-full p-1.5 text-pf-text-secondary transition-colors duration-150 hover:bg-pf-bg-1 hover:text-pf-text-primary focus-visible:ring-2 focus-visible:ring-pf-accent"
          iconCenter={<CloseIcon className="h-4 w-4" ariaLabel="Clear search" />}
        />
      ) : (
        <span className="pointer-events-none absolute right-3 top-1/2 hidden -translate-y-1/2 rounded-md border border-pf-border bg-pf-bg-1 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-[0.16em] text-pf-text-tertiary sm:inline-flex">
          /
        </span>
      )}
    </div>
  );
};
