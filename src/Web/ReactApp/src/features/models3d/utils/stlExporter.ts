import * as THREE from 'three';

/** Export a BufferGeometry as a binary STL file and trigger a browser download. */
export function exportSTL(geometry: THREE.BufferGeometry, sourceFilename: string): void {
  const positions = geometry.getAttribute('position') as THREE.BufferAttribute;
  const indices = geometry.index;

  const faceCount = indices ? indices.count / 3 : positions.count / 3;

  // Binary STL: 80-byte header + 4-byte face count + 50 bytes per face
  const bufferLength = 80 + 4 + faceCount * 50;
  const buffer = new ArrayBuffer(bufferLength);
  const view = new DataView(buffer);

  const header = 'PrintFarmer Decimated Model';
  for (let i = 0; i < 80; i++) {
    view.setUint8(i, i < header.length ? header.charCodeAt(i) : 0);
  }

  view.setUint32(80, faceCount, true);

  let offset = 84;
  const normal = new THREE.Vector3();
  const vA = new THREE.Vector3();
  const vB = new THREE.Vector3();
  const vC = new THREE.Vector3();
  const cb = new THREE.Vector3();
  const ab = new THREE.Vector3();

  for (let f = 0; f < faceCount; f++) {
    const i0 = indices ? indices.getX(f * 3) : f * 3;
    const i1 = indices ? indices.getX(f * 3 + 1) : f * 3 + 1;
    const i2 = indices ? indices.getX(f * 3 + 2) : f * 3 + 2;

    vA.fromBufferAttribute(positions, i0);
    vB.fromBufferAttribute(positions, i1);
    vC.fromBufferAttribute(positions, i2);

    cb.subVectors(vC, vB);
    ab.subVectors(vA, vB);
    normal.crossVectors(cb, ab).normalize();

    // Normal
    view.setFloat32(offset, normal.x, true); offset += 4;
    view.setFloat32(offset, normal.y, true); offset += 4;
    view.setFloat32(offset, normal.z, true); offset += 4;

    // Vertex A
    view.setFloat32(offset, vA.x, true); offset += 4;
    view.setFloat32(offset, vA.y, true); offset += 4;
    view.setFloat32(offset, vA.z, true); offset += 4;

    // Vertex B
    view.setFloat32(offset, vB.x, true); offset += 4;
    view.setFloat32(offset, vB.y, true); offset += 4;
    view.setFloat32(offset, vB.z, true); offset += 4;

    // Vertex C
    view.setFloat32(offset, vC.x, true); offset += 4;
    view.setFloat32(offset, vC.y, true); offset += 4;
    view.setFloat32(offset, vC.z, true); offset += 4;

    // Attribute byte count
    view.setUint16(offset, 0, true); offset += 2;
  }

  const blob = new Blob([buffer], { type: 'application/octet-stream' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = sourceFilename.replace(/\.\w+$/, '') + '_simplified.stl';
  link.click();
  URL.revokeObjectURL(url);
}
