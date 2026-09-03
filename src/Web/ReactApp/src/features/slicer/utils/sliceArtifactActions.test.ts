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
  });
});
