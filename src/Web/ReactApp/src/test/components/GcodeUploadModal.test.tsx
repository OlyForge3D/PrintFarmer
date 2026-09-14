import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import type { ComponentProps } from 'react';
import { toast } from 'sonner';
import { GcodeUploadModal } from '@/common/components/modals/GcodeUploadModal';

vi.mock('sonner', () => ({ toast: { error: vi.fn() } }));

function addFiles(method: 'select' | 'drop', files: File[]) {
  if (method === 'select') {
    fireEvent.change(document.querySelector('#gcode-file-upload')!, { target: { files } });
  } else {
    fireEvent.drop(screen.getByText('Drag files here or click to browse'), { dataTransfer: { files } });
  }
}

describe('GcodeUploadModal HTTP LAN queue identities', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('crypto', { getRandomValues: vi.fn(crypto.getRandomValues.bind(crypto)) });
  });
  afterEach(() => vi.unstubAllGlobals());

  it.each(['select', 'drop'] as const)('preserves identities and File-only payloads through %s, removal and progress', (method) => {
    const onFilesSelected = vi.fn<ComponentProps<typeof GcodeUploadModal>['onFilesSelected']>();
    const first = new File(['G1 X0'], 'first.gcode');
    const second = new File(['G1 Y0'], 'second.gcode');
    render(<GcodeUploadModal isOpen onClose={vi.fn()} onFilesSelected={onFilesSelected} />);
    expect(crypto.getRandomValues).not.toHaveBeenCalled();

    addFiles(method, [first]);
    const firstName = screen.getByText(first.name);
    addFiles(method, [second]);
    expect(screen.getByText(first.name)).toBe(firstName);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
    fireEvent.click(within(screen.getByText(second.name).parentElement!).getByRole('button'));
    expect(screen.queryByText(second.name)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /upload 1 file/i }));
    expect(onFilesSelected).toHaveBeenCalledExactlyOnceWith([first], expect.any(Function), expect.any(Function));
    const [, progress, complete] = onFilesSelected.mock.calls[0];
    act(() => progress?.(first.name, 50));
    expect(screen.getByText('50%')).toBeInTheDocument();
    act(() => complete?.(first.name, 'done'));
    expect(screen.getByText('✓ Done')).toBeInTheDocument();
    expect(screen.getByText(first.name)).toBe(firstName);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
  });

  it.each(['select', 'drop'] as const)('reports missing secure randomness during %s without queueing or uploading', (method) => {
    vi.stubGlobal('crypto', {});
    const onFilesSelected = vi.fn();
    render(<GcodeUploadModal isOpen onClose={vi.fn()} onFilesSelected={onFilesSelected} />);
    addFiles(method, [new File(['G1 X0'], 'test.gcode')]);

    expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('no cryptographically secure random source available'));
    expect(screen.queryByText('test.gcode')).not.toBeInTheDocument();
    expect(onFilesSelected).not.toHaveBeenCalled();
  });
});
