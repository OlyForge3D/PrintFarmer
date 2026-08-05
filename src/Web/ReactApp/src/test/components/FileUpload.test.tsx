import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FileUpload } from '@/common/components/ui/FileUpload';

describe('FileUpload', () => {
  it('preserves the shared danger foreground across rest and hover states', () => {
    render(
      <FileUpload
        buttonText="Choose file"
        buttonVariant="danger"
      />,
    );

    const button = screen.getByRole('button', {
      name: 'Choose file (file upload)',
    });
    expect(button).toHaveClass(
      'bg-[var(--pf-button-danger-bg)]',
      'enabled:hover:bg-[var(--pf-button-danger-hover)]',
      'text-[var(--pf-on-danger)]',
      'active:scale-95',
    );
    expect(button).not.toHaveClass(
      'bg-pf-error',
      'text-[var(--pf-text-inverse)]',
      'hover:opacity-90',
      'active:opacity-75',
    );
  });
});
