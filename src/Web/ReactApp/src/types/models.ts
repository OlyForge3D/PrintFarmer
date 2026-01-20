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
  fileType: 'stl' | '3mf' | 'obj' | 'ply';
  uploadedAt: string;
  url?: string;
  thumbnailUrl?: string;
  tags?: Array<{
    id: string;
    name: string;
    color?: string;
  }>;
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
