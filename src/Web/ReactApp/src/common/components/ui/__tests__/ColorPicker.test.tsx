import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { ColorPicker } from '../ColorPicker';

describe('ColorPicker', () => {
  it('renders an inline hex input in full (default) mode', () => {
    render(<ColorPicker value="FF5733" onChange={() => {}} aria-label="Filament colour" />);
    // The inline hex input is present without opening the popover.
    const input = screen.getByLabelText('Filament colour') as HTMLInputElement;
    expect(input).toBeInTheDocument();
    expect(input.value).toBe('#FF5733');
  });

  it('swatchOnly mode hides the inline hex input until the popover is opened', () => {
    render(
      <ColorPicker value="00A98F" onChange={() => {}} swatchOnly aria-label="Extruder 1 filament colour" />,
    );

    // No inline text input initially — only the swatch button.
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();

    const swatch = screen.getByRole('button', { name: /Extruder 1 filament colour/i });
    fireEvent.click(swatch);

    // Popover opens with the hex input inside it.
    expect(screen.getByRole('dialog', { name: /color picker/i })).toBeInTheDocument();
    const input = screen.getByLabelText('Extruder 1 filament colour') as HTMLInputElement;
    expect(input).toBeInTheDocument();
    expect(input.value).toBe('#00A98F');
  });

  it('applies a custom swatchClassName to the swatch button', () => {
    render(
      <ColorPicker value="123456" onChange={() => {}} swatchOnly swatchClassName="w-6 h-6" aria-label="Colour" />,
    );
    const swatch = screen.getByRole('button', { name: /Colour/i });
    expect(swatch.className).toContain('w-6');
    expect(swatch.className).toContain('h-6');
  });

  it('emits hex without the leading # when the text input changes', () => {
    const onChange = vi.fn();
    render(<ColorPicker value="FFFFFF" onChange={onChange} aria-label="Colour" />);
    const input = screen.getByLabelText('Colour');
    fireEvent.change(input, { target: { value: '#abcdef' } });
    expect(onChange).toHaveBeenCalledWith('abcdef');
  });
});
