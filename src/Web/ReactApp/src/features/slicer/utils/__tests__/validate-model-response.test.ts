import JSZip from 'jszip';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { STLLoader, PLYLoader } from 'three-stdlib';
import { BufferGeometry } from 'three';
import { validateModelResponse } from '@/features/slicer/utils/validate-model-response';
import { asciiPly, binaryStl, modelTextBuffer, threeMfBuffer } from '@/features/slicer/utils/__tests__/model-response-fixtures';

const indexedPly = `ply
format ascii 1.0
element vertex 4
property float x
property float y
property float z
element face 2
property list uchar int vertex_indices
end_header
0 0 0
1 0 0
1 1 0
0 1 0
3 0 1 2
3 0 2 3
`;

describe('validateModelResponse', () => {
  afterEach(() => vi.restoreAllMocks());

  it.each(['application/json', 'text/html', 'application/problem+json'])('rejects %s before mesh parsing', async (contentType) => {
    const parse = vi.spyOn(STLLoader.prototype, 'parse');
    await expect(validateModelResponse(binaryStl(), 'stl', contentType)).rejects.toThrow('direct download link');
    expect(parse).not.toHaveBeenCalled();
  });

  it.each([
    '',
    '{"models":[],"padding":"' + 'x'.repeat(200) + '"}',
    '<html><body>' + 'Not a model'.repeat(20) + '</body></html>',
    'solid this is not a mesh endsolid',
  ])('rejects empty and unrelated bodies even without an accurate MIME type', async (body) => {
    await expect(validateModelResponse(modelTextBuffer(body), 'stl', 'application/octet-stream')).rejects.toThrow('valid 3D model');
  });

  it('rejects a forged binary triangle count before STLLoader can allocate from it', async () => {
    const data = binaryStl();
    new DataView(data).setUint32(80, 0xffffffff, true);
    const parse = vi.spyOn(STLLoader.prototype, 'parse');
    await expect(validateModelResponse(data, 'stl')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('rejects truncated binary STL before parsing', async () => {
    const parse = vi.spyOn(STLLoader.prototype, 'parse');
    await expect(validateModelResponse(binaryStl().slice(0, 133), 'stl')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('preserves binary STL including headers beginning with solid', async () => {
    const data = binaryStl();
    new Uint8Array(data).set(new TextEncoder().encode('solid binary model'));
    await expect(validateModelResponse(data, 'stl', 'application/octet-stream')).resolves.toBeUndefined();
  });

  it('preserves ASCII STL served as text/plain and disposes validation geometry', async () => {
    const parse = vi.spyOn(STLLoader.prototype, 'parse');
    const dispose = vi.spyOn(BufferGeometry.prototype, 'dispose');
    await validateModelResponse(modelTextBuffer(), 'stl', 'text/plain');
    const geometry = parse.mock.results[0].value;
    expect(geometry.getAttribute('position').count).toBe(3);
    expect(dispose).toHaveBeenCalledOnce();
  });

  it('rejects non-finite coordinates', async () => {
    const data = binaryStl();
    new DataView(data).setFloat32(96, NaN, true);
    await expect(validateModelResponse(data, 'stl')).rejects.toThrow('valid 3D model');
  });

  it('preserves PLY models', async () => {
    await expect(validateModelResponse(modelTextBuffer(asciiPly), 'ply', 'text/plain')).resolves.toBeUndefined();
  });

  it('scans coordinates and indices only once for multi-triangle indexed meshes', async () => {
    const data = modelTextBuffer(indexedPly);
    const geometry = new PLYLoader().parse(data);
    const positions = geometry.getAttribute('position');
    const indices = geometry.getIndex();
    if (!indices) throw new Error('Expected indexed PLY fixture');
    expect(positions.count).toBe(4);
    expect(Array.from(indices.array)).toEqual([0, 1, 2, 0, 2, 3]);

    vi.spyOn(PLYLoader.prototype, 'parse').mockReturnValueOnce(geometry);
    // Count passes rather than wall-clock time so nested scans fail deterministically.
    const coordinatePasses = vi.spyOn(positions.array, Symbol.iterator);
    const indexPasses = vi.spyOn(indices.array, Symbol.iterator);
    const dispose = vi.spyOn(geometry, 'dispose');

    await expect(validateModelResponse(data, 'ply')).resolves.toBeUndefined();

    expect(coordinatePasses).toHaveBeenCalledOnce();
    expect(indexPasses).toHaveBeenCalledOnce();
    expect(dispose).toHaveBeenCalledOnce();
  });

  it.each(['coordinate', 'index'])('rejects an invalid final %s in an indexed mesh and disposes it', async (invalidValue) => {
    const data = modelTextBuffer(indexedPly);
    const geometry = new PLYLoader().parse(data);
    const positions = geometry.getAttribute('position');
    const indices = geometry.getIndex();
    if (!indices) throw new Error('Expected indexed PLY fixture');
    if (invalidValue === 'coordinate') {
      positions.array[positions.array.length - 1] = NaN;
    } else {
      indices.array[indices.array.length - 1] = positions.count;
    }
    vi.spyOn(PLYLoader.prototype, 'parse').mockReturnValueOnce(geometry);
    const dispose = vi.spyOn(geometry, 'dispose');

    await expect(validateModelResponse(data, 'ply')).rejects.toThrow('valid 3D model');
    expect(dispose).toHaveBeenCalledOnce();
  });

  it('preserves binary PLY models', async () => {
    const header = modelTextBuffer(asciiPly.slice(0, asciiPly.indexOf('end_header') + 'end_header\n'.length)
      .replace('format ascii', 'format binary_little_endian'));
    const data = new ArrayBuffer(header.byteLength + 36 + 13);
    new Uint8Array(data).set(new Uint8Array(header));
    const body = new DataView(data, header.byteLength);
    body.setFloat32(12, 1, true);
    body.setFloat32(28, 1, true);
    body.setUint8(36, 3);
    body.setInt32(41, 1, true);
    body.setInt32(45, 2, true);
    await expect(validateModelResponse(data, 'ply', 'application/octet-stream')).resolves.toBeUndefined();
  });

  it('rejects non-PLY responses before PLYLoader runs', async () => {
    const parse = vi.spyOn(PLYLoader.prototype, 'parse');
    await expect(validateModelResponse(modelTextBuffer('{"hello":"world"}'), 'ply')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('preserves real 3MF archives', async () => {
    await expect(validateModelResponse(await threeMfBuffer(), '3mf', 'application/zip')).resolves.toBeUndefined();
  });

  it('rejects unrelated ZIP archives instead of treating any ZIP as a 3MF', async () => {
    const zip = new JSZip();
    zip.file('manifest.json', '{}');
    await expect(validateModelResponse(await zip.generateAsync({ type: 'arraybuffer' }), '3mf')).rejects.toThrow('valid 3D model');
  });

  it('rejects JSON masquerading as 3MF', async () => {
    await expect(validateModelResponse(modelTextBuffer('{}'), '3mf')).rejects.toThrow('valid 3D model');
  });
});
