/**
 * Type definitions for G-code file management
 * Consolidated from scattered definitions across the gcode feature
 */

/**
 * Represents a G-code file in the viewer context (simplified from ModelsPage)
 */
export interface GCodeFile {
  id: string;
  name: string;
  url: string;
  printTime?: number;
  filamentUsed?: number;
  layerCount?: number;
}

/**
 * File entry for file browser/explorer
 */
export interface FileEntry {
  id: string;
  name: string;
  path: string;
  isDirectory: boolean;
  size?: number;
  modifiedAt?: string;
  thumbnailUrl?: string;
}

/**
 * Folder node structure for hierarchical file browser
 */
export interface FolderNode {
  path: string;
  name: string;
  children: FolderNode[];
  fileCount: number;
  expanded: boolean;
}
