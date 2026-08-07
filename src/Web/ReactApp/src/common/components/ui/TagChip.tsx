import type { CSSProperties } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui/Button';
import { getTagChipForeground } from '@/common/components/ui/tag-chip-colors';

export type TagChipSize = 'sm' | 'md';
export type TagChipAppearance = 'solid' | 'soft' | 'overlay';

interface TagChipBaseProps {
  /** Plain-text label. Keeping this a string prevents nested interactive controls. */
  label: string;
  /** User-authored tag color. Hex colors receive a WCAG-readable foreground. */
  color?: string | null;
  appearance?: TagChipAppearance;
  size?: TagChipSize;
  className?: string;
  style?: CSSProperties;
  title?: string;
  disabled?: boolean;
  truncate?: boolean;
  /** Announces a display chip as status text, or adds a live announcement to a removable chip. */
  statusLabel?: string;
}

interface DisplayTagChipProps extends TagChipBaseProps {
  mode?: 'display';
  onClick?: never;
  onRemove?: never;
}

interface ActionTagChipProps extends TagChipBaseProps {
  mode: 'action';
  onClick: () => void;
  onRemove?: never;
  ariaLabel?: string;
  pressed?: boolean;
}

interface RemovableTagChipProps extends TagChipBaseProps {
  mode: 'removable';
  onRemove: () => void;
  removeLabel: string;
  onClick?: () => void;
  ariaLabel?: string;
}

export type TagChipProps = DisplayTagChipProps | ActionTagChipProps | RemovableTagChipProps;

const sizeClasses: Record<TagChipSize, string> = {
  sm: 'min-h-5 px-2 py-0.5 text-xs',
  md: 'min-h-6 px-3 py-1 text-sm',
};

const appearanceClasses: Record<TagChipAppearance, string> = {
  solid: 'bg-pf-accent text-[var(--pf-on-accent)] border-pf-accent',
  soft: 'bg-pf-bg-2 text-pf-text-primary border-pf-accent/60',
  overlay: 'bg-black/70 text-white border-white/40',
};

function getColorStyle(color: string | null | undefined): CSSProperties | undefined {
  if (!color) {
    return undefined;
  }

  return {
    backgroundColor: color,
    borderColor: color,
    color: getTagChipForeground(color),
  };
}

export function TagChip(props: TagChipProps) {
  const {
    label,
    color,
    appearance = 'soft',
    size = 'sm',
    className,
    style,
    title,
    disabled = false,
    truncate = false,
    statusLabel,
  } = props;
  const colorStyle = getColorStyle(color);
  const rootClassName = clsx(
    'inline-flex max-w-full items-center gap-1 border font-medium leading-none transition-colors',
    'rounded-full',
    sizeClasses[size],
    !colorStyle && appearanceClasses[appearance],
    disabled && 'cursor-not-allowed opacity-50',
    className,
  );
  const mergedStyle = { ...style, ...colorStyle };
  const labelClassName = clsx('min-w-0', truncate && 'truncate');
  const resolvedTitle = title ?? (truncate ? label : undefined);

  if (props.mode === 'action') {
    return (
      <Button
        type="button"
        variant="unstyled"
        data-pf-radius="full"
        className={clsx(
          rootClassName,
          'enabled:cursor-pointer enabled:hover:shadow-md focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-2',
          props.pressed && 'ring-2 ring-pf-accent ring-offset-1',
        )}
        style={mergedStyle}
        title={resolvedTitle}
        aria-label={props.ariaLabel}
        aria-pressed={props.pressed}
        disabled={disabled}
        onClick={props.onClick}
      >
        <span className={labelClassName}>{label}</span>
      </Button>
    );
  }

  if (props.mode === 'removable') {
    return (
      <span
        data-pf-radius="full"
        className={rootClassName}
        style={mergedStyle}
        title={resolvedTitle}
        role="group"
        aria-label={props.ariaLabel ?? label}
      >
        {statusLabel && (
          <span className="sr-only" role="status">
            {statusLabel}
          </span>
        )}
        {props.onClick ? (
          <Button
            type="button"
            variant="unstyled"
            className={clsx(
              labelClassName,
              'enabled:cursor-pointer enabled:hover:underline focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
            )}
            disabled={disabled}
            onClick={props.onClick}
            onKeyDown={(event) => {
              if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
                event.preventDefault();
                props.onRemove();
              } else if (event.key === 'Delete') {
                event.preventDefault();
                props.onRemove();
              }
            }}
          >
            {label}
          </Button>
        ) : (
          <span className={labelClassName}>{label}</span>
        )}
        <Button
          type="button"
          variant="unstyled"
          className="inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full enabled:hover:bg-black/20 focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
          aria-label={props.removeLabel}
          title={props.removeLabel}
          disabled={disabled}
          onClick={(event) => {
            event.stopPropagation();
            props.onRemove();
          }}
        >
          <span aria-hidden="true">×</span>
        </Button>
      </span>
    );
  }

  return (
    <span
      data-pf-radius="full"
      className={rootClassName}
      style={mergedStyle}
      title={resolvedTitle}
      role={statusLabel ? 'status' : undefined}
      aria-label={statusLabel}
      aria-disabled={disabled || undefined}
    >
      <span className={labelClassName}>{label}</span>
    </span>
  );
}

export default TagChip;
