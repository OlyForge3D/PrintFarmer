import type { CSSProperties } from 'react';
import clsx from 'clsx';
import { Button } from '@/common/components/ui/Button';
import {
  getTagChipForeground,
  normalizeTagChipColor,
} from '@/common/components/ui/tag-chip-colors';

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
  /** Accessible name for the chip or its primary action. */
  ariaLabel?: string;
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
  pressed?: boolean;
}

interface RemovableTagChipProps extends TagChipBaseProps {
  mode: 'removable';
  onRemove: () => void;
  removeLabel: string;
  onClick?: () => void;
}

export type TagChipProps = DisplayTagChipProps | ActionTagChipProps | RemovableTagChipProps;

const sizeClasses: Record<TagChipSize, string> = {
  sm: 'min-h-6 px-2 py-0.5 text-xs',
  md: 'min-h-7 px-3 py-1 text-sm',
};

const appearanceClasses: Record<TagChipAppearance, string> = {
  solid: 'bg-pf-accent text-[var(--pf-on-accent)] border-pf-accent',
  soft: 'bg-pf-bg-2 text-pf-text-primary border-pf-accent/60',
  overlay: 'bg-black/70 text-white border-white/40',
};

function getColorStyle(
  color: string | null | undefined,
  appearance: TagChipAppearance,
): CSSProperties | undefined {
  if (!color) {
    return undefined;
  }

  const normalizedColor = normalizeTagChipColor(color);
  if (!normalizedColor) {
    return undefined;
  }

  if (appearance === 'overlay') {
    return { borderColor: normalizedColor };
  }

  return {
    backgroundColor: normalizedColor,
    borderColor: normalizedColor,
    color: getTagChipForeground(normalizedColor),
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
    ariaLabel,
  } = props;
  const colorStyle = getColorStyle(color, appearance);
  const rootClassName = clsx(
    'inline-flex max-w-full items-center gap-1 border font-medium leading-none transition-colors',
    'rounded-full',
    sizeClasses[size],
    appearanceClasses[appearance],
    disabled && 'cursor-not-allowed opacity-50',
    className,
  );
  const mergedStyle = { ...style, ...colorStyle };
  const labelClassName = clsx('block min-w-0 max-w-full', truncate && 'truncate');
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
        aria-label={ariaLabel}
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
        aria-label={props.onClick ? undefined : ariaLabel}
      >
        {props.onClick ? (
          <Button
            type="button"
            variant="unstyled"
            className={clsx(
              labelClassName,
              'enabled:cursor-pointer enabled:hover:underline focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent',
            )}
            disabled={disabled}
            aria-label={ariaLabel}
            onClick={props.onClick}
            onKeyDown={(event) => {
              if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
                event.preventDefault();
                event.stopPropagation();
                props.onRemove();
              } else if (event.key === 'Delete') {
                event.preventDefault();
                event.stopPropagation();
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
          className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full enabled:hover:bg-black/20 focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
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
      role={ariaLabel ? 'img' : undefined}
      aria-label={ariaLabel}
      aria-disabled={disabled || undefined}
    >
      <span className={labelClassName}>{label}</span>
    </span>
  );
}

export default TagChip;
