import type { ReactNode } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui';

/**
 * One file action, rendered as an icon button in cards/rows and as a labelled button in the
 * detail panel, so every surface offers the same actions with the same glyphs.
 */
export interface FileAction {
  id: string;
  label: string;
  icon: ReactNode;
  onClick: () => void;
  tone?: 'default' | 'primary' | 'danger';
  disabled?: boolean;
  /** Warms a lazily loaded dialog on hover/focus. */
  onPreload?: () => void;
}

const DANGER_CLASS = 'text-pf-error hover:bg-pf-error/10 hover:text-pf-error';

function variantFor(action: FileAction) {
  return action.tone === 'primary' ? 'primary' as const : 'secondary' as const;
}

/** Icon-only buttons: name via aria-label, tooltip via title, glyph hidden from AT. */
export function FileActionIconButtons({ actions, dense = false }: { actions: FileAction[]; dense?: boolean }) {
  return (
    <>
      {actions.map((action) => (
        <Button
          key={action.id}
          type="button"
          size="sm"
          variant={variantFor(action)}
          className={clsx(dense && 'p-1!', action.tone === 'danger' && DANGER_CLASS)}
          aria-label={action.label}
          title={action.label}
          disabled={action.disabled}
          onClick={action.onClick}
          onFocus={action.onPreload}
          onPointerEnter={action.onPreload}
          iconCenter={action.icon}
        />
      ))}
    </>
  );
}

/** Labelled buttons for the detail panel. */
export function FileActionList({ actions, label = 'File actions' }: { actions: FileAction[]; label?: string }) {
  return (
    <div role="group" aria-label={label} className="flex flex-wrap gap-2">
      {actions.map((action) => (
        <Button
          key={action.id}
          type="button"
          size="sm"
          variant={variantFor(action)}
          className={clsx(action.tone === 'danger' && DANGER_CLASS)}
          disabled={action.disabled}
          onClick={action.onClick}
          onFocus={action.onPreload}
          onPointerEnter={action.onPreload}
          iconLeft={action.icon}
        >
          {action.label}
        </Button>
      ))}
    </div>
  );
}