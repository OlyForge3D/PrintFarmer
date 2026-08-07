import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';
import type { GcodeFile } from '@/types/api';

describe('GcodeFileCard', () => {
  it('renders file tags through the shared color-safe, truncating chip', () => {
    const file: GcodeFile = {
      id: 'gcode-1',
      path: '/prints/example.gcode',
      fileName: 'example.gcode',
      name: 'Example',
      fileSize: 1024,
      uploadedAt: new Date('2026-08-07T00:00:00Z'),
      isDirectory: false,
      tags: [{
        id: 'tag-1',
        name: 'A very long safety tag',
        color: '#ffff00',
        description: 'Requires inspection',
      }],
    };

    render(<GcodeFileCard file={file} />);

    const label = screen.getByText('A very long safety tag');
    const tag = label.closest('[data-pf-radius="full"]');
    expect(tag).toHaveStyle({
      backgroundColor: '#ffff00',
      color: '#000000',
    });
    expect(tag).toHaveAttribute('title', 'Requires inspection');
    expect(label).toHaveClass('truncate');
  });
});
