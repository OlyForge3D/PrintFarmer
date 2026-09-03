import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  downloadGcodeArtifact,
  resolveGcodeArtifact,
  resolveGcodeArtifactForAction,
  saveGcodeArtifactToLibrary,
  selectGcodeArtifact,
} from './sliceArtifactActions';
import type { ArtifactListItemResponse } from '@/services/sliceJobService';

const getArtifactsByRoute = vi.fn();
const downloadArtifact = vi.fn();
const promoteSliceArtifact = vi.fn();

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getArtifactsByRoute: (...args: unknown[]) => getArtifactsByRoute(...args),
    downloadArtifact: (...args: unknown[]) => downloadArtifact(...args),
    promoteSliceArtifact: (...args: unknown[]) => promoteSliceArtifact(...args),
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
      { id: 'log-1', fileName: 'slice.log', isPrimary: false },
      { id: 'gcode-1', fileName: 'output.gcode', isPrimary: false },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'gcode-1',
      fileName: 'output.gcode',
    });
    expect(getArtifactsByRoute).toHaveBeenCalledWith('/api/artifacts/job/job-1');
  });

  it('previews the text primary instead of a preceding binary output', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'binary-1', fileName: 'output.bgcode', isPrimary: false },
      { id: 'text-1', fileName: 'output.gcode', isPrimary: true },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'text-1',
    });
  });

  it('does not select binary G-code for text preview', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'binary-1', fileName: 'output.bgcode', isPrimary: false },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toBeNull();
  });

  it('recognizes .gco as text G-code for preview', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'text-1', fileName: 'output.gco', isPrimary: false },
    ]);

    await expect(resolveGcodeArtifact('/api/artifacts/job/job-1')).resolves.toMatchObject({
      id: 'text-1',
    });
  });

  it('selects the declared primary instead of the newest first artifact', async () => {
    getArtifactsByRoute.mockResolvedValue([
      {
        id: 'newest-artifact',
        fileName: 'newest.gcode',
        createdAt: '2026-09-03T10:01:00Z',
        isPrimary: false,
      },
      {
        id: 'primary-artifact',
        fileName: 'primary.gcode',
        createdAt: '2026-09-03T10:00:00Z',
        isPrimary: true,
      },
    ]);

    await expect(
      resolveGcodeArtifactForAction('/api/artifacts/job/job-1'),
    ).resolves.toMatchObject({ id: 'primary-artifact' });
  });

  describe('selectGcodeArtifact', () => {
    const artifact = (
      id: string,
      fileName: string,
      isPrimary = false,
    ): ArtifactListItemResponse => ({
      id,
      jobId: 'job-1',
      fileName,
      contentType: 'application/octet-stream',
      sizeBytes: 100,
      downloadUrl: `/api/artifacts/${id}`,
      createdAt: '2026-09-03T10:00:00Z',
      isPrimary,
    });
    const select = (artifacts: ArtifactListItemResponse[]) => selectGcodeArtifact(artifacts);

    it('uses the only G-code artifact without requiring a primary declaration', () => {
      expect(select([
        artifact('log-1', 'slice.log', true),
        artifact('gcode-1', 'output.gcode'),
      ])).toMatchObject({
        status: 'selected',
        artifact: { id: 'gcode-1' },
      });
    });

    it('blocks preview, download, and save when multiple outputs have no primary', async () => {
      getArtifactsByRoute.mockResolvedValue([
        { id: 'gcode-1', fileName: 'first.gcode', isPrimary: false },
        { id: 'gcode-2', fileName: 'second.gcode', isPrimary: false },
      ]);

      await expect(
        resolveGcodeArtifact('/api/artifacts/job/job-1'),
      ).rejects.toThrow('did not declare exactly one valid primary artifact');
      await expect(
        downloadGcodeArtifact('/api/artifacts/job/job-1'),
      ).rejects.toThrow('did not declare exactly one valid primary artifact');
      await expect(
        saveGcodeArtifactToLibrary('/api/artifacts/job/job-1', 'job-1'),
      ).rejects.toThrow('did not declare exactly one valid primary artifact');
      expect(downloadArtifact).not.toHaveBeenCalled();
      expect(promoteSliceArtifact).not.toHaveBeenCalled();
    });

    it('uses the declared primary when a newer non-primary appears first', () => {
      const newest = {
        ...artifact('newest-artifact', 'newest.gcode'),
        createdAt: '2026-09-03T10:01:00Z',
      };
      const primary = artifact('primary-artifact', 'primary.gcode', true);

      expect(select([newest, primary])).toMatchObject({
        status: 'selected',
        artifact: { id: 'primary-artifact' },
      });
    });

    it('requires selection when multiple G-code artifacts have no valid primary', () => {
      expect(select([
        artifact('gcode-1', 'first.gcode'),
        artifact('gcode-2', 'second.gcode'),
      ])).toMatchObject({ status: 'selection-required' });
    });

    it('rejects a primary declaration that identifies a non-G-code artifact', () => {
      expect(select([
        artifact('log-1', 'slice.log', true),
        artifact('gcode-1', 'first.gcode'),
        artifact('gcode-2', 'second.gcode'),
      ])).toMatchObject({ status: 'selection-required' });
    });

    it('requires selection when multiple G-code artifacts are declared primary', () => {
      expect(select([
        artifact('gcode-1', 'first.gcode', true),
        artifact('gcode-2', 'second.gcode', true),
      ])).toMatchObject({ status: 'selection-required' });
    });
  });

  it('downloads the selected artifact endpoint as a blob without opening a raw URL', async () => {
    const blob = new Blob(['G1 X0 Y0']);
    getArtifactsByRoute.mockResolvedValue([
      { id: 'newest-artifact', fileName: 'newest.gcode', isPrimary: false },
      { id: 'primary-artifact', fileName: 'primary.gcode', isPrimary: true },
    ]);
    downloadArtifact.mockResolvedValue(blob);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const windowOpen = vi.spyOn(window, 'open');

    await downloadGcodeArtifact('/api/artifacts/job/job-1');

    expect(downloadArtifact).toHaveBeenCalledWith('primary-artifact');
    expect(URL.createObjectURL).toHaveBeenCalledWith(blob);
    expect(click).toHaveBeenCalledOnce();
    expect(windowOpen).not.toHaveBeenCalled();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:gcode');
  });

  it('retains binary G-code support for downloads', async () => {
    const blob = new Blob(['binary']);
    getArtifactsByRoute.mockResolvedValue([
      { id: 'binary-1', fileName: 'output.bgcode', isPrimary: false },
    ]);
    downloadArtifact.mockResolvedValue(blob);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    await downloadGcodeArtifact('/api/artifacts/job/job-1');

    expect(downloadArtifact).toHaveBeenCalledWith('binary-1');
  });

  it('revokes the object URL when starting the browser download throws', async () => {
    const blob = new Blob(['G1 X0 Y0']);
    getArtifactsByRoute.mockResolvedValue([
      { id: 'gcode-1', fileName: 'output.gcode', isPrimary: false },
    ]);
    downloadArtifact.mockResolvedValue(blob);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {
      throw new Error('download blocked');
    });

    await expect(downloadGcodeArtifact('/api/artifacts/job/job-1')).rejects.toThrow(
      'download blocked',
    );

    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:gcode');
  });

  it('promotes the resolved staged artifact and converges on the durable result when repeated', async () => {
    getArtifactsByRoute.mockResolvedValue([
      { id: 'newest-artifact', fileName: 'newest.gcode', isPrimary: false },
      { id: 'artifact-1', fileName: 'output.bgcode', isPrimary: true },
    ]);
    const durable = {
      gcodeFileId: 'file-1',
      name: 'output.bgcode',
      sizeBytes: 100,
      printable: true,
      sliceJobId: 'job-1',
      sourceArtifactId: 'artifact-1',
    };
    promoteSliceArtifact
      .mockResolvedValueOnce({ ...durable, createdNew: true })
      .mockResolvedValueOnce({ ...durable, createdNew: false });

    const first = await saveGcodeArtifactToLibrary('/api/artifacts/job/job-1', 'job-1');
    const replay = await saveGcodeArtifactToLibrary('/api/artifacts/job/job-1', 'job-1');

    expect(first.gcodeFileId).toBe('file-1');
    expect(replay.gcodeFileId).toBe('file-1');
    expect(promoteSliceArtifact).toHaveBeenNthCalledWith(1, 'job-1', 'artifact-1');
    expect(promoteSliceArtifact).toHaveBeenNthCalledWith(2, 'job-1', 'artifact-1');
  });
});
