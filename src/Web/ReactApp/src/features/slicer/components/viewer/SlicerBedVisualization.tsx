/**
 * Slicer 3D Bed Visualization Component
 * A Three.js canvas showing the print bed similar to OrcaSlicer
 */
import React, { Component, Suspense, useRef, useState, useEffect, useCallback, useMemo } from 'react';
import type { ReactNode, ErrorInfo } from 'react';
import { Canvas, useThree, useLoader, useFrame } from '@react-three/fiber';
import { OrbitControls, Environment, Html, TransformControls } from '@react-three/drei';
import { STLLoader } from 'three-stdlib';
import * as THREE from 'three';
import { toast } from 'sonner';
import { FacePaintOverlay } from './FacePaintOverlay';
import { CutPlaneOverlay } from './CutPlaneOverlay';

// W4: Module-level constant to avoid creating new Set on every render
const EMPTY_FACE_SET = new Set<number>();

export interface LoadedModel {
  id: string;
  url: string;
  fileName: string;
  fileType: 'stl' | 'ply' | '3mf';
  position: [number, number, number];
  rotation: [number, number, number];
  scale: [number, number, number];
  /** Pre-built geometry (e.g., from a cut operation) — bypasses URL loading */
  geometry?: THREE.BufferGeometry;
}

export interface BedConfig {
  width: number;  // X dimension in mm
  depth: number;  // Y dimension in mm  
  height: number; // Z dimension in mm (build volume height)
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
  bedModelUrl?: string; // STL URL for 3D bed model
  originCenter?: boolean; // If true, origin is at bed center; if false, at corner
}

export interface SlicerBedVisualizationProps {
  bedConfig: BedConfig;
  models?: LoadedModel[];
  selectedModelId?: string;
  onModelSelect?: (modelId: string | null) => void;
  /** Active transform tool: 'translate' | 'rotate' | 'scale' */
  transformMode?: 'translate' | 'rotate' | 'scale' | null;
  /** Called when a model is moved/rotated/scaled via TransformControls */
  onModelTransform?: (
    modelId: string,
    position: [number, number, number],
    rotation: [number, number, number],
    scale: [number, number, number],
    options?: {
      recordHistory?: boolean;
      actionLabel?: string;
      historyBefore?: {
        position: [number, number, number];
        rotation: [number, number, number];
        scale: [number, number, number];
      };
    },
  ) => void;
  /** Called with current selected model sizing information for scale UI */
  onSelectedModelMetricsChange?: (metrics: {
    modelId: string;
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  } | null) => void;
  showGrid?: boolean;
  showAxes?: boolean;
  showGridLines?: boolean;
  gridDivisions?: number;
  backgroundColor?: string;
  className?: string;
  /** Set of model IDs that are currently outside the build volume */
  outOfBoundsModelIds?: Set<string>;
  /** When true, face swatches are shown on the selected model for lay-flat picking */
  layFlatMode?: boolean;
  /** Called after a face is clicked in lay-flat mode (signals completion) */
  onLayFlatComplete?: () => void;
  /** Increment to trigger auto-orient on the selected model */
  autoOrientTrigger?: number;
  /** When true, measure tool is active for point-to-point distance */
  measureMode?: boolean;
  /** When true, models are offset radially for exploded assembly inspection */
  assemblyViewActive?: boolean;
  /** Increment to trigger connected-component split analysis on selected model */
  splitTrigger?: number;
  /** Whether cut mode is active */
  cutMode?: boolean;
  /** Called when a cut is completed with two new geometries */
  onCutComplete?: (geometryAbove: THREE.BufferGeometry, geometryBelow: THREE.BufferGeometry) => void;
  /** Called when cut is cancelled */
  onCutCancel?: () => void;
  /** Whether support paint mode is active */
  supportPaintMode?: boolean;
  /** Painted support face indices per model */
  supportPaintData?: Map<string, Set<number>>;
  /** Called when support paint data changes */
  onSupportPaintUpdate?: (faces: Set<number>) => void;
  /** Whether seam paint mode is active */
  seamPaintMode?: boolean;
  /** Painted seam face indices per model */
  seamPaintData?: Map<string, Set<number>>;
  /** Called when seam paint data changes */
  onSeamPaintUpdate?: (faces: Set<number>) => void;
}

/**
 * Loading indicator shown while models load
 */
function LoadingIndicator() {
  return (
    <Html center>
      <div className="bg-pf-bg-2/90 backdrop-blur-sm px-4 py-2 rounded-lg border border-pf-border shadow-lg">
        <div className="text-sm font-medium text-pf-text-primary flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          Loading model...
        </div>
      </div>
    </Html>
  );
}

/**
 * Textured print bed platform component (PNG via Three.js TextureLoader)
 */
/**
 * Textured print bed platform component (PNG via canvas compositing).
 * Many OrcaSlicer textures use alpha transparency — they look dark on a dark
 * background but invisible on white. We composite onto a dark canvas first.
 */
function TexturedPrintBed({ 
  width, 
  depth, 
  textureUrl 
}: { 
  width: number; 
  depth: number; 
  textureUrl: string;
}) {
  const thickness = 2;
  const meshRef = useRef<THREE.Mesh>(null);
  const [composited, setComposited] = useState<THREE.CanvasTexture | null>(null);

  useEffect(() => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = img.naturalWidth;
      canvas.height = img.naturalHeight;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      // Dark background matching OrcaSlicer's bed color
      ctx.fillStyle = '#2a2a3a';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      // Draw texture on top — alpha-transparent pixels blend with dark background
      ctx.drawImage(img, 0, 0);
      const tex = new THREE.CanvasTexture(canvas);
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.minFilter = THREE.LinearMipmapLinearFilter;
      tex.magFilter = THREE.LinearFilter;
      setComposited(tex);
    };
    img.src = textureUrl;
    return () => { img.onload = null; };
  }, [textureUrl]);

  useEffect(() => {
    return () => { composited?.dispose(); };
  }, [composited]);

  if (!composited) {
    return <PlainPrintBed width={width} depth={depth} />;
  }

  return (
    <mesh 
      ref={meshRef}
      position={[0, 0, -thickness / 2]} 
      receiveShadow
    >
      <boxGeometry args={[width, depth, thickness]} />
      <meshBasicMaterial 
        map={composited}
      />
    </mesh>
  );
}

/**
 * SVG textured print bed — rasterizes the SVG onto a high-res canvas,
 * then creates a CanvasTexture for Three.js.
 * Resolution is based on bed dimensions (4 pixels per mm) for crisp rendering.
 */
function SvgTexturedPrintBed({
  width,
  depth,
  textureUrl,
}: {
  width: number;
  depth: number;
  textureUrl: string;
}) {
  const thickness = 2;
  const meshRef = useRef<THREE.Mesh>(null);
  const [texture, setTexture] = useState<THREE.CanvasTexture | null>(null);

  useEffect(() => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
      // Use bed dimensions at 4px/mm for crisp textures, clamped to 4096 for GPU limits
      const pxPerMm = 4;
      const maxDim = 4096;
      const w = Math.min(Math.round(width * pxPerMm), maxDim);
      const h = Math.min(Math.round(depth * pxPerMm), maxDim);
      const canvas = document.createElement('canvas');
      canvas.width = w;
      canvas.height = h;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      // Dark background — SVGs may use alpha transparency
      ctx.fillStyle = '#2a2a3a';
      ctx.fillRect(0, 0, w, h);
      ctx.drawImage(img, 0, 0, w, h);
      const tex = new THREE.CanvasTexture(canvas);
      tex.colorSpace = THREE.SRGBColorSpace;
      tex.minFilter = THREE.LinearMipmapLinearFilter;
      tex.magFilter = THREE.LinearFilter;
      tex.anisotropy = 4;
      setTexture(tex);
    };
    img.src = textureUrl;

    return () => {
      img.onload = null;
    };
  }, [textureUrl, width, depth]);

  // Dispose old texture when replaced or unmounted
  useEffect(() => {
    return () => { texture?.dispose(); };
  }, [texture]);

  if (!texture) {
    return <PlainPrintBed width={width} depth={depth} />;
  }

  return (
    <mesh
      ref={meshRef}
      position={[0, 0, -thickness / 2]}
      receiveShadow
    >
      <boxGeometry args={[width, depth, thickness]} />
      <meshBasicMaterial
        map={texture}
      />
    </mesh>
  );
}

/**
 * Plain print bed platform component
 */
function PlainPrintBed({ 
  width, 
  depth 
}: { 
  width: number; 
  depth: number; 
}) {
  const thickness = 2;
  const meshRef = useRef<THREE.Mesh>(null);

  return (
    <mesh 
      ref={meshRef}
      position={[0, 0, -thickness / 2]} 
      receiveShadow
    >
      <boxGeometry args={[width, depth, thickness]} />
      <meshStandardMaterial 
        color="#2a2a3a"
        metalness={0.05}
        roughness={0.85}
      />
    </mesh>
  );
}

/**
 * Error boundary that catches texture load failures and falls back to plain bed.
 * Required because useLoader(TextureLoader, url) throws when the file is missing (404).
 */
interface TextureFallbackBoundaryProps {
  children: ReactNode;
  fallback: ReactNode;
}
interface TextureFallbackBoundaryState {
  hasError: boolean;
}
class TextureFallbackBoundary extends Component<TextureFallbackBoundaryProps, TextureFallbackBoundaryState> {
  state: TextureFallbackBoundaryState = { hasError: false };
  static getDerivedStateFromError(): TextureFallbackBoundaryState {
    return { hasError: true };
  }
  componentDidCatch(error: Error, info: ErrorInfo) {
    console.warn('[SlicerBed] Bed texture load failed, using plain bed:', error.message, info.componentStack);
  }
  render() {
    return this.state.hasError ? this.props.fallback : this.props.children;
  }
}

/**
 * Line-based grid overlay for the bed surface.
 * Uses actual line geometry so the bed texture is fully visible between lines.
 */
function BedGridLines({
  width,
  depth,
  cellSize = 10,
  sectionSize = 50,
}: {
  width: number;
  depth: number;
  cellSize?: number;
  sectionSize?: number;
}) {
  const gridRef = useRef<THREE.Group>(null);

  const { cellLines, sectionLines } = React.useMemo(() => {
    const halfW = width / 2;
    const halfD = depth / 2;
    const cellVerts: number[] = [];
    const sectionVerts: number[] = [];

    // Lines parallel to Y axis (varying X)
    for (let x = -halfW; x <= halfW + 0.01; x += cellSize) {
      const rounded = Math.round(x / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(rounded, -halfD, 0, rounded, halfD, 0);
    }

    // Lines parallel to X axis (varying Y)
    for (let y = -halfD; y <= halfD + 0.01; y += cellSize) {
      const rounded = Math.round(y / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(-halfW, rounded, 0, halfW, rounded, 0);
    }

    return {
      cellLines: new Float32Array(cellVerts),
      sectionLines: new Float32Array(sectionVerts),
    };
  }, [width, depth, cellSize, sectionSize]);

  return (
    <group ref={gridRef} position={[0, 0, 0.05]}>
      {/* Fine cell lines */}
      <lineSegments>
        <bufferGeometry>
          <float32BufferAttribute attach="attributes-position" args={[cellLines, 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#555577" transparent opacity={0.5} />
      </lineSegments>
      {/* Major section lines */}
      <lineSegments>
        <bufferGeometry>
          <float32BufferAttribute attach="attributes-position" args={[sectionLines, 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#7777aa" transparent opacity={0.7} />
      </lineSegments>
    </group>
  );
}

/**
 * Print bed platform with optional texture
 */
function PrintBedPlatform({ 
  width, 
  depth, 
  textureUrl, 
  textureFormat,
  showGridLines,
}: { 
  width: number; 
  depth: number; 
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
  showGridLines?: boolean;
}) {
  const shouldUsePngTexture = textureUrl && textureFormat === 'png';
  const shouldUseSvgTexture = textureUrl && textureFormat === 'svg';

  return (
    <group>
      {/* Main bed surface */}
      {shouldUsePngTexture ? (
        <TextureFallbackBoundary fallback={<PlainPrintBed width={width} depth={depth} />}>
          <Suspense fallback={<PlainPrintBed width={width} depth={depth} />}>
            <TexturedPrintBed width={width} depth={depth} textureUrl={textureUrl} />
          </Suspense>
        </TextureFallbackBoundary>
      ) : shouldUseSvgTexture ? (
        <SvgTexturedPrintBed width={width} depth={depth} textureUrl={textureUrl} />
      ) : (
        <PlainPrintBed width={width} depth={depth} />
      )}

      {/* Bed edge outline */}
      <lineSegments position={[0, 0, 0.01]}>
        <edgesGeometry attach="geometry">
          <planeGeometry args={[width, depth]} />
        </edgesGeometry>
        <lineBasicMaterial color="#4a4a6a" linewidth={2} />
      </lineSegments>

      {/* Grid lines — always shown when toggled on, otherwise only for plain beds */}
      {(showGridLines || (!shouldUsePngTexture && !shouldUseSvgTexture)) && (
        <BedGridLines width={width} depth={depth} cellSize={10} sectionSize={50} />
      )}
    </group>
  );
}

/**
 * Build volume wireframe visualization
 */
function BuildVolumeWireframe({ width, depth, height }: { width: number; depth: number; height: number }) {
  return (
    <lineSegments position={[0, 0, height / 2]}>
      <edgesGeometry attach="geometry">
        <boxGeometry args={[width, depth, height]} />
      </edgesGeometry>
      <lineBasicMaterial 
        color="#00ff88" 
        linewidth={1} 
        transparent 
        opacity={0.4} 
      />
    </lineSegments>
  );
}

/**
 * Axis indicators at origin
 */
function AxisIndicators({ size = 30 }: { size?: number }) {
  return (
    <group position={[0, 0, 0.1]}>
      {/* X axis - Red */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, size, 0, 0]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#ff0000" linewidth={3} />
      </line>
      
      {/* Y axis - Green */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, 0, size, 0]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#00ff00" linewidth={3} />
      </line>
      
      {/* Z axis - Blue */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, 0, 0, size]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#0088ff" linewidth={3} />
      </line>
    </group>
  );
}

/**
 * Wireframe bounding box rendered around the selected model.
 * Positioned in the mesh's local coordinate space so it inherits
 * all parent transforms (position, rotation, scale) automatically.
 */
function SelectionBoundingBox({ geometry, outOfBounds = false }: { geometry: THREE.BufferGeometry; outOfBounds?: boolean }) {
  const PADDING = 2; // visual padding around the model so the box is clearly outside surfaces
  const CORNER_LENGTH = 10; // length of corner indicator lines
  
  const { center, cornerLines } = useMemo(() => {
    const box = new THREE.Box3();
    geometry.computeBoundingBox();
    if (geometry.boundingBox) {
      box.copy(geometry.boundingBox);
    }
    const s = new THREE.Vector3();
    const c = new THREE.Vector3();
    box.getSize(s);
    box.getCenter(c);
    
    // Calculate padded dimensions
    const hsX = (s.x + PADDING * 2) / 2;
    const hsY = (s.y + PADDING * 2) / 2;
    const hsZ = (s.z + PADDING * 2) / 2;
    
    // 8 corners of the bounding box (in local coords)
    const corners: [number, number, number][] = [
      [-hsX, -hsY, -hsZ], [hsX, -hsY, -hsZ], [hsX, hsY, -hsZ], [-hsX, hsY, -hsZ],  // bottom 4
      [-hsX, -hsY, hsZ], [hsX, -hsY, hsZ], [hsX, hsY, hsZ], [-hsX, hsY, hsZ],    // top 4
    ];
    
    // Create line segments (corner brackets): 3 lines per corner
    const lines: [number, number, number, number, number, number][] = [];
    
    corners.forEach(([x, y, z]) => {
      // Line along X axis (inward)
      lines.push([x, y, z, x + (x > 0 ? -CORNER_LENGTH : CORNER_LENGTH), y, z]);
      // Line along Y axis (inward)
      lines.push([x, y, z, x, y + (y > 0 ? -CORNER_LENGTH : CORNER_LENGTH), z]);
      // Line along Z axis (inward)
      lines.push([x, y, z, x, y, z + (z > 0 ? -CORNER_LENGTH : CORNER_LENGTH)]);
    });
    
    return {
      center: [c.x, c.y, c.z] as [number, number, number],
      cornerLines: lines,
    };
  }, [geometry]);

  const lineSegmentsRef = useRef<THREE.LineSegments>(null);
  
  // Build geometry from corner lines
  useEffect(() => {
    if (!lineSegmentsRef.current) return;
    
    const positions: number[] = [];
    cornerLines.forEach(([x1, y1, z1, x2, y2, z2]) => {
      positions.push(x1, y1, z1, x2, y2, z2);
    });
    
    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(new Float32Array(positions), 3));
    lineSegmentsRef.current.geometry = geom;
  }, [cornerLines]);

  return (
    <lineSegments ref={lineSegmentsRef} position={center} renderOrder={1000}>
      <lineBasicMaterial
        color={outOfBounds ? '#ff4444' : '#ffffff'}
        linewidth={1}
        transparent
        opacity={1}
        depthTest={false}
        toneMapped={false}
      />
    </lineSegments>
  );
}

/**
 * Detect major planar face groups from a geometry by clustering triangles
 * with similar normals. Returns the most significant faces sorted by area.
 */
function detectMajorFaces(
  geometry: THREE.BufferGeometry,
  minAreaFraction = 0.005,
  maxFaces = 14,
): Array<{ normal: THREE.Vector3; center: THREE.Vector3; area: number }> {
  const posAttr = geometry.getAttribute('position');
  if (!posAttr) return [];

  const index = geometry.getIndex();
  const triCount = index ? index.count / 3 : posAttr.count / 3;

  const vA = new THREE.Vector3();
  const vB = new THREE.Vector3();
  const vC = new THREE.Vector3();
  const edge1 = new THREE.Vector3();
  const edge2 = new THREE.Vector3();
  const fn = new THREE.Vector3();

  interface FaceCluster {
    weightedNormal: THREE.Vector3;
    weightedCenter: THREE.Vector3;
    totalArea: number;
  }

  const clusters: FaceCluster[] = [];
  const ANGLE_THRESHOLD = 0.95; // ~18°
  let totalArea = 0;

  for (let i = 0; i < triCount; i++) {
    if (index) {
      vA.fromBufferAttribute(posAttr, index.getX(i * 3));
      vB.fromBufferAttribute(posAttr, index.getX(i * 3 + 1));
      vC.fromBufferAttribute(posAttr, index.getX(i * 3 + 2));
    } else {
      vA.fromBufferAttribute(posAttr, i * 3);
      vB.fromBufferAttribute(posAttr, i * 3 + 1);
      vC.fromBufferAttribute(posAttr, i * 3 + 2);
    }

    edge1.subVectors(vB, vA);
    edge2.subVectors(vC, vA);
    fn.crossVectors(edge1, edge2);
    const area = fn.length() / 2;
    if (area < 1e-6) continue;
    fn.normalize();
    totalArea += area;

    const cx = (vA.x + vB.x + vC.x) / 3;
    const cy = (vA.y + vB.y + vC.y) / 3;
    const cz = (vA.z + vB.z + vC.z) / 3;

    let matched = false;
    for (const cluster of clusters) {
      if (cluster.weightedNormal.clone().normalize().dot(fn) > ANGLE_THRESHOLD) {
        cluster.weightedNormal.addScaledVector(fn, area);
        cluster.weightedCenter.x += cx * area;
        cluster.weightedCenter.y += cy * area;
        cluster.weightedCenter.z += cz * area;
        cluster.totalArea += area;
        matched = true;
        break;
      }
    }

    if (!matched) {
      clusters.push({
        weightedNormal: fn.clone().multiplyScalar(area),
        weightedCenter: new THREE.Vector3(cx * area, cy * area, cz * area),
        totalArea: area,
      });
    }
  }

  const minArea = totalArea * minAreaFraction;
  return clusters
    .filter((c) => c.totalArea >= minArea)
    .map((c) => ({
      normal: c.weightedNormal.normalize(),
      center: c.weightedCenter.divideScalar(c.totalArea),
      area: c.totalArea,
    }))
    .sort((a, b) => b.area - a.area)
    .slice(0, maxFaces);
}

/**
 * Oval swatch indicators rendered on major faces of a model.
 * Size is proportional to the face area. Shown in lay-flat mode
 * so the user can click a face to orient it downward.
 */
function FaceSwatches({
  geometry,
  onFaceClick,
}: {
  geometry: THREE.BufferGeometry;
  onFaceClick: (normal: THREE.Vector3) => void;
}) {
  const [hoveredIdx, setHoveredIdx] = useState<number | null>(null);

  const faces = useMemo(() => detectMajorFaces(geometry), [geometry]);

  // Compute per-face oval radii proportional to sqrt(face.area)
  const ovalParams = useMemo(() => {
    geometry.computeBoundingSphere();
    const modelRadius = geometry.boundingSphere?.radius ?? 50;
    const maxOvalRadius = modelRadius * 0.12;
    const minOvalRadius = modelRadius * 0.03;
    const largestArea = faces.length > 0 ? faces[0].area : 1;
    return faces.map((face) => {
      const frac = Math.sqrt(face.area / largestArea);
      const r = minOvalRadius + frac * (maxOvalRadius - minOvalRadius);
      return { rx: r * 1.3, ry: r }; // wider than tall → oval
    });
  }, [faces, geometry]);

  // Unit circle (scaled per-face to create ovals)
  const circleGeo = useMemo(() => new THREE.CircleGeometry(1, 32), []);

  return (
    <group>
      {faces.map((face, i) => {
        const q = new THREE.Quaternion().setFromUnitVectors(
          new THREE.Vector3(0, 0, 1),
          face.normal,
        );
        const pos = face.center
          .clone()
          .addScaledVector(face.normal, 0.5);
        const hovered = hoveredIdx === i;
        const { rx, ry } = ovalParams[i];

        return (
          <mesh
            key={i}
            position={[pos.x, pos.y, pos.z]}
            quaternion={q}
            scale={[rx, ry, 1]}
            geometry={circleGeo}
            renderOrder={999}
            onPointerDown={(e) => {
              e.stopPropagation();
              onFaceClick(face.normal);
            }}
            onPointerOver={(e) => {
              e.stopPropagation();
              setHoveredIdx(i);
              document.body.style.cursor = 'pointer';
            }}
            onPointerOut={() => {
              setHoveredIdx(null);
              document.body.style.cursor = '';
            }}
          >
            <meshBasicMaterial
              color={hovered ? '#4fc3f7' : '#ffffff'}
              transparent
              opacity={hovered ? 0.95 : 0.75}
              side={THREE.DoubleSide}
              depthTest={false}
              toneMapped={false}
            />
          </mesh>
        );
      })}
    </group>
  );
}

/**
 * Compute the data-model position Z that places the transformed model on the bed (z=0).
 * STLModel offsets by halfZ internally, so we account for that.
 */
function computeZForBedPlacement(
  geometry: THREE.BufferGeometry,
  q: THREE.Quaternion,
  scale: THREE.Vector3 = new THREE.Vector3(1, 1, 1),
): number {
  const posAttr = geometry.getAttribute('position');
  if (!posAttr) return 0;

  const v = new THREE.Vector3();
  let minRotatedZ = Infinity;
  for (let i = 0; i < posAttr.count; i++) {
    v.fromBufferAttribute(posAttr, i);
    v.multiply(scale).applyQuaternion(q);
    if (v.z < minRotatedZ) minRotatedZ = v.z;
  }

  // The centered geometry has halfZ = (max.z - min.z) / 2.
  // STLModel group position.z = data_pz + halfZ.
  // World Z of lowest vertex = data_pz + halfZ + minScaledRotatedZ.
  // For bed placement (lowest at 0): data_pz = -halfZ - minScaledRotatedZ.
  geometry.computeBoundingBox();
  const halfZ = geometry.boundingBox
    ? (geometry.boundingBox.max.z - geometry.boundingBox.min.z) / 2
    : 0;
  return -halfZ - minRotatedZ;
}

/**
 * STL Model loader component.
 *
 * Selection uses onPointerDown so the hit registers immediately on press,
 * before OrbitControls can interpret the gesture as an orbit drag.  The
 * companion onClick handler is kept so R3F still considers the mesh
 * "interactive" for its click bookkeeping (prevents onPointerMissed from
 * firing when a mesh was actually pressed).
 */
function STLModel({ 
  url, 
  position = [0, 0, 0], 
  rotation = [0, 0, 0], 
  scale = [1, 1, 1],
  selected = false,
  outOfBounds = false,
  layFlatMode = false,
  draggable = false,
  onClick,
  onDragStart,
  meshRef,
  onSelectedMetrics,
  onLayFlatFaceClick,
}: { 
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  outOfBounds?: boolean;
  layFlatMode?: boolean;
  draggable?: boolean;
  onClick?: () => void;
  onDragStart?: (clientX: number, clientY: number) => void;
  meshRef?: React.RefObject<THREE.Object3D | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
}) {
  const rawGeometry = useLoader(STLLoader, url);
  const internalRef = useRef<THREE.Group>(null);
  const ref = meshRef || internalRef;

  // Clone geometry so we don't mutate the useLoader cache, center it on ALL
  // axes so the pivot / gizmo sits at the volumetric center of the model.
  const { geometry, halfZ } = useMemo(() => {
    const geo = rawGeometry.clone();
    geo.computeBoundingBox();
    let hz = 0;
    if (geo.boundingBox) {
      const centerX = (geo.boundingBox.min.x + geo.boundingBox.max.x) / 2;
      const centerY = (geo.boundingBox.min.y + geo.boundingBox.max.y) / 2;
      const centerZ = (geo.boundingBox.min.z + geo.boundingBox.max.z) / 2;
      hz = (geo.boundingBox.max.z - geo.boundingBox.min.z) / 2;
      geo.translate(-centerX, -centerY, -centerZ);
    }
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    return { geometry: geo, halfZ: hz };
  }, [rawGeometry]);

  const baseSize = useMemo<[number, number, number]>(() => {
    geometry.computeBoundingBox();
    if (!geometry.boundingBox) {
      return [0, 0, 0];
    }

    const size = new THREE.Vector3();
    geometry.boundingBox.getSize(size);
    return [size.x, size.y, size.z];
  }, [geometry]);

  useEffect(() => {
    if (!selected || !onSelectedMetrics) return;

    onSelectedMetrics({
      baseSize,
      currentSize: [
        baseSize[0] * scale[0],
        baseSize[1] * scale[1],
        baseSize[2] * scale[2],
      ],
      currentScale: scale,
    });
  }, [baseSize, onSelectedMetrics, scale, selected]);

  // Store halfZ on the group so BedScene can compensate when reading transforms
  useEffect(() => {
    if (ref.current) {
      ref.current.userData.halfZ = halfZ;
      ref.current.userData.geometry = geometry;
    }
  }, [geometry, halfZ, ref]);

  // Group origin = volumetric center. Position Z is offset by halfZ so the
  // data-model position.z=0 means "sitting on the bed".
  return (
    <group
      ref={ref as React.RefObject<THREE.Group | null>}
      position={[position[0], position[1], position[2] + halfZ]}
      rotation={rotation}
      scale={scale}
    >
      <mesh
        geometry={geometry}
        onPointerDown={(e) => {
          e.stopPropagation();
          onClick?.();
          if (draggable && onDragStart) {
            onDragStart(e.nativeEvent.clientX, e.nativeEvent.clientY);
          }
        }}
        onClick={(e) => {
          e.stopPropagation();
          onClick?.();
        }}
        onPointerOver={(e) => {
          if (draggable) {
            e.stopPropagation();
            document.body.style.cursor = 'grab';
          }
        }}
        onPointerOut={() => {
          if (draggable) {
            document.body.style.cursor = '';
          }
        }}
        castShadow
        receiveShadow
      >
        <meshStandardMaterial 
          color="#009688"
          metalness={0.05}
          roughness={0.7}
        />
        {selected && <SelectionBoundingBox geometry={geometry} outOfBounds={outOfBounds} />}
      </mesh>
      {selected && layFlatMode && onLayFlatFaceClick && (
        <FaceSwatches geometry={geometry} onFaceClick={onLayFlatFaceClick} />
      )}
    </group>
  );
}

/**
 * STL Model component for pre-built geometries (e.g., from cut operations).
 * Same visual output as STLModel but uses a provided BufferGeometry
 * instead of loading from URL via useLoader.
 */
function PrebuiltSTLModel({
  inputGeometry,
  position = [0, 0, 0],
  rotation = [0, 0, 0],
  scale = [1, 1, 1],
  selected = false,
  outOfBounds = false,
  layFlatMode = false,
  draggable = false,
  onClick,
  onDragStart,
  meshRef,
  onSelectedMetrics,
  onLayFlatFaceClick,
}: {
  inputGeometry: THREE.BufferGeometry;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  outOfBounds?: boolean;
  layFlatMode?: boolean;
  draggable?: boolean;
  onClick?: () => void;
  onDragStart?: (clientX: number, clientY: number) => void;
  meshRef?: React.RefObject<THREE.Object3D | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
}) {
  const internalRef = useRef<THREE.Group>(null);
  const ref = meshRef || internalRef;

  // Do NOT re-center cut fragment geometry — the vertices are already in the
  // parent model's centered local space.  Re-centering would collapse both
  // halves to origin, making them overlap.  Instead offset by -bb.min.z so
  // each fragment's bottom sits at `position.z` (same contract as STLModel).
  // NOTE: cut math is Z-scalar only; X/Y rotated models are unsupported
  // (acceptable because 3D-print models always sit flat on the bed).
  const { geometry, halfZ } = useMemo(() => {
    const geo = inputGeometry.clone();
    geo.computeBoundingBox();
    const hz = geo.boundingBox ? -geo.boundingBox.min.z : 0;
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    return { geometry: geo, halfZ: hz };
  }, [inputGeometry]);

  const baseSize = useMemo<[number, number, number]>(() => {
    geometry.computeBoundingBox();
    if (!geometry.boundingBox) return [0, 0, 0];
    const size = new THREE.Vector3();
    geometry.boundingBox.getSize(size);
    return [size.x, size.y, size.z];
  }, [geometry]);

  useEffect(() => {
    if (!selected || !onSelectedMetrics) return;
    onSelectedMetrics({
      baseSize,
      currentSize: [
        baseSize[0] * scale[0],
        baseSize[1] * scale[1],
        baseSize[2] * scale[2],
      ],
      currentScale: scale,
    });
  }, [baseSize, onSelectedMetrics, scale, selected]);

  useEffect(() => {
    if (ref.current) {
      ref.current.userData.halfZ = halfZ;
      ref.current.userData.geometry = geometry;
    }
  }, [geometry, halfZ, ref]);

  return (
    <group
      ref={ref as React.RefObject<THREE.Group | null>}
      position={[position[0], position[1], position[2] + halfZ]}
      rotation={rotation}
      scale={scale}
    >
      <mesh
        geometry={geometry}
        onPointerDown={(e) => {
          e.stopPropagation();
          onClick?.();
          if (draggable && onDragStart) {
            onDragStart(e.nativeEvent.clientX, e.nativeEvent.clientY);
          }
        }}
        onClick={(e) => {
          e.stopPropagation();
          onClick?.();
        }}
        onPointerOver={(e) => {
          if (draggable) {
            e.stopPropagation();
            document.body.style.cursor = 'grab';
          }
        }}
        onPointerOut={() => {
          if (draggable) {
            document.body.style.cursor = '';
          }
        }}
        castShadow
        receiveShadow
      >
        <meshStandardMaterial
          color="#009688"
          metalness={0.05}
          roughness={0.7}
        />
        {selected && <SelectionBoundingBox geometry={geometry} outOfBounds={outOfBounds} />}
      </mesh>
      {selected && layFlatMode && onLayFlatFaceClick && (
        <FaceSwatches geometry={geometry} onFaceClick={onLayFlatFaceClick} />
      )}
    </group>
  );
}

/**
 * Camera controller with optimal positioning.
 * OrbitControls ref is exposed so TransformControls can disable orbiting during drag.
 */
function CameraController({ 
  bedWidth, bedDepth, bedHeight, orbitRef 
}: { 
  bedWidth: number; bedDepth: number; bedHeight: number;
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
}) {
  const { camera } = useThree();

  useEffect(() => {
    const maxDimension = Math.max(bedWidth, bedDepth, bedHeight);
    const distance = maxDimension * 1.5;
    camera.position.set(distance * 0.7, -distance * 0.7, distance * 0.6);
    camera.up.set(0, 0, 1); // Enforce Z-up for 3D printing convention
    camera.lookAt(0, 0, bedHeight / 4);
    camera.updateProjectionMatrix();
  }, [camera, bedWidth, bedDepth, bedHeight]);

  return (
    <OrbitControls
      ref={orbitRef}
      makeDefault
      enablePan={true}
      enableRotate={true}
      enableZoom={true}
      minDistance={50}
      maxDistance={2000}
      dampingFactor={0.05}
      rotateSpeed={0.8}
      zoomSpeed={1.2}
      minPolarAngle={0}
      maxPolarAngle={Math.PI / 2 + 0.3}
    />
  );
}

/**
 * Wrapper that attaches TransformControls to the selected model mesh.
 * Uses an effect to detect when the mesh ref becomes available.
 */
function ModelTransformControls({
  meshRef,
  mode,
  orbitRef,
  onTransform,
  onTransformStart,
  onTransformEnd,
}: {
  meshRef: React.RefObject<THREE.Object3D | null>;
  mode: 'translate' | 'rotate' | 'scale';
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
  onTransform?: () => void;
  onTransformStart?: () => void;
  onTransformEnd?: () => void;
}) {
  const transformRef = useRef<React.ComponentRef<typeof TransformControls>>(null);
  const [mesh, setMesh] = useState<THREE.Object3D | null>(null);

  // Sync ref → state via animation frame: the mesh ref is set by STLModel
  // after Suspense resolves, which may be after this component mounts.
  // useEffect([meshRef]) never re-fires because ref identity is stable.
  useFrame(() => {
    const current = meshRef.current;
    if (current !== mesh) {
      setMesh(current);
    }
  });

  // Disable orbit while dragging
  useEffect(() => {
    const controls = transformRef.current;
    if (!controls) return;
    const controlsAny = controls as unknown as {
      addEventListener: (type: string, listener: EventListener) => void;
      removeEventListener: (type: string, listener: EventListener) => void;
    };

    const handler = (event: { value: boolean }) => {
      if (orbitRef.current) {
        orbitRef.current.enabled = !event.value;
      }
      if (event.value) {
        onTransformStart?.();
      } else {
        onTransformEnd?.();
      }
    };

    controlsAny.addEventListener('dragging-changed', handler as unknown as EventListener);
    return () => {
      controlsAny.removeEventListener('dragging-changed', handler as unknown as EventListener);
    };
  }, [onTransformEnd, onTransformStart, orbitRef]);

  useEffect(() => {
    const controls = transformRef.current;
    if (!controls) return;
    const controlsAny = controls as unknown as {
      addEventListener: (type: string, listener: EventListener) => void;
      removeEventListener: (type: string, listener: EventListener) => void;
    };

    const handler = () => {
      onTransform?.();
    };

    controlsAny.addEventListener('objectChange', handler as unknown as EventListener);
    return () => {
      controlsAny.removeEventListener('objectChange', handler as unknown as EventListener);
    };
  }, [onTransform]);

  if (!mesh) return null;

  return (
    <TransformControls
      ref={transformRef}
      object={mesh}
      mode={mode}
      size={0.7}
      translationSnap={1}
      rotationSnap={THREE.MathUtils.degToRad(5)}
      scaleSnap={0.05}
    />
  );
}

/**
 * Drag-to-move on the XY build plate.
 * Raycasts pointer against an invisible Z=0 plane and updates model position.
 */
function useBuildPlateDrag({
  bedConfig,
  models,
  orbitRef,
  onModelTransform,
  layFlatMode,
  transformMode,
}: {
  bedConfig: BedConfig;
  models: LoadedModel[];
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
  onModelTransform?: SlicerBedVisualizationProps['onModelTransform'];
  layFlatMode: boolean;
  transformMode?: 'translate' | 'rotate' | 'scale' | null;
}) {
  const { camera, gl, invalidate } = useThree();
  const raycaster = useMemo(() => new THREE.Raycaster(), []);
  const xyPlane = useMemo(() => new THREE.Plane(new THREE.Vector3(0, 0, 1), 0), []);
  const DRAG_THRESHOLD_PX = 3;

  const dragStateRef = useRef<{
    modelId: string;
    startPosition: [number, number, number];
    offset: THREE.Vector3;
    committed: boolean;
    startClientX: number;
    startClientY: number;
  } | null>(null);

  const justDraggedRef = useRef(false);

  // Mutable refs to avoid stale closures in DOM event handlers
  const modelsRef = useRef(models);
  const transformCbRef = useRef(onModelTransform);
  useEffect(() => {
    modelsRef.current = models;
  }, [models]);
  useEffect(() => {
    transformCbRef.current = onModelTransform;
  }, [onModelTransform]);

  const getPlaneIntersection = useCallback(
    (clientX: number, clientY: number): THREE.Vector3 | null => {
      const rect = gl.domElement.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((clientX - rect.left) / rect.width) * 2 - 1,
        -((clientY - rect.top) / rect.height) * 2 + 1,
      );
      raycaster.setFromCamera(ndc, camera);
      const target = new THREE.Vector3();
      return raycaster.ray.intersectPlane(xyPlane, target) ? target : null;
    },
    [camera, gl.domElement, raycaster, xyPlane],
  );

  const startDrag = useCallback(
    (modelId: string, clientX: number, clientY: number) => {
      if (layFlatMode) return;
      if (transformMode === 'rotate' || transformMode === 'scale') return;

      const model = modelsRef.current.find((m) => m.id === modelId);
      if (!model) return;

      const hit = getPlaneIntersection(clientX, clientY);
      if (!hit) return;

      dragStateRef.current = {
        modelId,
        startPosition: [...model.position] as [number, number, number],
        offset: new THREE.Vector3(
          model.position[0] - hit.x,
          model.position[1] - hit.y,
          0,
        ),
        committed: false,
        startClientX: clientX,
        startClientY: clientY,
      };
    },
    [getPlaneIntersection, layFlatMode, transformMode],
  );

  useEffect(() => {
    const el = gl.domElement;
    const orbit = orbitRef.current;

    const onPointerMove = (e: PointerEvent) => {
      const drag = dragStateRef.current;
      if (!drag) return;

      if (!drag.committed) {
        const dx = e.clientX - drag.startClientX;
        const dy = e.clientY - drag.startClientY;
        if (dx * dx + dy * dy < DRAG_THRESHOLD_PX * DRAG_THRESHOLD_PX) return;
        drag.committed = true;
        if (orbit) orbit.enabled = false;
        el.style.cursor = 'grabbing';
      }

      const hit = getPlaneIntersection(e.clientX, e.clientY);
      if (!hit) return;

      const halfW = bedConfig.width / 2;
      const halfD = bedConfig.depth / 2;
      const nx = Math.max(-halfW, Math.min(halfW, hit.x + drag.offset.x));
      const ny = Math.max(-halfD, Math.min(halfD, hit.y + drag.offset.y));

      const model = modelsRef.current.find((m) => m.id === drag.modelId);
      if (!model) return;

      transformCbRef.current?.(
        drag.modelId,
        [nx, ny, drag.startPosition[2]],
        model.rotation,
        model.scale,
        { recordHistory: false, actionLabel: 'Drag Move' },
      );
      invalidate();
    };

    const onPointerUp = () => {
      const drag = dragStateRef.current;
      if (!drag) return;

      if (drag.committed) {
        if (orbit) orbit.enabled = true;
        el.style.cursor = '';

        const model = modelsRef.current.find((m) => m.id === drag.modelId);
        if (model) {
          transformCbRef.current?.(
            drag.modelId,
            model.position,
            model.rotation,
            model.scale,
            {
              recordHistory: true,
              actionLabel: 'Move Model',
              historyBefore: {
                position: drag.startPosition,
                rotation: model.rotation,
                scale: model.scale,
              },
            },
          );
        }

        justDraggedRef.current = true;
        requestAnimationFrame(() => {
          justDraggedRef.current = false;
        });
      }

      dragStateRef.current = null;
    };

    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerUp);
    el.addEventListener('pointerleave', onPointerUp);

    return () => {
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerUp);
      el.removeEventListener('pointerleave', onPointerUp);
      if (dragStateRef.current?.committed) {
        if (orbit) orbit.enabled = true;
        el.style.cursor = '';
      }
      dragStateRef.current = null;
    };
  }, [bedConfig.depth, bedConfig.width, getPlaneIntersection, gl.domElement, invalidate, orbitRef]);

  return { startDrag, justDraggedRef };
}

/**
 * Interactive point-to-point distance measurement tool.
 * Click two points on model surfaces → shows distance line + label.
 */
function MeasureTool() {
  const { camera, gl, scene, invalidate } = useThree();
  const [pointA, setPointA] = useState<THREE.Vector3 | null>(null);
  const [pointB, setPointB] = useState<THREE.Vector3 | null>(null);
  const raycaster = useMemo(() => new THREE.Raycaster(), []);

  // Reset state when component unmounts (measure mode toggled off)
  useEffect(() => {
    return () => {
      setPointA(null);
      setPointB(null);
    };
  }, []);

  // Escape key clears measurement
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setPointA(null);
        setPointB(null);
        invalidate();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [invalidate]);

  // Click handler on the canvas
  useEffect(() => {
    const el = gl.domElement;

    const onClick = (e: MouseEvent) => {
      const rect = el.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1,
      );
      raycaster.setFromCamera(ndc, camera);

      // Raycast against all mesh children in the scene
      const meshes: THREE.Object3D[] = [];
      scene.traverse((obj) => {
        if ((obj as THREE.Mesh).isMesh) meshes.push(obj);
      });
      const hits = raycaster.intersectObjects(meshes, false);
      if (hits.length === 0) return;

      const hitPoint = hits[0].point.clone();

      setPointA((prevA) => {
        if (prevA === null) {
          // First click → set point A
          setPointB(null);
          invalidate();
          return hitPoint;
        }
        // Second click → set point B (point A already set)
        setPointB(hitPoint);
        invalidate();
        return prevA;
      });
    };

    el.addEventListener('click', onClick);
    return () => el.removeEventListener('click', onClick);
  }, [camera, gl.domElement, invalidate, raycaster, scene]);

  // Allow resetting by clicking again after both points are set
  useEffect(() => {
    if (pointA && pointB) {
      const el = gl.domElement;
      const onReset = () => {
        setPointA(null);
        setPointB(null);
        invalidate();
      };
      // Use a timeout to avoid the same click that set pointB from clearing
      const timer = setTimeout(() => {
        el.addEventListener('click', onReset, { once: true });
      }, 200);
      return () => {
        clearTimeout(timer);
        el.removeEventListener('click', onReset);
      };
    }
  }, [pointA, pointB, gl.domElement, invalidate]);

  const distance = pointA && pointB ? pointA.distanceTo(pointB) : null;
  const midpoint = pointA && pointB
    ? new THREE.Vector3().addVectors(pointA, pointB).multiplyScalar(0.5)
    : null;

  return (
    <group>
      {/* Point A marker */}
      {pointA && (
        <mesh position={pointA}>
          <sphereGeometry args={[1.2, 16, 16]} />
          <meshBasicMaterial color="#ef4444" depthTest={false} toneMapped={false} />
        </mesh>
      )}
      {/* Point B marker */}
      {pointB && (
        <mesh position={pointB}>
          <sphereGeometry args={[1.2, 16, 16]} />
          <meshBasicMaterial color="#3b82f6" depthTest={false} toneMapped={false} />
        </mesh>
      )}
      {/* Line between A and B */}
      {pointA && pointB && (
        <line>
          <bufferGeometry>
            <bufferAttribute
              attach="attributes-position"
              args={[new Float32Array([
                pointA.x, pointA.y, pointA.z,
                pointB.x, pointB.y, pointB.z,
              ]), 3]}
              count={2}
              itemSize={3}
            />
          </bufferGeometry>
          <lineBasicMaterial color="#ffffff" depthTest={false} linewidth={2} />
        </line>
      )}
      {/* Distance label */}
      {midpoint && distance !== null && (
        <Html position={midpoint} center style={{ pointerEvents: 'none' }}>
          <div className="bg-pf-bg-2/95 backdrop-blur-sm px-2.5 py-1 rounded-md border border-pf-accent shadow-lg text-sm font-mono text-pf-text-primary whitespace-nowrap">
            {distance.toFixed(2)} mm
          </div>
        </Html>
      )}
    </group>
  );
}

/**
 * Main scene content
 */
function BedScene({
  bedConfig,
  models = [],
  selectedModelId,
  onModelSelect,
  transformMode,
  onModelTransform,
  onSelectedModelMetricsChange,
  showAxes = true,
  showGridLines = true,
  outOfBoundsModelIds,
  layFlatMode = false,
  onLayFlatComplete,
  autoOrientTrigger = 0,
  measureMode = false,
  assemblyViewActive = false,
  splitTrigger = 0,
  cutMode = false,
  onCutComplete,
  onCutCancel,
  supportPaintMode = false,
  supportPaintData,
  onSupportPaintUpdate,
  seamPaintMode = false,
  seamPaintData,
  onSeamPaintUpdate,
}: Omit<SlicerBedVisualizationProps, 'className' | 'backgroundColor' | 'showGrid' | 'gridDivisions'>) {
  const { width, depth, height, textureUrl, textureFormat } = bedConfig;
  const orbitRef = useRef<React.ComponentRef<typeof OrbitControls>>(null);
  const selectedMeshRef = useRef<THREE.Object3D>(null);

  const { startDrag, justDraggedRef } = useBuildPlateDrag({
    bedConfig,
    models,
    orbitRef,
    onModelTransform,
    layFlatMode,
    transformMode,
  });

  const dragStartTransformRef = useRef<{
    position: [number, number, number];
    rotation: [number, number, number];
    scale: [number, number, number];
  } | null>(null);

  const transformActionLabel = transformMode === 'translate'
    ? 'Move Model'
    : transformMode === 'rotate'
      ? 'Rotate Model'
      : 'Scale Model';

  const handleTransformStart = useCallback(() => {
    if (!selectedMeshRef.current) return;
    const obj = selectedMeshRef.current;
    const halfZ: number = obj.userData.halfZ || 0;
    dragStartTransformRef.current = {
      position: [obj.position.x, obj.position.y, obj.position.z - halfZ] as [number, number, number],
      rotation: [obj.rotation.x, obj.rotation.y, obj.rotation.z],
      scale: obj.scale.toArray() as [number, number, number],
    };
  }, []);

  const handleTransformEnd = useCallback(() => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const obj = selectedMeshRef.current;
    const halfZ: number = obj.userData.halfZ || 0;
    const geo: THREE.BufferGeometry | undefined = obj.userData.geometry;
    const scaleVec = new THREE.Vector3(obj.scale.x, obj.scale.y, obj.scale.z);
    const positionZ = (transformMode === 'rotate' || transformMode === 'scale') && geo
      ? computeZForBedPlacement(geo, obj.quaternion, scaleVec)
      : obj.position.z - halfZ;
    onModelTransform(
      selectedModelId,
      [obj.position.x, obj.position.y, positionZ] as [number, number, number],
      [obj.rotation.x, obj.rotation.y, obj.rotation.z],
      obj.scale.toArray() as [number, number, number],
      {
        recordHistory: true,
        actionLabel: transformActionLabel,
        historyBefore: dragStartTransformRef.current ?? undefined,
      },
    );
    dragStartTransformRef.current = null;
  }, [onModelTransform, selectedModelId, transformActionLabel, transformMode]);

  const handleTransformChange = useCallback(() => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const obj = selectedMeshRef.current;
    const halfZ: number = obj.userData.halfZ || 0;
    const geo: THREE.BufferGeometry | undefined = obj.userData.geometry;
    const scaleVec = new THREE.Vector3(obj.scale.x, obj.scale.y, obj.scale.z);
    const positionZ = (transformMode === 'rotate' || transformMode === 'scale') && geo
      ? computeZForBedPlacement(geo, obj.quaternion, scaleVec)
      : obj.position.z - halfZ;
    onModelTransform(
      selectedModelId,
      [obj.position.x, obj.position.y, positionZ] as [number, number, number],
      [obj.rotation.x, obj.rotation.y, obj.rotation.z],
      obj.scale.toArray() as [number, number, number],
      { recordHistory: false, actionLabel: transformActionLabel },
    );
  }, [onModelTransform, selectedModelId, transformActionLabel, transformMode]);

  // Deselect when clicking the bed/background
  const handlePointerMissed = useCallback(() => {
    if (justDraggedRef.current) return;
    onModelSelect?.(null);
    onSelectedModelMetricsChange?.(null);
  }, [justDraggedRef, onModelSelect, onSelectedModelMetricsChange]);

  useEffect(() => {
    if (!selectedModelId) {
      onSelectedModelMetricsChange?.(null);
    }
  }, [onSelectedModelMetricsChange, selectedModelId]);

  // Handle lay-flat face click: compute rotation + Z placement inside scene
  const handleLayFlatFace = useCallback((normal: THREE.Vector3) => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const obj = selectedMeshRef.current;
    const geo: THREE.BufferGeometry | undefined = obj.userData.geometry;
    if (!geo) return;

    const q = new THREE.Quaternion().setFromUnitVectors(normal, new THREE.Vector3(0, 0, -1));
    const euler = new THREE.Euler().setFromQuaternion(q);
    const scaleVec = new THREE.Vector3(obj.scale.x, obj.scale.y, obj.scale.z);
    const dataZ = computeZForBedPlacement(geo, q, scaleVec);

    onModelTransform(
      selectedModelId,
      [obj.position.x, obj.position.y, dataZ],
      [euler.x, euler.y, euler.z],
      obj.scale.toArray() as [number, number, number],
      { recordHistory: true, actionLabel: 'Lay Flat' },
    );
    onLayFlatComplete?.();
  }, [onLayFlatComplete, onModelTransform, selectedModelId]);

  // Auto-orient: find the orientation that minimises height while avoiding overhangs
  const lastAutoOrientRef = useRef(0);
  useEffect(() => {
    if (autoOrientTrigger === 0 || autoOrientTrigger === lastAutoOrientRef.current) return;
    lastAutoOrientRef.current = autoOrientTrigger;

    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const obj = selectedMeshRef.current;
    const geo: THREE.BufferGeometry | undefined = obj.userData.geometry;
    if (!geo) return;

    const faces = detectMajorFaces(geo, 0.005, 20);

    // Build candidate normals: face normals + 6 principal axes
    const candidateNormals: THREE.Vector3[] = [
      new THREE.Vector3(1, 0, 0),
      new THREE.Vector3(-1, 0, 0),
      new THREE.Vector3(0, 1, 0),
      new THREE.Vector3(0, -1, 0),
      new THREE.Vector3(0, 0, 1),
      new THREE.Vector3(0, 0, -1),
    ];
    for (const face of faces) {
      candidateNormals.push(face.normal);
    }

    // Precompute per-triangle normals and areas for overhang scoring
    const posAttr = geo.getAttribute('position');
    const index = geo.getIndex();
    const triCount = index ? index.count / 3 : posAttr.count / 3;
    const triNormals: THREE.Vector3[] = [];
    const triAreas: number[] = [];
    const triCentroids: THREE.Vector3[] = [];
    let totalArea = 0;
    const tA = new THREE.Vector3(), tB = new THREE.Vector3(), tC = new THREE.Vector3();
    const e1 = new THREE.Vector3(), e2 = new THREE.Vector3(), tn = new THREE.Vector3();
    for (let i = 0; i < triCount; i++) {
      if (index) {
        tA.fromBufferAttribute(posAttr, index.getX(i * 3));
        tB.fromBufferAttribute(posAttr, index.getX(i * 3 + 1));
        tC.fromBufferAttribute(posAttr, index.getX(i * 3 + 2));
      } else {
        tA.fromBufferAttribute(posAttr, i * 3);
        tB.fromBufferAttribute(posAttr, i * 3 + 1);
        tC.fromBufferAttribute(posAttr, i * 3 + 2);
      }
      e1.subVectors(tB, tA);
      e2.subVectors(tC, tA);
      tn.crossVectors(e1, e2);
      const area = tn.length() / 2;
      if (area < 1e-6) {
        triNormals.push(new THREE.Vector3(0, 0, 1));
        triAreas.push(0);
        triCentroids.push(new THREE.Vector3());
        continue;
      }
      triNormals.push(tn.clone().normalize());
      triAreas.push(area);
      triCentroids.push(new THREE.Vector3().addVectors(tA, tB).add(tC).multiplyScalar(1 / 3));
      totalArea += area;
    }

    // Evaluate each candidate: score by height + unsupported-overhang penalty
    const OVERHANG_THRESH = -0.5; // ~60° from horizontal
    const OVERHANG_WEIGHT = 2.0;
    const SUPPORT_Z_TOL_MM = 0.8;
    const v = new THREE.Vector3();
    const rn = new THREE.Vector3();
    const rc = new THREE.Vector3();
    const scaleVec = new THREE.Vector3(obj.scale.x, obj.scale.y, obj.scale.z);
    let bestQ: THREE.Quaternion | null = null;
    let bestScore = Infinity;

    for (const normal of candidateNormals) {
      const candidateQ = new THREE.Quaternion().setFromUnitVectors(
        normal,
        new THREE.Vector3(0, 0, -1),
      );

      // Height from scaled + rotated vertices
      let minZ = Infinity;
      let maxZ = -Infinity;
      for (let i = 0; i < posAttr.count; i++) {
        v.fromBufferAttribute(posAttr, i);
        v.multiply(scaleVec).applyQuaternion(candidateQ);
        if (v.z < minZ) minZ = v.z;
        if (v.z > maxZ) maxZ = v.z;
      }
      const height = maxZ - minZ;

      // Overhang area from triangles, excluding surfaces effectively supported by the bed.
      let overhangArea = 0;
      for (let i = 0; i < triCount; i++) {
        rn.copy(triNormals[i]).applyQuaternion(candidateQ);
        if (rn.z >= OVERHANG_THRESH) continue;

        rc.copy(triCentroids[i]).multiply(scaleVec).applyQuaternion(candidateQ);
        const isBedSupported = rc.z <= minZ + SUPPORT_Z_TOL_MM;
        if (!isBedSupported) {
          overhangArea += triAreas[i];
        }
      }
      const overhangRatio = totalArea > 0 ? overhangArea / totalArea : 0;

      // Keep height as a first-order term, but strongly penalize unsupported overhang.
      const score = height * (1 + OVERHANG_WEIGHT * overhangRatio);
      if (score < bestScore) {
        bestScore = score;
        bestQ = candidateQ;
      }
    }

    if (!bestQ) return;
    const euler = new THREE.Euler().setFromQuaternion(bestQ);
    const dataZ = computeZForBedPlacement(geo, bestQ, scaleVec);

    onModelTransform(
      selectedModelId,
      [obj.position.x, obj.position.y, dataZ],
      [euler.x, euler.y, euler.z],
      obj.scale.toArray() as [number, number, number],
      { recordHistory: true, actionLabel: 'Auto-Orient' },
    );
  }, [autoOrientTrigger, onModelTransform, selectedModelId]);

  // Assembly view: compute radial offsets from centroid
  const assemblyPositions = useMemo(() => {
    if (!assemblyViewActive || models.length < 2) return null;
    const cx = models.reduce((s, m) => s + m.position[0], 0) / models.length;
    const cy = models.reduce((s, m) => s + m.position[1], 0) / models.length;
    const MIN_DISPLACEMENT = 30;
    const SCALE_FACTOR = 1.5;
    const map = new Map<string, [number, number, number]>();
    for (const model of models) {
      const dx = model.position[0] - cx;
      const dy = model.position[1] - cy;
      const dist = Math.sqrt(dx * dx + dy * dy);
      let ox: number, oy: number;
      if (dist < 0.01) {
        // Model at centroid — push outward along arbitrary direction based on index
        const idx = models.indexOf(model);
        const angle = (idx / models.length) * Math.PI * 2;
        ox = Math.cos(angle) * MIN_DISPLACEMENT;
        oy = Math.sin(angle) * MIN_DISPLACEMENT;
      } else {
        const displacement = Math.max(dist * (SCALE_FACTOR - 1), MIN_DISPLACEMENT);
        const nx = dx / dist;
        const ny = dy / dist;
        ox = nx * displacement;
        oy = ny * displacement;
      }
      map.set(model.id, [
        model.position[0] + ox,
        model.position[1] + oy,
        model.position[2],
      ]);
    }
    return map;
  }, [assemblyViewActive, models]);

  // Split: connected component analysis via union-find
  const lastSplitRef = useRef(0);
  useEffect(() => {
    if (splitTrigger === 0 || splitTrigger === lastSplitRef.current) return;
    lastSplitRef.current = splitTrigger;

    if (!selectedModelId || !selectedMeshRef.current) return;
    const geo: THREE.BufferGeometry | undefined = selectedMeshRef.current.userData.geometry;
    if (!geo) {
      toast.info('No geometry available for split analysis');
      return;
    }

    const posAttr = geo.getAttribute('position');
    const index = geo.getIndex();
    const vertexCount = posAttr.count;
    const triCount = index ? index.count / 3 : vertexCount / 3;

    // Union-find
    const parent = new Int32Array(vertexCount);
    const rank = new Int32Array(vertexCount);
    for (let i = 0; i < vertexCount; i++) parent[i] = i;

    function find(x: number): number {
      while (parent[x] !== x) {
        parent[x] = parent[parent[x]];
        x = parent[x];
      }
      return x;
    }

    function union(a: number, b: number) {
      const ra = find(a), rb = find(b);
      if (ra === rb) return;
      if (rank[ra] < rank[rb]) parent[ra] = rb;
      else if (rank[ra] > rank[rb]) parent[rb] = ra;
      else { parent[rb] = ra; rank[ra]++; }
    }

    // Build spatial hash to merge vertices at same position
    const PRECISION = 1e4;
    const vertexMap = new Map<string, number>();
    const canonicalIndex = new Int32Array(vertexCount);
    for (let i = 0; i < vertexCount; i++) {
      const x = Math.round(posAttr.getX(i) * PRECISION);
      const y = Math.round(posAttr.getY(i) * PRECISION);
      const z = Math.round(posAttr.getZ(i) * PRECISION);
      const key = `${x},${y},${z}`;
      const existing = vertexMap.get(key);
      if (existing !== undefined) {
        canonicalIndex[i] = existing;
        union(i, existing);
      } else {
        vertexMap.set(key, i);
        canonicalIndex[i] = i;
      }
    }

    // Union triangle vertices
    for (let t = 0; t < triCount; t++) {
      const i0 = index ? index.getX(t * 3) : t * 3;
      const i1 = index ? index.getX(t * 3 + 1) : t * 3 + 1;
      const i2 = index ? index.getX(t * 3 + 2) : t * 3 + 2;
      union(canonicalIndex[i0], canonicalIndex[i1]);
      union(canonicalIndex[i1], canonicalIndex[i2]);
    }

    // Count components
    const roots = new Set<number>();
    for (let i = 0; i < vertexCount; i++) roots.add(find(i));
    const componentCount = roots.size;

    if (componentCount <= 1) {
      toast.info('Model is a single solid — nothing to split');
    } else {
      toast.info(`Model has ${componentCount} separate parts. Split support coming soon.`);
    }
  }, [splitTrigger, selectedModelId]);

  return (
    <>
      {/* Lighting — matched to OrcaSlicer dark theme shader values */}
      <ambientLight intensity={0.3} />
      <directionalLight 
        position={[width, -depth, height * 2]} 
        intensity={0.48} 
        castShadow
        shadow-mapSize-width={2048}
        shadow-mapSize-height={2048}
      />
      <directionalLight position={[-width, depth, height]} intensity={0.18} />

      {/* Camera controls */}
      <CameraController bedWidth={width} bedDepth={depth} bedHeight={height} orbitRef={orbitRef} />

      {/* Print bed */}
      <PrintBedPlatform 
        width={width} 
        depth={depth} 
        textureUrl={textureUrl}
        textureFormat={textureFormat}
        showGridLines={showGridLines}
      />

      {/* Build volume wireframe */}
      <BuildVolumeWireframe width={width} depth={depth} height={height} />

      {/* Axis indicators */}
      {showAxes && <AxisIndicators size={Math.min(width, depth) * 0.15} />}

      {/* Loaded models */}
      <group onPointerMissed={handlePointerMissed}>
        {models.map((model) => {
          const displayPos = assemblyPositions?.get(model.id) ?? model.position;
          return (
            <Suspense key={model.id} fallback={<LoadingIndicator />}>
              {model.fileType === 'stl' && (
                model.geometry ? (
                  <PrebuiltSTLModel
                    inputGeometry={model.geometry}
                    position={displayPos}
                    rotation={model.rotation}
                    scale={model.scale}
                    selected={model.id === selectedModelId}
                    outOfBounds={outOfBoundsModelIds?.has(model.id)}
                    layFlatMode={model.id === selectedModelId && layFlatMode}
                    draggable={!layFlatMode && !assemblyViewActive && transformMode !== 'rotate' && transformMode !== 'scale'}
                    onClick={() => onModelSelect?.(model.id)}
                    onDragStart={(cx, cy) => startDrag(model.id, cx, cy)}
                    onLayFlatFaceClick={model.id === selectedModelId ? handleLayFlatFace : undefined}
                    meshRef={model.id === selectedModelId ? selectedMeshRef as React.RefObject<THREE.Object3D | null> : undefined}
                    onSelectedMetrics={model.id === selectedModelId
                      ? (metrics) => onSelectedModelMetricsChange?.({
                        modelId: model.id,
                        baseSize: metrics.baseSize,
                        currentSize: metrics.currentSize,
                        currentScale: metrics.currentScale,
                      })
                      : undefined}
                  />
                ) : (
                  <STLModel
                    url={model.url}
                    position={displayPos}
                    rotation={model.rotation}
                    scale={model.scale}
                    selected={model.id === selectedModelId}
                    outOfBounds={outOfBoundsModelIds?.has(model.id)}
                    layFlatMode={model.id === selectedModelId && layFlatMode}
                    draggable={!layFlatMode && !assemblyViewActive && transformMode !== 'rotate' && transformMode !== 'scale'}
                    onClick={() => onModelSelect?.(model.id)}
                    onDragStart={(cx, cy) => startDrag(model.id, cx, cy)}
                    onLayFlatFaceClick={model.id === selectedModelId ? handleLayFlatFace : undefined}
                    meshRef={model.id === selectedModelId ? selectedMeshRef as React.RefObject<THREE.Object3D | null> : undefined}
                    onSelectedMetrics={model.id === selectedModelId
                      ? (metrics) => onSelectedModelMetricsChange?.({
                        modelId: model.id,
                        baseSize: metrics.baseSize,
                        currentSize: metrics.currentSize,
                        currentScale: metrics.currentScale,
                      })
                      : undefined}
                  />
                )
              )}
            </Suspense>
          );
        })}
      </group>

      {/* Measure tool overlay */}
      {measureMode && <MeasureTool />}

      {/* Cut plane overlay */}
      {cutMode && selectedModelId && onCutComplete && onCutCancel && (
        <CutPlaneOverlay
          meshRef={selectedMeshRef}
          active={cutMode}
          onCutComplete={onCutComplete}
          onCutCancel={onCutCancel}
        />
      )}

      {/* Support paint overlay — C6: key forces remount on model switch */}
      {supportPaintMode && selectedModelId && onSupportPaintUpdate && (
        <FacePaintOverlay
          key={`support-${selectedModelId}`}
          meshRef={selectedMeshRef}
          paintedFaces={supportPaintData?.get(selectedModelId) ?? EMPTY_FACE_SET}
          onPaintUpdate={onSupportPaintUpdate}
          color="#22d3ee"
          opacity={0.4}
          active={supportPaintMode}
        />
      )}

      {/* Seam paint overlay — C6: key forces remount on model switch */}
      {seamPaintMode && selectedModelId && onSeamPaintUpdate && (
        <FacePaintOverlay
          key={`seam-${selectedModelId}`}
          meshRef={selectedMeshRef}
          paintedFaces={seamPaintData?.get(selectedModelId) ?? EMPTY_FACE_SET}
          onPaintUpdate={onSeamPaintUpdate}
          color="#4ade80"
          opacity={0.4}
          active={seamPaintMode}
        />
      )}

      {/* TransformControls for the selected model */}
      {selectedModelId && transformMode && (
        <ModelTransformControls
          meshRef={selectedMeshRef}
          mode={transformMode}
          orbitRef={orbitRef}
          onTransform={handleTransformChange}
          onTransformStart={handleTransformStart}
          onTransformEnd={handleTransformEnd}
        />
      )}

      {/* Environment for reflections */}
      <Environment preset="studio" />
    </>
  );
}

/**
 * Main SlicerBedVisualization Component
 */
export const SlicerBedVisualization: React.FC<SlicerBedVisualizationProps> = ({
  bedConfig,
  models = [],
  selectedModelId,
  onModelSelect,
  transformMode = null,
  onModelTransform,
  onSelectedModelMetricsChange,
  showAxes = true,
  showGridLines = true,
  backgroundColor = '#53535a',
  className = '',
  outOfBoundsModelIds,
  layFlatMode = false,
  onLayFlatComplete,
  autoOrientTrigger = 0,
  measureMode = false,
  assemblyViewActive = false,
  splitTrigger = 0,
  cutMode = false,
  onCutComplete,
  onCutCancel,
  supportPaintMode = false,
  supportPaintData,
  onSupportPaintUpdate,
  seamPaintMode = false,
  seamPaintData,
  onSeamPaintUpdate,
}) => {
  return (
    <div className={`w-full h-full ${className}`}>
      <Canvas
        frameloop="demand"
        style={{ background: backgroundColor }}
        gl={{
          antialias: true,
          preserveDrawingBuffer: false,
          alpha: false,
          powerPreference: 'high-performance',
        }}
        shadows
        camera={{ 
          fov: 45, 
          near: 0.1, 
          far: 10000,
          position: [300, -300, 250],
          up: [0, 0, 1] // Z-up: standard 3D printing convention
        }}
        onCreated={({ gl, scene }) => {
          gl.toneMapping = THREE.ACESFilmicToneMapping;
          gl.toneMappingExposure = 1.0;
          scene.background = new THREE.Color('#53535a');
        }}
      >
        <Suspense fallback={null}>
          <BedScene
            bedConfig={bedConfig}
            models={models}
            selectedModelId={selectedModelId}
            onModelSelect={onModelSelect}
            transformMode={transformMode}
            onModelTransform={onModelTransform}
            onSelectedModelMetricsChange={onSelectedModelMetricsChange}
            outOfBoundsModelIds={outOfBoundsModelIds}
            showAxes={showAxes}
            showGridLines={showGridLines}
            layFlatMode={layFlatMode}
            onLayFlatComplete={onLayFlatComplete}
            autoOrientTrigger={autoOrientTrigger}
            measureMode={measureMode}
            assemblyViewActive={assemblyViewActive}
            splitTrigger={splitTrigger}
            cutMode={cutMode}
            onCutComplete={onCutComplete}
            onCutCancel={onCutCancel}
            supportPaintMode={supportPaintMode}
            supportPaintData={supportPaintData}
            onSupportPaintUpdate={onSupportPaintUpdate}
            seamPaintMode={seamPaintMode}
            seamPaintData={seamPaintData}
            onSeamPaintUpdate={onSeamPaintUpdate}
          />
        </Suspense>
      </Canvas>
    </div>
  );
};

export default SlicerBedVisualization;
