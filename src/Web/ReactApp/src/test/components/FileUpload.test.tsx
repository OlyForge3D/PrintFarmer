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
    expect(button).toHaveAttribute('data-pf-variant', 'danger');
    expect(button).toHaveClass(
      'bg-[var(--pf-button-danger-bg)]',
      'enabled:hover:bg-[var(--pf-button-danger-hover)]',
      'text-[var(--pf-on-danger)]',
      'border-[var(--pf-button-danger-border)]',
      'active:scale-95',
    );
    expect(button).not.toHaveClass(
      'bg-pf-error',
      'enabled:hover:bg-pf-error-hover',
      'text-[var(--pf-text-inverse)]',
      'hover:opacity-90',
      'active:opacity-75',
    );
  });
});
