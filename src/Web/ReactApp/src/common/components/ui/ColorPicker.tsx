import React, { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { HexColorPicker } from 'react-colorful';

export interface ColorPickerProps {
  /** Current hex color value (with or without '#' prefix). */
  value: string;
  /** Called when the color changes (value always without '#' prefix). */
  onChange: (hex: string) => void;
  /** Input id for label association. */
  id?: string;
  /** Accessible label for the color picker. */
  'aria-label'?: string;
  /** Placeholder text for the hex input. */
  placeholder?: string;
  /** Whether the field is disabled. */
  disabled?: boolean;
  /**
   * Compact mode: render only the color swatch (no inline hex input). The hex
   * text input is moved inside the popover. Useful for dense rows.
   */
  swatchOnly?: boolean;
  /** Optional className applied to the swatch button (e.g. to size it down). */
  swatchClassName?: string;
  /**
   * Optional content rendered inside the swatch button (e.g. an extruder number).
   * Its text colour auto-adjusts to black/white for contrast against the swatch.
   */
  swatchContent?: React.ReactNode;
}

/**
 * A color picker with an inline HEX-mode saturation/hue panel (via react-colorful)
 * and a hex text input. Click the swatch to toggle the picker popover.
 * Both the picker and the text input stay in sync.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export const ColorPicker: React.FC<ColorPickerProps> = ({
  value,
  onChange,
  id,
  'aria-label': ariaLabel = 'Color',
  placeholder = '#FF5733',
  disabled = false,
  swatchOnly = false,
  swatchClassName,
  swatchContent,
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const popoverRef = useRef<HTMLDivElement>(null);
  const swatchRef = useRef<HTMLButtonElement>(null);

  const normalizedHex = normalizeToHash(value);

  // Close popover on outside click or Escape
  useEffect(() => {
    if (!isOpen) return;

    const handleClick = (e: MouseEvent) => {
      if (
        popoverRef.current &&
        !popoverRef.current.contains(e.target as Node) &&
        swatchRef.current &&
        !swatchRef.current.contains(e.target as Node)
      ) {
        setIsOpen(false);
      }
    };

    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setIsOpen(false);
        swatchRef.current?.focus();
      }
    };

    document.addEventListener('mousedown', handleClick);
    document.addEventListener('keydown', handleKey);
    return () => {
      document.removeEventListener('mousedown', handleClick);
      document.removeEventListener('keydown', handleKey);
    };
  }, [isOpen]);

  const handlePickerChange = useCallback(
    (hex: string) => {
      onChange(hex.replace(/^#/, ''));
    },
    [onChange],
  );

  const handleTextChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      onChange(e.target.value.replace(/^#+/, ''));
    },
    [onChange],
  );

  return (
    <div className={clsx('flex items-center gap-2', swatchOnly && 'inline-flex')}>
      {/* Color swatch — toggles the picker popover */}
      <div className="relative">
        {/* eslint-disable-next-line local/pf-no-raw-html-controls -- Custom color swatch requires raw button for background-color styling */}
        <button
          ref={swatchRef}
          type="button"
          onClick={() => !disabled && setIsOpen((o) => !o)}
          disabled={disabled}
          className={clsx(
            'flex items-center justify-center rounded-md border-2 border-pf-border shrink-0 cursor-pointer transition',
            'hover:border-pf-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-pf-accent',
            disabled && 'opacity-50 cursor-not-allowed',
            swatchClassName ?? 'w-9 h-9',
          )}
          style={{ backgroundColor: normalizedHex, color: readableTextColor(normalizedHex) }}
          aria-label={`${ariaLabel} — click to ${isOpen ? 'close' : 'open'} color picker`}
          aria-expanded={isOpen}
          title={isOpen ? 'Close color picker' : 'Open color picker'}
          data-pf-button=""
        >
          {swatchContent}
        </button>

        {/* Popover with react-colorful HEX picker */}
        {isOpen && (
          <div
            ref={popoverRef}
            className="absolute left-0 top-full z-50 mt-2 rounded-lg shadow-lg border border-pf-border bg-pf-surface-elevated p-2"
            role="dialog"
            aria-label="Color picker"
          >
            <HexColorPicker
              color={normalizedHex}
              onChange={handlePickerChange}
            />
            {swatchOnly && (
              <input
                id={id}
                type="text"
                value={value ? `#${value}` : ''}
                onChange={handleTextChange}
                placeholder={placeholder.startsWith('#') ? placeholder : `#${placeholder}`}
                disabled={disabled}
                maxLength={7}
                aria-label={ariaLabel}
                className={clsx(
                  'mt-2 w-full border rounded-sm p-1.5 text-xs font-mono',
                  'bg-pf-bg-0 text-pf-text-primary border-pf-border',
                  'focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-pf-accent transition',
                  'disabled:bg-pf-disabled disabled:cursor-not-allowed',
                )}
              />
            )}
          </div>
        )}
      </div>

      {/* Hex text input (full mode only) */}
      {!swatchOnly && (
        <div className="flex-1">
          <input
            id={id}
            type="text"
            value={value ? `#${value}` : ''}
            onChange={handleTextChange}
            placeholder={placeholder.startsWith('#') ? placeholder : `#${placeholder}`}
            disabled={disabled}
            maxLength={7}
            aria-label={ariaLabel}
            className={clsx(
              'w-full border rounded-sm p-2 text-sm font-mono',
              'bg-pf-bg-0 text-pf-text-primary border-pf-border',
              'focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:border-pf-accent transition',
              'disabled:bg-pf-disabled disabled:cursor-not-allowed',
            )}
          />
        </div>
      )}
    </div>
  );
};

/** Ensures a value like 'FF5733' or '#FF5733' becomes '#FF5733'. Falls back to '#888888'. */
function normalizeToHash(hex: string): string {
  const clean = hex.replace(/^#/, '').trim();
  if (/^[0-9a-fA-F]{6}$/.test(clean)) {
    return `#${clean}`;
  }
  if (/^[0-9a-fA-F]{3}$/.test(clean)) {
    return `#${clean[0]}${clean[0]}${clean[1]}${clean[1]}${clean[2]}${clean[2]}`;
  }
  return '#888888';
}

/**
 * Pick black or white for legible text on top of a hex background, using the
 * W3C relative-luminance threshold. Expects a normalized '#RRGGBB' value.
 */
function readableTextColor(hex: string): string {
  const clean = hex.replace(/^#/, '');
  if (clean.length !== 6) return '#000000';
  const r = parseInt(clean.slice(0, 2), 16) / 255;
  const g = parseInt(clean.slice(2, 4), 16) / 255;
  const b = parseInt(clean.slice(4, 6), 16) / 255;
  const toLinear = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  const luminance = 0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b);
  return luminance > 0.179 ? '#000000' : '#FFFFFF';
}

export default ColorPicker;
