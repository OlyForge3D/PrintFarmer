import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { GcodeFile } from '@/types/api';

const queueModuleLoad = vi.hoisted(() => vi.fn());
const queueModule = vi.hoisted(() => {
  let resolve: () => void = () => undefined;
  const ready = new Promise<void>((resolveReady) => {
    resolve = resolveReady;
  });
  return { ready, resolve };
});

vi.mock('@/features/gcode/components/QueueGcodeModal', async () => {
  queueModuleLoad();
  await queueModule.ready;
  return {
    QueueGcodeModal: () => <div role="dialog" aria-label="Queue G-code card mock" />,
  };
});

import { GcodeFileCard } from '@/features/gcode/components/GcodeFileCard';

describe('GcodeFileCard queue lazy boundary', () => {
  it('preloads on direct intent and opens without an eager route import', async () => {
    const file: GcodeFile = {
      id: 'gcode-1',
      path: '/prints/example.gcode',
      fileName: 'example.gcode',
      name: 'Example',
      fileSize: 1024,
      uploadedAt: new Date('2026-08-07T00:00:00Z'),
      isDirectory: false,
      tags: [],
    };

    render(<GcodeFileCard file={file} />);

    expect(queueModuleLoad).not.toHaveBeenCalled();
    const queueButton = screen.getByRole('button', { name: 'Queue for Print' });
    fireEvent.click(queueButton);
    expect(await screen.findByRole('status', { name: 'Loading print queue' })).toBeVisible();
    await waitFor(() => expect(queueModuleLoad).toHaveBeenCalledTimes(1));
    queueModule.resolve();
    expect(await screen.findByRole('dialog', { name: 'Queue G-code card mock' })).toBeInTheDocument();
  });
});
