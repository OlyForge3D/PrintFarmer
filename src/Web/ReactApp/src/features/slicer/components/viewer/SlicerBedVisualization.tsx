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
import { ColorPaintOverlay } from './ColorPaintOverlay';
import { CutPlaneOverlay } from './CutPlaneOverlay';
import { PlateBedOverlay } from './PlateBedOverlay';
import { ModelViewerErrorBoundary } from './ModelViewerErrorBoundary';
import { ThreeMFViewer } from '@/features/slicer/components/ThreeMFViewer';
import {
  detectMajorFaces,
  computeAutoOrientation,
  computeBedPlacementZ,
} from '@/features/slicer/utils/autoOrient';
import { localizeDragTarget } from '@/features/slicer/utils/bedDrag';

// W4: Module-level constant to avoid creating new Set on every render
const EMPTY_FACE_SET = new Set<number>();
const EMPTY_COLOR_FACE_MAP = new Map<number, number>();

/**
 * Walks an object's ancestry to determine whether it belongs to the active
 * plate. Plate offset groups tag themselves with `userData.plateActive`; a mesh
 * with no plate marker (e.g. the legacy single-plate scene) is treated as
 * active. Used to scope measure / text raycasts to the active plate only so
 * all-plates-visible mode doesn't produce cross-plate surprises.
 */
function meshIsOnActivePlate(obj: THREE.Object3D): boolean {
  let node: THREE.Object3D | null = obj;
  while (node) {
    if (typeof node.userData?.plateActive === 'boolean') return node.userData.plateActive;
    node = node.parent;
  }
  return true;
}

/**
 * Offset wrapper for a single plate's bed + models. Tags itself with
 * `userData.plateActive` so descendant model meshes can be scoped to the active
 * plate by measure / text tools. Model positions inside stay bed-local; the
 * group's position carries the grid offset.
 */
function PlateGroup({
  offset,
  active,
  children,
}: {
  offset: [number, number, number];
  active: boolean;
  children: React.ReactNode;
}) {
  const ref = useRef<THREE.Group>(null);
  useEffect(() => {
    if (ref.current) ref.current.userData.plateActive = active;
  }, [active]);
  return (
    <group ref={ref} position={offset} userData={{ plateActive: active }}>
      {children}
    </group>
  );
}

export interface LoadedModel {
  id: string;
  url: string;
  viewerUrl?: string;
  fileName: string;
  fileType: 'stl' | 'ply' | '3mf' | 'step';
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

/**
 * Describes one build plate to render in the all-plates-visible grid. The
 * world-space `offset` translates the plate's bed + models; model positions
 * themselves stay bed-local. `modelIds` selects (and orders) which of the
 * scene's `models` belong to this plate.
 */
export interface ScenePlate {
  id: string;
  name: string;
  offset: [number, number, number];
  active: boolean;
  locked: boolean;
  modelIds: string[];
}

export interface SlicerBedVisualizationProps {
  bedConfig: BedConfig;
  models?: LoadedModel[];
  /**
   * Build plates to render simultaneously in the grid. Each plate's models are
   * resolved from the scene `models` by id and wrapped in an offset group so
   * model positions stay bed-local. When omitted, all `models` render on a
   * single active plate at offset [0,0,0] (legacy single-plate parity).
   */
  plates?: ScenePlate[];
  selectedModelId?: string;
  onModelSelect?: (modelId: string | null) => void;
  /** Called when a plate's bed is clicked — makes that plate the active slice target. */
  onPlateActivate?: (plateId: string) => void;
  /** Per-plate in-scene overlay actions (rendered only when >1 plate). */
  onPlateRename?: (plateId: string, name: string) => void;
  onPlateDelete?: (plateId: string) => void;
  onPlateArrange?: (plateId: string) => void;
  onPlateOrient?: (plateId: string) => void;
  onPlateToggleLock?: (plateId: string) => void;
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
  /**
   * Called when a model's processed geometry becomes available or is removed.
   * Lets the parent maintain a modelId → geometry registry so it can run
   * geometry-dependent operations (e.g. auto-orient) on non-selected models.
   * Passes `null` when the model unmounts.
   */
  onModelGeometryChange?: (modelId: string, geometry: THREE.BufferGeometry | null) => void;
  /** When true, measure tool is active for point-to-point distance */
  measureMode?: boolean;
  /** When true, models are offset radially for exploded assembly inspection */
  assemblyViewActive?: boolean;
  /** Increment to trigger connected-component split analysis on selected model */
  splitTrigger?: number;
  /** Whether cut mode is active */
  cutMode?: boolean;
  /** Called when a cut is completed with two new geometries */
  onCutComplete?: (
    geometryAbove: THREE.BufferGeometry,
    geometryBelow: THREE.BufferGeometry,
    options?: {
      keepUpper: boolean;
      keepLower: boolean;
      placeOnCutUpper: boolean;
      placeOnCutLower: boolean;
      flipUpper: boolean;
      flipLower: boolean;
      cutToParts: boolean;
    }
  ) => void;
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
  /** Whether color paint mode is active */
  colorPaintMode?: boolean;
  /** Painted color face data per model: face index → extruder index */
  colorPaintData?: Map<string, Map<number, number>>;
  /** Called when color paint data changes */
  onColorPaintUpdate?: (faces: Map<number, number>) => void;
  /** Active extruder index for color painting */
  activeColorIndex?: number;
  /** Whether fuzzy skin paint mode is active */
  fuzzySkinPaintMode?: boolean;
  /** Painted fuzzy skin face indices per model */
  fuzzySkinPaintData?: Map<string, Set<number>>;
  /** Called when fuzzy skin paint data changes */
  onFuzzySkinPaintUpdate?: (faces: Set<number>) => void;
  /** Current paint/erase mode for overlays */
  paintMode?: 'paint' | 'erase';
  /** Brush size for paint overlays */
  paintBrushSize?: number;
  /** Hide bed (grid, axes, build volume) — used during paint mode */
  hideBed?: boolean;
  /** Whether text placement mode is active (crosshair, click to place) */
  textPlacementMode?: boolean;
  /** Called when the user clicks a model surface during text placement */
  onTextPlace?: (point: THREE.Vector3, normal: THREE.Vector3) => void;
  /** Optional React Three Fiber elements to render inside the canvas scene */
  sceneOverlay?: React.ReactNode;
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
  active = true,
  highlight = false,
  onBedClick,
}: { 
  width: number; 
  depth: number; 
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
  showGridLines?: boolean;
  /** Whether this plate is the active slice target. */
  active?: boolean;
  /** When true (multi-plate only), render the active/inactive highlight chrome. */
  highlight?: boolean;
  /** Click handler on the bed surface — activates the plate / deselects. */
  onBedClick?: () => void;
}) {
  const shouldUsePngTexture = textureUrl && textureFormat === 'png';
  const shouldUseSvgTexture = textureUrl && textureFormat === 'svg';

  // Highlight chrome is only applied when multiple plates are visible, so the
  // single-plate view stays pixel-identical to the legacy layout.
  const edgeColor = highlight ? (active ? '#3b82f6' : '#3a3a52') : '#4a4a6a';

  return (
    <group onClick={onBedClick ? (e) => { e.stopPropagation(); onBedClick(); } : undefined}>
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
        <lineBasicMaterial color={edgeColor} linewidth={2} />
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
 * STLModel offsets by halfZ internally, so we account for that. Thin wrapper over the
 * pure {@link computeBedPlacementZ} util to keep a single source of truth for the math.
 */
function computeZForBedPlacement(
  geometry: THREE.BufferGeometry,
  q: THREE.Quaternion,
  scale: THREE.Vector3 = new THREE.Vector3(1, 1, 1),
): number {
  return computeBedPlacementZ(geometry, q, scale);
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
  dimmed = false,
  onClick,
  onDragStart,
  meshRef,
  onSelectedMetrics,
  onLayFlatFaceClick,
  onGeometryReady,
}: { 
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  outOfBounds?: boolean;
  layFlatMode?: boolean;
  draggable?: boolean;
  dimmed?: boolean;
  onClick?: () => void;
  onDragStart?: (clientX: number, clientY: number) => void;
  meshRef?: React.RefObject<THREE.Object3D | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
  onGeometryReady?: (geometry: THREE.BufferGeometry | null) => void;
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

  // Publish geometry to the parent registry so geometry-dependent operations
  // (e.g. auto-orient a whole plate) can reach non-selected models too.
  useEffect(() => {
    onGeometryReady?.(geometry);
    return () => onGeometryReady?.(null);
  }, [geometry, onGeometryReady]);

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
        userData={{ isModelMesh: true }}
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
          transparent={dimmed}
          opacity={dimmed ? 0.4 : 1}
        />
        {selected && <SelectionBoundingBox geometry={geometry} outOfBounds={outOfBounds} />}
      </mesh>
      {selected && layFlatMode && onLayFlatFaceClick && (
        <FaceSwatches geometry={geometry} onFaceClick={onLayFlatFaceClick} />
      )}
    </group>
  );
}

function UrlModelViewer({
  fileType,
  url,
  position = [0, 0, 0],
  rotation = [0, 0, 0],
  scale = [1, 1, 1],
  selected = false,
  outOfBounds = false,
  layFlatMode = false,
  draggable = false,
  dimmed = false,
  onClick,
  onDragStart,
  meshRef,
  onSelectedMetrics,
  onLayFlatFaceClick,
  onGeometryReady,
}: {
  fileType: LoadedModel['fileType'];
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  outOfBounds?: boolean;
  layFlatMode?: boolean;
  draggable?: boolean;
  dimmed?: boolean;
  onClick?: () => void;
  onDragStart?: (clientX: number, clientY: number) => void;
  meshRef?: React.RefObject<THREE.Object3D | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
  onGeometryReady?: (geometry: THREE.BufferGeometry | null) => void;
}) {
  if (fileType === '3mf') {
    return (
      <ThreeMFViewer
        url={url}
        position={position}
        rotation={rotation}
        scale={scale}
        selected={selected}
        outOfBounds={outOfBounds}
        layFlatMode={layFlatMode}
        draggable={draggable}
        dimmed={dimmed}
        onClick={onClick}
        onDragStart={onDragStart}
        meshRef={meshRef}
        onSelectedMetrics={onSelectedMetrics}
        onLayFlatFaceClick={onLayFlatFaceClick}
        onGeometryReady={onGeometryReady}
        renderSelectionBoundingBox={(geometry, isOutOfBounds) => (
          <SelectionBoundingBox geometry={geometry} outOfBounds={isOutOfBounds} />
        )}
        renderFaceSwatches={(geometry, handleFaceClick) => (
          <FaceSwatches geometry={geometry} onFaceClick={handleFaceClick} />
        )}
      />
    );
  }

  return (
    <STLModel
      url={url}
      position={position}
      rotation={rotation}
      scale={scale}
      selected={selected}
      outOfBounds={outOfBounds}
      layFlatMode={layFlatMode}
      draggable={draggable}
      dimmed={dimmed}
      onClick={onClick}
      onDragStart={onDragStart}
      meshRef={meshRef}
      onSelectedMetrics={onSelectedMetrics}
      onLayFlatFaceClick={onLayFlatFaceClick}
      onGeometryReady={onGeometryReady}
    />
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
  dimmed = false,
  onClick,
  onDragStart,
  meshRef,
  onSelectedMetrics,
  onLayFlatFaceClick,
  onGeometryReady,
}: {
  inputGeometry: THREE.BufferGeometry;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  outOfBounds?: boolean;
  layFlatMode?: boolean;
  draggable?: boolean;
  dimmed?: boolean;
  onClick?: () => void;
  onDragStart?: (clientX: number, clientY: number) => void;
  meshRef?: React.RefObject<THREE.Object3D | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
  onGeometryReady?: (geometry: THREE.BufferGeometry | null) => void;
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

  // Publish geometry to the parent registry (see STLModel for rationale).
  useEffect(() => {
    onGeometryReady?.(geometry);
    return () => onGeometryReady?.(null);
  }, [geometry, onGeometryReady]);

  return (
    <group
      ref={ref as React.RefObject<THREE.Group | null>}
      position={[position[0], position[1], position[2] + halfZ]}
      rotation={rotation}
      scale={scale}
    >
      <mesh
        geometry={geometry}
        userData={{ isModelMesh: true }}
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
          transparent={dimmed}
          opacity={dimmed ? 0.4 : 1}
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
  bedHeight, gridRadius, orbitRef 
}: { 
  bedHeight: number;
  /** Half-extent of the full plate grid (max over plates of |offset| + bed/2). */
  gridRadius: number;
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
}) {
  const { camera, invalidate } = useThree();

  // Frame the WHOLE grid. Re-runs when the grid bound changes (plate add/remove)
  // so newly added plates fit in view. Framing is instant (no animation) — we
  // never animate the camera on plate selection.
  useEffect(() => {
    // n=1 → gridRadius*2 == max(bedW,bedD), so span == max(bedW,bedD,bedH) and
    // distance == maxDimension*1.5 — identical framing to the legacy single bed.
    const span = Math.max(gridRadius * 2, bedHeight);
    const distance = span * 1.5;
    camera.position.set(distance * 0.7, -distance * 0.7, distance * 0.6);
    camera.up.set(0, 0, 1); // Enforce Z-up for 3D printing convention
    camera.lookAt(0, 0, bedHeight / 4);
    camera.updateProjectionMatrix();
    // Sync OrbitControls' own target to the look-at point. Without this the
    // controls keep their previous internal target and snap the camera back on
    // the next user interaction (visible when the grid re-frames on add/remove).
    if (orbitRef.current) {
      orbitRef.current.target.set(0, 0, bedHeight / 4);
      orbitRef.current.update();
    }
    invalidate(); // frameloop="demand": redraw after re-framing
  }, [camera, invalidate, bedHeight, gridRadius, orbitRef]);

  // Allow zooming out far enough to see the entire grid, with headroom.
  const maxDistance = Math.max(2000, gridRadius * 8);

  return (
    <OrbitControls
      ref={orbitRef}
      makeDefault
      enablePan={true}
      enableRotate={true}
      enableZoom={true}
      minDistance={50}
      maxDistance={maxDistance}
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

      const [nx, ny] = localizeDragTarget(
        { x: hit.x, y: hit.y },
        { x: drag.offset.x, y: drag.offset.y },
        { width: bedConfig.width, depth: bedConfig.depth },
      );

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

      // Raycast against active-plate mesh children only
      const meshes: THREE.Object3D[] = [];
      scene.traverse((obj) => {
        if ((obj as THREE.Mesh).isMesh && obj.userData.isModelMesh && meshIsOnActivePlate(obj)) meshes.push(obj);
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
 * Text placement tool — rendered inside the R3F Canvas.
 * Sets a crosshair cursor and raycasts on click to find a surface point +
 * face normal for placing 3D text geometry.
 */
function TextPlacementTool({ onPlace }: { onPlace: (point: THREE.Vector3, normal: THREE.Vector3) => void }) {
  const { camera, gl, scene } = useThree();
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const raycaster = useMemo(() => new THREE.Raycaster(), []);

  useEffect(() => {
    canvasRef.current = gl.domElement;
    canvasRef.current.style.cursor = 'crosshair';
    return () => {
      if (canvasRef.current) canvasRef.current.style.cursor = '';
    };
  }, [gl.domElement]);

  useEffect(() => {
    const el = gl.domElement;

    const onClick = (e: MouseEvent) => {
      const rect = el.getBoundingClientRect();
      const ndc = new THREE.Vector2(
        ((e.clientX - rect.left) / rect.width) * 2 - 1,
        -((e.clientY - rect.top) / rect.height) * 2 + 1,
      );
      raycaster.setFromCamera(ndc, camera);

      const meshes: THREE.Object3D[] = [];
      scene.traverse((obj) => {
        if ((obj as THREE.Mesh).isMesh && obj.userData.isModelMesh && meshIsOnActivePlate(obj)) meshes.push(obj);
      });

      const hits = raycaster.intersectObjects(meshes, false);
      if (hits.length === 0) return;

      const hit = hits[0];
      const point = hit.point.clone();
      const normal = hit.face ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld).normalize() : new THREE.Vector3(0, 0, 1);
      onPlace(point, normal);
    };

    el.addEventListener('click', onClick);
    return () => el.removeEventListener('click', onClick);
  }, [camera, gl.domElement, onPlace, raycaster, scene]);

  return null;
}

/**
 * Main scene content
 */
function BedScene({
  bedConfig,
  models = [],
  plates,
  selectedModelId,
  onModelSelect,
  onPlateActivate,
  onPlateRename,
  onPlateDelete,
  onPlateArrange,
  onPlateOrient,
  onPlateToggleLock,
  transformMode,
  onModelTransform,
  onSelectedModelMetricsChange,
  showAxes = true,
  showGridLines = true,
  outOfBoundsModelIds,
  layFlatMode = false,
  onLayFlatComplete,
  autoOrientTrigger = 0,
  onModelGeometryChange,
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
  colorPaintMode = false,
  colorPaintData,
  onColorPaintUpdate,
  activeColorIndex = 0,
  fuzzySkinPaintMode = false,
  fuzzySkinPaintData,
  onFuzzySkinPaintUpdate,
  paintMode: paintModeOverride,
  paintBrushSize: paintBrushSizeOverride,
  hideBed = false,
  textPlacementMode = false,
  onTextPlace,
}: Omit<SlicerBedVisualizationProps, 'className' | 'backgroundColor' | 'showGrid' | 'gridDivisions'>) {
  const { width, depth, height, textureUrl, textureFormat } = bedConfig;
  const orbitRef = useRef<React.ComponentRef<typeof OrbitControls>>(null);
  const selectedMeshRef = useRef<THREE.Object3D>(null);

  // Stable-per-render map of per-model geometry registrars. Built with useMemo
  // (not a ref) so we never read a ref during render. Each registrar forwards
  // the model's processed geometry up to the parent's registry so non-selected
  // models can still be auto-oriented.
  const geometryRegistrars = useMemo(() => {
    const map = new Map<string, (g: THREE.BufferGeometry | null) => void>();
    for (const model of models) {
      map.set(model.id, (g: THREE.BufferGeometry | null) => onModelGeometryChange?.(model.id, g));
    }
    return map;
  }, [models, onModelGeometryChange]);

  // Disable orbit controls while painting on the model
  const handlePaintingStateChange = useCallback((isPainting: boolean) => {
    if (orbitRef.current) {
      orbitRef.current.enabled = !isPainting;
    }
  }, []);

  const { startDrag, justDraggedRef } = useBuildPlateDrag({
    bedConfig,
    models,
    orbitRef,
    onModelTransform,
    layFlatMode,
    transformMode,
  });

  // Resolve the plate descriptors into rendered plates carrying their own
  // models. When no plates are supplied, fall back to a single active plate
  // holding every model at offset [0,0,0] (legacy single-plate parity).
  const renderPlates = useMemo(() => {
    const byId = new Map(models.map(m => [m.id, m]));
    const source: ScenePlate[] = plates && plates.length > 0
      ? plates
      : [{ id: '__single__', name: 'Plate', offset: [0, 0, 0], active: true, locked: false, modelIds: models.map(m => m.id) }];
    return source.map(p => ({
      ...p,
      models: p.modelIds.map(id => byId.get(id)).filter((m): m is LoadedModel => m != null),
    }));
  }, [plates, models]);

  const multiPlate = renderPlates.length > 1;
  const activePlate = renderPlates.find(p => p.active) ?? renderPlates[0];
  const activePlateLocked = activePlate?.locked ?? false;

  // Half-extent of the whole grid, used to frame the camera and raise the
  // zoom-out limit. n=1 → max(width,depth)/2 (legacy single-bed framing).
  const gridRadius = useMemo(() => {
    let radius = Math.max(width, depth) / 2;
    for (const p of renderPlates) {
      radius = Math.max(radius, Math.abs(p.offset[0]) + width / 2, Math.abs(p.offset[1]) + depth / 2);
    }
    return radius;
  }, [renderPlates, width, depth]);

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

  // Clicking a plate's bed activates that plate (slice target) and clears the
  // current selection — mirroring the legacy "click empty bed to deselect"
  // behavior while also switching the active plate (requirement: highlight and
  // slice target never diverge). For the single-plate scene this is a plain
  // deselect, identical to before.
  const handleBedClick = useCallback((plateId: string) => {
    if (justDraggedRef.current) return;
    onPlateActivate?.(plateId);
    onModelSelect?.(null);
    onSelectedModelMetricsChange?.(null);
  }, [justDraggedRef, onPlateActivate, onModelSelect, onSelectedModelMetricsChange]);

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

    const scaleArr: [number, number, number] = [obj.scale.x, obj.scale.y, obj.scale.z];
    const result = computeAutoOrientation(geo, scaleArr);
    if (!result) return;

    const scaleVec = new THREE.Vector3(obj.scale.x, obj.scale.y, obj.scale.z);
    const dataZ = computeZForBedPlacement(geo, result.quaternion, scaleVec);

    onModelTransform(
      selectedModelId,
      [obj.position.x, obj.position.y, dataZ],
      result.rotation,
      obj.scale.toArray() as [number, number, number],
      { recordHistory: true, actionLabel: 'Auto-Orient' },
    );
  }, [autoOrientTrigger, onModelTransform, selectedModelId]);

  // Assembly view: compute radial offsets from centroid of the ACTIVE plate's
  // models (assembly inspection is per active plate so other plates aren't
  // dragged into the explode).
  const assemblyPositions = useMemo(() => {
    const activeModels = activePlate?.models ?? models;
    if (!assemblyViewActive || activeModels.length < 2) return null;
    const cx = activeModels.reduce((s, m) => s + m.position[0], 0) / activeModels.length;
    const cy = activeModels.reduce((s, m) => s + m.position[1], 0) / activeModels.length;
    const MIN_DISPLACEMENT = 30;
    const SCALE_FACTOR = 1.5;
    const map = new Map<string, [number, number, number]>();
    for (const model of activeModels) {
      const dx = model.position[0] - cx;
      const dy = model.position[1] - cy;
      const dist = Math.sqrt(dx * dx + dy * dy);
      let ox: number, oy: number;
      if (dist < 0.01) {
        // Model at centroid — push outward along arbitrary direction based on index
        const idx = activeModels.indexOf(model);
        const angle = (idx / activeModels.length) * Math.PI * 2;
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
  }, [assemblyViewActive, activePlate, models]);

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
      <CameraController bedHeight={height} gridRadius={gridRadius} orbitRef={orbitRef} />

      {/* Build plates — each plate's bed + models live in an offset group so
          model positions stay bed-local (the group carries the grid offset).
          For a single plate the offset is [0,0,0] → identical to the legacy
          layout. */}
      <group onPointerMissed={handlePointerMissed}>
        {(() => {
          // m3: single source of truth for "any tool/mode active that should
          // suppress drag". Used by both PrebuiltSTLModel and STLModel branches
          // below so the two `draggable` predicates can never drift.
          const isToolActive =
            layFlatMode ||
            assemblyViewActive ||
            cutMode ||
            supportPaintMode ||
            seamPaintMode ||
            colorPaintMode ||
            fuzzySkinPaintMode ||
            measureMode ||
            textPlacementMode ||
            transformMode === 'rotate' ||
            transformMode === 'scale';

          const renderModel = (model: LoadedModel, dimmed: boolean, plateLocked: boolean) => {
            const displayPos = assemblyPositions?.get(model.id) ?? model.position;
            // Locked plates and inactive (dimmed) plates are not draggable so a
            // stray drag can't mutate models the user isn't focused on.
            const draggable = !isToolActive && !plateLocked && !dimmed;
            return (
              <Suspense key={model.id} fallback={<LoadingIndicator />}>
                {(model.fileType === 'stl' || model.fileType === '3mf') && (
                  model.geometry ? (
                    <PrebuiltSTLModel
                      inputGeometry={model.geometry}
                      position={displayPos}
                      rotation={model.rotation}
                      scale={model.scale}
                      selected={model.id === selectedModelId}
                      outOfBounds={outOfBoundsModelIds?.has(model.id)}
                      layFlatMode={model.id === selectedModelId && layFlatMode}
                      draggable={draggable}
                      dimmed={dimmed}
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
                      onGeometryReady={geometryRegistrars.get(model.id)}
                    />
                  ) : (
                    <ModelViewerErrorBoundary
                      resetKey={`${model.id}:${model.viewerUrl ?? model.url}`}
                      fallback={(
                        <Html center>
                          <div
                            className="max-w-xs rounded-lg border border-pf-border bg-pf-bg-1/95 px-4 py-3 text-center text-sm text-pf-text-primary shadow-lg backdrop-blur-sm"
                            role="alert"
                          >
                            Failed to load this 3D model. Select another model or retry with a refreshed source.
                          </div>
                        </Html>
                      )}
                    >
                      <UrlModelViewer
                        fileType={model.fileType}
                        url={model.viewerUrl ?? model.url}
                        position={displayPos}
                        rotation={model.rotation}
                        scale={model.scale}
                        selected={model.id === selectedModelId}
                        outOfBounds={outOfBoundsModelIds?.has(model.id)}
                        layFlatMode={model.id === selectedModelId && layFlatMode}
                        draggable={draggable}
                        dimmed={dimmed}
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
                        onGeometryReady={geometryRegistrars.get(model.id)}
                      />
                    </ModelViewerErrorBoundary>
                  )
                )}
              </Suspense>
            );
          };

          return renderPlates.map((plate, plateIndex) => (
            <PlateGroup key={plate.id} offset={plate.offset} active={plate.active}>
              {/* Print bed — hidden during paint mode */}
              {!hideBed && (
                <PrintBedPlatform
                  width={width}
                  depth={depth}
                  textureUrl={textureUrl}
                  textureFormat={textureFormat}
                  showGridLines={showGridLines}
                  active={plate.active}
                  highlight={multiPlate}
                  onBedClick={() => handleBedClick(plate.id)}
                />
              )}

              {/* Build volume wireframe */}
              {!hideBed && <BuildVolumeWireframe width={width} depth={depth} height={height} />}

              {/* Axis indicators — only on the active plate to avoid clutter */}
              {!hideBed && showAxes && plate.active && (
                <AxisIndicators size={Math.min(width, depth) * 0.15} />
              )}

              {/* Per-plate chrome (number / title / actions). Suppressed at n=1
                  to preserve legacy single-plate visual parity, and while the
                  bed is hidden (paint/cut modes). */}
              {multiPlate && !hideBed && (
                <PlateBedOverlay
                  plateNumber={plateIndex + 1}
                  name={plate.name}
                  active={plate.active}
                  locked={plate.locked}
                  canDelete={renderPlates.length > 1}
                  bedWidth={width}
                  bedDepth={depth}
                  onActivate={() => onPlateActivate?.(plate.id)}
                  onRename={(name) => onPlateRename?.(plate.id, name)}
                  onDelete={() => onPlateDelete?.(plate.id)}
                  onArrange={() => onPlateArrange?.(plate.id)}
                  onOrient={() => onPlateOrient?.(plate.id)}
                  onToggleLock={() => onPlateToggleLock?.(plate.id)}
                />
              )}

              {plate.models.map((model) =>
                renderModel(model, multiPlate && !plate.active, plate.locked),
              )}
            </PlateGroup>
          ));
        })()}
      </group>

      {/* Measure tool overlay */}
      {measureMode && <MeasureTool />}

      {/* Text placement overlay */}
      {textPlacementMode && onTextPlace && <TextPlacementTool onPlace={onTextPlace} />}

      {/* Cut plane overlay */}
      {cutMode && selectedModelId && onCutComplete && onCutCancel && (
        <CutPlaneOverlay
          meshRef={selectedMeshRef}
          active={cutMode}
          bedConfig={bedConfig}
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
          paintMode={paintModeOverride}
          brushSize={paintBrushSizeOverride}
          onPaintingStateChange={handlePaintingStateChange}
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
          paintMode={paintModeOverride}
          brushSize={paintBrushSizeOverride}
          onPaintingStateChange={handlePaintingStateChange}
        />
      )}

      {/* Color paint overlay — multi-color per-face */}
      {colorPaintMode && selectedModelId && onColorPaintUpdate && (
        <ColorPaintOverlay
          key={`color-${selectedModelId}`}
          meshRef={selectedMeshRef}
          paintedFaces={colorPaintData?.get(selectedModelId) ?? EMPTY_COLOR_FACE_MAP}
          onPaintUpdate={onColorPaintUpdate}
          activeColorIndex={activeColorIndex}
          opacity={0.5}
          active={colorPaintMode}
          paintMode={paintModeOverride}
          brushSize={paintBrushSizeOverride}
          onPaintingStateChange={handlePaintingStateChange}
        />
      )}

      {/* Fuzzy skin paint overlay */}
      {fuzzySkinPaintMode && selectedModelId && onFuzzySkinPaintUpdate && (
        <FacePaintOverlay
          key={`fuzzy-${selectedModelId}`}
          meshRef={selectedMeshRef}
          paintedFaces={fuzzySkinPaintData?.get(selectedModelId) ?? EMPTY_FACE_SET}
          onPaintUpdate={onFuzzySkinPaintUpdate}
          color="#f59e0b"
          opacity={0.35}
          active={fuzzySkinPaintMode}
          paintMode={paintModeOverride}
          brushSize={paintBrushSizeOverride}
          onPaintingStateChange={handlePaintingStateChange}
        />
      )}

      {/* TransformControls for the selected model — suppressed when the active
          plate is locked (no gizmo, no transform). */}
      {selectedModelId && transformMode && !activePlateLocked && (
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
  plates,
  selectedModelId,
  onModelSelect,
  onPlateActivate,
  onPlateRename,
  onPlateDelete,
  onPlateArrange,
  onPlateOrient,
  onPlateToggleLock,
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
  onModelGeometryChange,
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
  colorPaintMode = false,
  colorPaintData,
  onColorPaintUpdate,
  activeColorIndex = 0,
  fuzzySkinPaintMode = false,
  fuzzySkinPaintData,
  onFuzzySkinPaintUpdate,
  paintMode,
  paintBrushSize,
  hideBed = false,
  textPlacementMode = false,
  onTextPlace,
  sceneOverlay,
}) => {
  const resetKey = `${selectedModelId ?? 'none'}:${models.map((model) => `${model.id}:${model.url}:${model.viewerUrl ?? ''}:${model.fileType}`).join('|')}`;

  return (
    <div className={`w-full h-full ${className}`}>
      <ModelViewerErrorBoundary className="h-full" resetKey={resetKey}>
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
              plates={plates}
              selectedModelId={selectedModelId}
              onModelSelect={onModelSelect}
              onPlateActivate={onPlateActivate}
              onPlateRename={onPlateRename}
              onPlateDelete={onPlateDelete}
              onPlateArrange={onPlateArrange}
              onPlateOrient={onPlateOrient}
              onPlateToggleLock={onPlateToggleLock}
              transformMode={transformMode}
              onModelTransform={onModelTransform}
              onSelectedModelMetricsChange={onSelectedModelMetricsChange}
              outOfBoundsModelIds={outOfBoundsModelIds}
              showAxes={showAxes}
              showGridLines={showGridLines}
              layFlatMode={layFlatMode}
              onLayFlatComplete={onLayFlatComplete}
              autoOrientTrigger={autoOrientTrigger}
              onModelGeometryChange={onModelGeometryChange}
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
              colorPaintMode={colorPaintMode}
              colorPaintData={colorPaintData}
              onColorPaintUpdate={onColorPaintUpdate}
              activeColorIndex={activeColorIndex}
              fuzzySkinPaintMode={fuzzySkinPaintMode}
              fuzzySkinPaintData={fuzzySkinPaintData}
              onFuzzySkinPaintUpdate={onFuzzySkinPaintUpdate}
              paintMode={paintMode}
              paintBrushSize={paintBrushSize}
              hideBed={hideBed}
              textPlacementMode={textPlacementMode}
              onTextPlace={onTextPlace}
            />
            {sceneOverlay}
          </Suspense>
        </Canvas>
      </ModelViewerErrorBoundary>
    </div>
  );
};

export default SlicerBedVisualization;
