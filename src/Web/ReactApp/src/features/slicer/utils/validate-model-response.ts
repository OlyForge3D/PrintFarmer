import type { BufferGeometry } from 'three';
import type { SlicerViewerFileType } from '@/features/slicer/utils/model-file-utils';

const STL_HEADER_BYTES = 84;
const STL_TRIANGLE_BYTES = 50;
const MAX_STL_TRIANGLES = 5_000_000;
const INVALID_MODEL_MESSAGE = 'The URL did not return a valid 3D model. Use a direct download link to an STL, PLY, or 3MF file, not a web page or JSON response.';

const PLY_TYPES = {
  char: 'Int8', int8: 'Int8', uchar: 'Uint8', uint8: 'Uint8',
  short: 'Int16', int16: 'Int16', ushort: 'Uint16', uint16: 'Uint16',
  int: 'Int32', int32: 'Int32', uint: 'Uint32', uint32: 'Uint32',
  float: 'Float32', float32: 'Float32', double: 'Float64', float64: 'Float64',
} as const;
type PlyType = typeof PLY_TYPES[keyof typeof PLY_TYPES];
const PLY_BYTES: Record<PlyType, number> = {
  Int8: 1, Uint8: 1, Int16: 2, Uint16: 2, Int32: 4, Uint32: 4, Float32: 4, Float64: 8,
};
interface PlyProperty {
  type: PlyType;
  countType?: PlyType;
}

function plyType(name: string): PlyType {
  if (!Object.hasOwn(PLY_TYPES, name)) throw new Error(INVALID_MODEL_MESSAGE);
  return PLY_TYPES[name as keyof typeof PLY_TYPES];
}

function assertPlyBody(data: ArrayBuffer): void {
  const text = new TextDecoder().decode(data);
  // Match three-stdlib's header selection and UTF-8 byte offset exactly.
  const header = /^ply([\s\S]*)end_header\r?\n/.exec(text);
  if (!header) throw new Error(INVALID_MODEL_MESSAGE);
  const elements: { count: number; properties: PlyProperty[] }[] = [];
  let format = '';
  for (const line of header[1].split('\n')) {
    const [directive, ...values] = line.trim().split(/\s+/);
    if (directive === 'format') {
      if (format || values.length !== 2 || values[1] !== '1.0') throw new Error(INVALID_MODEL_MESSAGE);
      format = values[0];
    } else if (directive === 'element') {
      const count = Number(values[1]);
      if (values.length !== 2 || !/^\d+$/.test(values[1]) || !Number.isSafeInteger(count) || count > data.byteLength) {
        throw new Error(INVALID_MODEL_MESSAGE);
      }
      elements.push({ count, properties: [] });
    } else if (directive === 'property') {
      const element = elements.at(-1);
      if (!element) throw new Error(INVALID_MODEL_MESSAGE);
      if (values[0] === 'list') {
        if (values.length !== 4) throw new Error(INVALID_MODEL_MESSAGE);
        const countType = plyType(values[1]);
        if (countType.startsWith('Float')) throw new Error(INVALID_MODEL_MESSAGE);
        element.properties.push({ type: plyType(values[2]), countType });
      } else {
        if (values.length !== 2) throw new Error(INVALID_MODEL_MESSAGE);
        element.properties.push({ type: plyType(values[0]) });
      }
    } else if (directive && directive !== 'comment' && directive !== 'obj_info') {
      throw new Error(INVALID_MODEL_MESSAGE);
    }
  }
  if (!elements.length || elements.some(({ count, properties }) => count > 0 && !properties.length)) {
    throw new Error(INVALID_MODEL_MESSAGE);
  }

  if (format === 'ascii') {
    const body = /end_header\s([\s\S]*)$/.exec(text);
    if (!body || body.index !== header[0].lastIndexOf('end_header')) {
      throw new Error(INVALID_MODEL_MESSAGE);
    }
    let elementIndex = 0;
    let elementCount = 0;
    for (const line of body[1].split('\n')) {
      if (!line.trim()) continue;
      // Mirror the loader's single element transition, including zero-count elements.
      if (elementCount >= elements[elementIndex].count) {
        elementIndex++;
        elementCount = 0;
      }
      const element = elements[elementIndex];
      if (!element) throw new Error(INVALID_MODEL_MESSAGE);
      const tokens = line.trim().split(/\s+/);
      let offset = 0;
      for (const property of element.properties) {
        if (offset >= tokens.length) throw new Error(INVALID_MODEL_MESSAGE);
        if (property.countType) {
          const token = tokens[offset++];
          const count = Number(token);
          if (!/^[+-]?\d+$/.test(token) || !Number.isSafeInteger(count) || count < 0 || count > tokens.length - offset) {
            throw new Error(INVALID_MODEL_MESSAGE);
          }
          offset += count;
        } else {
          offset++;
        }
      }
      if (offset !== tokens.length) throw new Error(INVALID_MODEL_MESSAGE);
      elementCount++;
    }
  } else {
    if (format !== 'binary_little_endian' && format !== 'binary_big_endian') throw new Error(INVALID_MODEL_MESSAGE);
    const view = new DataView(data, new TextEncoder().encode(header[0]).byteLength);
    const littleEndian = format === 'binary_little_endian';
    let offset = 0;
    for (const element of elements) {
      for (let row = 0; row < element.count; row++) {
        for (const property of element.properties) {
          let count = 1;
          if (property.countType) {
            count = view[`get${property.countType}`](offset, littleEndian);
            offset += PLY_BYTES[property.countType];
          }
          const size = PLY_BYTES[property.type];
          if (!Number.isSafeInteger(count) || count < 0 || count > Math.floor((view.byteLength - offset) / size)) {
            throw new Error(INVALID_MODEL_MESSAGE);
          }
          offset += count * size;
        }
      }
    }
  }
}

function assertMesh(geometry: BufferGeometry): void {
  const positions = geometry.getAttribute('position');
  if (!positions || positions.count < 3 || (geometry.index && geometry.index.count < 3)) {
    throw new Error(INVALID_MODEL_MESSAGE);
  }
  for (const coordinate of positions.array) {
    if (!Number.isFinite(coordinate)) {
      throw new Error(INVALID_MODEL_MESSAGE);
    }
  }
  if (geometry.index) {
    for (const index of geometry.index.array) {
      if (index >= positions.count) throw new Error(INVALID_MODEL_MESSAGE);
    }
  }
}

/** Rejects unrelated response bodies before a loader interprets bytes as mesh counts. */
export async function validateModelResponse(
  data: ArrayBuffer,
  fileType: SlicerViewerFileType,
  contentType = '',
): Promise<void> {
  const mimeType = contentType.split(';')[0].trim().toLowerCase();
  if (!data.byteLength || /(?:json|html|^image\/)/.test(mimeType)) {
    throw new Error(INVALID_MODEL_MESSAGE);
  }

  try {
    if (fileType === '3mf') {
      const signature = new Uint8Array(data, 0, Math.min(4, data.byteLength));
      if (signature[0] !== 0x50 || signature[1] !== 0x4b || signature[2] !== 3 || signature[3] !== 4) {
        throw new Error(INVALID_MODEL_MESSAGE);
      }
      const { parseThreeMfArchive, disposeParsedThreeMfModel } = await import('@/features/slicer/utils/threemf-parser');
      const model = await parseThreeMfArchive(data);
      try {
        if (!model.meshes.length) throw new Error(INVALID_MODEL_MESSAGE);
        model.meshes.forEach(({ geometry }) => assertMesh(geometry));
      } finally {
        disposeParsedThreeMfModel(model);
      }
      return;
    }

    const prefix = new TextDecoder().decode(new Uint8Array(data, 0, Math.min(512, data.byteLength)));
    if (fileType === 'stl') {
      const triangles = data.byteLength >= STL_HEADER_BYTES ? new DataView(data).getUint32(80, true) : 0;
      const isBinary = triangles > 0 && STL_HEADER_BYTES + triangles * STL_TRIANGLE_BYTES === data.byteLength;
      // Check binary length first: binary STL headers can also start with "solid".
      // STLLoader allocates from this count before reading any triangles.
      if (isBinary) {
        if (triangles > MAX_STL_TRIANGLES) throw new Error(INVALID_MODEL_MESSAGE);
      } else if (!/^solid\b/.test(prefix) || !/endsolid\b/.test(new TextDecoder().decode(data))) {
        throw new Error(INVALID_MODEL_MESSAGE);
      }
    } else {
      if (!/^ply\r?\n/.test(prefix)) throw new Error(INVALID_MODEL_MESSAGE);
      assertPlyBody(data);
    }

    const { STLLoader, PLYLoader } = await import('three-stdlib');
    const geometry = fileType === 'stl' ? new STLLoader().parse(data) : new PLYLoader().parse(data);
    try {
      assertMesh(geometry);
    } finally {
      geometry.dispose();
    }
  } catch (cause) {
    throw new Error(INVALID_MODEL_MESSAGE, { cause });
  }
}
