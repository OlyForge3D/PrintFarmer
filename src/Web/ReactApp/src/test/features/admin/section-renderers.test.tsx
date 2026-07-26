import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { getSectionRenderer } from '@/features/admin/settings/section-renderers';

// Mocks for section renderers that pull in React Query / SignalR / API clients.
// The per-engine editor doesn't need any of these, but the module imports them
// at load time via the Obico renderer, so keeping the mocks silent is enough.
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

/**
 * Regression guard for the per-engine slicer editor. Backend truth
 * (`[JsonPropertyName("perEngine")]` on `SlicerSettings.PerEngine`) is that
 * the wire key is camelCase `perEngine`, and `GET /api/settings/{key}` returns
 * the raw wire JSON with no normalization. A previous version of this
 * component indexed `values['PerEngine']` (PascalCase), which always resolved
 * to `undefined` and made the entire editor render nothing — a section that
 * had never worked in production.
 */
describe('renderPerEngine (SlicerSettings section renderer)', () => {
  const renderer = getSectionRenderer({ key: 'SlicerSettings', className: 'SlicerSettings' });
  if (!renderer?.extension) {
    throw new Error('SlicerSettings must have an extension renderer registered');
  }
  const extension = renderer.extension;

  it('renders the engine fields from a camelCase perEngine values object', () => {
    render(
      <>
        {extension({
          values: {
            perEngine: {
              PrusaSlicer: { profile: 'default', nozzle: '0.4' },
            },
          },
          onChange: vi.fn(),
        })}
      </>,
    );
    expect(screen.getByText('Per-Engine Slicer Settings')).toBeInTheDocument();
    expect(screen.getByText('PrusaSlicer')).toBeInTheDocument();
    // Both fields render.
    expect(screen.getByLabelText('PrusaSlicer profile')).toHaveValue('default');
    expect(screen.getByLabelText('PrusaSlicer nozzle')).toHaveValue('0.4');
  });

  it('propagates edits through onChange using the camelCase perEngine key', () => {
    const onChange = vi.fn();
    render(
      <>
        {extension({
          values: {
            perEngine: {
              OrcaSlicer: { profile: 'draft' },
            },
          },
          onChange,
        })}
      </>,
    );

    fireEvent.change(screen.getByLabelText('OrcaSlicer profile'), { target: { value: 'quality' } });

    expect(onChange).toHaveBeenCalledTimes(1);
    // The write must use the camelCase wire key, or the payload posted to
    // `POST /api/settings/Slicer` would carry a bogus `PerEngine` property
    // that the backend ignores.
    expect(onChange).toHaveBeenCalledWith('perEngine', {
      OrcaSlicer: { profile: 'quality' },
    });
  });

  it('renders nothing when perEngine is missing (does NOT fall through to the PascalCase key)', () => {
    const { container } = render(
      <>
        {extension({
          // Only a PascalCase key — the wire never sends this, but this asserts
          // the renderer isn't lenient about casing.
          values: { PerEngine: { PrusaSlicer: { profile: 'x' } } } as unknown as Record<
            string,
            never
          >,
          onChange: vi.fn(),
        })}
      </>,
    );
    expect(container.textContent).toBe('');
  });
});
