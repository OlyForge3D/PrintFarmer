/**
 * Pure geometry helpers for the cut tool. Extracted from CutPlaneOverlay so the
 * algorithms can be unit-tested without bundling the React component (and so
 * the component file remains a pure component module for HMR / fast-refresh).
 */
import * as THREE from 'three';

export type CutAxis = 'x' | 'y' | 'z';

/** Linearly interpolate between two 3D points. */
function lerpVertex(a: THREE.Vector3, b: THREE.Vector3, t: number): THREE.Vector3 {
  return new THREE.Vector3(
    a.x + (b.x - a.x) * t,
    a.y + (b.y - a.y) * t,
    a.z + (b.z - a.z) * t,
  );
}

/**
 * Order cap edges into one or more closed polygon loops by matching endpoints.
 * A planar cross-section of a non-convex / multi-region mesh may produce several
 * disjoint loops; each is returned as a separate polygon (without repeating the
 * first vertex at the end).
 */
export function orderCapEdges(
  edges: Array<[THREE.Vector3, THREE.Vector3]>,
  epsilon = 1e-5,
): THREE.Vector3[][] {
  if (edges.length === 0) return [];
  const eq = (a: THREE.Vector3, b: THREE.Vector3) => a.distanceTo(b) < epsilon;

  const loops: THREE.Vector3[][] = [];
  const used = new Set<number>();

  while (used.size < edges.length) {
    let seed = -1;
    for (let i = 0; i < edges.length; i++) {
      if (!used.has(i)) { seed = i; break; }
    }
    if (seed === -1) break;

    const polygon: THREE.Vector3[] = [edges[seed][0], edges[seed][1]];
    used.add(seed);

    while (used.size < edges.length) {
      const last = polygon[polygon.length - 1];
      let found = false;
      for (let i = 0; i < edges.length; i++) {
        if (used.has(i)) continue;
        if (eq(edges[i][0], last)) {
          polygon.push(edges[i][1]);
          used.add(i);
          found = true;
          break;
        }
        if (eq(edges[i][1], last)) {
          polygon.push(edges[i][0]);
          used.add(i);
          found = true;
          break;
        }
      }
      if (!found) break;
      if (eq(polygon[0], polygon[polygon.length - 1])) {
        polygon.pop();
        break;
      }
    }

    if (polygon.length >= 3) loops.push(polygon);
  }

  return loops;
}

/**
 * Compute 2D signed area of a polygon in the given projection.
 * Positive = CCW, negative = CW.
 */
function signedArea2D(
  polygon: THREE.Vector3[],
  project: (v: THREE.Vector3) => [number, number],
): number {
  let area = 0;
  for (let i = 0; i < polygon.length; i++) {
    const [x1, y1] = project(polygon[i]);
    const [x2, y2] = project(polygon[(i + 1) % polygon.length]);
    area += x1 * y2 - x2 * y1;
  }
  return area;
}

/**
 * Bridge hole polygons into the outer polygon to produce a single simple
 * polygon suitable for ear-clipping.  Uses the standard rightmost-vertex
 * ray-cast algorithm: for each hole find its rightmost projected vertex,
 * cast a ray to +X, locate the nearest outer-polygon edge, and insert a
 * zero-width bridge connecting the two polygons.
 *
 * Outer polygon must be CCW; holes must be CW (in the 2D projection).
 */
function bridgeHoles(
  outer: THREE.Vector3[],
  holes: THREE.Vector3[][],
  project: (v: THREE.Vector3) => [number, number],
): THREE.Vector3[] {
  if (holes.length === 0) return outer;

  // Ensure winding: outer CCW, holes CW
  let merged = signedArea2D(outer, project) >= 0 ? [...outer] : [...outer].reverse();
  const cwHoles = holes.map(h =>
    signedArea2D(h, project) < 0 ? [...h] : [...h].reverse(),
  );

  // Sort holes by rightmost projected X (descending) so the outermost
  // holes are bridged first, keeping bridge edges short.
  const holeInfos = cwHoles.map(hole => {
    let maxX = -Infinity;
    let idx = 0;
    for (let i = 0; i < hole.length; i++) {
      const x = project(hole[i])[0];
      if (x > maxX) { maxX = x; idx = i; }
    }
    return { hole, rightIdx: idx, rightX: maxX };
  }).sort((a, b) => b.rightX - a.rightX);

  for (const { hole, rightIdx } of holeInfos) {
    const hv = hole[rightIdx];
    const [hx, hy] = project(hv);

    // Cast a horizontal ray to +X from hv; find the nearest intersecting
    // edge of `merged`.
    let bestDist = Infinity;
    let bestEdge = -1;
    let bestIx = 0;
    for (let i = 0; i < merged.length; i++) {
      const j = (i + 1) % merged.length;
      const [ay, ax] = [project(merged[i])[1], project(merged[i])[0]];
      const [by, bx] = [project(merged[j])[1], project(merged[j])[0]];
      // Edge must straddle the ray's Y
      if ((ay <= hy && by <= hy) || (ay > hy && by > hy)) continue;
      const t = (hy - ay) / (by - ay);
      const ix = ax + t * (bx - ax);
      if (ix < hx) continue;
      const d = ix - hx;
      if (d < bestDist) { bestDist = d; bestEdge = i; bestIx = ix; }
    }
    if (bestEdge === -1) continue;

    // Pick the bridge vertex on the outer polygon: the endpoint of the
    // intersected edge with the larger projected X (visible from hv).
    const eA = bestEdge;
    const eB = (bestEdge + 1) % merged.length;
    let bridgeIdx = project(merged[eA])[0] >= project(merged[eB])[0] ? eA : eB;

    // Reflex-vertex visibility refinement: check if any merged vertex
    // sits inside the triangle (hv, intersection, candidate) and is
    // closer to hv — if so, use that vertex as the bridge target.
    const [cx, cy] = project(merged[bridgeIdx]);
    for (let k = 0; k < merged.length; k++) {
      if (k === bridgeIdx) continue;
      const [px, py] = project(merged[k]);
      // Must be inside the X-band [hx, bestIx] and Y-band
      if (px < hx || px > bestIx) continue;
      const minY = Math.min(hy, cy);
      const maxY = Math.max(hy, cy);
      if (py < minY || py > maxY) continue;
      if (pointInTriangle2D(px, py, hx, hy, bestIx, hy, cx, cy)) {
        // Closer reflex vertex — use it instead
        const dOld = (cx - hx) * (cx - hx) + (cy - hy) * (cy - hy);
        const dNew = (px - hx) * (px - hx) + (py - hy) * (py - hy);
        if (dNew < dOld) {
          bridgeIdx = k;
        }
      }
    }

    // Reorder hole starting from rightIdx
    const reordered: THREE.Vector3[] = [];
    for (let k = 0; k < hole.length; k++) {
      reordered.push(hole[(rightIdx + k) % hole.length]);
    }

    // Splice: merged[..bridgeIdx] → hole[rightIdx..] → hole[rightIdx] → merged[bridgeIdx] → merged[bridgeIdx+1..]
    merged = [
      ...merged.slice(0, bridgeIdx + 1),
      ...reordered,
      reordered[0].clone(),
      merged[bridgeIdx].clone(),
      ...merged.slice(bridgeIdx + 1),
    ];
  }

  return merged;
}

/** Drop adjacent duplicate and collinear vertices from a polygon. */
function removeCollinearVertices(
  polygon: THREE.Vector3[],
  project: (v: THREE.Vector3) => [number, number],
  epsilon = 1e-9,
): THREE.Vector3[] {
  if (polygon.length < 3) return polygon;
  const out: THREE.Vector3[] = [];
  const n = polygon.length;
  for (let i = 0; i < n; i++) {
    const prev = polygon[(i + n - 1) % n];
    const curr = polygon[i];
    const next = polygon[(i + 1) % n];
    const [px, py] = project(prev);
    const [cx, cy] = project(curr);
    const [nx, ny] = project(next);
    if (Math.abs(cx - px) < epsilon && Math.abs(cy - py) < epsilon) continue;
    const cross = (cx - px) * (ny - py) - (cy - py) * (nx - px);
    if (Math.abs(cross) < epsilon) continue;
    out.push(curr);
  }
  return out.length >= 3 ? out : polygon;
}

/** Check if point (px,py) is inside triangle (x1,y1)-(x2,y2)-(x3,y3). */
function pointInTriangle2D(
  px: number, py: number,
  x1: number, y1: number,
  x2: number, y2: number,
  x3: number, y3: number,
): boolean {
  const d1 = (px - x3) * (y1 - y3) - (x1 - x3) * (py - y3);
  const d2 = (px - x1) * (y2 - y1) - (x2 - x1) * (py - y1);
  const d3 = (px - x2) * (y3 - y2) - (x3 - x2) * (py - y2);
  return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
}

/**
 * Triangulate a polygon using ear-clipping. Projects to 2D such that CCW winding
 * in the projection corresponds to a +cutAxis triangle normal in 3D.
 */
export function earClipTriangulate(
  polygon: THREE.Vector3[],
  axis: CutAxis,
): Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> {
  if (polygon.length < 3) return [];
  if (polygon.length === 3) return [[polygon[0], polygon[1], polygon[2]]];

  const project = (v: THREE.Vector3): [number, number] => {
    if (axis === 'x') return [v.y, v.z];
    if (axis === 'y') return [v.z, v.x];
    return [v.x, v.y];
  };

  let work = removeCollinearVertices(polygon, project);
  if (work.length < 3) return [];
  if (work.length === 3) return [[work[0], work[1], work[2]]];

  let signedArea = 0;
  for (let i = 0; i < work.length; i++) {
    const [x1, y1] = project(work[i]);
    const [x2, y2] = project(work[(i + 1) % work.length]);
    signedArea += x1 * y2 - x2 * y1;
  }

  if (signedArea < 0) work = work.slice().reverse();

  const indices = Array.from({ length: work.length }, (_, i) => i);
  const triangles: Array<[THREE.Vector3, THREE.Vector3, THREE.Vector3]> = [];

  let safety = indices.length * indices.length + 4;
  while (indices.length > 3) {
    let earFound = false;
    for (let i = 0; i < indices.length; i++) {
      const prevIdx = indices[(i + indices.length - 1) % indices.length];
      const currIdx = indices[i];
      const nextIdx = indices[(i + 1) % indices.length];

      const [px, py] = project(work[prevIdx]);
      const [cx, cy] = project(work[currIdx]);
      const [nx, ny] = project(work[nextIdx]);

      const cross = (cx - px) * (ny - py) - (cy - py) * (nx - px);
      if (cross <= 1e-12) continue;

      let containsPoint = false;
      for (const idx of indices) {
        if (idx === prevIdx || idx === currIdx || idx === nextIdx) continue;
        const [tx, ty] = project(work[idx]);
        // Skip vertices coincident with ear vertices (from hole bridges)
        if (Math.abs(tx - px) < 1e-8 && Math.abs(ty - py) < 1e-8) continue;
        if (Math.abs(tx - cx) < 1e-8 && Math.abs(ty - cy) < 1e-8) continue;
        if (Math.abs(tx - nx) < 1e-8 && Math.abs(ty - ny) < 1e-8) continue;
        if (pointInTriangle2D(tx, ty, px, py, cx, cy, nx, ny)) {
          containsPoint = true;
          break;
        }
      }
      if (containsPoint) continue;

      triangles.push([work[prevIdx], work[currIdx], work[nextIdx]]);
      indices.splice(i, 1);
      earFound = true;
      break;
    }
    if (!earFound) {
      if (--safety <= 0) {
        console.warn('earClipTriangulate: fallback fan triangulation', {
          remaining: indices.length,
          axis,
        });
        for (let i = 1; i < indices.length - 1; i++) {
          triangles.push([work[indices[0]], work[indices[i]], work[indices[i + 1]]]);
        }
        return triangles;
      }
    }
  }

  if (indices.length === 3) {
    triangles.push([work[indices[0]], work[indices[1]], work[indices[2]]]);
  }
  return triangles;
}

/** Filter out degenerate (near-zero-area) triangles from a flat vertex array. */
export function filterDegenerateTriangles(verts: number[], minArea2 = 1e-16): number[] {
  const result: number[] = [];
  for (let i = 0; i < verts.length; i += 9) {
    const abx = verts[i + 3] - verts[i],     aby = verts[i + 4] - verts[i + 1], abz = verts[i + 5] - verts[i + 2];
    const acx = verts[i + 6] - verts[i],     acy = verts[i + 7] - verts[i + 1], acz = verts[i + 8] - verts[i + 2];
    const nx = aby * acz - abz * acy;
    const ny = abz * acx - abx * acz;
    const nz = abx * acy - aby * acx;
    if (nx * nx + ny * ny + nz * nz > minArea2) {
      result.push(
        verts[i], verts[i + 1], verts[i + 2],
        verts[i + 3], verts[i + 4], verts[i + 5],
        verts[i + 6], verts[i + 7], verts[i + 8],
      );
    }
  }
  return result;
}

/**
 * Split geometry along a world-space plane. Caller specifies the plane axis
 * (which determines the +/- side meaning) and a point on the plane in world
 * space. The plane is transformed into model-local space via the inverse of
 * `modelMatrix`, supporting rotated/scaled parents correctly.
 *
 * "above" = vertices on the +cutAxis side of the plane in world space.
 * "below" = vertices on the -cutAxis side.
 */
export function splitGeometryAtPlane(
  geometry: THREE.BufferGeometry,
  axis: CutAxis,
  worldPlanePos: number,
  modelMatrix?: THREE.Matrix4,
): { above: THREE.BufferGeometry; below: THREE.BufferGeometry } {
  const worldNormal = axis === 'x'
    ? new THREE.Vector3(1, 0, 0)
    : axis === 'y'
    ? new THREE.Vector3(0, 1, 0)
    : new THREE.Vector3(0, 0, 1);
  const worldPlane = new THREE.Plane(worldNormal.clone(), -worldPlanePos);

  const localPlane = worldPlane.clone();
  if (modelMatrix) {
    const inv = modelMatrix.clone().invert();
    localPlane.applyMatrix4(inv);
  }

  const posAttr = geometry.getAttribute('position');
  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;

  const aboveVerts: number[] = [];
  const belowVerts: number[] = [];
  const capEdges: Array<[THREE.Vector3, THREE.Vector3]> = [];

  const getVertex = (i: number): THREE.Vector3 => new THREE.Vector3(
    posAttr.getX(i),
    posAttr.getY(i),
    posAttr.getZ(i),
  );

  const signedDist = (v: THREE.Vector3): number => localPlane.distanceToPoint(v);
  const classify = (d: number, eps = 1e-6): -1 | 0 | 1 =>
    d > eps ? 1 : d < -eps ? -1 : 0;

  const intersectOnPlane = (a: THREE.Vector3, b: THREE.Vector3, dA: number, dB: number): THREE.Vector3 => {
    const denom = dA - dB;
    if (Math.abs(denom) < 1e-12) return a.clone();
    const t = Math.max(0, Math.min(1, dA / denom));
    return lerpVertex(a, b, t);
  };

  const pushTriVerts = (arr: number[], a: THREE.Vector3, b: THREE.Vector3, c: THREE.Vector3) => {
    arr.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
  };

  for (let t = 0; t < triCount; t++) {
    const i0 = index ? index.getX(t * 3) : t * 3;
    const i1 = index ? index.getX(t * 3 + 1) : t * 3 + 1;
    const i2 = index ? index.getX(t * 3 + 2) : t * 3 + 2;

    const v0 = getVertex(i0);
    const v1 = getVertex(i1);
    const v2 = getVertex(i2);

    const d0 = signedDist(v0);
    const d1 = signedDist(v1);
    const d2 = signedDist(v2);

    const c0 = classify(d0);
    const c1 = classify(d1);
    const c2 = classify(d2);

    if (c0 >= 0 && c1 >= 0 && c2 >= 0) {
      pushTriVerts(aboveVerts, v0, v1, v2);
      continue;
    }
    if (c0 <= 0 && c1 <= 0 && c2 <= 0) {
      pushTriVerts(belowVerts, v0, v1, v2);
      continue;
    }

    const verts = [v0, v1, v2];
    const dists = [d0, d1, d2];
    const classes = [c0, c1, c2];

    let loneIdx = -1;
    for (let i = 0; i < 3; i++) {
      const ci = classes[i];
      const cj = classes[(i + 1) % 3];
      const ck = classes[(i + 2) % 3];
      if (ci !== 0 && ((ci > 0 && cj <= 0 && ck <= 0) || (ci < 0 && cj >= 0 && ck >= 0))) {
        loneIdx = i;
        break;
      }
    }

    if (loneIdx === -1) {
      if (d0 + d1 + d2 > 0) pushTriVerts(aboveVerts, v0, v1, v2);
      else pushTriVerts(belowVerts, v0, v1, v2);
      continue;
    }

    const vA = verts[loneIdx];
    const vB = verts[(loneIdx + 1) % 3];
    const vC = verts[(loneIdx + 2) % 3];
    const dA = dists[loneIdx];
    const dB = dists[(loneIdx + 1) % 3];
    const dC = dists[(loneIdx + 2) % 3];
    const cA = classes[loneIdx];

    const pAB = intersectOnPlane(vA, vB, dA, dB);
    const pAC = intersectOnPlane(vA, vC, dA, dC);

    capEdges.push([pAB.clone(), pAC.clone()]);

    if (cA > 0) {
      pushTriVerts(aboveVerts, vA, pAB, pAC);
      pushTriVerts(belowVerts, pAB, vB, vC);
      pushTriVerts(belowVerts, pAB, vC, pAC);
    } else {
      pushTriVerts(belowVerts, vA, pAB, pAC);
      pushTriVerts(aboveVerts, pAB, vB, vC);
      pushTriVerts(aboveVerts, pAB, vC, pAC);
    }
  }

  if (capEdges.length > 0) {
    const ln = localPlane.normal;
    const localAxis: CutAxis =
      Math.abs(ln.x) >= Math.abs(ln.y) && Math.abs(ln.x) >= Math.abs(ln.z) ? 'x'
        : Math.abs(ln.y) >= Math.abs(ln.z) ? 'y'
        : 'z';
    const loops = orderCapEdges(capEdges);

    const projectXY = (v: THREE.Vector3): [number, number] =>
      localAxis === 'x' ? [v.y, v.z]
        : localAxis === 'y' ? [v.z, v.x]
        : [v.x, v.y];

    // Detect nested loops (hollow cross-sections).  When one loop's AABB
    // is entirely inside another's the inner loop is a hole that must be
    // bridged into the outer polygon before triangulation.
    let polygonsToTriangulate: THREE.Vector3[][] = [];
    if (loops.length >= 2) {
      const aabbs = loops.map((loop) => {
        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const v of loop) {
          const [x, y] = projectXY(v);
          if (x < minX) minX = x; if (y < minY) minY = y;
          if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }
        return { minX, minY, maxX, maxY };
      });
      const areas = loops.map(l => Math.abs(signedArea2D(l, projectXY)));

      // Build containment map: parent[i] = index of the loop that
      // directly contains loop i (-1 if top-level).
      const parent = new Array<number>(loops.length).fill(-1);
      for (let i = 0; i < loops.length; i++) {
        let bestArea = Infinity;
        for (let j = 0; j < loops.length; j++) {
          if (i === j) continue;
          const a = aabbs[i], b = aabbs[j];
          if (a.minX >= b.minX && a.minY >= b.minY && a.maxX <= b.maxX && a.maxY <= b.maxY) {
            if (areas[j] < bestArea) { bestArea = areas[j]; parent[i] = j; }
          }
        }
      }

      // Group: top-level loops (parent === -1) are outers; their direct
      // children are holes.
      const childrenOf = new Map<number, number[]>();
      const topLevel: number[] = [];
      for (let i = 0; i < loops.length; i++) {
        if (parent[i] === -1) {
          topLevel.push(i);
        } else {
          const siblings = childrenOf.get(parent[i]) ?? [];
          siblings.push(i);
          childrenOf.set(parent[i], siblings);
        }
      }

      for (const outerIdx of topLevel) {
        const holes = (childrenOf.get(outerIdx) ?? []).map(i => loops[i]);
        if (holes.length > 0) {
          polygonsToTriangulate.push(bridgeHoles(loops[outerIdx], holes, projectXY));
        } else {
          polygonsToTriangulate.push(loops[outerIdx]);
        }
        // Holes' own children become independent outers (nested solids
        // inside a hollow); handle recursively would be ideal but for
        // practical 3D-print cross-sections one nesting level suffices.
        for (const holeIdx of childrenOf.get(outerIdx) ?? []) {
          for (const grandchild of childrenOf.get(holeIdx) ?? []) {
            polygonsToTriangulate.push(loops[grandchild]);
          }
        }
      }
    } else {
      polygonsToTriangulate = loops;
    }

    for (const polygon of polygonsToTriangulate) {
      if (polygon.length < 3) continue;
      const tris = earClipTriangulate(polygon, localAxis);
      for (const [a, b, c] of tris) {
        pushTriVerts(belowVerts, a, b, c);
        pushTriVerts(aboveVerts, c, b, a);
      }
    }
  }

  const makeGeo = (verts: number[]): THREE.BufferGeometry => {
    const filtered = filterDegenerateTriangles(verts);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(filtered, 3));
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    return geo;
  };

  return {
    above: makeGeo(aboveVerts),
    below: makeGeo(belowVerts),
  };
}
