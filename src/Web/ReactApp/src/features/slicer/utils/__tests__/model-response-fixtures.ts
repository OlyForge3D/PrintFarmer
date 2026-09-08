import JSZip from 'jszip';

export const asciiStl = `solid triangle
facet normal 0 0 1
outer loop
vertex 0 0 0
vertex 1 0 0
vertex 0 1 0
endloop
endfacet
endsolid triangle`;

export const asciiPly = `ply
format ascii 1.0
element vertex 3
property float x
property float y
property float z
element face 1
property list uchar int vertex_indices
end_header
0 0 0
1 0 0
0 1 0
3 0 1 2
`;

export function modelTextBuffer(text = asciiStl): ArrayBuffer {
  const encoded = new TextEncoder().encode(text);
  // TextEncoder is supplied by Node in jsdom; use the active realm's ArrayBuffer.
  const buffer = new ArrayBuffer(encoded.byteLength);
  new Uint8Array(buffer).set(encoded);
  return buffer;
}

export function binaryStl(): ArrayBuffer {
  const data = new ArrayBuffer(134);
  const view = new DataView(data);
  view.setUint32(80, 1, true);
  view.setFloat32(108, 1, true);
  view.setFloat32(124, 1, true);
  return data;
}

export async function threeMfBuffer(): Promise<ArrayBuffer> {
  const zip = new JSZip();
  zip.file('3D/3dmodel.model', `<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
<resources><object id="1" type="model"><mesh>
<vertices><vertex x="0" y="0" z="0"/><vertex x="1" y="0" z="0"/><vertex x="0" y="1" z="0"/></vertices>
<triangles><triangle v1="0" v2="1" v3="2"/></triangles>
</mesh></object></resources><build><item objectid="1"/></build></model>`);
  return zip.generateAsync({ type: 'arraybuffer' });
}
