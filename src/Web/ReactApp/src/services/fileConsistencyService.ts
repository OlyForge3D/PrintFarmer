import { apiClient } from './api';
import type {
  FileHealthSummaryDto,
  FileHealthAuditDto,
  FileIssuesSummaryDto,
  FileHealthDetailDto,
} from '@/types/api';

/**
 * Service for interacting with File Consistency API endpoints
 * Provides methods to fetch file health status, audit history, and issue details
 */
export const fileConsistencyService = {
  /**
   * Get overall file health summary
   * @returns Health summary with percentages and file counts
   */
  async getHealthSummary(): Promise<FileHealthSummaryDto> {
    return apiClient.getFileHealthSummary();
  },

  /**
   * Get audit history with pagination
   * @param pageSize Number of audit records to retrieve (default: 20)
   * @returns Array of audit records in reverse chronological order
   */
  async getAuditHistory(pageSize: number = 20): Promise<FileHealthAuditDto[]> {
    return apiClient.getFileAuditHistory(pageSize);
  },

  /**
   * Get all files with health issues
   * @returns Summary of missing, corrupted, and inaccessible files
   */
  async getFilesWithIssues(): Promise<FileIssuesSummaryDto> {
    return apiClient.getFilesWithIssues();
  },

  /**
   * Get detailed health information for a specific Model3D file
   * @param id Model3D file ID
   * @returns Detailed health status and verification history
   */
  async getModel3DHealth(id: string): Promise<FileHealthDetailDto> {
    return apiClient.getModel3DHealth(id);
  },

  /**
   * Get detailed health information for a specific GcodeFile
   * @param id GcodeFile ID
   * @returns Detailed health status and verification history
   */
  async getGcodeFileHealth(id: string): Promise<FileHealthDetailDto> {
    return apiClient.getGcodeFileHealth(id);
  },
};

// Export types for convenience
export type { FileHealthSummaryDto, FileHealthAuditDto, FileIssuesSummaryDto, FileHealthDetailDto };
export { FileHealthStatus, FileAuditType } from '@/types/api';
