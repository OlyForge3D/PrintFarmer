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

const binaryCountTypes = [
  { type: 'uchar', bytes: 1, setter: 'setUint8' },
  { type: 'int8', bytes: 1, setter: 'setInt8' },
  { type: 'ushort', bytes: 2, setter: 'setUint16' },
  { type: 'int16', bytes: 2, setter: 'setInt16' },
  { type: 'uint', bytes: 4, setter: 'setUint32' },
  { type: 'int32', bytes: 4, setter: 'setInt32' },
] as const;

function binaryPly(
  littleEndian: boolean,
  countType: typeof binaryCountTypes[number] = binaryCountTypes[4],
  count = 3,
  hostileProperty: 'weights' | 'vertex_indices' = 'vertex_indices',
): ArrayBuffer {
  const header = modelTextBuffer(asciiPly.slice(0, asciiPly.indexOf('end_header') + 'end_header\n'.length)
    .replace('format ascii', `format binary_${littleEndian ? 'little' : 'big'}_endian`)
    .replace('element vertex 3', 'comment café\nelement vertex 3')
    .replace('property list uchar int vertex_indices', `property uchar material
property list ${countType.type} float weights
property list ${countType.type} int vertex_indices
property uchar confidence`));
  const data = new ArrayBuffer(header.byteLength + 36 + 1 + countType.bytes * 2 + 8 + 12 + 1);
  new Uint8Array(data).set(new Uint8Array(header));
  const body = new DataView(data, header.byteLength);
  body.setFloat32(12, 1, littleEndian);
  body.setFloat32(28, 1, littleEndian);
  body.setUint8(36, 7);
  body[countType.setter](37, hostileProperty === 'weights' ? count : 2, littleEndian);
  let offset = 37 + countType.bytes;
  body.setFloat32(offset, 0.5, littleEndian);
  body.setFloat32(offset + 4, 1, littleEndian);
  offset += 8;
  body[countType.setter](offset, hostileProperty === 'vertex_indices' ? count : 3, littleEndian);
  offset += countType.bytes;
  body.setInt32(offset + 4, 1, littleEndian);
  body.setInt32(offset + 8, 2, littleEndian);
  body.setUint8(offset + 12, 255);
  return data;
}

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

  it.each(['4294967295', '-1', '3.5', '1e9', 'Infinity', 'NaN', '4'])(
    'rejects unsafe ASCII PLY list count %s without invoking the loader',
    async (count) => {
      const data = modelTextBuffer(asciiPly
        .replace('list uchar int', 'list uint int')
        .replace('3 0 1 2\n', `${count} 0 1 2\n`));
      // Never run the vulnerable loader if preflight regresses: fail rather than hang.
      const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
        throw new Error('Unsafe PLY reached the loader');
      });
      await expect(validateModelResponse(data, 'ply')).rejects.toThrow('direct download link');
      expect(parse).not.toHaveBeenCalled();
    },
  );

  it.each(['weights', 'vertex_indices'])('checks ASCII %s lists after other properties', async (property) => {
    const data = modelTextBuffer(asciiPly
      .replace('property list uchar int vertex_indices', `property uchar material
property list uint float weights
property list uint int vertex_indices`)
      .replace('3 0 1 2\n', property === 'weights' ? '7 4294967295 0.5 1 3 0 1 2\n' : '7 2 0.5 1 4294967295 0 1 2\n'));
    const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
      throw new Error('Unsafe PLY reached the loader');
    });
    await expect(validateModelResponse(data, 'ply')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('bounds ASCII lists by their own row, not all remaining body tokens', async () => {
    const data = modelTextBuffer(indexedPly.replace('3 0 1 2', '7 0 1 2'));
    const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
      throw new Error('Unsafe PLY reached the loader');
    });
    await expect(validateModelResponse(data, 'ply')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('checks lists in unknown elements even when earlier elements have zero counts', async () => {
    const data = modelTextBuffer(asciiPly
      .replace('element vertex 3', 'element edge 0\nproperty int unused\nelement metadata 1\nproperty list uint int values\nelement vertex 3')
      .replace('end_header\n', 'end_header\n4294967295 0\n'));
    const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
      throw new Error('Unsafe PLY reached the loader');
    });
    await expect(validateModelResponse(data, 'ply')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it('rejects ambiguous ASCII header/body delimiters before parsing', async () => {
    const data = modelTextBuffer(asciiPly.replace('format ascii', 'comment end_header\nformat ascii'));
    const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
      throw new Error('Unsafe PLY reached the loader');
    });
    await expect(validateModelResponse(data, 'ply')).rejects.toThrow('valid 3D model');
    expect(parse).not.toHaveBeenCalled();
  });

  it.each(['\n', '\r\n'])('preserves ASCII scalar and multiple list properties with %j newlines', async (newline) => {
    const text = indexedPly
      .replace('element vertex 4', 'comment café\nobj_info valid model\nelement vertex 4')
      .replace('property list uchar int vertex_indices', `property uchar material
property list ushort float weights
property list uint32 int32 vertex_indices
property uchar confidence`)
      .replace('3 0 1 2\n', '7 0 3 0 1 2 255\n')
      .replace('3 0 2 3\n', '7 2 0.5 1 3 0 2 3 255\n')
      .replaceAll('\n', newline);
    const parse = vi.spyOn(PLYLoader.prototype, 'parse');
    await expect(validateModelResponse(modelTextBuffer(text), 'ply')).resolves.toBeUndefined();
    expect(parse).toHaveBeenCalledOnce();
    expect(Array.from(parse.mock.results[0].value.getIndex().array)).toEqual([0, 1, 2, 0, 2, 3]);
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

  describe.each([true, false])('binary PLY (little endian: %s)', (littleEndian) => {
    it.each(binaryCountTypes)('preserves $type counts with scalar and multiple list properties', async (countType) => {
      const parse = vi.spyOn(PLYLoader.prototype, 'parse');
      await expect(validateModelResponse(binaryPly(littleEndian, countType), 'ply', 'application/octet-stream')).resolves.toBeUndefined();
      expect(parse).toHaveBeenCalledOnce();
      expect(Array.from(parse.mock.results[0].value.getIndex().array)).toEqual([0, 1, 2]);
    });

    it.each(['weights', 'vertex_indices'] as const)('rejects forged uint32 %s cardinality before parsing', async (property) => {
      const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
        throw new Error('Unsafe PLY reached the loader');
      });
      await expect(validateModelResponse(binaryPly(littleEndian, undefined, 0xffffffff, property), 'ply')).rejects.toThrow('direct download link');
      expect(parse).not.toHaveBeenCalled();
    });

    it.each(binaryCountTypes)('rejects a truncated $type list before parsing', async (countType) => {
      const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
        throw new Error('Unsafe PLY reached the loader');
      });
      await expect(validateModelResponse(binaryPly(littleEndian, countType).slice(0, -3), 'ply')).rejects.toThrow('valid 3D model');
      expect(parse).not.toHaveBeenCalled();
    });

    it('rejects negative signed binary counts before parsing', async () => {
      const parse = vi.spyOn(PLYLoader.prototype, 'parse').mockImplementation(() => {
        throw new Error('Unsafe PLY reached the loader');
      });
      await expect(validateModelResponse(binaryPly(littleEndian, binaryCountTypes[5], -1), 'ply')).rejects.toThrow('valid 3D model');
      expect(parse).not.toHaveBeenCalled();
    });
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
