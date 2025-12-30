/**
 * Bed Geometry Generator
 * Generates Three.js geometries for 3D printer beds based on printer specifications
 */

import * as THREE from 'three';
import { PrinterModelDto } from '@/types/api';

export interface BedDimensions {
  width: number;  // X axis (mm)
  depth: number;  // Y axis (mm)
  height: number; // Z axis (mm)
  thickness?: number; // Bed plate thickness (mm), default 5
}

export interface BedGeometry {
  bedMesh: THREE.Mesh;
  buildVolumeMesh: THREE.Mesh;
  gridHelper: THREE.GridHelper;
  axesHelper: THREE.AxesHelper;
}

/**
 * Extract bed dimensions from a PrinterModelDto
 * Falls back to reasonable defaults if dimensions not specified
 */
export function extractBedDimensions(printerModel: PrinterModelDto): BedDimensions {
  return {
    width: (printerModel.maxX && printerModel.maxX > 0) ? printerModel.maxX : 235,    // Default: Prusa MINI bed width
    depth: (printerModel.maxY && printerModel.maxY > 0) ? printerModel.maxY : 235,    // Default: Prusa MINI bed depth
    height: (printerModel.maxZ && printerModel.maxZ > 0) ? printerModel.maxZ : 210,   // Default: Prusa MINI build height
    thickness: 5, // Standard bed plate thickness
  };
}

/**
 * Generate a 3D bed platform mesh
 * Creates a rectangular platform at Z=0 representing the print bed
 */
export function generateBedPlatformMesh(dimensions: BedDimensions): THREE.Mesh {
  const { width, depth, thickness } = dimensions;

  // Create bed platform geometry (positioned with top surface at Z=0)
  const geometry = new THREE.BoxGeometry(width, thickness, depth);

  // Material with slight reflectivity for realism
  const material = new THREE.MeshPhongMaterial({
    color: 0x1a1a1a, // Dark gray
    shininess: 20,
    emissive: 0x0a0a0a,
  });

  const mesh = new THREE.Mesh(geometry, material);
  // Position so top of bed is at Z=0
  mesh.position.y = -thickness / 2;

  return mesh;
}

/**
 * Generate a build volume wireframe
 * Shows the printable area boundaries
 */
export function generateBuildVolumeWireframe(dimensions: BedDimensions): THREE.Mesh {
  const { width, depth, height } = dimensions;

  // Create a box representing the build volume
  const geometry = new THREE.BoxGeometry(width, height, depth);

  // Edges only (wireframe)
  const edgesGeometry = new THREE.EdgesGeometry(geometry);
  const line = new THREE.LineSegments(
    edgesGeometry,
    new THREE.LineBasicMaterial({
      color: 0x00ff00, // Bright green
      linewidth: 1,
      transparent: true,
      opacity: 0.6,
    })
  );

  // Position so bottom is at Z=0, top is at Z=height
  line.position.y = height / 2;

  return line;
}

/**
 * Generate a grid overlay on the bed surface
 * Provides visual reference for bed position and dimensions
 */
export function generateGridHelper(dimensions: BedDimensions): THREE.GridHelper {
  const { width, depth } = dimensions;

  // Calculate appropriate grid divisions
  // Aim for 10-20mm squares
  const targetSquareSize = 20;
  const divisionsX = Math.max(5, Math.floor(width / targetSquareSize));
  const divisionsZ = Math.max(5, Math.floor(depth / targetSquareSize));

  const grid = new THREE.GridHelper(
    Math.max(width, depth), // Use larger dimension for grid size
    divisionsX + divisionsZ, // Total divisions
    0x404040, // Center line color (dark gray)
    0x303030  // Grid line color (darker gray)
  );

  // Position grid at bed surface (Z=0)
  grid.position.y = 0;

  return grid;
}

/**
 * Generate axes helper for spatial reference
 * Red = X axis, Green = Y axis, Blue = Z axis
 */
export function generateAxesHelper(scale: number = 50): THREE.AxesHelper {
  return new THREE.AxesHelper(scale);
}

/**
 * Generate nozzle indicator geometry
 * Visual representation of the print nozzle
 */
export function generateNozzleGeometry(nozzleDiameter: number = 0.4): THREE.BufferGeometry {
  // Create a cone shape pointing downward (toward the bed)
  const height = nozzleDiameter * 3;
  const radius = nozzleDiameter / 2;

  return new THREE.ConeGeometry(radius, height, 8);
}

/**
 * Create a complete bed visualization with all components
 * Returns a group containing all visual elements
 */
export function createBedVisualization(printerModel: PrinterModelDto): {
  group: THREE.Group;
  dimensions: BedDimensions;
} {
  const dimensions = extractBedDimensions(printerModel);

  const group = new THREE.Group();

  // Add bed platform
  const bedMesh = generateBedPlatformMesh(dimensions);
  group.add(bedMesh);

  // Add build volume wireframe
  const buildVolume = generateBuildVolumeWireframe(dimensions);
  group.add(buildVolume);

  // Add grid for spatial reference
  const grid = generateGridHelper(dimensions);
  group.add(grid);

  // Add axes for orientation
  const axes = generateAxesHelper(Math.max(dimensions.width, dimensions.depth) * 0.3);
  group.add(axes);

  return { group, dimensions };
}

/**
 * Calculate camera position to view the entire bed
 * Positions camera at a good angle to see the entire build volume
 */
export function calculateOptimalCameraPosition(
  dimensions: BedDimensions
): { position: THREE.Vector3; target: THREE.Vector3 } {
  const { width, depth, height } = dimensions;

  // Camera positioned to see entire bed from a 45-degree angle
  const distance = Math.max(width, depth) * 0.8;
  const position = new THREE.Vector3(distance * 0.6, distance * 0.5, distance * 0.6);

  // Look at center of bed at mid-height
  const target = new THREE.Vector3(0, height / 2, 0);

  return { position, target };
}

/**
 * Calculate scale factors for positioning objects in scene
 * Ensures consistent sizing regardless of bed dimensions
 */
export function calculateScaleFactors(dimensions: BedDimensions): {
  nozzleScale: number;
  markerScale: number;
  textScale: number;
} {
  const maxDimension = Math.max(dimensions.width, dimensions.depth, dimensions.height);

  return {
    nozzleScale: maxDimension * 0.005,     // 0.5% of largest dimension
    markerScale: maxDimension * 0.01,      // 1% of largest dimension
    textScale: maxDimension * 0.05,        // 5% of largest dimension
  };
}

/**
 * Validate bed dimensions
 * Ensures dimensions are reasonable and positive
 */
export function validateBedDimensions(dimensions: BedDimensions): { valid: boolean; error?: string } {
  if (dimensions.width <= 0) {
    return { valid: false, error: 'Bed width must be positive' };
  }
  if (dimensions.depth <= 0) {
    return { valid: false, error: 'Bed depth must be positive' };
  }
  if (dimensions.height <= 0) {
    return { valid: false, error: 'Build height must be positive' };
  }

  // Check for unreasonable dimensions (larger than 1000mm)
  if (dimensions.width > 1000 || dimensions.depth > 1000 || dimensions.height > 1000) {
    return { valid: false, error: 'Bed dimensions exceed maximum allowed size (1000mm)' };
  }

  return { valid: true };
}
