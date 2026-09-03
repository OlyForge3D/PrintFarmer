import { beforeEach, describe, expect, it, vi } from 'vitest';
import { downloadGcodeArtifact, resolveGcodeArtifact } from './sliceArtifactActions';

const getArtifactsByRoute = vi.fn();
const downloadArtifact = vi.fn();

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getArtifactsByRoute: (...args: unknown[]) => getArtifactsByRoute(...args),
    downloadArtifact: (...args: unknown[]) => downloadArtifact(...args),
  },
}));

describe('sliceArtifactActions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn(() => 'blob:gcode'),
      revokeObjectURL: vi.fn(),
    });
  });

  it('selects the generated G-code artifact from the canonical artifact list', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'log-1', fileName: 'slice.log' },
      { id: 'gcode-1', fileName: 'output.gcode' },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'gcode-1',
      fileName: 'output.gcode',
    });
    expect(getArtifactsByRoute).toHaveBeenCalledWith('/api/artifacts/job/job-1');
  });

  it('prefers text G-code over binary G-code for preview', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'binary-1', fileName: 'output.bgcode' },
      { id: 'text-1', fileName: 'output.gcode' },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'text-1',
    });
  });

  it('does not select binary G-code for text preview', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'binary-1', fileName: 'output.bgcode' },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toBeNull();
  });

  it('recognizes .gco as text G-code for preview', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'text-1', fileName: 'output.gco' },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'text-1',
    });
  });

  it('downloads the selected artifact endpoint as a blob without opening a raw URL', async () => {
    const blob = new Blob(['G1 X0 Y0']);
    getArtifactsByRoute.mockResolvedValue([{ id: 'gcode-1', fileName: 'output.gcode' }]);
    downloadArtifact.mockResolvedValue(blob);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const windowOpen = vi.spyOn(window, 'open');

    await downloadGcodeArtifact('/api/artifacts/job/job-1');

    expect(downloadArtifact).toHaveBeenCalledWith('gcode-1');
    expect(URL.createObjectURL).toHaveBeenCalledWith(blob);
    expect(click).toHaveBeenCalledOnce();
    expect(windowOpen).not.toHaveBeenCalled();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:gcode');
  });

  it('retains binary G-code support for downloads', async () => {
    const blob = new Blob(['binary']);
    getArtifactsByRoute.mockResolvedValue([{ id: 'binary-1', fileName: 'output.bgcode' }]);
    downloadArtifact.mockResolvedValue(blob);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    await downloadGcodeArtifact('/api/artifacts/job/job-1');

    expect(downloadArtifact).toHaveBeenCalledWith('binary-1');
  });

  it('revokes the object URL when starting the browser download throws', async () => {
    const blob = new Blob(['G1 X0 Y0']);
    getArtifactsByRoute.mockResolvedValue([{ id: 'gcode-1', fileName: 'output.gcode' }]);
    downloadArtifact.mockResolvedValue(blob);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {
      throw new Error('download blocked');
    });

    await expect(downloadGcodeArtifact('/api/artifacts/job/job-1')).rejects.toThrow(
      'download blocked',
    );

    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:gcode');
  });
});
