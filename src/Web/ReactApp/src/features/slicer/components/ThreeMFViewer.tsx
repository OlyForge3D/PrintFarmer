import { Html } from '@react-three/drei';
import { useLoader } from '@react-three/fiber';
import { Select } from '@/common/components/ui';
import { DEFAULT_EXTRUDER_COLORS } from '@/features/slicer/components/viewer/extruderColors';
import { apiClient } from '@/services/api';
import { cloneThreeMfMeshesDroppedToBed } from '@/features/slicer/utils/threemf-display';
import { disposeParsedThreeMfModel, parseThreeMfArchive, ThreeMfSecurityError, type ParsedThreeMfModel } from '@/features/slicer/utils/threemf-parser';
import { STLLoader } from 'three-stdlib';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { useEffect, useMemo, useRef, useState } from 'react';
import * as THREE from 'three';

interface ViewerMetrics {
  baseSize: [number, number, number];
  currentSize: [number, number, number];
  currentScale: [number, number, number];
}

interface SharedModelViewerProps {
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
  onSelectedMetrics?: (metrics: ViewerMetrics) => void;
  onLayFlatFaceClick?: (normal: THREE.Vector3) => void;
  onGeometryReady?: (geometry: THREE.BufferGeometry | null) => void;
  renderSelectionBoundingBox: (geometry: THREE.BufferGeometry, outOfBounds?: boolean) => React.ReactNode;
  renderFaceSwatches: (geometry: THREE.BufferGeometry, onFaceClick: (normal: THREE.Vector3) => void) => React.ReactNode;
}

interface PreparedDisplayMesh {
  extruderIndex: number;
  geometry: THREE.BufferGeometry;
}

interface PreparedDisplayData {
  meshes: PreparedDisplayMesh[];
  selectionGeometry: THREE.BufferGeometry;
  halfZ: number;
  availablePlateIds: number[];
  maxZ: number;
}

const DEFAULT_VIEWER_COLOR = '#009688';
const PLATE_ALL_VALUE = 'all';

function buildFallbackStlUrl(url: string): string {
  const separator = url.includes('?') ? '&' : '?';
  return `${url}${separator}forceStl=true`;
}

function getExtruderColor(index: number): string {
  const color = DEFAULT_EXTRUDER_COLORS[index % DEFAULT_EXTRUDER_COLORS.length];
  return `#${color.getHexString()}`;
}

function createSelectionGeometry(geometries: THREE.BufferGeometry[]): THREE.BufferGeometry {
  if (geometries.length === 0) {
    return new THREE.BufferGeometry();
  }

  if (geometries.length === 1) {
    const geometry = geometries[0].clone();
    geometry.computeBoundingBox();
    geometry.computeBoundingSphere();
    return geometry;
  }

  const merged = mergeGeometries(geometries.map((geometry) => geometry.clone()), false);
  if (!merged) {
    const fallback = geometries[0].clone();
    fallback.computeBoundingBox();
    fallback.computeBoundingSphere();
    return fallback;
  }

  merged.computeBoundingBox();
  merged.computeBoundingSphere();
  return merged;
}

function usePreparedDisplayData(
  parsedModel: ParsedThreeMfModel | null,
  selectedPlateValue: number | null,
): PreparedDisplayData | null {
  const preparedData = useMemo(() => {
    if (!parsedModel) {
      return null;
    }

    const visibleMeshes = selectedPlateValue == null
      ? parsedModel.meshes
      : parsedModel.meshes.filter((mesh) => mesh.plateId === selectedPlateValue);
    const meshesToRender = visibleMeshes.length > 0 ? visibleMeshes : parsedModel.meshes;

    const { meshes: workingGeometries, center, size } = cloneThreeMfMeshesDroppedToBed(
      meshesToRender.map((mesh) => ({
        buildItemIndex: mesh.buildItemIndex,
        extruderIndex: mesh.extruderIndex,
        geometry: mesh.geometry,
      })),
    );
    const halfZ = size.z / 2;

    for (const mesh of workingGeometries) {
      mesh.geometry.translate(-center.x, -center.y, -center.z);
      mesh.geometry.computeVertexNormals();
      mesh.geometry.computeBoundingBox();
      mesh.geometry.computeBoundingSphere();
    }

    return {
      meshes: workingGeometries,
      selectionGeometry: createSelectionGeometry(workingGeometries.map((mesh) => mesh.geometry)),
      halfZ,
      availablePlateIds: parsedModel.availablePlateIds,
      maxZ: halfZ,
    } satisfies PreparedDisplayData;
  }, [parsedModel, selectedPlateValue]);

  useEffect(() => {
    return () => {
      if (!preparedData) {
        return;
      }

      for (const mesh of preparedData.meshes) {
        mesh.geometry.dispose();
      }
      preparedData.selectionGeometry.dispose();
    };
  }, [preparedData]);

  return preparedData;
}

function FallbackStlModel({
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
  renderSelectionBoundingBox,
  renderFaceSwatches,
}: SharedModelViewerProps) {
  const rawGeometry = useLoader(STLLoader, url);
  const internalRef = useRef<THREE.Group>(null);
  const ref = meshRef || internalRef;

  const { geometry, halfZ } = useMemo(() => {
    const clonedGeometry = rawGeometry.clone();
    clonedGeometry.computeBoundingBox();

    let computedHalfZ = 0;
    if (clonedGeometry.boundingBox) {
      const centerX = (clonedGeometry.boundingBox.min.x + clonedGeometry.boundingBox.max.x) / 2;
      const centerY = (clonedGeometry.boundingBox.min.y + clonedGeometry.boundingBox.max.y) / 2;
      const centerZ = (clonedGeometry.boundingBox.min.z + clonedGeometry.boundingBox.max.z) / 2;
      computedHalfZ = (clonedGeometry.boundingBox.max.z - clonedGeometry.boundingBox.min.z) / 2;
      clonedGeometry.translate(-centerX, -centerY, -centerZ);
    }

    clonedGeometry.computeVertexNormals();
    clonedGeometry.computeBoundingBox();
    clonedGeometry.computeBoundingSphere();
    return {
      geometry: clonedGeometry,
      halfZ: computedHalfZ,
    };
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
    return () => {
      geometry.dispose();
    };
  }, [geometry]);

  useEffect(() => {
    if (!selected || !onSelectedMetrics) {
      return;
    }

    onSelectedMetrics({
      baseSize,
      currentSize: [baseSize[0] * scale[0], baseSize[1] * scale[1], baseSize[2] * scale[2]],
      currentScale: scale,
    });
  }, [baseSize, onSelectedMetrics, scale, selected]);

  useEffect(() => {
    if (!ref.current) {
      return;
    }

    ref.current.userData.halfZ = halfZ;
    ref.current.userData.geometry = geometry;
  }, [geometry, halfZ, ref]);

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
        onPointerDown={(event) => {
          event.stopPropagation();
          onClick?.();
          if (draggable && onDragStart) {
            onDragStart(event.nativeEvent.clientX, event.nativeEvent.clientY);
          }
        }}
        onClick={(event) => {
          event.stopPropagation();
          onClick?.();
        }}
        onPointerOver={(event) => {
          if (draggable) {
            event.stopPropagation();
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
        <meshStandardMaterial color={DEFAULT_VIEWER_COLOR} metalness={0.05} roughness={0.7} transparent={dimmed} opacity={dimmed ? 0.4 : 1} />
        {selected ? renderSelectionBoundingBox(geometry, outOfBounds) : null}
      </mesh>
      {selected && layFlatMode && onLayFlatFaceClick ? renderFaceSwatches(geometry, onLayFlatFaceClick) : null}
    </group>
  );
}

export function ThreeMFViewer({
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
  renderSelectionBoundingBox,
  renderFaceSwatches,
}: SharedModelViewerProps) {
  const [parsedModel, setParsedModel] = useState<ParsedThreeMfModel | null>(null);
  const [parseError, setParseError] = useState<string | null>(null);
  const [fallbackUrl, setFallbackUrl] = useState<string | null>(null);
  const [selectedPlate, setSelectedPlate] = useState<string>(PLATE_ALL_VALUE);
  const internalRef = useRef<THREE.Group>(null);
  const ref = meshRef || internalRef;

  useEffect(() => {
    let cancelled = false;

    async function loadModel(): Promise<void> {
      setParsedModel(null);
      setParseError(null);
      setFallbackUrl(null);
      setSelectedPlate(PLATE_ALL_VALUE);

      try {
        // url is already fully-qualified (built via getApiBaseUrl()); override baseURL
        // to avoid Axios double-prefixing it with the instance's /api baseURL.
        const response = await apiClient.get<ArrayBuffer>(url, { responseType: 'arraybuffer', baseURL: '' });
        if (cancelled) {
          return;
        }

        const nextModel = await parseThreeMfArchive(response.data);
        if (cancelled) {
          disposeParsedThreeMfModel(nextModel);
          return;
        }

        setParsedModel(nextModel);
        if (nextModel.availablePlateIds.length > 1 && nextModel.defaultPlateId != null) {
          setSelectedPlate(nextModel.defaultPlateId.toString());
        }
      } catch (error) {
        if (cancelled) {
          return;
        }

        setParseError(error instanceof Error ? error.message : 'Failed to parse 3MF model.');
        // Security/limit errors must NOT fall back to STL — the file is malicious or too large.
        if (!(error instanceof ThreeMfSecurityError)) {
          setFallbackUrl(buildFallbackStlUrl(url));
        }
      }
    }

    void loadModel();

    return () => {
      cancelled = true;
    };
  }, [url]);

  useEffect(() => {
    return () => {
      disposeParsedThreeMfModel(parsedModel);
    };
  }, [parsedModel]);

  const selectedPlateValue = selectedPlate === PLATE_ALL_VALUE ? null : Number.parseInt(selectedPlate, 10);
  const displayData = usePreparedDisplayData(
    parsedModel,
    Number.isFinite(selectedPlateValue ?? Number.NaN) ? selectedPlateValue : null,
  );

  const baseSize = useMemo<[number, number, number]>(() => {
    if (!displayData) {
      return [0, 0, 0];
    }

    const size = new THREE.Vector3();
    displayData.selectionGeometry.computeBoundingBox();
    displayData.selectionGeometry.boundingBox?.getSize(size);
    return [size.x, size.y, size.z];
  }, [displayData]);

  useEffect(() => {
    if (!selected || !onSelectedMetrics || !displayData) {
      return;
    }

    onSelectedMetrics({
      baseSize,
      currentSize: [baseSize[0] * scale[0], baseSize[1] * scale[1], baseSize[2] * scale[2]],
      currentScale: scale,
    });
  }, [baseSize, displayData, onSelectedMetrics, scale, selected]);

  useEffect(() => {
    if (!displayData || !ref.current) {
      return;
    }

    ref.current.userData.halfZ = displayData.halfZ;
    ref.current.userData.geometry = displayData.selectionGeometry;
  }, [displayData, ref]);

  useEffect(() => {
    if (!displayData) return;
    onGeometryReady?.(displayData.selectionGeometry);
    return () => onGeometryReady?.(null);
  }, [displayData, onGeometryReady]);

  if (fallbackUrl) {
    return (
      <group>
        <FallbackStlModel
          url={fallbackUrl}
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
          renderSelectionBoundingBox={renderSelectionBoundingBox}
          renderFaceSwatches={renderFaceSwatches}
        />
        {selected && parseError ? (
          <Html center>
            <div className="max-w-xs rounded-lg border border-pf-warning/40 bg-pf-bg-1/95 px-3 py-2 text-xs text-pf-text-primary shadow-lg backdrop-blur-sm">
              Native 3MF parsing failed. Showing the STL fallback instead.
            </div>
          </Html>
        ) : null}
      </group>
    );
  }

  if (!displayData) {
    return (
      <Html center>
        <div className="rounded-lg border border-pf-border bg-pf-bg-2/90 px-4 py-2 text-sm text-pf-text-primary shadow-lg backdrop-blur-sm">
          Loading 3MF model…
        </div>
      </Html>
    );
  }

  return (
    <group
      ref={ref as React.RefObject<THREE.Group | null>}
      position={[position[0], position[1], position[2] + displayData.halfZ]}
      rotation={rotation}
      scale={scale}
    >
      {displayData.meshes.map((mesh, index) => (
        <mesh
          key={`${mesh.extruderIndex}-${index}`}
          geometry={mesh.geometry}
          userData={{ isModelMesh: true }}
          onPointerDown={(event) => {
            event.stopPropagation();
            onClick?.();
            if (draggable && onDragStart) {
              onDragStart(event.nativeEvent.clientX, event.nativeEvent.clientY);
            }
          }}
          onClick={(event) => {
            event.stopPropagation();
            onClick?.();
          }}
          onPointerOver={(event) => {
            if (draggable) {
              event.stopPropagation();
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
            color={getExtruderColor(mesh.extruderIndex)}
            metalness={0.05}
            roughness={0.7}
            transparent={dimmed}
            opacity={dimmed ? 0.4 : 1}
          />
        </mesh>
      ))}

      {selected ? renderSelectionBoundingBox(displayData.selectionGeometry, outOfBounds) : null}
      {selected && layFlatMode && onLayFlatFaceClick
        ? renderFaceSwatches(displayData.selectionGeometry, onLayFlatFaceClick)
        : null}

      {selected && displayData.availablePlateIds.length > 1 ? (
        <Html position={[0, 0, displayData.maxZ + 18]} center>
          <div className="min-w-[10rem] rounded-lg border border-pf-border bg-pf-bg-1/95 p-3 shadow-lg backdrop-blur-sm">
            <div className="mb-1 text-xs font-medium text-pf-text-primary">Plate</div>
            <Select
              aria-label="Select 3MF plate"
              value={selectedPlate}
              onChange={(event) => setSelectedPlate(event.target.value)}
              className="w-full min-w-[9rem]"
            >
              <option value={PLATE_ALL_VALUE}>All plates</option>
              {displayData.availablePlateIds.map((plateId) => (
                <option key={plateId} value={plateId.toString()}>
                  Plate {plateId}
                </option>
              ))}
            </Select>
          </div>
        </Html>
      ) : null}

    </group>
  );
}
