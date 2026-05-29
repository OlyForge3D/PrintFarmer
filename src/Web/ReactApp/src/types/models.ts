/**
 * Type definitions for 3D model management
 * Consolidated from scattered definitions across the models3d feature
 */

/**
 * Represents a 3D model file in the system
 * This type is used throughout the models3d feature for displaying and managing 3D model files
 */
export interface Model {
  id: string;
  path: string;
  name: string;
  fileName: string;
  fileSize: number;
  fileType: 'stl' | '3mf' | 'obj' | 'ply' | 'step' | 'stp';
  uploadedAt: string;
  url?: string;
  thumbnailUrl?: string;
  tags?: Array<{
    id: string;
    name: string;
    color?: string;
  }>;
  extractedMetadata?: ThreeMfMetadata | null;
  autoTags?: string[];
}

/**
 * Metadata extracted from a 3MF file
 */
export interface ThreeMfMetadata {
  title: string | null;
  designer: string | null;
  description: string | null;
  application: string | null;
  creationDate: string | null;
  modificationDate: string | null;
  materials: string[];
  autoTags: string[];
}

/**
 * Represents a tag that can be applied to 3D models
 */
export interface ModelTag {
  id: string;
  name: string;
  color?: string;
  description?: string;
}

/**
 * Lightweight model DTO interface for model picker (subset of Model3DDto)
 */
export interface ModelListItem {
  id: string;
  fileName: string;
  originalFileName: string;
  fileFormat: number;
  uploadedAt: string;
  filePath?: string;
}

/**
 * Response from backend /3d-models/query endpoint
 */
export interface Model3DSearchResponse {
  models: Model[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

/**
 * Response from POST /api/3d-models/upload-geometry
 * Used when uploading STL geometry blobs (e.g. cut model pieces).
 */
export interface GeometryUploadResultDto {
  id: string;
  fileName: string;
  fileSize: number;
  fileUrl: string;
}
