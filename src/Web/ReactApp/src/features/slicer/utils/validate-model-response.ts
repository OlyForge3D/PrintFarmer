import type { BufferGeometry } from 'three';
import type { SlicerViewerFileType } from '@/features/slicer/utils/model-file-utils';

const STL_HEADER_BYTES = 84;
const STL_TRIANGLE_BYTES = 50;
const MAX_STL_TRIANGLES = 5_000_000;
const INVALID_MODEL_MESSAGE = 'The URL did not return a valid 3D model. Use a direct download link to an STL, PLY, or 3MF file, not a web page or JSON response.';

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
      const text = new TextDecoder().decode(data);
      const headerEnd = text.indexOf('end_header');
      if (headerEnd < 0) throw new Error(INVALID_MODEL_MESSAGE);
      for (const match of text.slice(0, headerEnd).matchAll(/^element\s+\S+\s+(\S+)/gm)) {
        const count = Number(match[1]);
        if (!Number.isSafeInteger(count) || count < 0 || count > data.byteLength) {
          throw new Error(INVALID_MODEL_MESSAGE);
        }
      }
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
